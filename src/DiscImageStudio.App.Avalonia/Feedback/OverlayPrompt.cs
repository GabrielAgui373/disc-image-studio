namespace DiscImageStudio.App.Avalonia.Feedback;

public sealed record OverlayPromptRequest(
    string Title,
    string Message,
    string PrimaryLabel,
    string? SecondaryLabel = null,
    string? TertiaryLabel = null,
    int CancelResult = -1);

public interface IOverlayPromptPresenter
{
    Task<int> ShowPromptAsync(OverlayPromptRequest request, CancellationToken cancellationToken = default);
}
