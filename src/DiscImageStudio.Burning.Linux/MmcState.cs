using System.Buffers.Binary;
using System.Security.Cryptography;

namespace DiscImageStudio.Burning.Linux;

/// <summary>What a GET CONFIGURATION reply says about a drive: the current profile and the write features it lists.</summary>
public sealed record MmcConfiguration(int CurrentProfile, IReadOnlySet<int> Features, IReadOnlySet<int> Profiles)
{
    /// <summary>
    /// Write modes from the feature list (MMC-6): CD Mastering (0x002E, session-at-once and raw,
    /// which both CD modes use), DVD-R/-RW Write (0x002F), DVD+R (0x002B), DVD+RW (0x002A),
    /// DVD+R DL (0x003B) and BD Write (0x0041).
    /// </summary>
    public OpticalWriteModes WriteModes
    {
        get
        {
            OpticalWriteModes modes = OpticalWriteModes.None;
            if (Features.Contains(0x002E))
            {
                modes |= OpticalWriteModes.CdAudio | OpticalWriteModes.CdData;
            }

            if (Features.Contains(0x002F) || Features.Contains(0x002B) || Features.Contains(0x002A) || Features.Contains(0x003B))
            {
                modes |= OpticalWriteModes.DvdData;
            }

            if (Features.Contains(0x0041))
            {
                modes |= OpticalWriteModes.BluRayData;
            }

            return modes;
        }
    }
}

/// <summary>READ DISC INFORMATION, the fields used here.</summary>
public sealed record MmcDiscInformation(int DiscStatus, bool Erasable, int LastTrackInLastSession)
{
    public bool IsBlank => DiscStatus == 0;

    public bool IsAppendable => DiscStatus == 1;
}

/// <summary>READ TRACK INFORMATION, the fields used here.</summary>
public sealed record MmcTrackInformation(uint TrackStart, uint FreeBlocks, uint TrackSize, bool NextWritableValid, uint NextWritable);

