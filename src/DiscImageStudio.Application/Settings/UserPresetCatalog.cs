using System.Text;
using System.Text.Json;
using DiscImageStudio.Application.Ports;
using DiscImageStudio.Application.Projects;
using DiscImageStudio.Application.Results;
using DiscImageStudio.Core;

namespace DiscImageStudio.Application.Settings;

/// <summary>A geometry preset presented by hosts; <see cref="Media"/> is already validated.</summary>
public sealed record ProjectPresetDefinition(
    string Id,
    OpticalDiscFamily Family,
    string DisplayName,
    string Description,
    MediaState Media,
    bool IsUserPreset);

/// <summary>
/// Standard built-in presets plus the user's own geometry presets. User presets live in a
/// <see cref="DiscPresetJsonStore"/> file (the format the WPF host also writes), so the file stays
/// interchangeable. A user file that cannot be read is reported and never overwritten.
/// </summary>
public sealed class UserPresetCatalog
{
    public const string StandardCdId = "cd-80";
    public const string StandardDvdId = "dvd-5-120mm";
    public const string StandardBluRayId = "bd-r-sl-25gb";
    public const int MaximumNameLength = 80;

    private readonly string _filePath;
    private readonly IAppLogger _logger;

    public UserPresetCatalog(string filePath, IAppLogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = filePath;
        _logger = logger ?? new NullAppLogger();
    }

    public string FilePath => _filePath;

    /// <summary>The standard preset of each family from the built-in catalog.</summary>
    public static IReadOnlyList<ProjectPresetDefinition> LoadStandard()
    {
        DiscPresetJsonDocument catalog = DiscPresetJsonStore.LoadBuiltIn();
        CdDiscPresetDefinition cd = catalog.CdPresets.Single(preset => preset.Id == StandardCdId);
        DvdDiscPresetDefinition dvd = catalog.DvdPresets.Single(preset => preset.Id == StandardDvdId);
        BluRayDiscPresetDefinition bluRay = (catalog.BluRayPresets ?? []).Single(preset => preset.Id == StandardBluRayId);
        return [ToDefinition(cd, isUser: false)!, ToDefinition(dvd, isUser: false)!, ToDefinition(bluRay, isUser: false)!];
    }

    public IReadOnlyList<ProjectPresetDefinition> LoadUserPresets()
    {
        if (!File.Exists(_filePath))
        {
            return [];
        }

        try
        {
            DiscPresetJsonDocument document = DiscPresetJsonStore.Load(_filePath);
            return document.CdPresets.Select(preset => ToDefinition(preset, isUser: true))
                .Concat(document.DvdPresets.Select(preset => ToDefinition(preset, isUser: true)))
                .Concat((document.BluRayPresets ?? []).Select(preset => ToDefinition(preset, isUser: true)))
                .OfType<ProjectPresetDefinition>()
                .ToArray();
        }
        catch (Exception exception) when (IsReadFailure(exception))
        {
            _logger.Log(AppLogLevel.Warning, "user-presets.load.failed", "User presets could not be read.", exception);
            return [];
        }
    }

    public ApplicationResult<ProjectPresetDefinition> SaveUserPreset(
        string displayName,
        string description,
        MediaState media)
    {
        ArgumentNullException.ThrowIfNull(media);
        string name = displayName?.Trim() ?? string.Empty;
        if (name.Length is 0 or > MaximumNameLength)
        {
            return ApplicationResult<ProjectPresetDefinition>.Failure(ApplicationErrorCode.InvalidRequest, "A preset name is required.");
        }

        if (!TryLoadForWrite(out DiscPresetJsonDocument document, out ApplicationError? error))
        {
            return ApplicationResult<ProjectPresetDefinition>.Failure(error!.Code, error.Detail, error.Exception);
        }

        string id = CreateId(name, document);
        string text = string.IsNullOrWhiteSpace(description) ? name : description.Trim();
        DiscPresetJsonDocument updated = media switch
        {
            DvdState dvd => document with
            {
                DvdPresets =
                [
                    .. document.DvdPresets,
                    new DvdDiscPresetDefinition(
                        id,
                        name,
                        text,
                        checked((uint)dvd.Sectors),
                        dvd.InnerRadiusMm,
                        dvd.OuterRadiusMm,
                        PitchLinear: dvd.PitchLinear,
                        PitchQuadratic: dvd.PitchQuadratic,
                        PitchCubic: dvd.PitchCubic),
                ],
            },
            BluRayState bluRay => document with
            {
                BluRayPresets =
                [
                    .. document.BluRayPresets ?? [],
                    new BluRayDiscPresetDefinition(
                        id,
                        name,
                        text,
                        checked((uint)bluRay.Sectors),
                        bluRay.InnerRadiusMm,
                        bluRay.OuterRadiusMm,
                        bluRay.InvertMapping),
                ],
            },
            _ => document with
            {
                CdPresets =
                [
                    .. document.CdPresets,
                    new CdDiscPresetDefinition(id, name, text, media.Sectors, media.InnerRadiusMm, media.OuterRadiusMm),
                ],
            },
        };

        ApplicationResult saved = Save(updated);
        return saved.Succeeded
            ? ApplicationResult<ProjectPresetDefinition>.Success(
                new ProjectPresetDefinition(id, media.Family, name, text, media, IsUserPreset: true))
            : ApplicationResult<ProjectPresetDefinition>.Failure(saved.Error!.Code, saved.Error.Detail, saved.Error.Exception);
    }

