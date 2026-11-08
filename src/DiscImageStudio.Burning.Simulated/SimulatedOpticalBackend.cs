using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;

namespace DiscImageStudio.Burning.Simulated;

/// <summary>Faults a simulated recorder injects into its next burns.</summary>
public sealed class SimulatedFaults
{
    /// <summary>The recorder runs dry at this fraction of the burn (the producer "stalls").</summary>
    public double? UnderrunAtFraction { get; set; }

    /// <summary>A write error at this fraction of the burn.</summary>
    public double? FailWriteAtFraction { get; set; }

    /// <summary>Closing the session after a failure or a cancellation fails too.</summary>
    public bool FailCleanup { get; set; }

    /// <summary>The disc is swapped between the confirmation and the start of writing.</summary>
    public bool ChangeMediaBeforeWrite { get; set; }
}

/// <summary>What the last burn wrote, for checking it against the source.</summary>
public sealed record SimulatedWrite(OpticalBurnMediaKind Mode, long Bytes, string Sha256, bool Completed);

/// <summary>One simulated recorder. Its state can be changed while the app runs (disc swap, permission).</summary>
public sealed class SimulatedRecorder(string id, string name, OpticalWriteModes writeModes)
{
    public string Id { get; } = id;

    public string Name { get; } = name;

    public string Vendor { get; init; } = "SIMULATED";

    public string Model { get; init; } = name;

    public string Firmware { get; init; } = "1.00";

    public OpticalWriteModes WriteModes { get; } = writeModes;

    public OpticalDeviceFeatures Features { get; init; } = OpticalDeviceFeatures.Eject | OpticalDeviceFeatures.Lock | OpticalDeviceFeatures.BufferUnderrunProtection;

    public OpticalDeviceAccess Access { get; set; } = OpticalDeviceAccess.Ready;

    public string? AccessDetail { get; set; }

    public OpticalMediaState? Media { get; set; }

    /// <summary>Simulated recording rate; burns sleep to match it.</summary>
    public double BytesPerSecond { get; set; } = 256.0 * 1024 * 1024;

    public SimulatedFaults Faults { get; } = new();

    public SimulatedWrite? LastWrite { get; internal set; }

    internal OpticalBurnOperationGate Gate { get; } = new();
}

/// <summary>
/// A recorder backend without hardware (Task 13.2): the whole burn lifecycle — checks, prebuffer,
/// a bounded host buffer between the producer and the "recorder", progress with throughput,
/// underrun, write failure, cancellation with cleanup, a swapped disc and denied access — so the
/// Burn page and its rules can be exercised before a real Linux backend exists. Bytes are hashed,
/// never written anywhere. It is only offered when explicitly enabled.
/// </summary>
public sealed class SimulatedOpticalBackend : IOpticalDeviceService, IOpticalBurner
{
    /// <summary>Waiting this long for data while the producer is still running counts as an underrun.</summary>
    public static readonly TimeSpan UnderrunTimeout = TimeSpan.FromSeconds(2);

    private const int ChunkBytes = 256 * 1024;
    private static readonly TimeSpan ReportInterval = TimeSpan.FromMilliseconds(100);
    private readonly List<SimulatedRecorder> _recorders;
    private readonly OpticalWriteModes _validated;

    public SimulatedOpticalBackend(IEnumerable<SimulatedRecorder> recorders, OpticalWriteModes validated = OpticalWriteModes.All)
    {
        _recorders = [.. recorders ?? throw new ArgumentNullException(nameof(recorders))];
        _validated = validated;
    }

    public string BackendName => "simulated";

    public OpticalBackendAvailability Availability => OpticalBackendAvailability.Available;

    public IReadOnlyList<SimulatedRecorder> Recorders => _recorders;

    /// <summary>Three recorders: a BD writer with a blank BD-R, a DVD writer with a blank DVD-R and a CD writer without a disc.</summary>
    public static SimulatedOpticalBackend CreateDemo()
        => new(
        [
            new SimulatedRecorder("sim-bd", "Simulated BD writer", OpticalWriteModes.All) { Media = BlankMedia(OpticalMediaProfile.BdR) },
            new SimulatedRecorder("sim-dvd", "Simulated DVD writer", OpticalWriteModes.CdAudio | OpticalWriteModes.CdData | OpticalWriteModes.DvdData)
            {
                Media = BlankMedia(OpticalMediaProfile.DvdMinusR),
            },
            new SimulatedRecorder("sim-cd", "Simulated CD writer", OpticalWriteModes.CdAudio | OpticalWriteModes.CdData),
        ]);

