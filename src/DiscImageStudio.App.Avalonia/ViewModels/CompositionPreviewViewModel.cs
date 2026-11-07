using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using DiscImageStudio.App.Avalonia.Localization;
using DiscImageStudio.App.Avalonia.Platform;
using DiscImageStudio.Application.Composition;
using DiscImageStudio.Application.Projects;
using DiscImageStudio.Rendering;

namespace DiscImageStudio.App.Avalonia.ViewModels;

/// <summary>
/// Design-page preview of the project's composed layers at 2048 px. Composition and bitmap
/// conversion run off the UI thread; a newer refresh supersedes older ones at both the service
/// channel and here, so a slow composition can never replace a newer preview.
/// </summary>
public sealed partial class CompositionPreviewViewModel : ObservableObject
{
    private const string Channel = "design-preview";

    /// <summary>Side of the view-box the ring overlay is drawn in.</summary>
    public const double OverlaySize = 200;

    private readonly ProjectCompositionService _compositions;
    private readonly ProjectLifecycleService _projects;
    private readonly ITextCatalog _text;
    private long _generation;

    public CompositionPreviewViewModel(
        ProjectCompositionService compositions,
        ProjectLifecycleService projects,
        ITextCatalog text)
    {
        _compositions = compositions ?? throw new ArgumentNullException(nameof(compositions));
        _projects = projects ?? throw new ArgumentNullException(nameof(projects));
        _text = text ?? throw new ArgumentNullException(nameof(text));
        Title = text["design.preview.title"];
        Description = text["design.preview.description"];
        RenderingLabel = text["design.preview.rendering"];
        NoLayersTitle = text["design.preview.noLayers.title"];
        NoLayersDescription = text["design.preview.noLayers.description"];
        Issues.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasIssues));
    }

    public string Title { get; }

    public string Description { get; }

    public string RenderingLabel { get; }

    public string NoLayersTitle { get; }

    public string NoLayersDescription { get; }

    public ObservableCollection<string> Issues { get; } = [];

    public bool HasIssues => Issues.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImage))]
    public partial IImage? Image { get; private set; }

    public bool HasImage => Image is not null;

    [ObservableProperty]
    public partial bool IsRendering { get; private set; }

    [ObservableProperty]
    public partial bool HasNoLayers { get; private set; }

    [ObservableProperty]
    public partial string Summary { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string? Error { get; private set; }

    /// <summary>Diameter and offset of the inner (hub) ring inside the overlay view-box.</summary>
    [ObservableProperty]
    public partial double InnerRingDiameter { get; private set; }

    [ObservableProperty]
    public partial double InnerRingOffset { get; private set; }

    public async Task RefreshAsync()
    {
        long generation = ++_generation;
        ProjectWorkspace? workspace = _projects.Current;
        string? projectPath = _projects.ProjectFilePath;
        if (workspace is null)
        {
            Clear();
            return;
        }

        HasNoLayers = workspace.Design.Layers.Count == 0;
        if (HasNoLayers)
        {
            Image = null;
            Issues.Clear();
            Summary = string.Empty;
            Error = null;
            return;
        }

        IsRendering = true;
        try
        {
            ProjectComposition? composition = await _compositions.ComposeLatestAsync(
                Channel,
                workspace,
                projectPath,
                ProjectCompositionService.PreviewSize);
            if (composition is null || generation != _generation)
            {
                return;
            }

            IImage bitmap = await Task.Run(() => CompositionBitmap.ToBitmap(composition.Image));
            if (generation != _generation)
            {
                return;
            }

            Image = bitmap;
            Error = null;
            ApplyDetails(workspace, composition);
        }
        catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or InvalidDataException
                                              or ArgumentException
                                              or InvalidOperationException)
        {
            if (generation == _generation)
            {
                Error = _text["design.preview.error"];
            }
        }
        finally
        {
            if (generation == _generation)
            {
                IsRendering = false;
            }
        }
    }

    public void Clear()
    {
        ++_generation;
        Image = null;
        Issues.Clear();
        Summary = string.Empty;
        Error = null;
        HasNoLayers = false;
        IsRendering = false;
    }

    private void ApplyDetails(ProjectWorkspace workspace, ProjectComposition composition)
    {
        int total = workspace.Design.Layers.Count(layer => layer.Visible);
        Summary = string.Format(
            CultureInfo.CurrentUICulture,
            _text["design.preview.summary"],
            composition.PaintedLayerCount,
            total,
            composition.Size);
        double ratio = Math.Clamp(composition.ContentInnerRadiusMm / composition.CanvasOuterRadiusMm, 0, 1);
        InnerRingDiameter = OverlaySize * ratio;
        InnerRingOffset = (OverlaySize - InnerRingDiameter) / 2;

        Issues.Clear();
        foreach (CompositionIssue issue in composition.Issues)
        {
            string layerName = workspace.Design.Layers.FirstOrDefault(layer => layer.Id == issue.LayerId)?.Name ?? issue.LayerId;
            string key = issue.Kind switch
            {
                CompositionIssueKind.ImageMissing => "design.preview.issue.missing",
                CompositionIssueKind.ImageTooLarge => "design.preview.issue.tooLarge",
                _ => "design.preview.issue.unreadable",
            };
            Issues.Add(string.Format(CultureInfo.CurrentUICulture, _text[key], layerName));
        }
    }
}
