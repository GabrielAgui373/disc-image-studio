using System.Security.Cryptography;
using DiscImageStudio.Burning;
using DiscImageStudio.Burning.Simulated;
using Xunit;

namespace DiscImageStudio.PortableArchitectureTests;

/// <summary>Portable burning contracts (Task 13.1) and the simulated backend (Task 13.2).</summary>
public sealed class BurningTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("dis-burn-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void Iso_source_passes_the_file_through()
    {
        byte[] data = Bytes(2048 * 40, 1);
        string path = Write("disc.iso", data);
        Assert.Equal(data, Produce(OpticalBurnSources.FromArtifact(path, OpticalBurnMediaKind.DvdData)));
    }

    [Fact]
    public void Wave_source_drops_the_header_and_raw_source_swaps_samples()
    {
        byte[] pcm = Bytes(2352 * 10, 2);
        string wave = Path.Combine(_directory, "track.wav");
        using (BinaryWriter writer = new(File.Create(wave)))
        {
            writer.Write("RIFF"u8);
            writer.Write((uint)(pcm.Length + 36));
            writer.Write("WAVEfmt "u8);
            writer.Write(16u);
            writer.Write((ushort)1);
            writer.Write((ushort)2);
            writer.Write(44100u);
            writer.Write(176400u);
            writer.Write((ushort)4);
            writer.Write((ushort)16);
            writer.Write("data"u8);
            writer.Write((uint)pcm.Length);
            writer.Write(pcm);
        }

        byte[] bigEndian = (byte[])pcm.Clone();
        for (int i = 0; i < bigEndian.Length; i += 2)
        {
            (bigEndian[i], bigEndian[i + 1]) = (bigEndian[i + 1], bigEndian[i]);
        }

        Assert.Equal(pcm, Produce(OpticalBurnSources.FromArtifact(wave, OpticalBurnMediaKind.CdAudio)));
        Assert.Equal(pcm, Produce(OpticalBurnSources.FromArtifact(Write("track.raw", bigEndian), OpticalBurnMediaKind.CdAudio)));
    }

    [Fact]
    public void Partial_sectors_and_files_changed_after_the_check_are_refused()
    {
        OpticalBurnException partial = Assert.Throws<OpticalBurnException>(
            () => OpticalBurnSources.FromArtifact(Write("odd.iso", new byte[1000]), OpticalBurnMediaKind.DvdData));
        Assert.Equal(OpticalBurnErrorCode.SourceFailed, partial.Code);

        string path = Write("changing.iso", new byte[2048 * 4]);
        OpticalBurnSource source = OpticalBurnSources.FromArtifact(path, OpticalBurnMediaKind.DvdData);
        File.AppendAllText(path, "x");
        Assert.Equal(OpticalBurnErrorCode.SourceFailed, Assert.Throws<OpticalBurnException>(() => source.Produce(Stream.Null, default)).Code);
    }

    [Theory]
    [InlineData("cd", "a.wav", OpticalBurnMediaKind.CdAudio)]
    [InlineData("cd", "a.raw", OpticalBurnMediaKind.CdAudio)]
    [InlineData("cd", "a.iso", OpticalBurnMediaKind.CdData)]
    [InlineData("dvd", "a.iso", OpticalBurnMediaKind.DvdData)]
    [InlineData("bd", "a.iso", OpticalBurnMediaKind.BluRayData)]
    public void Modes_follow_the_family_and_the_extension(string family, string path, OpticalBurnMediaKind expected)
        => Assert.Equal(expected, OpticalBurnSources.ModeFor(family, path));

    [Fact]
    public void Capabilities_explain_every_mode()
    {
        OpticalMediaState blankDvd = SimulatedOpticalBackend.BlankMedia(OpticalMediaProfile.DvdMinusR);
        IReadOnlyList<OpticalModeCapability> modes = OpticalDeviceStatus.Evaluate(
            OpticalWriteModes.CdAudio | OpticalWriteModes.DvdData, OpticalDeviceAccess.Ready, blankDvd, OpticalWriteModes.All);
        Assert.True(Mode(modes, OpticalBurnMediaKind.DvdData).IsAvailable);
        Assert.Equal(OpticalBurnUnavailableReason.MediaIncompatible, Mode(modes, OpticalBurnMediaKind.CdAudio).Reason);
        Assert.Equal(OpticalBurnUnavailableReason.DeviceCannotWrite, Mode(modes, OpticalBurnMediaKind.BluRayData).Reason);

        Assert.Equal(
            OpticalBurnUnavailableReason.NotValidated,
            Mode(OpticalDeviceStatus.Evaluate(OpticalWriteModes.All, OpticalDeviceAccess.Ready, blankDvd, OpticalWriteModes.None), OpticalBurnMediaKind.DvdData).Reason);
        Assert.Equal(
            OpticalBurnUnavailableReason.PermissionDenied,
            Mode(OpticalDeviceStatus.Evaluate(OpticalWriteModes.All, OpticalDeviceAccess.PermissionDenied, blankDvd, OpticalWriteModes.All), OpticalBurnMediaKind.DvdData).Reason);
        Assert.Equal(
            OpticalBurnUnavailableReason.MediaNotBlank,
            Mode(OpticalDeviceStatus.Evaluate(OpticalWriteModes.All, OpticalDeviceAccess.Ready, blankDvd with { IsBlank = false }, OpticalWriteModes.All), OpticalBurnMediaKind.DvdData).Reason);
        Assert.Equal(
            OpticalBurnUnavailableReason.NoMedia,
            Mode(OpticalDeviceStatus.Evaluate(OpticalWriteModes.All, OpticalDeviceAccess.Ready, null, OpticalWriteModes.All), OpticalBurnMediaKind.DvdData).Reason);
    }

    [Fact]
    public async Task Simulated_burn_writes_the_exact_source_and_reports_every_stage()
    {
        (SimulatedOpticalBackend backend, SimulatedRecorder dvd) = Backend();
        byte[] data = Bytes(2048 * 2000, 3);
        List<string> stages = [];
        OpticalBurnResult result = await backend.BurnAsync(Request(data, dvd.Media!.Identity), new Inline(progress => stages.Add(progress.Stage)));

        Assert.Equal(data.Length, result.BytesWritten);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(data)), dvd.LastWrite!.Sha256);
        Assert.Equal(["prepare", "media", "buffer", "write", "finalize"], stages.Distinct());
        Assert.False(dvd.Media!.IsBlank);
        Assert.Equal(OpticalBurnErrorCode.MediaNotBlank, (await Assert.ThrowsAsync<OpticalBurnException>(() => backend.BurnAsync(Request(data)))).Code);
    }

    [Fact]
    public async Task Simulated_faults_end_with_their_codes()
    {
        (SimulatedOpticalBackend backend, SimulatedRecorder dvd) = Backend();
        byte[] data = Bytes(2048 * 1000, 4);

        dvd.Faults.UnderrunAtFraction = 0.3;
        Assert.Equal(OpticalBurnErrorCode.BufferUnderrun, (await Assert.ThrowsAsync<OpticalBurnException>(() => backend.BurnAsync(Request(data)))).Code);
        dvd.Faults.UnderrunAtFraction = null;

        Fresh(dvd);
        dvd.Faults.FailWriteAtFraction = 0.5;
        dvd.Faults.FailCleanup = true;
        OpticalBurnException failed = await Assert.ThrowsAsync<OpticalBurnException>(() => backend.BurnAsync(Request(data)));
        Assert.Equal(OpticalBurnErrorCode.WriteFailed, failed.Code);
        Assert.True(failed.CleanupFailed);
        dvd.Faults.FailWriteAtFraction = null;
        dvd.Faults.FailCleanup = false;

        Fresh(dvd);
        string confirmed = dvd.Media!.Identity;
        Fresh(dvd);
        Assert.Equal(OpticalBurnErrorCode.MediaChanged, (await Assert.ThrowsAsync<OpticalBurnException>(() => backend.BurnAsync(Request(data, confirmed)))).Code);
        Assert.True(dvd.Media!.IsBlank);

        dvd.Access = OpticalDeviceAccess.PermissionDenied;
        Assert.Equal(OpticalBurnErrorCode.PermissionDenied, (await Assert.ThrowsAsync<OpticalBurnException>(() => backend.BurnAsync(Request(data)))).Code);
        dvd.Access = OpticalDeviceAccess.Ready;

        dvd.Media = SimulatedOpticalBackend.BlankMedia(OpticalMediaProfile.DvdMinusR) with { FreeSectors = 10 };
        Assert.Equal(OpticalBurnErrorCode.InsufficientCapacity, (await Assert.ThrowsAsync<OpticalBurnException>(() => backend.BurnAsync(Request(data)))).Code);
    }

    [Fact]
    public async Task A_slow_source_runs_the_simulated_recorder_dry()
    {
        (SimulatedOpticalBackend backend, _) = Backend();
        OpticalBurnRequest slow = new(
            "sim-dvd",
            OpticalBurnMediaKind.DvdData,
            2048L * 1024,
            (stream, _) =>
            {
                stream.Write(new byte[2048 * 512]);
                Thread.Sleep(SimulatedOpticalBackend.UnderrunTimeout + TimeSpan.FromSeconds(1));
                stream.Write(new byte[2048 * 512]);
            },
            BufferOptions: new OpticalBurnBufferOptions(256 * 1024 * 4, 256 * 1024));
        Assert.Equal(OpticalBurnErrorCode.BufferUnderrun, (await Assert.ThrowsAsync<OpticalBurnException>(() => backend.BurnAsync(slow))).Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_cleans_up_or_says_it_could_not(bool failCleanup)
    {
        (SimulatedOpticalBackend backend, SimulatedRecorder dvd) = Backend();
        dvd.BytesPerSecond = 4e6;
        dvd.Faults.FailCleanup = failCleanup;
        using CancellationTokenSource cancellation = new();
        Task burn = backend.BurnAsync(
            Request(Bytes(2048 * 4000, 5)),
            new Inline(progress =>
            {
                if (progress.Stage == "write" && progress.Fraction > 0.2)
                {
                    cancellation.Cancel();
                }
            }),
            cancellation.Token);
        Exception exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => burn);
        Assert.Equal(failCleanup, exception is OpticalBurnCleanupException);
    }

    [Fact]
    public async Task Recording_a_passing_test_ends_the_test_only_state()
    {
        (SimulatedOpticalBackend backend, _) = Backend();
        backend.TestOnlyModes = OpticalWriteModes.DvdData;
        OpticalDeviceStatus status = await backend.GetStatusAsync("sim-dvd");
        Assert.True(backend.IsTestOnly(status, OpticalBurnMediaKind.DvdData));
        Assert.Contains("hardware test", status.Mode(OpticalBurnMediaKind.DvdData).Detail);

        backend.Record(new OpticalValidationResult(status, OpticalBurnMediaKind.DvdData, OpticalMediaProfile.DvdMinusR, "brand", "8x", Passed: true, null));
        Assert.False(backend.IsTestOnly(await backend.GetStatusAsync("sim-dvd"), OpticalBurnMediaKind.DvdData));
    }

    private static (SimulatedOpticalBackend Backend, SimulatedRecorder Dvd) Backend()
    {
        SimulatedOpticalBackend backend = SimulatedOpticalBackend.CreateDemo();
        SimulatedRecorder dvd = backend.Recorder("sim-dvd");
        dvd.BytesPerSecond = 512e6;
        return (backend, dvd);
    }

    private static void Fresh(SimulatedRecorder recorder) => recorder.Media = SimulatedOpticalBackend.BlankMedia(OpticalMediaProfile.DvdMinusR);

    private static OpticalBurnRequest Request(byte[] data, string? identity = null)
        => new("sim-dvd", OpticalBurnMediaKind.DvdData, data.Length, (stream, _) => stream.Write(data),
            BufferOptions: new OpticalBurnBufferOptions(256 * 1024 * 8, 256 * 1024), ExpectedMediaIdentity: identity);

    private static OpticalModeCapability Mode(IReadOnlyList<OpticalModeCapability> modes, OpticalBurnMediaKind mode)
        => modes.Single(capability => capability.Mode == mode);

    private string Write(string name, byte[] data)
    {
        string path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, data);
        return path;
    }

    private static byte[] Produce(OpticalBurnSource source)
    {
        using MemoryStream output = new();
        source.Produce(output, default);
        return output.ToArray();
    }

    private static byte[] Bytes(int length, int seed)
    {
        byte[] data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    private sealed class Inline(Action<OpticalBurnProgress> report) : IProgress<OpticalBurnProgress>
    {
        public void Report(OpticalBurnProgress value) => report(value);
    }
}