/// <summary>
/// The read-only MMC conversation of Task 13.4 and the pure parsing of its replies (MMC-6):
/// TEST UNIT READY, GET CONFIGURATION, READ DISC INFORMATION, READ TRACK INFORMATION and
/// READ CAPACITY. Nothing here writes, locks, ejects, formats or changes the drive.
/// </summary>
public static class MmcState
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public static readonly byte[] TestUnitReady = [0x00, 0, 0, 0, 0, 0];

    public static byte[] GetConfiguration(int allocation) => [0x46, 0x00, 0, 0, 0, 0, 0, (byte)(allocation >> 8), (byte)allocation, 0];

    public static byte[] ReadDiscInformation(int allocation) => [0x51, 0, 0, 0, 0, 0, 0, (byte)(allocation >> 8), (byte)allocation, 0];

    /// <summary>Address type 01: the operand is a track number.</summary>
    public static byte[] ReadTrackInformation(int track, int allocation)
        => [0x52, 0x01, (byte)(track >> 24), (byte)(track >> 16), (byte)(track >> 8), (byte)track, 0, (byte)(allocation >> 8), (byte)allocation, 0];

    public static readonly byte[] ReadCapacity = [0x25, 0, 0, 0, 0, 0, 0, 0, 0, 0];

    public static MmcConfiguration ParseConfiguration(ReadOnlySpan<byte> reply)
    {
        if (reply.Length < 8)
        {
            throw new InvalidDataException("GET CONFIGURATION reply is shorter than its header.");
        }

        int length = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(reply) + 4, (uint)reply.Length);
        int current = BinaryPrimitives.ReadUInt16BigEndian(reply[6..]);
        HashSet<int> features = [];
        HashSet<int> profiles = [];
        int offset = 8;
        while (offset + 4 <= length)
        {
            int code = BinaryPrimitives.ReadUInt16BigEndian(reply[offset..]);
            int additional = reply[offset + 3];
            if (offset + 4 + additional > length)
            {
                break;
            }

            features.Add(code);
            if (code == 0x0000)
            {
                // Profile List: four bytes per profile.
                for (int p = offset + 4; p + 4 <= offset + 4 + additional; p += 4)
                {
                    profiles.Add(BinaryPrimitives.ReadUInt16BigEndian(reply[p..]));
                }
            }

            offset += 4 + additional;
        }

        return new MmcConfiguration(current, features, profiles);
    }

    public static MmcDiscInformation ParseDiscInformation(ReadOnlySpan<byte> reply)
    {
        if (reply.Length < 12)
        {
            throw new InvalidDataException("READ DISC INFORMATION reply is too short.");
        }

        return new MmcDiscInformation(reply[2] & 0x03, (reply[2] & 0x10) != 0, reply[6] | (reply[11] << 8));
    }

    public static MmcTrackInformation ParseTrackInformation(ReadOnlySpan<byte> reply)
    {
        if (reply.Length < 28)
        {
            throw new InvalidDataException("READ TRACK INFORMATION reply is too short.");
        }

        return new MmcTrackInformation(
            BinaryPrimitives.ReadUInt32BigEndian(reply[8..]),
            BinaryPrimitives.ReadUInt32BigEndian(reply[16..]),
            BinaryPrimitives.ReadUInt32BigEndian(reply[24..]),
            (reply[7] & 0x01) != 0,
            BinaryPrimitives.ReadUInt32BigEndian(reply[12..]));
    }

    /// <summary>The MMC profile number as the product's media profile.</summary>
    public static OpticalMediaProfile Profile(int profile) => profile switch
    {
        0x0008 => OpticalMediaProfile.CdRom,
        0x0009 => OpticalMediaProfile.CdR,
        0x000A => OpticalMediaProfile.CdRw,
        0x0010 => OpticalMediaProfile.DvdRom,
        0x0011 => OpticalMediaProfile.DvdMinusR,
        0x0013 or 0x0014 => OpticalMediaProfile.DvdMinusRw,
        0x0015 or 0x0016 => OpticalMediaProfile.DvdMinusRDualLayer,
        0x001A => OpticalMediaProfile.DvdPlusRw,
        0x001B => OpticalMediaProfile.DvdPlusR,
        0x002B => OpticalMediaProfile.DvdPlusRDualLayer,
        0x0040 => OpticalMediaProfile.BdRom,
        0x0041 or 0x0042 => OpticalMediaProfile.BdR,
        0x0043 => OpticalMediaProfile.BdRe,
        _ => OpticalMediaProfile.Unknown,
    };

    /// <summary>The drive's write modes and the loaded disc (null without one), read with the commands above.</summary>
    public static (OpticalWriteModes WriteModes, OpticalMediaState? Media) Read(IScsiTransport transport)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ScsiResult configurationReply = Require(transport.Read(GetConfiguration(0xfff8), 0xfff8, Timeout), "GET CONFIGURATION");
        MmcConfiguration configuration = ParseConfiguration(configurationReply.Data);

        ScsiResult ready = transport.Read(TestUnitReady, 0, Timeout);
        if (ready.IsNoMedium || configuration.CurrentProfile == 0)
        {
            return (configuration.WriteModes, null);
        }

        if (!ready.Succeeded && ready.IsBecomingReady)
        {
            throw new IOException("The drive is still reading the disc.");
        }

        byte[] discReply = Require(transport.Read(ReadDiscInformation(34), 34, Timeout), "READ DISC INFORMATION").Data;
        MmcDiscInformation disc = ParseDiscInformation(discReply);
        byte[] trackReply = [];
        MmcTrackInformation? track = null;
        if (disc.LastTrackInLastSession > 0)
        {
            trackReply = Require(transport.Read(ReadTrackInformation(disc.LastTrackInLastSession, 48), 48, Timeout), "READ TRACK INFORMATION").Data;
            track = ParseTrackInformation(trackReply);
        }

        long recorded = 0;
        if (!disc.IsBlank)
        {
            ScsiResult capacity = transport.Read(ReadCapacity, 8, Timeout);
            if (capacity.Succeeded && capacity.Data.Length >= 8)
            {
                recorded = BinaryPrimitives.ReadUInt32BigEndian(capacity.Data) + 1L;
            }
        }

        long free = disc.IsBlank || disc.IsAppendable ? track?.FreeBlocks ?? 0 : 0;
        long total = disc.IsBlank ? (track?.TrackStart ?? 0) + free : recorded + free;
        OpticalMediaState media = new(
            Profile(configuration.CurrentProfile),
            disc.IsBlank,
            disc.IsBlank || disc.IsAppendable,
            total,
            free,
            Identity(configuration.CurrentProfile, discReply, trackReply, recorded));
        return (configuration.WriteModes, media);
    }

    /// <summary>
    /// A fingerprint of the disc from the replies that describe it (profile, disc and track
    /// information, recorded size). Two blank discs of the same kind and capacity may share it,
    /// which is harmless: they are equivalent for a burn.
    /// </summary>
    public static string Identity(int profile, ReadOnlySpan<byte> discInformation, ReadOnlySpan<byte> trackInformation, long recorded)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> number = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(number, profile);
        hash.AppendData(number);
        hash.AppendData(discInformation);
        hash.AppendData(trackInformation);
        BinaryPrimitives.WriteInt64BigEndian(number, recorded);
        hash.AppendData(number);
        return "mmc-" + Convert.ToHexStringLower(hash.GetHashAndReset())[..24];
    }

    private static ScsiResult Require(ScsiResult result, string command)
    {
        if (!result.Succeeded)
        {
            (int key, int asc, int ascq) = result.SenseCode;
            throw new IOException($"{command} failed (status {result.Status:X2}, sense {key:X}/{asc:X2}/{ascq:X2}).");
        }

        return result;
    }
}
