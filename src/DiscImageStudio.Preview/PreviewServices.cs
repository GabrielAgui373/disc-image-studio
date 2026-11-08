using DiscImageStudio.Application.Preview;

namespace DiscImageStudio.Preview;

/// <summary>Builds the preview services a host registers with <see cref="PreviewCoordinator"/>.</summary>
public static class PreviewServices
{
    /// <summary>The three projection previews, all reading sources from <paramref name="sources"/>.</summary>
    public static IReadOnlyList<IPreviewService> CreateProjection(PreviewSourceCache sources) =>
    [
        new CdProjectionPreviewService(sources),
        new DvdProjectionPreviewService(sources),
        new BluRayProjectionPreviewService(sources),
    ];

    /// <summary>The three artifact read-backs.</summary>
    public static IReadOnlyList<IPreviewService> CreateArtifact() =>
    [
        new CdArtifactPreviewService(),
        new DvdArtifactPreviewService(),
        new BluRayArtifactPreviewService(),
    ];

    /// <summary>Every preview service: projection and read-back for the three families.</summary>
    public static IReadOnlyList<IPreviewService> CreateAll(PreviewSourceCache sources)
        => [.. CreateProjection(sources), .. CreateArtifact()];
}
