using Avalonia.Controls;
using Avalonia.Platform.Storage;
using DiscImageStudio.Application.Ports;

namespace DiscImageStudio.App.Avalonia.Platform;

public sealed class AvaloniaFilePickerService(Func<TopLevel?> topLevel) : IFilePickerService
{
    public async Task<IReadOnlyList<string>> OpenFilesAsync(
        OpenFilePickerRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        IStorageProvider provider = RequireProvider();
        IReadOnlyList<IStorageFile> files = await provider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = request.Title,
            AllowMultiple = request.AllowMultiple,
            SuggestedStartLocation = await SuggestedFolderAsync(provider, request.SuggestedDirectory),
            FileTypeFilter = ConvertTypes(request.FileTypes),
        });
        cancellationToken.ThrowIfCancellationRequested();
        return files.Select(StorageProviderExtensions.TryGetLocalPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Cast<string>()
            .ToArray();
    }

    public async Task<string?> SaveFileAsync(
        SaveFilePickerRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        IStorageProvider provider = RequireProvider();
        IStorageFile? file = await provider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = request.Title,
            SuggestedFileName = request.SuggestedFileName,
            SuggestedStartLocation = await SuggestedFolderAsync(provider, request.SuggestedDirectory),
            DefaultExtension = request.DefaultExtension,
            FileTypeChoices = ConvertTypes(request.FileTypes),
            ShowOverwritePrompt = true,
        });
        cancellationToken.ThrowIfCancellationRequested();
        return file?.TryGetLocalPath();
    }

    public async Task<string?> OpenFolderAsync(
        FolderPickerRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        IStorageProvider provider = RequireProvider();
        IReadOnlyList<IStorageFolder> folders = await provider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = request.Title,
            AllowMultiple = false,
            SuggestedStartLocation = await SuggestedFolderAsync(provider, request.SuggestedDirectory),
        });
        cancellationToken.ThrowIfCancellationRequested();
        return folders.FirstOrDefault()?.TryGetLocalPath();
    }

    private IStorageProvider RequireProvider()
    {
        TopLevel owner = topLevel()
            ?? throw new InvalidOperationException("A visible top-level window is required to open a picker.");
        return owner.StorageProvider;
    }

    private static async Task<IStorageFolder?> SuggestedFolderAsync(
        IStorageProvider provider,
        string? suggestedDirectory)
        => !string.IsNullOrWhiteSpace(suggestedDirectory) && Path.IsPathRooted(suggestedDirectory)
            ? await provider.TryGetFolderFromPathAsync(suggestedDirectory)
            : null;

    private static IReadOnlyList<FilePickerFileType>? ConvertTypes(IReadOnlyList<FilePickerType>? types)
        => types?.Select(type => new FilePickerFileType(type.Name)
            {
                Patterns = type.Patterns,
                MimeTypes = type.MimeTypes,
            })
            .ToArray();
}
