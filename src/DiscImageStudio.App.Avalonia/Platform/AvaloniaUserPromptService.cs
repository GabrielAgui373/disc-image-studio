using Avalonia.Controls;
using Avalonia.Layout;
using DiscImageStudio.App.Avalonia.Localization;
using DiscImageStudio.Application.Ports;

namespace DiscImageStudio.App.Avalonia.Platform;

public sealed class AvaloniaUserPromptService(Func<Window?> owner, ITextCatalog text) : IUserPromptService
{
    public async Task<UnsavedChangesDecision> ConfirmUnsavedChangesAsync(
        UnsavedChangesRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Window? dialogOwner = owner();
        if (dialogOwner is null)
        {
            return UnsavedChangesDecision.Cancel;
        }

        return await ShowChoiceAsync(
            dialogOwner,
            text["prompt.unsaved.title"],
            $"{text["prompt.unsaved.message"]} ({request.ProjectName})",
            new DialogChoice<UnsavedChangesDecision>(text["prompt.save"], UnsavedChangesDecision.Save),
            new DialogChoice<UnsavedChangesDecision>(text["prompt.discard"], UnsavedChangesDecision.Discard),
            new DialogChoice<UnsavedChangesDecision>(text["prompt.cancel"], UnsavedChangesDecision.Cancel),
            UnsavedChangesDecision.Cancel,
            cancellationToken);
    }

    public async Task<bool> ConfirmShutdownAsync(
        int activeJobCount,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Window? dialogOwner = owner();
        if (dialogOwner is null)
        {
            return false;
        }

        string message = $"{text["prompt.shutdown.message"]} Active jobs: {activeJobCount}.";
        return await ShowChoiceAsync(
            dialogOwner,
            text["prompt.shutdown.title"],
            message,
            new DialogChoice<bool>(text["prompt.continue"], true),
            new DialogChoice<bool>(text["prompt.cancel"], false),
            fallback: null,
            canceledValue: false,
            cancellationToken);
    }

    public async Task ShowErrorAsync(
        string title,
        string message,
        string? correlationId = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Window? dialogOwner = owner();
        if (dialogOwner is null)
        {
            return;
        }

        string detail = correlationId is null ? message : $"{message}\n\nCorrelation ID: {correlationId}";
        _ = await ShowChoiceAsync(
            dialogOwner,
            title,
            detail,
            new DialogChoice<bool>(text["prompt.ok"], true),
            secondary: null,
            fallback: null,
            canceledValue: true,
            cancellationToken);
    }

    private static async Task<T> ShowChoiceAsync<T>(
        Window dialogOwner,
        string title,
        string message,
        DialogChoice<T> primary,
        DialogChoice<T>? secondary,
        DialogChoice<T>? fallback,
        T canceledValue,
        CancellationToken cancellationToken)
    {
        Window dialog = new()
        {
            Title = title,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };

        StackPanel buttons = new()
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
        };

        AddButton(primary);
        if (secondary is not null)
        {
            AddButton(secondary);
        }

        if (fallback is not null)
        {
            AddButton(fallback);
        }

        dialog.Content = new StackPanel
        {
            Margin = new global::Avalonia.Thickness(24),
            Spacing = 20,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = global::Avalonia.Media.TextWrapping.Wrap },
                buttons,
            },
        };

        using CancellationTokenRegistration registration = cancellationToken.Register(dialog.Close);
        DialogResult<T>? result = await dialog.ShowDialog<DialogResult<T>?>(dialogOwner);
        return result is null ? canceledValue : result.Value;

        void AddButton(DialogChoice<T> choice)
        {
            Button button = new() { Content = choice.Label, MinWidth = 88 };
            button.Click += (_, _) => dialog.Close(new DialogResult<T>(choice.Value));
            buttons.Children.Add(button);
        }
    }

    private sealed record DialogChoice<T>(string Label, T Value);

    private sealed record DialogResult<T>(T Value);
}
