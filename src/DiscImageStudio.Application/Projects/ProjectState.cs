using System.Collections.ObjectModel;
using DiscImageStudio.Core;

namespace DiscImageStudio.Application.Projects;

public sealed record ProjectIdentity
{
    public ProjectIdentity(
        string name,
        DateTimeOffset createdUtc,
        DateTimeOffset modifiedUtc,
        string? calibrationSessionPath = null,
        string? thumbnailPngBase64 = null)
    {
        Name = RequireText(name, nameof(name));
        CreatedUtc = createdUtc;
        ModifiedUtc = modifiedUtc;
        CalibrationSessionPath = calibrationSessionPath;
        ThumbnailPngBase64 = thumbnailPngBase64;
    }

    public string Name { get; }

    public DateTimeOffset CreatedUtc { get; }

    public DateTimeOffset ModifiedUtc { get; }

    public string? CalibrationSessionPath { get; }

    public string? ThumbnailPngBase64 { get; }

    internal static string RequireText(string value, string parameterName)
        => string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A non-empty value is required.", parameterName)
            : value;
}

public sealed record DesignLayerState
{
    public DesignLayerState(
        string id,
        string name,
        string imagePath,
        bool visible,
        double translateXMm,
        double translateYMm,
        double rotationDeg,
        double scale)
    {
        Id = ProjectIdentity.RequireText(id, nameof(id));
        Name = ProjectIdentity.RequireText(name, nameof(name));
        ImagePath = ProjectIdentity.RequireText(imagePath, nameof(imagePath));
        ValidateFinite(translateXMm, nameof(translateXMm));
        ValidateFinite(translateYMm, nameof(translateYMm));
        ValidateFinite(rotationDeg, nameof(rotationDeg));
        if (!double.IsFinite(scale) || scale <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(scale), "Scale must be positive and finite.");
        }

        Visible = visible;
        TranslateXMm = translateXMm;
        TranslateYMm = translateYMm;
        RotationDeg = rotationDeg;
        Scale = scale;
    }

    public string Id { get; }

    public string Name { get; }

    public string ImagePath { get; }

    public bool Visible { get; }

    public double TranslateXMm { get; }

    public double TranslateYMm { get; }

    public double RotationDeg { get; }

    public double Scale { get; }

    private static void ValidateFinite(double value, string parameterName)
    {
        if (!double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(parameterName, "The value must be finite.");
        }
    }
}

public sealed record DesignState
{
    public DesignState(IEnumerable<DesignLayerState>? layers = null)
    {
        DesignLayerState[] copy = layers?.ToArray() ?? [];
        if (copy.Any(layer => layer is null))
        {
            throw new ArgumentException("Design layers cannot contain null entries.", nameof(layers));
        }

        string? duplicate = copy
            .GroupBy(layer => layer.Id, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1)?.Key;
        if (duplicate is not null)
        {
            throw new ArgumentException($"Design layer id '{duplicate}' is duplicated.", nameof(layers));
        }

        Layers = new ReadOnlyCollection<DesignLayerState>(copy);
    }

    public IReadOnlyList<DesignLayerState> Layers { get; }
}

public sealed record ToneState
{
    public ToneState(ToneProfile profile)
    {
        profile.Validate(nameof(profile));
        Profile = profile;
    }

    public ToneProfile Profile { get; }
}

public sealed record DeliveryState
{
    public DeliveryState(
        string outputPath,
        string? dataDirectory,
        string volumeLabel,
        bool filesAtOuterRing = false)
    {
        OutputPath = ProjectIdentity.RequireText(outputPath, nameof(outputPath));
        DataDirectory = dataDirectory ?? string.Empty;
        VolumeLabel = volumeLabel ?? string.Empty;
        FilesAtOuterRing = filesAtOuterRing;
    }

    public string OutputPath { get; }

    public string DataDirectory { get; }

    public string VolumeLabel { get; }

    public bool FilesAtOuterRing { get; }
}