    /// <summary>A blank disc of a profile with its nominal capacity and a fresh identity.</summary>
    public static OpticalMediaState BlankMedia(OpticalMediaProfile profile)
    {
        long sectors = profile switch
        {
            OpticalMediaProfile.CdR or OpticalMediaProfile.CdRw => 359_849,
            OpticalMediaProfile.DvdMinusR or OpticalMediaProfile.DvdMinusRw => 2_298_496,
            OpticalMediaProfile.DvdPlusR or OpticalMediaProfile.DvdPlusRw => 2_295_104,
            OpticalMediaProfile.DvdMinusRDualLayer or OpticalMediaProfile.DvdPlusRDualLayer => 4_171_712,
            OpticalMediaProfile.BdR or OpticalMediaProfile.BdRe => 12_219_392,
            _ => 0,
        };
        return new OpticalMediaState(profile, sectors > 0, sectors > 0, sectors, sectors, $"{profile}-{Guid.NewGuid():N}");
    }

    public SimulatedRecorder Recorder(string id)
        => _recorders.FirstOrDefault(recorder => recorder.Id == id)
           ?? throw new OpticalBurnException(OpticalBurnErrorCode.DeviceNotFound, "The simulated recorder does not exist.");

    // ---- IOpticalDeviceService ----

    public Task<IReadOnlyList<OpticalDeviceStatus>> GetDevicesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<OpticalDeviceStatus>>([.. _recorders.Select(Describe)]);
    }

    public Task<OpticalDeviceStatus> GetStatusAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Describe(Recorder(deviceId)));
    }

    private OpticalDeviceStatus Describe(SimulatedRecorder recorder)
    {
        OpticalMediaState? media = recorder.Access == OpticalDeviceAccess.Ready ? recorder.Media : null;
        return new OpticalDeviceStatus(
            new OpticalBurnDevice(recorder.Id, recorder.Name, []),
            recorder.Vendor,
            recorder.Model,
            recorder.Firmware,
            recorder.WriteModes,
            recorder.Features,
            recorder.Access,
            recorder.AccessDetail,
            media,
            OpticalDeviceStatus.Evaluate(recorder.WriteModes, recorder.Access, media, _validated));
    }

    // ---- IOpticalBurner ----

    public Task<IReadOnlyList<OpticalWriteSpeed>> GetSupportedWriteSpeedsAsync(
        string deviceId,
        OpticalBurnMediaKind mediaKind,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = Recorder(deviceId);
        int[] sectorsPerSecond = mediaKind switch
        {
            OpticalBurnMediaKind.CdAudio or OpticalBurnMediaKind.CdData => [16 * 75, 24 * 75, 48 * 75],
            OpticalBurnMediaKind.DvdData => [.. new[] { 4, 8, 16 }.Select(x => (int)(x * 1_385_000.0 / 2048))],
            _ => [.. new[] { 2, 4, 6 }.Select(x => (int)(x * 4_500_000.0 / 2048))],
        };
        return Task.FromResult<IReadOnlyList<OpticalWriteSpeed>>([.. sectorsPerSecond.Select(speed => new OpticalWriteSpeed(speed, false))]);
    }

    public Task<OpticalBurnResult> BurnAsync(
        OpticalBurnRequest request,
        IProgress<OpticalBurnProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        SimulatedRecorder recorder = Recorder(request.DeviceId);
        return recorder.Gate.RunAsync(() => Task.Run(() => Burn(recorder, request, progress, cancellationToken), CancellationToken.None), cancellationToken);
    }

    private OpticalBurnResult Burn(
        SimulatedRecorder recorder,
        OpticalBurnRequest request,
        IProgress<OpticalBurnProgress>? progress,
        CancellationToken cancellationToken)
    {
        Stopwatch clock = Stopwatch.StartNew();
        long total = request.ContentLength;
        progress?.Report(new OpticalBurnProgress(OpticalBurnStages.Prepare, "Checking the recorder", 0, total));
        Check(recorder, request);
        if (recorder.Faults.ChangeMediaBeforeWrite && recorder.Media is { } current)
        {
            recorder.Media = BlankMedia(current.Profile);
            recorder.Faults.ChangeMediaBeforeWrite = false;
        }

        // The disc is read again right before writing: a swap since the confirmation stops here.
        progress?.Report(new OpticalBurnProgress(OpticalBurnStages.Media, "Checking the disc again", 0, total));
        Check(recorder, request);
        cancellationToken.ThrowIfCancellationRequested();

        OpticalBurnBufferOptions buffer = request.EffectiveBufferOptions;
        using BlockingCollection<byte[]> queue = new(Math.Max(1, buffer.CapacityBytes / ChunkBytes));
        using CancellationTokenSource producerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Exception? producerFailure = null;
        Task producer = Task.Run(() =>
        {
            try
            {
                using ChunkStream stream = new(queue, producerCancellation.Token);
                request.ProduceContent(stream, producerCancellation.Token);
                stream.Flush();
            }
            catch (Exception exception)
            {
                producerFailure = exception;
            }
            finally
            {
                queue.CompleteAdding();
            }
        });

        long written = 0;
        bool startedWriting = false;
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        try
        {
            // Prebuffer: fill the host buffer before the laser starts.
            progress?.Report(new OpticalBurnProgress(OpticalBurnStages.Buffer, "Filling the buffer", 0, total));
            while (!queue.IsAddingCompleted && queue.Count * (long)ChunkBytes < buffer.PrebufferBytes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Thread.Sleep(5);
            }

            startedWriting = true;
            TimeSpan? lastReport = null;
            Stopwatch writing = Stopwatch.StartNew();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                double fraction = (double)written / total;
                if (recorder.Faults.UnderrunAtFraction is { } underrunAt && fraction >= underrunAt)
                {
                    throw new OpticalBurnException(OpticalBurnErrorCode.BufferUnderrun, "The recorder ran out of data (simulated).");
                }

                if (recorder.Faults.FailWriteAtFraction is { } failAt && fraction >= failAt)
                {
                    throw new OpticalBurnException(OpticalBurnErrorCode.WriteFailed, "The recorder reported a write error (simulated).");
                }

                if (!queue.TryTake(out byte[]? chunk, (int)UnderrunTimeout.TotalMilliseconds, cancellationToken) || chunk is null)
                {
                    if (queue.IsCompleted)
                    {
                        break;
                    }

                    throw new OpticalBurnException(OpticalBurnErrorCode.BufferUnderrun, "The source was too slow and the recorder ran out of data.");
                }

                hash.AppendData(chunk);
                written += chunk.Length;
                if (written > total)
                {
                    throw new OpticalBurnException(OpticalBurnErrorCode.SourceFailed, "The source produced more data than announced.");
                }

                // Keep to the simulated recording rate.
                TimeSpan due = TimeSpan.FromSeconds(written / recorder.BytesPerSecond);
                if (due > writing.Elapsed)
                {
                    Thread.Sleep(due - writing.Elapsed);
                }

                if (lastReport is not { } last || writing.Elapsed - last >= ReportInterval || written == total)
                {
                    lastReport = writing.Elapsed;
                    progress?.Report(new OpticalBurnProgress(OpticalBurnStages.Write, "Writing", written, total, written / Math.Max(writing.Elapsed.TotalSeconds, 1e-3)));
                }
            }

            producer.Wait(CancellationToken.None);
            if (producerFailure is not null)
            {
                throw producerFailure as OpticalBurnException
                      ?? new OpticalBurnException(OpticalBurnErrorCode.SourceFailed, producerFailure.Message, inner: producerFailure);
            }

            if (written != total)
            {
                throw new OpticalBurnException(OpticalBurnErrorCode.SourceFailed, $"The source produced {written} of {total} bytes.");
            }

            progress?.Report(new OpticalBurnProgress(OpticalBurnStages.Finalize, "Closing the disc", written, total));
            Thread.Sleep(50);
            recorder.Media = recorder.Media! with { IsBlank = false, IsAppendable = false, FreeSectors = 0 };
            recorder.LastWrite = new SimulatedWrite(request.MediaKind, written, Convert.ToHexStringLower(hash.GetHashAndReset()), true);
            OpticalWriteSpeed? actual = request.WriteSpeed;
            return new OpticalBurnResult(request.DeviceId, request.MediaKind, written, clock.Elapsed, actual);
        }
        catch (Exception exception) when (exception is OperationCanceledException or OpticalBurnException)
        {
            producerCancellation.Cancel();
            Drain(queue, producer);
            progress?.Report(new OpticalBurnProgress(OpticalBurnStages.Cleanup, "Closing the session", written, total));
            if (startedWriting && recorder.Media is { } media)
            {
                // A disc the laser touched is no longer blank.
                recorder.Media = media with { IsBlank = false, IsAppendable = false, FreeSectors = 0 };
            }

            recorder.LastWrite = new SimulatedWrite(request.MediaKind, written, Convert.ToHexStringLower(hash.GetHashAndReset()), false);
            if (recorder.Faults.FailCleanup)
            {
                if (exception is OperationCanceledException)
                {
                    throw new OpticalBurnCleanupException("The burn was canceled but the session could not be closed (simulated).", exception);
                }

                OpticalBurnErrorCode code = exception is OpticalBurnException burn ? burn.Code : OpticalBurnErrorCode.WriteFailed;
                throw new OpticalBurnException(code, exception.Message, cleanupFailed: true, exception);
            }

            throw;
        }
    }

    private void Check(SimulatedRecorder recorder, OpticalBurnRequest request)
    {
        OpticalBurnMediaKind mode = request.MediaKind;
        if (recorder.Access == OpticalDeviceAccess.PermissionDenied)
        {
            throw new OpticalBurnException(OpticalBurnErrorCode.PermissionDenied, recorder.AccessDetail ?? "The recorder cannot be opened by this user.");
        }

        if (recorder.Access is OpticalDeviceAccess.Busy or OpticalDeviceAccess.Missing)
        {
            throw new OpticalBurnException(OpticalBurnErrorCode.DeviceBusy, "The recorder is in use.");
        }

        if ((recorder.WriteModes & OpticalDeviceStatus.Flag(mode)) == 0 || (_validated & OpticalDeviceStatus.Flag(mode)) == 0)
        {
            throw new OpticalBurnException(OpticalBurnErrorCode.Unsupported, $"The recorder cannot write {mode}.");
        }

        if (recorder.Media is not { } media)
        {
            throw new OpticalBurnException(OpticalBurnErrorCode.NoMedia, "There is no disc in the recorder.");
        }

        if (request.ExpectedMediaIdentity is { } expected && expected != media.Identity)
        {
            throw new OpticalBurnException(OpticalBurnErrorCode.MediaChanged, "The disc is not the one that was confirmed.");
        }

        if (!media.Fits(mode) || !media.IsWritable)
        {
            throw new OpticalBurnException(OpticalBurnErrorCode.MediaIncompatible, $"A {media.Profile} disc cannot take {mode}.");
        }

        if (!media.IsBlank)
        {
            throw new OpticalBurnException(OpticalBurnErrorCode.MediaNotBlank, "The disc is not blank.");
        }

        if (media.FreeBytes(mode) < request.ContentLength)
        {
            throw new OpticalBurnException(OpticalBurnErrorCode.InsufficientCapacity, "The image is larger than the disc.");
        }
    }

    private static void Drain(BlockingCollection<byte[]> queue, Task producer)
    {
        // Unblock a producer waiting on a full buffer, then let it finish.
        while (!producer.IsCompleted)
        {
            while (queue.TryTake(out _))
            {
            }

            producer.Wait(TimeSpan.FromMilliseconds(20));
        }
    }

    /// <summary>A write-only stream that hands the producer's bytes over in fixed chunks.</summary>
    private sealed class ChunkStream(BlockingCollection<byte[]> queue, CancellationToken cancellationToken) : Stream
    {
        private byte[] _chunk = new byte[ChunkBytes];
        private int _filled;

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            while (!buffer.IsEmpty)
            {
                int take = Math.Min(buffer.Length, ChunkBytes - _filled);
                buffer[..take].CopyTo(_chunk.AsSpan(_filled));
                _filled += take;
                buffer = buffer[take..];
                if (_filled == ChunkBytes)
                {
                    queue.Add(_chunk, cancellationToken);
                    _chunk = new byte[ChunkBytes];
                    _filled = 0;
                }
            }
        }

        public override void Flush()
        {
            if (_filled > 0)
            {
                queue.Add(_chunk[.._filled], cancellationToken);
                _chunk = new byte[ChunkBytes];
                _filled = 0;
            }
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }
}

/// <summary>The backend used when no real one exists on this platform: lists nothing and says why.</summary>
public sealed class UnavailableOpticalBackend(string reason) : IOpticalDeviceService, IOpticalBurner
{
    public string BackendName => "unavailable";

    public OpticalBackendAvailability Availability { get; } = new(false, OpticalBurnUnavailableReason.BackendUnavailable, reason);

    public Task<IReadOnlyList<OpticalDeviceStatus>> GetDevicesAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<OpticalDeviceStatus>>([]);

    public Task<OpticalDeviceStatus> GetStatusAsync(string deviceId, CancellationToken cancellationToken = default)
        => throw new OpticalBurnException(OpticalBurnErrorCode.BackendUnavailable, reason);

    public Task<IReadOnlyList<OpticalWriteSpeed>> GetSupportedWriteSpeedsAsync(string deviceId, OpticalBurnMediaKind mediaKind, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<OpticalWriteSpeed>>([]);

    public Task<OpticalBurnResult> BurnAsync(OpticalBurnRequest request, IProgress<OpticalBurnProgress>? progress = null, CancellationToken cancellationToken = default)
        => throw new OpticalBurnException(OpticalBurnErrorCode.BackendUnavailable, reason);
}
