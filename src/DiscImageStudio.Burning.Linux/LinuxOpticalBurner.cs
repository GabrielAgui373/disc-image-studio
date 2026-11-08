using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace DiscImageStudio.Burning.Linux;

/// <summary>
/// The Linux writer of Task 13.5: DVD and BD-R data images over SG_IO, following the ADR of Task
/// 13.3. A burn opens the drive exclusively, reads the disc again and checks it is the confirmed
/// one, locks the tray, (DVD-R/-RW) selects disc-at-once and reserves the track, streams WRITE(10)
/// from the next writable address through a bounded host buffer, synchronizes the cache, closes
/// the track and the session and unlocks. Cancellation and failures stop writing, flush what was
/// sent and unlock; a cleanup that fails is reported as such. Every attempt is appended to the
/// local burn log. CD modes are not implemented here (the ADR compares SG_IO with cdrdao/cdrskin
/// on hardware first). Nothing here formats, erases or blanks a disc.
/// <para>Not validated on hardware: modes stay disabled by <see cref="LinuxBurnValidation"/> until
/// a physical test is recorded or the test campaign enables them explicitly.</para>
/// </summary>
public sealed class LinuxOpticalBurner : IOpticalBurner
{
    private const int SectorBytes = 2048;
    private const int BlocksPerWrite = 32;
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FlushTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan BusyRetryLimit = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan SourceStallLimit = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ReportInterval = TimeSpan.FromMilliseconds(200);

    private readonly LinuxDeviceTree _tree;
    private readonly IScsiTransportFactory _transports;
    private readonly LinuxBurnValidation _validation;
    private readonly BurnTestLog _log;
    private readonly ConcurrentDictionary<string, OpticalBurnOperationGate> _gates = new(StringComparer.Ordinal);

    public LinuxOpticalBurner(
        LinuxDeviceTree? tree = null,
        IScsiTransportFactory? transports = null,
        LinuxBurnValidation? validation = null,
        BurnTestLog? log = null)
    {
        _tree = tree ?? new LinuxDeviceTree();
        _transports = transports ?? new SgIoTransportFactory();
        _validation = validation ?? new LinuxBurnValidation();
        _log = log ?? new BurnTestLog(null);
    }

    public Task<IReadOnlyList<OpticalWriteSpeed>> GetSupportedWriteSpeedsAsync(
        string deviceId,
        OpticalBurnMediaKind mediaKind,
        CancellationToken cancellationToken = default)
        => Task.Run<IReadOnlyList<OpticalWriteSpeed>>(
            () =>
            {
                if (mediaKind is OpticalBurnMediaKind.CdAudio or OpticalBurnMediaKind.CdData)
                {
                    return [];
                }

                LinuxOpticalNode node = Node(deviceId);
                try
                {
                    using IScsiTransport transport = _transports.OpenForReading(node.DevicePath);
                    ScsiResult reply = transport.Read(GetPerformanceWriteSpeeds(32), 8 + (32 * 16), CommandTimeout);
                    return reply.Succeeded ? ParseWriteSpeeds(reply.Data) : [];
                }
                catch (Exception exception) when (exception is IOException)
                {
                    return [];
                }
            },
            cancellationToken);

    public Task<OpticalBurnResult> BurnAsync(
        OpticalBurnRequest request,
        IProgress<OpticalBurnProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        OpticalBurnOperationGate gate = _gates.GetOrAdd(request.DeviceId, _ => new OpticalBurnOperationGate());
        // The burn runs on its own thread; the gate keeps a second burn of the same drive waiting
        // until the first has returned from the drive, even if the first was canceled.
        return gate.RunAsync(
            () => Task.Factory.StartNew(() => Burn(request, progress, cancellationToken), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default),
            cancellationToken);
    }

