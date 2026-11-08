using DiscImageStudio.BluRay;
using DiscImageStudio.Cd;
using DiscImageStudio.Core;
using DiscImageStudio.Dvd;

namespace DiscImageStudio.Generation;

public enum FilePlacement
{
    InnerRing,
    OuterRing,
}

public enum CdArtifactFormat
{
    RawAudio,
    WaveAudio,
    Mode1Iso,
}

public enum GenerationFilesystem
{
    None,
    CdDa,
    Iso9660Joliet,
    Udf201,
}

[Flags]
public enum GenerationCapability
{
    None = 0,
    SequentialStream = 1 << 0,
    Filesystem = 1 << 1,
    CueSheet = 1 << 2,
    MetadataSidecar = 1 << 3,
}

public abstract record GenerateDiscRequest(string SourceImagePath, ToneProfile Tone)
{
    public abstract OpticalDiscFamily Family { get; }
}

public sealed record GenerateCdRequest(
    string SourceImagePath,
    CdDiscParameters Parameters,
    ToneProfile Tone,
    CdArtifactFormat Format = CdArtifactFormat.RawAudio,
    bool Interleave = true,
    bool WriteCue = true,
    CdAudioByteOrder AudioByteOrder = CdAudioByteOrder.BigEndian,
    string? DataDirectory = null,
    string VolumeLabel = "CD_IMAGE",
    FilePlacement FilePlacement = FilePlacement.InnerRing)
    : GenerateDiscRequest(SourceImagePath, Tone)
{
    public override OpticalDiscFamily Family => OpticalDiscFamily.CompactDisc;
}

public sealed record GenerateDvdRequest(
    string SourceImagePath,
    DvdStreamingOptions Options,
    string? DataDirectory = null,
    string VolumeLabel = "DVD_IMAGE",
    FilePlacement FilePlacement = FilePlacement.InnerRing)
    : GenerateDiscRequest(SourceImagePath, Options.Tone ?? ToneProfile.LegacyBinary)
{
    public override OpticalDiscFamily Family => OpticalDiscFamily.Dvd;
}

public sealed record GenerateBluRayRequest(
    string SourceImagePath,
    BluRayDiscParameters Parameters,
    ToneProfile Tone,
    bool InvertMapping = false,
    string? DataDirectory = null,
    string VolumeLabel = "BD_IMAGE",
    FilePlacement FilePlacement = FilePlacement.InnerRing)
    : GenerateDiscRequest(SourceImagePath, Tone)
{
    public override OpticalDiscFamily Family => OpticalDiscFamily.BluRay;
}

public sealed record GenerationRange(long StartSector, long EndSectorExclusive)
{
    public long SectorCount => EndSectorExclusive - StartSector;
}

public sealed record GenerationSidecar(string Kind, string Suffix, bool Required);

public abstract record GenerationPlanDetails;

public sealed record CdGenerationPlanDetails(
    GenerateCdRequest Request,
    CdDataLayoutPlan? DataLayout)
    : GenerationPlanDetails;

public sealed record DvdGenerationPlanDetails(
    GenerateDvdRequest Request,
    DvdHybridStreamingPlan? HybridLayout)
    : GenerationPlanDetails;

public sealed record BluRayGenerationPlanDetails(
    GenerateBluRayRequest Request,
    BluRayIsoPlan IsoLayout)
    : GenerationPlanDetails;

public sealed record GenerationPlan(
    GenerateDiscRequest Request,
    OpticalDiscFamily Family,
    GenerationFilesystem Filesystem,
    GenerationRange DrawingRange,
    long EstimatedBytes,
    int FileCount,
    long FileBytes,
    GenerationCapability RequiredCapabilities,
    IReadOnlyList<GenerationSidecar> Sidecars,
    string Summary,
    GenerationPlanDetails Details);

public sealed record GenerationProgress(
    string Stage,
    long CompletedBytes,
    long TotalBytes,
    TimeSpan Elapsed,
    string MessageCode)
{
    public double Fraction => TotalBytes <= 0 ? 0 : (double)CompletedBytes / TotalBytes;
}

public sealed record GenerationValidationError(
    string Code,
    string Field,
    IReadOnlyDictionary<string, object?> Parameters);

public sealed class GenerationValidationException : ArgumentException
{
    public GenerationValidationException(GenerationValidationError error, Exception? innerException = null)
        : base(error.Code, error.Field, innerException)
    {
        Error = error;
    }

    public GenerationValidationError Error { get; }
}

public interface IDiscGenerationService
{
    Task<GenerationPlan> PlanAsync(
        GenerateDiscRequest request,
        CancellationToken cancellationToken = default);

    Task GenerateFileAsync(
        GenerationPlan plan,
        string outputPath,
        IProgress<GenerationProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task GenerateStreamAsync(
        GenerationPlan plan,
        Stream output,
        IProgress<GenerationProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>Checks destination, existing files, permission and free space before a long job.</summary>
    OutputPreflight Preflight(GenerationPlan plan, string outputPath, ArtifactReplacePolicy replacePolicy);

    /// <summary>Generates and publishes artifact, sidecars and manifest as one unit.</summary>
    Task<PublishedArtifact> PublishFileAsync(
        GenerationPlan plan,
        string outputPath,
        ArtifactPublishOptions? options = null,
        IProgress<GenerationProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