    public ApplicationResult DeleteUserPreset(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (!TryLoadForWrite(out DiscPresetJsonDocument document, out ApplicationError? error))
        {
            return ApplicationResult.Failure(error!.Code, error.Detail, error.Exception);
        }

        return Save(document with
        {
            CdPresets = document.CdPresets.Where(preset => preset.Id != id).ToArray(),
            DvdPresets = document.DvdPresets.Where(preset => preset.Id != id).ToArray(),
            BluRayPresets = (document.BluRayPresets ?? []).Where(preset => preset.Id != id).ToArray(),
        });
    }

    /// <summary>
    /// Applies a preset's geometry to the current media of the same family. CD output options
    /// (CUE sheet, interleave) are project choices, not geometry, so they are preserved.
    /// </summary>
    public static MediaState ApplyGeometry(MediaState current, MediaState preset)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(preset);
        if (current.Family != preset.Family)
        {
            throw new ArgumentException("A project's disc family cannot change.", nameof(preset));
        }

        return (current, preset) switch
        {
            (CdState cd, _) => new CdState(preset.InnerRadiusMm, preset.OuterRadiusMm, preset.Sectors, cd.WriteCue, cd.Interleave),
            _ => preset,
        };
    }

    private bool TryLoadForWrite(out DiscPresetJsonDocument document, out ApplicationError? error)
    {
        error = null;
        if (!File.Exists(_filePath))
        {
            document = new DiscPresetJsonDocument(
                DiscPresetJsonStore.CurrentSchemaVersion,
                [],
                [],
                DisabledCdPresetIds: [],
                DisabledDvdPresetIds: [],
                BluRayPresets: [],
                DisabledBluRayPresetIds: []);
            return true;
        }

        try
        {
            document = DiscPresetJsonStore.Load(_filePath);
            return true;
        }
        catch (Exception exception) when (IsReadFailure(exception))
        {
            // Never replace a file the user may have edited by hand with a partial copy.
            document = null!;
            error = new ApplicationError(ApplicationErrorCode.PresetReadFailed, exception.Message, exception);
            return false;
        }
    }

    private ApplicationResult Save(DiscPresetJsonDocument document)
    {
        try
        {
            DiscPresetJsonStore.Save(_filePath, document);
            return ApplicationResult.Success();
        }
        catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or InvalidDataException
                                              or ArgumentException)
        {
            _logger.Log(AppLogLevel.Warning, "user-presets.save.failed", "User presets could not be saved.", exception);
            return ApplicationResult.Failure(ApplicationErrorCode.PresetWriteFailed, exception.Message, exception);
        }
    }

    private static string CreateId(string name, DiscPresetJsonDocument document)
    {
        StringBuilder slug = new("user-");
        foreach (char character in name.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(character))
            {
                slug.Append(character);
            }
            else if (slug[^1] != '-')
            {
                slug.Append('-');
            }
        }

        HashSet<string> existing = new(
            document.CdPresets.Select(preset => preset.Id)
                .Concat(document.DvdPresets.Select(preset => preset.Id))
                .Concat((document.BluRayPresets ?? []).Select(preset => preset.Id)),
            StringComparer.Ordinal);
        string stem = slug.ToString().TrimEnd('-');
        string id = stem;
        for (int suffix = 2; existing.Contains(id); suffix++)
        {
            id = $"{stem}-{suffix}";
        }

        return id;
    }

    private static bool IsReadFailure(Exception exception)
        => exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or JsonException
            or NotSupportedException;

    private static ProjectPresetDefinition? ToDefinition(CdDiscPresetDefinition preset, bool isUser)
        => TryCreate(preset.Id, OpticalDiscFamily.CompactDisc, preset.DisplayName, preset.Description, isUser, () =>
            new CdState(preset.InnerRadiusMm, preset.OuterRadiusMm, preset.Sectors, writeCue: true, interleave: true));

    private static ProjectPresetDefinition? ToDefinition(DvdDiscPresetDefinition preset, bool isUser)
        => TryCreate(preset.Id, OpticalDiscFamily.Dvd, preset.DisplayName, preset.Description, isUser, () =>
            new DvdState(
                preset.InnerRadiusMm,
                preset.OuterRadiusMm,
                preset.TotalSectors,
                preset.PitchLinear,
                preset.PitchQuadratic,
                preset.PitchCubic));

    private static ProjectPresetDefinition? ToDefinition(BluRayDiscPresetDefinition preset, bool isUser)
        => TryCreate(preset.Id, OpticalDiscFamily.BluRay, preset.DisplayName, preset.Description, isUser, () =>
            new BluRayState(preset.InnerRadiusMm, preset.OuterRadiusMm, preset.TotalSectors, preset.InvertMapping));

    /// <summary>A user preset the portable media rules reject is skipped instead of failing the list.</summary>
    private static ProjectPresetDefinition? TryCreate(
        string id,
        OpticalDiscFamily family,
        string displayName,
        string description,
        bool isUser,
        Func<MediaState> create)
    {
        try
        {
            return new ProjectPresetDefinition(id, family, displayName, description, create(), isUser);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