    private OpticalBurnResult Burn(OpticalBurnRequest request, IProgress<OpticalBurnProgress>? progress, CancellationToken cancellationToken)
    {
        Stopwatch clock = Stopwatch.StartNew();
        long total = request.ContentLength;
        long written = 0;
        LinuxOpticalNode? node = null;
        OpticalMediaState? media = null;
        string result = "error";
        string? sense = null;
        bool testOnly = false;
        try
        {
            progress?.Report(new OpticalBurnProgress(OpticalBurnStages.Prepare, "Opening the drive", 0, total));
            node = Node(request.DeviceId);
            if (request.MediaKind is OpticalBurnMediaKind.CdAudio or OpticalBurnMediaKind.CdData)
            {
                throw new OpticalBurnException(OpticalBurnErrorCode.Unsupported, "CD modes are not implemented on Linux yet.");
            }

            using IScsiWriteTransport transport = Open(node.DevicePath);
            progress?.Report(new OpticalBurnProgress(OpticalBurnStages.Media, "Checking the disc again", 0, total));
            (OpticalWriteModes writeModes, media) = MmcState.Read(transport);
            Check(node, request, writeModes, media);
            testOnly = _validation.IsTestOnly(node, media, request.MediaKind);
            written = Write(transport, request, media!, progress, cancellationToken);
            result = "pass";
            return new OpticalBurnResult(request.DeviceId, request.MediaKind, written, clock.Elapsed, request.WriteSpeed);
        }
        catch (OpticalBurnCleanupException)
        {
            result = "canceled-cleanup-failed";
            throw;
        }
        catch (OperationCanceledException)
        {
            result = "canceled";
            throw;
        }
        catch (OpticalBurnException exception)
        {
            result = exception.Code + (exception.CleanupFailed ? "+cleanup-failed" : string.Empty);
            sense = exception.InnerException?.Message;
            throw;
        }
        finally
        {
            _log.Append(new BurnTestLogEntry(
                DateTimeOffset.UtcNow,
                request.DeviceId,
                node?.Vendor,
                node?.Model,
                node?.Revision,
                media?.Profile,
                request.MediaKind,
                request.WriteSpeed?.SectorsPerSecond,
                written,
                total,
                clock.Elapsed.TotalSeconds,
                result,
                sense,
                testOnly));
        }
    }

