using DiscImageStudio.App.Avalonia.Feedback;
using DiscImageStudio.App.Avalonia.Localization;
using DiscImageStudio.Application.Ports;

namespace DiscImageStudio.App.Avalonia.Platform;

/// <summary>Maps framework-neutral prompt requests to the shell's in-window overlay.</summary>
public sealed class OverlayUserPromptService(ITextCatalog text) : IUserPromptService
{
    private IOverlayPromptPresenter? _presenter;

    public void Attach(IOverlayPromptPresenter presenter)
        => _presenter = presenter ?? throw new ArgumentNullException(nameof(presenter));

    public async Task<UnsavedChangesDecision> ConfirmUnsavedChangesAsync(
        UnsavedChangesRequest request,
        CancellationToken cancellationToken = default)
    {
        int choice = await ShowAsync(
            new OverlayPromptRequest(
                text["prompt.unsaved.title"],
                $"{text["prompt.unsaved.message"]} ({request.ProjectName})",
                text["prompt.save"],
                text["prompt.discard"],
                text["prompt.cancel"],
                CancelResult: 2),
            cancellationToken);

        return choice switch
        {
            0 => UnsavedChangesDecision.Save,
            1 => UnsavedChangesDecision.Discard,
            _ => UnsavedChangesDecision.Cancel,
        };
    }

    public async Task<bool> ConfirmShutdownAsync(int activeJobCount, CancellationToken cancellationToken = default)
        => await ShowAsync(
            new OverlayPromptRequest(
                text["prompt.shutdown.title"],
                $"{text["prompt.shutdown.message"]} Active jobs: {activeJobCount}.",
                text["prompt.continue"],
                TertiaryLabel: text["prompt.cancel"],
                CancelResult: 2),
            cancellationToken) == 0;

    public async Task ShowErrorAsync(
        string title,
        string message,
        string? correlationId = null,
        CancellationToken cancellationToken = default)
    {
        string detail = correlationId is null ? message : $"{message}\n\nCorrelation ID: {correlationId}";
        _ = await ShowAsync(
            new OverlayPromptRequest(title, detail, text["prompt.ok"]),
            cancellationToken);
    }

    private Task<int> ShowAsync(OverlayPromptRequest request, CancellationToken cancellationToken)
        => _presenter?.ShowPromptAsync(request, cancellationToken)
           ?? Task.FromResult(request.CancelResult);
}
