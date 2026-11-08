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
}
