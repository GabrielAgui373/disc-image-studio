using System.Buffers.Binary;
using System.Text.Json;
using DiscImageStudio.Burning;
using DiscImageStudio.Burning.Linux;
using Xunit;

namespace DiscImageStudio.PortableArchitectureTests;

/// <summary>The Linux backend (Tasks 13.4, 13.5 and 05/06): enumeration, MMC parsing and the SG_IO writer against a fake drive.</summary>
public sealed class LinuxBurningTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dis-linux-").FullName;
    private readonly LinuxDeviceTree _tree;

    public LinuxBurningTests()
    {
        Drive("sr0", 0, 5, "VENDOR", "WRITER", "1.00", "E:ID_CDROM_CD_R=1\nE:ID_CDROM_DVD_R=1\nE:ID_CDROM_BD_R=1\n");
        Drive("sr10", 10, 5, "OTHER", "DVD+-RW", "B2", "E:ID_CDROM_CD_R=1\n");
        Drive("sr1", 1, 5, "ASUS", "DRW", "1.00", "E:ID_CDROM_DVD_R=1\nE:ID_CDROM_MEDIA=1\nE:ID_CDROM_MEDIA_DVD_R=1\nE:ID_CDROM_MEDIA_STATE=blank\n");
        Drive("sr3", 3, 0, "X", "Not optical", "0", string.Empty);
        _tree = new LinuxDeviceTree(_root);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Drives_are_listed_in_kernel_order_without_non_optical_devices()
    {
        IReadOnlyList<LinuxOpticalNode> nodes = _tree.Enumerate();
        Assert.Equal(["sr0", "sr1", "sr10"], nodes.Select(node => node.Name));
        Assert.Equal("VENDOR", nodes[0].Vendor);
        Assert.Equal("1.00", nodes[0].Revision);
        Assert.True(nodes[0].Flag("ID_CDROM_BD_R"));
        Assert.EndsWith("/dev/sr0", nodes[0].DevicePath);
    }

    [Fact]
    public void Get_configuration_gives_the_profile_and_the_write_features()
    {
        MmcConfiguration configuration = MmcState.ParseConfiguration(FakeDrive.Configuration(0x0041, 0x002E, 0x002F, 0x0041));
        Assert.Equal(0x0041, configuration.CurrentProfile);
        Assert.Equal(OpticalWriteModes.All, configuration.WriteModes);
        Assert.Equal(OpticalWriteModes.DvdData, MmcState.ParseConfiguration(FakeDrive.Configuration(0x0010, 0x002F)).WriteModes);
        Assert.Equal(OpticalMediaProfile.BdR, MmcState.Profile(0x41));
        Assert.Equal(OpticalMediaProfile.DvdPlusR, MmcState.Profile(0x1B));
        Assert.Equal(OpticalMediaProfile.Unknown, MmcState.Profile(0x12));
    }

    [Fact]
    public void A_truncated_reply_keeps_only_complete_descriptors()
    {
        byte[] reply = FakeDrive.Configuration(0x0041, 0x002E, 0x0041);
        // Header (8), the whole profile list (4 + 7 × 4), then half of the next descriptor.
        MmcConfiguration cut = MmcState.ParseConfiguration(reply.AsSpan(0, 8 + 32 + 2).ToArray());
        Assert.Contains(0x0000, cut.Features);
        Assert.DoesNotContain(0x0041, cut.Features);
    }

    [Fact]
    public async Task The_service_reads_discs_and_keeps_every_mode_disabled_until_validated()
    {
        FakeFactory factory = new(new() { ["sr0"] = new FakeDrive(0x0041), ["sr10"] = new FakeDrive(0x0011) { Status = 2, Recorded = 2_000_000 } });
        LinuxOpticalDeviceService service = new(_tree, factory, diagnose: Diagnose);
        IReadOnlyList<OpticalDeviceStatus> devices = await service.GetDevicesAsync();

        OpticalDeviceStatus sr0 = devices[0];
        Assert.Equal(OpticalMediaProfile.BdR, sr0.Media!.Profile);
        Assert.True(sr0.Media.IsBlank);
        Assert.All(sr0.Modes, mode => Assert.Equal(OpticalBurnUnavailableReason.NotValidated, mode.Reason));

        OpticalDeviceStatus sr1 = devices[1];
        Assert.Equal(OpticalDeviceAccess.PermissionDenied, sr1.Access);
        Assert.Equal(OpticalMediaProfile.DvdMinusR, sr1.Media!.Profile);
        Assert.DoesNotContain("sr1", factory.Opened);
        Assert.Contains("group cdrom", sr1.AccessDetail);
        Assert.Contains("never asks for root", sr1.AccessDetail);

        Assert.False(devices[2].Media!.IsBlank);
        Assert.Equal(2_000_000, devices[2].Media!.CapacitySectors);

        IReadOnlyList<OpticalDeviceStatus> again = await service.GetDevicesAsync();
        Assert.Equal(devices.Select(device => device.Media?.Identity), again.Select(device => device.Media?.Identity));
        Assert.All(factory.Commands, command => Assert.Contains(command, new byte[] { 0x46, 0x00, 0x51, 0x52, 0x25 }));
    }

    [Fact]
    public async Task Dvd_minus_r_is_written_disc_at_once_and_closed()
    {
        FakeDrive drive = new(0x0011);
        byte[] image = Bytes(2048 * 1100, 1);
        OpticalBurnResult result = await Burner(drive).BurnAsync(Request(image, OpticalBurnMediaKind.DvdData));

        Assert.Equal(image.Length, result.BytesWritten);
        Assert.Equal(image, drive.Written);
        Assert.Equal(0x42, drive.ModePage![2] & 0x4f);
        Assert.Equal((uint)(image.Length / 2048), drive.Reserved);
        Assert.EndsWith("35 00 51 5B 00 51 5B 00 1E", drive.Sequence);
        Assert.False(drive.Locked);
    }

    [Fact]
    public async Task Bd_r_is_written_without_mode_select_reserve_or_format()
    {
        FakeDrive drive = new(0x0041);
        byte[] image = Bytes(2048 * 640, 2);
        await Burner(drive).BurnAsync(Request(image, OpticalBurnMediaKind.BluRayData));
        Assert.Equal(image, drive.Written);
        Assert.DoesNotContain("55", drive.Sequence.Split(' '));
        Assert.DoesNotContain("53", drive.Sequence.Split(' '));
        Assert.DoesNotContain("04", drive.Sequence.Split(' '));
    }

    [Fact]
    public async Task Cd_audio_is_written_session_at_once_from_a_cue_sheet()
    {
        FakeDrive drive = new(0x0009) { FreeBlocks = 359_849 };
        byte[] pcm = Bytes(2352 * 400, 3);
        await Burner(drive).BurnAsync(Request(pcm, OpticalBurnMediaKind.CdAudio));
        Assert.Equal(pcm, drive.Written);
        Assert.Equal(0x42, drive.ModePage![2] & 0x4f);
        Assert.Equal(0x00, drive.ModePage[4]);
        // Lead-in, pregap and track at 00:02:00, lead-out at 150 + 400 frames = 00:07:25.
        Assert.Equal("01000001000000000101000100000000010101000000020001AA010100000719", Convert.ToHexString(drive.Cue!));
        Assert.DoesNotContain("5B", drive.Sequence.Split(' '));
    }

    [Fact]
    public async Task Cd_data_is_written_as_mode_1_from_a_data_cue_sheet()
    {
        FakeDrive drive = new(0x0009) { FreeBlocks = 359_849 };
        byte[] image = Bytes(2048 * 300, 4);
        await Burner(drive).BurnAsync(Request(image, OpticalBurnMediaKind.CdData));
        Assert.Equal(image, drive.Written);
        Assert.Equal(0x08, drive.ModePage![4]);
        Assert.StartsWith("4100001400000000", Convert.ToHexString(drive.Cue!));
    }

    [Fact]
    public async Task Without_a_record_or_test_mode_nothing_is_written()
    {
        FakeDrive drive = new(0x0011);
        LinuxOpticalBurner burner = new(_tree, new FakeFactory(new() { ["sr0"] = drive }), new LinuxBurnValidation());
        OpticalBurnException refused = await Assert.ThrowsAsync<OpticalBurnException>(() => burner.BurnAsync(Request(Bytes(2048 * 64, 5), OpticalBurnMediaKind.DvdData)));
        Assert.Equal(OpticalBurnErrorCode.Unsupported, refused.Code);
        Assert.Empty(drive.Written);
    }

    [Fact]
    public async Task Failures_unlock_the_tray_and_keep_their_codes()
    {
        FakeDrive failing = new(0x0011) { FailWriteAt = 100 };
        Assert.Equal(OpticalBurnErrorCode.WriteFailed, (await Assert.ThrowsAsync<OpticalBurnException>(() => Burner(failing).BurnAsync(Request(Bytes(2048 * 600, 6), OpticalBurnMediaKind.DvdData)))).Code);
        Assert.False(failing.Locked);

        FakeDrive drive = new(0x0011);
        Assert.Equal(OpticalBurnErrorCode.MediaChanged, (await Assert.ThrowsAsync<OpticalBurnException>(
            () => Burner(drive).BurnAsync(Request(Bytes(2048 * 64, 7), OpticalBurnMediaKind.DvdData) with { ExpectedMediaIdentity = "another-disc" }))).Code);
        Assert.Empty(drive.Written);

        Assert.Equal(OpticalBurnErrorCode.PermissionDenied, (await Assert.ThrowsAsync<OpticalBurnException>(
            () => Burner(new FakeDrive(0x0011) { OpenErrno = 13 }).BurnAsync(Request(Bytes(2048 * 64, 8), OpticalBurnMediaKind.DvdData)))).Code);
        Assert.Equal(OpticalBurnErrorCode.DeviceBusy, (await Assert.ThrowsAsync<OpticalBurnException>(
            () => Burner(new FakeDrive(0x0011) { OpenErrno = 16 }).BurnAsync(Request(Bytes(2048 * 64, 9), OpticalBurnMediaKind.DvdData)))).Code);
        Assert.Equal(OpticalBurnErrorCode.InsufficientCapacity, (await Assert.ThrowsAsync<OpticalBurnException>(
            () => Burner(new FakeDrive(0x0011) { FreeBlocks = 10 }).BurnAsync(Request(Bytes(2048 * 64, 10), OpticalBurnMediaKind.DvdData)))).Code);
    }

    [Fact]
    public async Task Cancellation_flushes_and_unlocks()
    {
        FakeDrive drive = new(0x001B) { SlowWrites = true };
        using CancellationTokenSource cancellation = new();
        Task burn = Burner(drive).BurnAsync(
            Request(Bytes(2048 * 3000, 11), OpticalBurnMediaKind.DvdData),
            new Inline(progress =>
            {
                if (progress.Stage == "write" && progress.Fraction > 0.2)
                {
                    cancellation.Cancel();
                }
            }),
            cancellation.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => burn);
        Assert.Contains("35", drive.Sequence.Split(' '));
        Assert.False(drive.Locked);
    }

    [Fact]
    public void Write_speeds_come_from_get_performance()
    {
        IReadOnlyList<OpticalWriteSpeed> dvd = LinuxOpticalBurner.ParseWriteSpeeds(Performance(5540, 11080, 22160));
        Assert.Equal([2705, 5410, 10820], dvd.Select(speed => speed.SectorsPerSecond));
        IReadOnlyList<OpticalWriteSpeed> cd = LinuxOpticalBurner.ParseWriteSpeeds(Performance(7056), 2352);
        Assert.Equal(40, cd[0].GetMultiplier(OpticalBurnMediaKind.CdAudio), 0);
    }

    [Fact]
    public async Task A_recorded_pass_enables_the_mode_for_that_recorder_and_disc_type_only()
    {
        string path = Path.Combine(_root, "burn-validations.json");
        LinuxBurnValidation validation = LinuxBurnValidation.Load(path, "DvdData");
        LinuxOpticalDeviceService service = new(_tree, new FakeFactory(new() { ["sr0"] = new FakeDrive(0x0011) }), validation, Diagnose);
        OpticalDeviceStatus status = await service.GetStatusAsync("sr0");
        Assert.True(validation.IsTestOnly(status, OpticalBurnMediaKind.DvdData));
        Assert.Contains("hardware test", status.Mode(OpticalBurnMediaKind.DvdData).Detail);

        validation.Record(new OpticalValidationResult(status, OpticalBurnMediaKind.DvdData, OpticalMediaProfile.DvdMinusR, "Brand DVD-R", "8x", Passed: true, "fine"));
        using (JsonDocument saved = JsonDocument.Parse(File.ReadAllText(path)))
        {
            JsonElement record = saved.RootElement.GetProperty("validations")[0];
            Assert.Equal("pass", record.GetProperty("result").GetString());
            Assert.Equal("WRITER", record.GetProperty("model").GetString());
        }

        LinuxBurnValidation reloaded = LinuxBurnValidation.Load(path);
        OpticalDeviceStatus after = await new LinuxOpticalDeviceService(_tree, new FakeFactory(new() { ["sr0"] = new FakeDrive(0x0011) }), reloaded, Diagnose).GetStatusAsync("sr0");
        Assert.True(after.Mode(OpticalBurnMediaKind.DvdData).IsAvailable);
        Assert.Null(after.Mode(OpticalBurnMediaKind.DvdData).Detail);
        Assert.Equal(OpticalBurnUnavailableReason.NotValidated, after.Mode(OpticalBurnMediaKind.CdAudio).Reason);
    }

    [Fact]
    public async Task Every_burn_is_logged_without_paths()
    {
        string log = Path.Combine(_root, "burn-tests.jsonl");
        LinuxOpticalBurner burner = new(_tree, new FakeFactory(new() { ["sr0"] = new FakeDrive(0x0011) }), new LinuxBurnValidation(testModes: OpticalWriteModes.All), new BurnTestLog(log));
        await burner.BurnAsync(Request(Bytes(2048 * 64, 12), OpticalBurnMediaKind.DvdData));
        string line = File.ReadAllLines(log).Single();
        Assert.Contains("\"result\":\"pass\"", line);
        Assert.Contains("\"testOnly\":true", line);
        Assert.DoesNotContain(_root, line);
    }

    private LinuxOpticalBurner Burner(FakeDrive drive)
        => new(_tree, new FakeFactory(new() { ["sr0"] = drive }), new LinuxBurnValidation(testModes: OpticalWriteModes.All));

    private static OpticalBurnRequest Request(byte[] data, OpticalBurnMediaKind mode)
        => new("sr0", mode, data.Length, (stream, _) => stream.Write(data), BufferOptions: new OpticalBurnBufferOptions(256 * 1024 * 8, 256 * 1024));

    private static LinuxAccessDiagnosis Diagnose(string path)
        => path.EndsWith("sr1", StringComparison.Ordinal)
            ? new LinuxAccessDiagnosis(OpticalDeviceAccess.PermissionDenied, true, false, false, "cdrom", 0x1b0, false)
            : new LinuxAccessDiagnosis(OpticalDeviceAccess.Ready, true, true, true, "cdrom", 0x1b0, true);

    private void Drive(string name, int minor, int type, string vendor, string model, string revision, string udev)
    {
        string block = Path.Combine(_root, "sys/class/block", name);
        Directory.CreateDirectory(Path.Combine(block, "device"));
        File.WriteAllText(Path.Combine(block, "dev"), $"11:{minor}\n");
        File.WriteAllText(Path.Combine(block, "device/type"), $"{type}\n");
        File.WriteAllText(Path.Combine(block, "device/vendor"), vendor + "  \n");
        File.WriteAllText(Path.Combine(block, "device/model"), model + "\n");
        File.WriteAllText(Path.Combine(block, "device/rev"), revision + "\n");
        Directory.CreateDirectory(Path.Combine(_root, "run/udev/data"));
        File.WriteAllText(Path.Combine(_root, $"run/udev/data/b11:{minor}"), $"E:DEVNAME=/dev/{name}\nE:ID_CDROM=1\n{udev}");
        Directory.CreateDirectory(Path.Combine(_root, "dev"));
        File.WriteAllText(Path.Combine(_root, "dev", name), string.Empty);
    }

    private static byte[] Performance(params uint[] kilobytes)
    {
        byte[] reply = new byte[8 + (16 * kilobytes.Length)];
        BinaryPrimitives.WriteUInt32BigEndian(reply, (uint)(reply.Length - 4));
        for (int i = 0; i < kilobytes.Length; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(reply.AsSpan(8 + (16 * i) + 12), kilobytes[i]);
        }

        return reply;
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

    private sealed class FakeFactory(Dictionary<string, FakeDrive> drives) : IScsiTransportFactory
    {
        public List<string> Opened { get; } = [];

        public List<byte> Commands { get; } = [];

        public IScsiTransport OpenForReading(string devicePath) => Open(devicePath);

        public IScsiWriteTransport OpenForWriting(string devicePath) => Open(devicePath);

        private FakeDrive Open(string devicePath)
        {
            string name = Path.GetFileName(devicePath);
            Opened.Add(name);
            FakeDrive drive = drives[name];
            if (drive.OpenErrno > 0)
            {
                throw new LinuxDeviceOpenException(devicePath, drive.OpenErrno);
            }

            drive.Commands = Commands;
            return drive;
        }
    }

    /// <summary>A blank disc behind MMC: it tracks the next writable address and refuses writes elsewhere.</summary>
    private sealed class FakeDrive(int profile) : IScsiWriteTransport
    {
        private readonly MemoryStream _data = new();
        private uint _nextWritable;

        public int OpenErrno { get; init; }

        public uint FreeBlocks { get; init; } = 2_298_496;

        public int Status { get; set; }

        public uint Recorded { get; init; }

        public bool SlowWrites { get; init; }

        public long FailWriteAt { get; init; } = -1;

        public List<byte> Commands { get; set; } = [];

        public List<byte> Log { get; } = [];

        public string Sequence => string.Join(' ', Log.Select(command => command.ToString("X2")));

        public bool Locked { get; private set; }

        public byte[]? ModePage { get; private set; }

        public byte[]? Cue { get; private set; }

        public uint Reserved { get; private set; }

        public byte[] Written => _data.ToArray();

        public static byte[] Configuration(int current, params int[] features)
        {
            int[] profiles = [0x0043, 0x0041, 0x0040, 0x001B, 0x0011, 0x0009, 0x0008];
            List<byte> body = [0x00, 0x00, 0x03, (byte)(profiles.Length * 4)];
            foreach (int p in profiles)
            {
                body.AddRange([(byte)(p >> 8), (byte)p, (byte)(p == current ? 1 : 0), 0]);
            }

            foreach (int feature in features)
            {
                body.AddRange([(byte)(feature >> 8), (byte)feature, 0x01, 0x04, 0, 0, 0, 0]);
            }

            byte[] reply = new byte[8 + body.Count];
            BinaryPrimitives.WriteUInt32BigEndian(reply, (uint)(reply.Length - 4));
            BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(6), (ushort)current);
            body.CopyTo(reply, 8);
            return reply;
        }

        public ScsiResult Read(byte[] cdb, int allocationLength, TimeSpan timeout)
        {
            Log.Add(cdb[0]);
            Commands.Add(cdb[0]);
            switch (cdb[0])
            {
                case 0x46:
                    return Ok(profile >= 0x40 ? Configuration(profile, 0x002E, 0x002F, 0x0041) : Configuration(profile, 0x002E, 0x002F, 0x002B));
                case 0x00:
                case 0xBB:
                case 0xAC:
                    return Ok([]);
                case 0x51:
                {
                    byte[] disc = new byte[34];
                    disc[2] = (byte)Status;
                    disc[6] = 1;
                    return Ok(disc);
                }

                case 0x52:
                {
                    byte[] track = new byte[48];
                    track[7] = 1;
                    BinaryPrimitives.WriteUInt32BigEndian(track.AsSpan(12), _nextWritable);
                    BinaryPrimitives.WriteUInt32BigEndian(track.AsSpan(16), Status == 2 ? 0 : FreeBlocks - _nextWritable);
                    BinaryPrimitives.WriteUInt32BigEndian(track.AsSpan(24), FreeBlocks);
                    return Ok(track);
                }

                case 0x25:
                {
                    byte[] capacity = new byte[8];
                    BinaryPrimitives.WriteUInt32BigEndian(capacity, Math.Max(Recorded, _nextWritable) - 1);
                    return Ok(capacity);
                }

                case 0x1E:
                    Locked = cdb[4] == 1;
                    return Ok([]);
                case 0x5A:
                {
                    byte[] page = new byte[60];
                    BinaryPrimitives.WriteUInt16BigEndian(page, 58);
                    page[8] = 0x05;
                    page[9] = 50;
                    page[10] = 0x01;
                    page[11] = 0x04;
                    return Ok(page);
                }

                case 0x53:
                    Reserved = BinaryPrimitives.ReadUInt32BigEndian(cdb.AsSpan(5));
                    return Ok([]);
                case 0x35:
                    return Ok([]);
                case 0x5B:
                    Status = cdb[2] is 2 or 5 ? 2 : 1;
                    return Ok([]);
                default:
                    return Sense(5, 0x20, 0);
            }
        }

        public ScsiResult Send(byte[] cdb, byte[] data, int length, TimeSpan timeout)
        {
            Log.Add(cdb[0]);
            Commands.Add(cdb[0]);
            switch (cdb[0])
            {
                case 0x55:
                    ModePage = data[8..length];
                    return Ok([]);
                case 0x5D:
                    Cue = data[..length];
                    return Ok([]);
                case 0x2A:
                    break;
                default:
                    return Sense(5, 0x20, 0);
            }

            uint lba = BinaryPrimitives.ReadUInt32BigEndian(cdb.AsSpan(2));
            int blocks = BinaryPrimitives.ReadUInt16BigEndian(cdb.AsSpan(7));
            int sector = ModePage is { } page && profile < 0x10 && page[4] == 0 ? 2352 : 2048;
            if (lba != _nextWritable || blocks * sector != length)
            {
                return Sense(5, 0x21, 0x02);
            }

            if (FailWriteAt >= 0 && _nextWritable >= FailWriteAt)
            {
                return Sense(3, 0x0c, 0);
            }

            if (SlowWrites)
            {
                Thread.Sleep(2);
            }

            _data.Write(data, 0, length);
            _nextWritable += (uint)blocks;
            Status = 1;
            return Ok([]);
        }

        public void Dispose()
        {
        }

        private static ScsiResult Ok(byte[] data) => new(0, 0, 0, [], data);

        private static ScsiResult Sense(int key, int asc, int ascq)
            => new(2, 0, 0, [0x70, 0, (byte)key, 0, 0, 0, 0, 10, 0, 0, 0, 0, (byte)asc, (byte)ascq], []);
    }
}