public abstract record MediaState
{
    protected MediaState(double innerRadiusMm, double outerRadiusMm, long sectors)
    {
        ValidateRadius(innerRadiusMm, nameof(innerRadiusMm));
        ValidateRadius(outerRadiusMm, nameof(outerRadiusMm));
        if (outerRadiusMm <= innerRadiusMm)
        {
            throw new ArgumentException("Outer radius must be greater than inner radius.", nameof(outerRadiusMm));
        }

        if (sectors <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sectors), "Sector count must be positive.");
        }

        InnerRadiusMm = innerRadiusMm;
        OuterRadiusMm = outerRadiusMm;
        Sectors = sectors;
    }

    public double InnerRadiusMm { get; }

    public double OuterRadiusMm { get; }

    public long Sectors { get; }

    public abstract OpticalDiscFamily Family { get; }

    private static void ValidateRadius(double value, string parameterName)
    {
        if (!double.IsFinite(value) || value <= 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, "Radius must be positive and finite.");
        }
    }
}

public sealed record CdState : MediaState
{
    public CdState(
        double innerRadiusMm,
        double outerRadiusMm,
        long sectors,
        bool writeCue,
        bool interleave)
        : base(innerRadiusMm, outerRadiusMm, sectors)
    {
        WriteCue = writeCue;
        Interleave = interleave;
    }

    public override OpticalDiscFamily Family => OpticalDiscFamily.CompactDisc;

    public bool WriteCue { get; }

    public bool Interleave { get; }
}

public sealed record DvdState : MediaState
{
    public DvdState(
        double innerRadiusMm,
        double outerRadiusMm,
        long sectors,
        double pitchLinear,
        double pitchQuadratic,
        double pitchCubic)
        : base(innerRadiusMm, outerRadiusMm, sectors)
    {
        if (sectors % 16 != 0)
        {
            throw new ArgumentException("DVD sector count must be divisible by 16.", nameof(sectors));
        }

        DvdTrackGeometry.ValidatePitchCoefficients(pitchLinear, pitchQuadratic, pitchCubic);
        PitchLinear = pitchLinear;
        PitchQuadratic = pitchQuadratic;
        PitchCubic = pitchCubic;
    }

    public override OpticalDiscFamily Family => OpticalDiscFamily.Dvd;

    public double PitchLinear { get; }

    public double PitchQuadratic { get; }

    public double PitchCubic { get; }
}

public sealed record BluRayState : MediaState
{
    public BluRayState(
        double innerRadiusMm,
        double outerRadiusMm,
        long sectors,
        bool invertMapping)
        : base(innerRadiusMm, outerRadiusMm, sectors)
    {
        if (sectors % ProjectDocument.BluRaySectorsPerCluster != 0)
        {
            throw new ArgumentException("Blu-ray sector count must be divisible by 32.", nameof(sectors));
        }

        if (sectors > ProjectDocument.BluRaySectorsPerLayer)
        {
            throw new ArgumentOutOfRangeException(nameof(sectors), "Blu-ray sector count exceeds one layer.");
        }

        if (outerRadiusMm > 60)
        {
            throw new ArgumentOutOfRangeException(nameof(outerRadiusMm), "Blu-ray outer radius cannot exceed 60 mm.");
        }

        InvertMapping = invertMapping;
    }

    public override OpticalDiscFamily Family => OpticalDiscFamily.BluRay;

    public bool InvertMapping { get; }
}

/// <summary>
/// Framework-neutral, validated project state. UI edit buffers are deliberately not part of
/// this type: adapters convert complete text input to typed values before constructing it.
/// </summary>
public sealed record ProjectWorkspace(
    ProjectIdentity Identity,
    DesignState Design,
    ToneState Tone,
    DeliveryState Delivery,
    MediaState Media)
{
    public ProjectWorkspace DeepCopy()
        => new(
            Identity,
            new DesignState(Design.Layers),
            Tone,
            Delivery,
            Media);
}

public sealed record ProjectSnapshot(ProjectWorkspace Workspace);