    private void Check(LinuxOpticalNode node, OpticalBurnRequest request, OpticalWriteModes writeModes, OpticalMediaState? media)
    {
        OpticalBurnMediaKind mode = request.MediaKind;
        if ((_validation.Validated(node, media) & OpticalDeviceStatus.Flag(mode)) == 0)
        {
            throw new OpticalBurnException(OpticalBurnErrorCode.Unsupported, "This mode is not validated on this recorder and disc.");
        }

        if ((writeModes & OpticalDeviceStatus.Flag(mode)) == 0)
        {
            throw new OpticalBurnException(OpticalBurnErrorCode.Unsupported, $"The recorder does not report writing {mode}.");
        }

        if (media is null)
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

    private long Write(
        IScsiWriteTransport transport,
        OpticalBurnRequest request,
        OpticalMediaState media,
        IProgress<OpticalBurnProgress>? progress,
        CancellationToken cancellationToken)
    {
        long total = request.ContentLength;
        uint sectors = checked((uint)(total / SectorBytes));
        MmcDiscInformation disc = MmcState.ParseDiscInformation(Require(transport.Read(MmcState.ReadDiscInformation(34), 34, CommandTimeout), "READ DISC INFORMATION").Data);
        int track = Math.Max(1, disc.LastTrackInLastSession);
        MmcTrackInformation info = MmcState.ParseTrackInformation(Require(transport.Read(MmcState.ReadTrackInformation(track, 48), 48, CommandTimeout), "READ TRACK INFORMATION").Data);
        uint address = info.NextWritableValid ? info.NextWritable : info.TrackStart;

        Run(transport, PreventRemoval(true), "PREVENT MEDIUM REMOVAL");
        bool started = false;
        Exception? failure = null;
        using CancellationTokenSource producerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        OpticalBurnBufferOptions buffer = request.EffectiveBufferOptions;
        int chunkBytes = BlocksPerWrite * SectorBytes;
        using BlockingCollection<byte[]> queue = new(Math.Max(2, buffer.CapacityBytes / chunkBytes));
        Exception? producerFailure = null;
        Task producer = Task.CompletedTask;
        long written = 0;
        try
        {
            if (media.Profile is OpticalMediaProfile.DvdMinusR or OpticalMediaProfile.DvdMinusRw or OpticalMediaProfile.DvdMinusRDualLayer)
            {
                // DVD-R: disc-at-once with a reserved track, as growisofs does; buffer-underrun protection on.
                SelectDiscAtOnce(transport);
                Run(transport, ReserveTrack(sectors), "RESERVE TRACK");
            }

            if (request.WriteSpeed is { } speed)
            {
                // A speed the drive refuses is not an error; it keeps its own choice.
                _ = transport.Read(SetSpeed((int)Math.Min(ushort.MaxValue, speed.SectorsPerSecond * (long)SectorBytes / 1000)), 0, CommandTimeout);
            }

            producer = Task.Run(() =>
            {
                try
                {
                    using ChunkStream stream = new(queue, chunkBytes, producerCancellation.Token);
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

            progress?.Report(new OpticalBurnProgress(OpticalBurnStages.Buffer, "Filling the buffer", 0, total));
            while (!queue.IsAddingCompleted && queue.Count * (long)chunkBytes < buffer.PrebufferBytes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Thread.Sleep(5);
            }

            Stopwatch writing = Stopwatch.StartNew();
            TimeSpan? lastReport = null;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!queue.TryTake(out byte[]? chunk, (int)SourceStallLimit.TotalMilliseconds, cancellationToken) || chunk is null)
                {
                    if (queue.IsCompleted)
                    {
                        break;
                    }

                    throw new OpticalBurnException(OpticalBurnErrorCode.SourceFailed, "The image stopped arriving.");
                }

                if (chunk.Length % SectorBytes != 0 || written + chunk.Length > total)
                {
                    throw new OpticalBurnException(OpticalBurnErrorCode.SourceFailed, "The image does not match its announced size.");
                }

                int blocks = chunk.Length / SectorBytes;
                started = true;
                RunBusyRetry(transport, Write10(address, blocks), chunk, chunk.Length, "WRITE(10)");
                address += (uint)blocks;
                written += chunk.Length;
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
                throw new OpticalBurnException(OpticalBurnErrorCode.SourceFailed, $"The image produced {written} of {total} bytes.");
            }

            progress?.Report(new OpticalBurnProgress(OpticalBurnStages.Finalize, "Closing the disc", written, total));
            Flush(transport);
            Close(transport, track, media);
            return written;
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            producerCancellation.Cancel();
            while (!producer.IsCompleted)
            {
                while (queue.TryTake(out _))
                {
                }

                producer.Wait(TimeSpan.FromMilliseconds(20));
            }

            Cleanup(transport, started && failure is not null, failure, progress, written, total);
        }
    }

    /// <summary>After a failure or cancellation: flush what was sent and unlock. Always unlock.</summary>
    private static void Cleanup(IScsiWriteTransport transport, bool flush, Exception? failure, IProgress<OpticalBurnProgress>? progress, long written, long total)
    {
        Exception? cleanupFailure = null;
        if (failure is not null)
        {
            progress?.Report(new OpticalBurnProgress(OpticalBurnStages.Cleanup, "Closing the session", written, total));
        }

        if (flush)
        {
            try
            {
                Flush(transport);
            }
            catch (Exception exception) when (exception is IOException)
            {
                cleanupFailure = exception;
            }
        }

        try
        {
            Run(transport, PreventRemoval(false), "ALLOW MEDIUM REMOVAL");
        }
        catch (Exception exception) when (exception is IOException)
        {
            cleanupFailure ??= exception;
        }

        if (cleanupFailure is null)
        {
            return;
        }

        if (failure is null)
        {
            // The burn itself succeeded; the tray stays locked until the drive is reset.
            throw new OpticalBurnException(OpticalBurnErrorCode.WriteFailed, "The disc was written but the drive could not be released.", cleanupFailed: true, cleanupFailure);
        }

        if (failure is OperationCanceledException canceled)
        {
            throw new OpticalBurnCleanupException("The burn was canceled but the session could not be closed.", canceled);
        }

        OpticalBurnErrorCode code = failure is OpticalBurnException burn ? burn.Code : OpticalBurnErrorCode.WriteFailed;
        throw new OpticalBurnException(code, failure.Message, cleanupFailed: true, failure);
    }

    private static void Flush(IScsiWriteTransport transport)
    {
        RunBusyRetry(transport, SynchronizeCache, [], 0, "SYNCHRONIZE CACHE", FlushTimeout);
        WaitUntilReady(transport);
    }

    private static void Close(IScsiWriteTransport transport, int track, OpticalMediaState media)
    {
        // A reserved DVD-R track and a full BD-R can close by themselves; close only what is still open.
        if (DiscStatus(transport) == 2)
        {
            return;
        }

        RunBusyRetry(transport, CloseTrackSession(1, track), [], 0, "CLOSE TRACK", FlushTimeout);
        WaitUntilReady(transport);
        if (DiscStatus(transport) == 2)
        {
            return;
        }

        RunBusyRetry(transport, CloseTrackSession(2, 0), [], 0, "CLOSE SESSION", FlushTimeout);
        WaitUntilReady(transport);
        if (media.Profile == OpticalMediaProfile.DvdPlusR && DiscStatus(transport) != 2)
        {
            // DVD+R: finalize so the disc reads like a pressed one.
            RunBusyRetry(transport, CloseTrackSession(5, 0), [], 0, "FINALIZE", FlushTimeout);
            WaitUntilReady(transport);
        }
    }

    private static int DiscStatus(IScsiWriteTransport transport)
        => MmcState.ParseDiscInformation(Require(transport.Read(MmcState.ReadDiscInformation(34), 34, CommandTimeout), "READ DISC INFORMATION").Data).DiscStatus;

    private static void SelectDiscAtOnce(IScsiWriteTransport transport)
    {
        ScsiResult sense = Require(transport.Read(ModeSenseWriteParameters(64), 64, CommandTimeout), "MODE SENSE (write parameters)");
        byte[] reply = sense.Data;
        if (reply.Length < 8)
        {
            throw new OpticalBurnException(OpticalBurnErrorCode.WriteFailed, "The drive returned no write parameters page.");
        }

        int descriptors = BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(6));
        int page = 8 + descriptors;
        if (reply.Length < page + 4 || (reply[page] & 0x3f) != 0x05)
        {
            throw new OpticalBurnException(OpticalBurnErrorCode.WriteFailed, "The drive returned no write parameters page.");
        }

        int pageLength = Math.Min(reply[page + 1] + 2, reply.Length - page);
        byte[] parameters = new byte[8 + pageLength];
        reply.AsSpan(page, pageLength).CopyTo(parameters.AsSpan(8));
        parameters[8] &= 0x3f;                                   // PS bit must be zero in MODE SELECT.
        parameters[8 + 2] = (byte)((parameters[8 + 2] & 0xb0) | 0x40 | 0x02); // BUFE on, write type 2 (disc-at-once).
        parameters[8 + 3] = (byte)((parameters[8 + 3] & 0xf0) | 0x05);       // Track mode 5 (data).
        parameters[8 + 4] = 0x08;                                            // Data block type: Mode 1, 2048 bytes.
        RunBusyRetry(transport, ModeSelect(parameters.Length), parameters, parameters.Length, "MODE SELECT (write parameters)");
    }

    private static void WaitUntilReady(IScsiWriteTransport transport)
    {
        Stopwatch waited = Stopwatch.StartNew();
        while (true)
        {
            ScsiResult ready = transport.Read(MmcState.TestUnitReady, 0, CommandTimeout);
            if (ready.Succeeded)
            {
                return;
            }

            if (!ready.IsBecomingReady || waited.Elapsed > FlushTimeout)
            {
                throw Failure("TEST UNIT READY", ready);
            }

            Thread.Sleep(200);
        }
    }

    private static void Run(IScsiWriteTransport transport, byte[] cdb, string command)
        => RunBusyRetry(transport, cdb, [], 0, command);

    /// <summary>Sends a command, waiting while the drive reports a long write in progress (02/04/07–08).</summary>
    private static void RunBusyRetry(IScsiWriteTransport transport, byte[] cdb, byte[] data, int length, string command, TimeSpan? timeout = null)
    {
        Stopwatch waited = Stopwatch.StartNew();
        while (true)
        {
            ScsiResult result = length > 0
                ? transport.Send(cdb, data, length, timeout ?? CommandTimeout)
                : transport.Read(cdb, 0, timeout ?? CommandTimeout);
            if (result.Succeeded)
            {
                return;
            }

            if (result.SenseCode is (2, 0x04, 0x07 or 0x08) && waited.Elapsed < BusyRetryLimit)
            {
                Thread.Sleep(5);
                continue;
            }

            throw Failure(command, result);
        }
    }

    private static ScsiResult Require(ScsiResult result, string command) => result.Succeeded ? result : throw Failure(command, result);

    private static OpticalBurnException Failure(string command, ScsiResult result)
    {
        (int key, int asc, int ascq) = result.SenseCode;
        string sense = $"{command}: status {result.Status:X2}, sense {key:X}/{asc:X2}/{ascq:X2}";
        OpticalBurnErrorCode code = result.IsNoMedium ? OpticalBurnErrorCode.NoMedia
            : (key, asc) is (3, 0x0c) or (3, 0x11) ? OpticalBurnErrorCode.WriteFailed
            : asc == 0x28 || asc == 0x3a ? OpticalBurnErrorCode.MediaChanged
            : OpticalBurnErrorCode.WriteFailed;
        return new OpticalBurnException(code, $"The drive refused {command}.", inner: new IOException(sense));
    }

    private IScsiWriteTransport Open(string path)
    {
        try
        {
            return _transports.OpenForWriting(path);
        }
        catch (LinuxDeviceOpenException exception)
        {
            throw exception.Errno switch
            {
                LinuxNative.ErrorAccess or LinuxNative.ErrorPermission => new OpticalBurnException(OpticalBurnErrorCode.PermissionDenied, "This user cannot open the drive for writing.", inner: exception),
                LinuxNative.ErrorBusy => new OpticalBurnException(OpticalBurnErrorCode.DeviceBusy, "Another program holds the drive.", inner: exception),
                LinuxNative.ErrorNoEntry => new OpticalBurnException(OpticalBurnErrorCode.DeviceNotFound, "The drive is gone.", inner: exception),
                LinuxNative.ErrorNoMedium => new OpticalBurnException(OpticalBurnErrorCode.NoMedia, "There is no disc in the drive.", inner: exception),
                _ => new OpticalBurnException(OpticalBurnErrorCode.WriteFailed, exception.Message, inner: exception),
            };
        }
    }

    private LinuxOpticalNode Node(string deviceId)
        => _tree.Enumerate().FirstOrDefault(node => LinuxOpticalDeviceService.Id(node) == deviceId)
           ?? throw new OpticalBurnException(OpticalBurnErrorCode.DeviceNotFound, "The drive is no longer listed.");

    // ---- MMC command blocks (MMC-6) ----

    public static byte[] PreventRemoval(bool prevent) => [0x1e, 0, 0, 0, prevent ? (byte)1 : (byte)0, 0];

    public static byte[] Write10(uint lba, int blocks)
        => [0x2a, 0, (byte)(lba >> 24), (byte)(lba >> 16), (byte)(lba >> 8), (byte)lba, 0, (byte)(blocks >> 8), (byte)blocks, 0];

    public static readonly byte[] SynchronizeCache = [0x35, 0, 0, 0, 0, 0, 0, 0, 0, 0];

    /// <summary>CLOSE TRACK/SESSION: function 1 closes a track, 2 the session, 5 finalizes a DVD+R.</summary>
    public static byte[] CloseTrackSession(int function, int track)
        => [0x5b, 0, (byte)function, 0, (byte)(track >> 8), (byte)track, 0, 0, 0, 0];

    public static byte[] ReserveTrack(uint blocks)
        => [0x53, 0, 0, 0, 0, (byte)(blocks >> 24), (byte)(blocks >> 16), (byte)(blocks >> 8), (byte)blocks, 0];

    public static byte[] ModeSenseWriteParameters(int allocation) => [0x5a, 0x08, 0x05, 0, 0, 0, 0, (byte)(allocation >> 8), (byte)allocation, 0];

    public static byte[] ModeSelect(int length) => [0x55, 0x10, 0, 0, 0, 0, 0, (byte)(length >> 8), (byte)length, 0];

    /// <summary>SET CD SPEED, honoured by DVD and BD drives too; read speed left at the maximum.</summary>
    public static byte[] SetSpeed(int writeKilobytesPerSecond)
        => [0xbb, 0, 0xff, 0xff, (byte)(writeKilobytesPerSecond >> 8), (byte)writeKilobytesPerSecond, 0, 0, 0, 0, 0, 0];

    /// <summary>GET PERFORMANCE, type 03h: write speed descriptors.</summary>
    public static byte[] GetPerformanceWriteSpeeds(int descriptors)
        => [0xac, 0, 0, 0, 0, 0, 0, 0, (byte)(descriptors >> 8), (byte)descriptors, 0x03, 0];

    /// <summary>Write speeds (kB/s, 1000 bytes) of a GET PERFORMANCE type 03h reply, as sectors per second.</summary>
    public static IReadOnlyList<OpticalWriteSpeed> ParseWriteSpeeds(ReadOnlySpan<byte> reply)
    {
        if (reply.Length < 8)
        {
            return [];
        }

        int length = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(reply) + 4, (uint)reply.Length);
        SortedSet<int> speeds = [];
        for (int offset = 8; offset + 16 <= length; offset += 16)
        {
            uint kilobytes = BinaryPrimitives.ReadUInt32BigEndian(reply[(offset + 12)..]);
            int sectors = (int)Math.Min(int.MaxValue, kilobytes * 1000L / SectorBytes);
            if (sectors > 0)
            {
                speeds.Add(sectors);
            }
        }

        return [.. speeds.Select(speed => new OpticalWriteSpeed(speed, false))];
    }

    /// <summary>Hands the producer's bytes over in whole sector chunks.</summary>
    private sealed class ChunkStream(BlockingCollection<byte[]> queue, int chunkBytes, CancellationToken cancellationToken) : Stream
    {
        private byte[] _chunk = new byte[chunkBytes];
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
                int take = Math.Min(buffer.Length, chunkBytes - _filled);
                buffer[..take].CopyTo(_chunk.AsSpan(_filled));
                _filled += take;
                buffer = buffer[take..];
                if (_filled == chunkBytes)
                {
                    queue.Add(_chunk, cancellationToken);
                    _chunk = new byte[chunkBytes];
                    _filled = 0;
                }
            }
        }

        public override void Flush()
        {
            if (_filled > 0)
            {
                queue.Add(_chunk[.._filled], cancellationToken);
                _chunk = new byte[chunkBytes];
                _filled = 0;
            }
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
