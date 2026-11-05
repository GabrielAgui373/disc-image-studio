using System.Globalization;
using DiscImageStudio.BluRay;
using DiscImageStudio.Cd;
using DiscImageStudio.Core;
using DiscImageStudio.Dvd;

namespace DiscImageStudio.Generation;

public sealed record TypedGenerationCommand(GenerateDiscRequest Request, string OutputPath);

/// <summary>Text exists only at the CLI edge; returned requests contain typed values.</summary>
public static class TypedGenerationCommandLine
{
    public static string HelpText =>
        "Typed generation outputs:\n"
        + "  cd-generate  CD-DA RAW/WAV or Mode 1 ISO9660/Joliet when --data-dir is set\n"
        + "  solve        DVD drawing ISO or ISO9660/Joliet hybrid when --data-dir is set\n"
        + "  bd-generate  Blu-ray drawing ISO or UDF 2.01 when --data-dir is set\n"
        + "All generation modes support planning, byte progress and cancellation.";

    public static bool TryParse(
        IReadOnlyList<string> arguments,
        out TypedGenerationCommand? command)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        command = null;
        if (arguments.Count == 0
            || arguments[0] is not ("cd-generate" or "solve" or "bd-generate"))
        {
            return false;
        }

        string name = arguments[0];
        Options options = Options.Parse(arguments.Skip(1).ToArray());
        command = name switch
        {
            "cd-generate" => ParseCd(options),
            "solve" => ParseDvd(options),
            "bd-generate" => ParseBluRay(options),
            _ => null,
        };
        return true;
    }

    private static TypedGenerationCommand ParseCd(Options options)
    {
        options.EnsureKnown(
            "input", "output", "data-dir", "volume-label", "file-position",
            "r0", "r1", "sectors", "velocity", "theta0", "outer",
            "interleave", "cue", "tone-dither", "tone-levels",
            "tone-brightness", "tone-gamma");
        string output = options.Require("output");
        string? dataDirectory = options.Optional("data-dir");
        CdArtifactFormat format = dataDirectory is not null
            ? CdArtifactFormat.Mode1Iso
            : Path.GetExtension(output).Equals(".wav", StringComparison.OrdinalIgnoreCase)
                ? CdArtifactFormat.WaveAudio
                : CdArtifactFormat.RawAudio;
        CdDiscParameters parameters = new(
            options.Double("r0", 24.5),
            options.Double("r1", 56.8),
            options.Long("sectors", 359_849),
            options.Double("velocity", CdDiscParameters.StandardLinearVelocityMmPerSecond),
            options.Double("theta0", 0) * Math.PI / 180,
            options.Double("outer", CdDiscParameters.StandardImageOuterRadiusMm));
        ToneProfile tone = ReadTone(options, allowFourLevels: true);
        GenerateCdRequest request = new(
            options.Require("input"),
            parameters,
            tone,
            format,
            options.Boolean("interleave", true),
            options.Boolean("cue", true),
            format == CdArtifactFormat.RawAudio
                ? CdAudioByteOrder.BigEndian
                : CdAudioByteOrder.LittleEndian,
            dataDirectory,
            options.String("volume-label", "CD_IMAGE"),
            options.Placement());
        return new TypedGenerationCommand(request, output);
    }

    private static TypedGenerationCommand ParseDvd(Options options)
    {
        options.EnsureKnown(
            "image", "iso-output", "data-dir", "file-position", "volume-label",
            "total-sectors", "fill-sectors", "inner-radius-mm", "outer-radius-mm",
            "channel-bit-nm", "start-angle", "image-threshold", "alpha-threshold",
            "random-seed", "fast-parallelism", "pitch-linear", "pitch-quadratic",
            "pitch-cubic", "tone-dither", "tone-levels", "tone-brightness", "tone-gamma",
            "algorithm", "iterations-per-block", "fast-output", "spiral-direction");
        uint sectors = options.UInt("total-sectors");
        if (options.Optional("data-dir") is null
            && options.Optional("fill-sectors") is string fill
            && ParseUInt(fill, "fill-sectors") != sectors)
        {
            throw Error("generation.cli.dvd_fill_mismatch", "fill-sectors", ("totalSectors", sectors));
        }

        ValidateFixed(options, "algorithm", "dispersion");
        ValidateFixed(options, "iterations-per-block", "1");
        ValidateFixed(options, "fast-output", "true");
        ValidateFixed(options, "spiral-direction", "cw");
        ToneProfile tone = ReadTone(options, allowFourLevels: false);
        DvdStreamingOptions streaming = new(
            sectors,
            options.Double("inner-radius-mm", 24),
            options.Double("outer-radius-mm", 58),
            options.Double("channel-bit-nm", DvdStreamingOptions.StandardChannelBitLengthNm),
            options.Double("start-angle", 0),
            options.Byte("image-threshold", 128),
            options.Byte("alpha-threshold", 1),
            options.Int("random-seed", 1),
            options.Int("fast-parallelism", 0),
            options.Double("pitch-linear", 0),
            options.Double("pitch-quadratic", 0),
            options.Double("pitch-cubic", 0),
            tone);
        GenerateDvdRequest request = new(
            options.Require("image"),
            streaming,
            options.Optional("data-dir"),
            options.String("volume-label", "DVD_IMAGE"),
            options.Placement());
        return new TypedGenerationCommand(request, options.Require("iso-output"));
    }

    private static TypedGenerationCommand ParseBluRay(Options options)
    {
        options.EnsureKnown(
            "preset", "total-sectors", "r0", "r1", "start-angle",
            "input", "output", "data-dir", "file-position", "volume-label",
            "invert", "tone-dither", "tone-levels", "tone-brightness", "tone-gamma");
        string presetId = options.String("preset", "bd-r-sl-25gb");
        BluRayDiscPreset preset;
        try
        {
            preset = BluRayDiscPreset.Find(presetId);
        }
        catch (ArgumentException exception)
        {
            throw Error(
                "generation.cli.invalid_choice",
                "preset",
                ("value", presetId),
                ("reason", exception.Message));
        }
        BluRayDiscParameters defaults = preset.CreateParameters();
        BluRayDiscParameters parameters = new(
            options.UInt("total-sectors", preset.TotalSectors),
            preset.LayerCount,
            options.Double("r0", defaults.InnerRadiusMm),
            options.Double("r1", defaults.OuterRadiusMm),
            options.Double("start-angle", defaults.StartAngleDegrees));
        GenerateBluRayRequest request = new(
            options.Require("input"),
            parameters,
            ReadTone(options, allowFourLevels: false),
            options.Boolean("invert", preset.InvertMapping),
            options.Optional("data-dir"),
            options.String("volume-label", "BD_IMAGE"),
            options.Placement());
        return new TypedGenerationCommand(request, options.Require("output"));
    }

    private static ToneProfile ReadTone(Options options, bool allowFourLevels)
    {
        ToneProfile tone = new(
            options.Double("tone-dither", 0),
            options.Int("tone-levels", ToneProfile.TwoLevels),
            options.Double("tone-brightness", 0),
            options.Boolean("tone-gamma", false));
        try
        {
            tone.Validate("tone");
        }
        catch (ArgumentException exception)
        {
            throw Error(
                "generation.tone.invalid",
                "tone",
                ("reason", exception.Message));
        }
        if (!allowFourLevels && !tone.IsDvdCompatible)
        {
            throw Error("generation.tone.binary_required", "tone-levels");
        }

        return tone;
    }

    private static void ValidateFixed(Options options, string name, string expected)
    {
        string? actual = options.Optional(name);
        if (actual is not null && !actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            throw Error(
                "generation.cli.fixed_option",
                name,
                ("expected", expected),
                ("actual", actual));
        }
    }

    private static uint ParseUInt(string value, string name)
        => uint.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint parsed)
            ? parsed
            : throw Error("generation.cli.invalid_integer", name, ("value", value));

    private static GenerationValidationException Error(
        string code,
        string field,
        params (string Key, object? Value)[] parameters)
        => new(new GenerationValidationError(
            code,
            field,
            parameters.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal)));

    private sealed class Options
    {
        private readonly Dictionary<string, string> _values;

        private Options(Dictionary<string, string> values) => _values = values;

        internal static Options Parse(IReadOnlyList<string> arguments)
        {
            Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < arguments.Count; index++)
            {
                string argument = arguments[index];
                if (!argument.StartsWith("--", StringComparison.Ordinal) || argument.Length == 2)
                {
                    throw Error("generation.cli.expected_option", "arguments", ("value", argument));
                }

                int separator = argument.IndexOf('=');
                string name = separator < 0 ? argument[2..] : argument[2..separator];
                string value;
                if (separator >= 0)
                {
                    value = argument[(separator + 1)..];
                }
                else if (++index < arguments.Count && !arguments[index].StartsWith("--", StringComparison.Ordinal))
                {
                    value = arguments[index];
                }
                else
                {
                    throw Error("generation.cli.missing_value", name);
                }

                if (string.IsNullOrWhiteSpace(value))
                {
                    throw Error("generation.cli.empty_value", name);
                }

                if (!values.TryAdd(name, value))
                {
                    throw Error("generation.cli.duplicate_option", name);
                }
            }

            return new Options(values);
        }

        internal void EnsureKnown(params string[] allowed)
        {
            HashSet<string> names = new(allowed, StringComparer.OrdinalIgnoreCase);
            string? unknown = _values.Keys.FirstOrDefault(name => !names.Contains(name));
            if (unknown is not null)
            {
                throw Error("generation.cli.unknown_option", unknown);
            }
        }

        internal string Require(string name)
            => _values.TryGetValue(name, out string? value)
                ? value
                : throw Error("generation.cli.required_option", name);

        internal string? Optional(string name)
            => _values.GetValueOrDefault(name);

        internal string String(string name, string fallback)
            => _values.GetValueOrDefault(name, fallback);

        internal int Int(string name, int fallback)
            => _values.TryGetValue(name, out string? value)
                && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
                ? parsed
                : _values.ContainsKey(name)
                    ? throw Error("generation.cli.invalid_integer", name, ("value", value))
                    : fallback;

        internal byte Byte(string name, byte fallback)
        {
            int value = Int(name, fallback);
            return value is >= byte.MinValue and <= byte.MaxValue
                ? (byte)value
                : throw Error(
                    "generation.cli.integer_out_of_range",
                    name,
                    ("value", value),
                    ("minimum", byte.MinValue),
                    ("maximum", byte.MaxValue));
        }

        internal long Long(string name, long fallback)
            => _values.TryGetValue(name, out string? value)
                && long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed)
                ? parsed
                : _values.ContainsKey(name)
                    ? throw Error("generation.cli.invalid_integer", name, ("value", value))
                    : fallback;

        internal uint UInt(string name)
            => _values.TryGetValue(name, out string? value)
                ? ParseUInt(value, name)
                : throw Error("generation.cli.required_option", name);

        internal uint UInt(string name, uint fallback)
            => _values.TryGetValue(name, out string? value) ? ParseUInt(value, name) : fallback;

        internal double Double(string name, double fallback)
            => _values.TryGetValue(name, out string? value)
                && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
                && double.IsFinite(parsed)
                ? parsed
                : _values.ContainsKey(name)
                    ? throw Error("generation.cli.invalid_number", name, ("value", value))
                    : fallback;

        internal bool Boolean(string name, bool fallback)
        {
            if (!_values.TryGetValue(name, out string? value))
            {
                return fallback;
            }

            return value switch
            {
                "1" => true,
                "0" => false,
                _ when bool.TryParse(value, out bool parsed) => parsed,
                _ => throw Error("generation.cli.invalid_boolean", name, ("value", value)),
            };
        }

        internal FilePlacement Placement()
            => String("file-position", "inner").ToLowerInvariant() switch
            {
                "inner" => FilePlacement.InnerRing,
                "outer" => FilePlacement.OuterRing,
                string value => throw Error(
                    "generation.cli.invalid_choice",
                    "file-position",
                    ("value", value),
                    ("allowed", "inner,outer")),
            };
    }
}
