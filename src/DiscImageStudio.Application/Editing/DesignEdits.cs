using DiscImageStudio.Application.Projects;
using DiscImageStudio.Core;

namespace DiscImageStudio.Application.Editing;

/// <summary>An image to add as a layer; it starts centred, unrotated, at scale 1 and visible.</summary>
public sealed record NewLayer(string Name, string ImagePath);

/// <summary>The placement of a layer: millimetres from the disc centre, clockwise degrees, scale.</summary>
public readonly record struct LayerTransform(double TranslateXMm, double TranslateYMm, double RotationDeg, double Scale)
{
    public static LayerTransform Of(DesignLayerState layer)
        => new(layer.TranslateXMm, layer.TranslateYMm, layer.RotationDeg, layer.Scale);
}

/// <summary>
/// Pure edits of a <see cref="ProjectWorkspace"/>. Each returns a new workspace and leaves the
/// input untouched, which is what makes snapshot history cheap: unchanged layers are shared.
/// Layer indexes are document indexes, bottom-first (index 0 is painted first); lists that show
/// the top layer first must convert. Invalid input throws <see cref="ArgumentException"/>.
/// </summary>
public static class DesignEdits
{
    /// <summary>Generates a stable layer id, in the same format the WPF host uses.</summary>
    public static string NewLayerId() => Guid.NewGuid().ToString("N");

    /// <summary>Adds layers above the current top layer, in the given order (last ends on top).</summary>
    public static ProjectWorkspace AddLayers(
        ProjectWorkspace workspace,
        IReadOnlyList<NewLayer> layers,
        out IReadOnlyList<string> addedIds)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(layers);
        if (layers.Count == 0)
        {
            throw new ArgumentException("At least one layer is required.", nameof(layers));
        }

        List<string> ids = [];
        List<DesignLayerState> result = [.. workspace.Design.Layers];
        foreach (NewLayer layer in layers)
        {
            string id = NewLayerId();
            ids.Add(id);
            result.Add(new DesignLayerState(id, RequireName(layer.Name), layer.ImagePath, true, 0, 0, 0, 1));
        }

        addedIds = ids;
        return WithLayers(workspace, result);
    }

    public static ProjectWorkspace RemoveLayers(ProjectWorkspace workspace, IReadOnlyCollection<string> layerIds)
    {
        ArgumentNullException.ThrowIfNull(layerIds);
        HashSet<string> remove = new(layerIds, StringComparer.Ordinal);
        if (!workspace.Design.Layers.Any(layer => remove.Contains(layer.Id)))
        {
            throw new ArgumentException("None of the layers exist.", nameof(layerIds));
        }

        return WithLayers(workspace, workspace.Design.Layers.Where(layer => !remove.Contains(layer.Id)));
    }

    /// <summary>Moves a layer to <paramref name="documentIndex"/> (bottom-first, clamped).</summary>
    public static ProjectWorkspace MoveLayer(ProjectWorkspace workspace, string layerId, int documentIndex)
    {
        List<DesignLayerState> layers = [.. workspace.Design.Layers];
        int from = IndexOf(layers, layerId);
        DesignLayerState layer = layers[from];
        layers.RemoveAt(from);
        layers.Insert(Math.Clamp(documentIndex, 0, layers.Count), layer);
        return WithLayers(workspace, layers);
    }

    public static ProjectWorkspace RenameLayer(ProjectWorkspace workspace, string layerId, string name)
        => Update(workspace, layerId, layer => Rebuild(layer, name: RequireName(name)));

    /// <summary>Copies a layer directly above the original with a new id.</summary>
    public static ProjectWorkspace DuplicateLayer(
        ProjectWorkspace workspace,
        string layerId,
        string copyName,
        out string newId)
    {
        List<DesignLayerState> layers = [.. workspace.Design.Layers];
        int index = IndexOf(layers, layerId);
        string id = NewLayerId();
        layers.Insert(index + 1, Rebuild(layers[index], id: id, name: RequireName(copyName)));
        newId = id;
        return WithLayers(workspace, layers);
    }

    public static ProjectWorkspace SetVisibility(ProjectWorkspace workspace, string layerId, bool visible)
        => Update(workspace, layerId, layer => Rebuild(layer, visible: visible));

    public static ProjectWorkspace SetTransform(ProjectWorkspace workspace, string layerId, LayerTransform transform)
        => Update(workspace, layerId, layer => Rebuild(layer, transform: transform));

    public static ProjectWorkspace SetOpacity(ProjectWorkspace workspace, string layerId, double opacity)
        => Update(workspace, layerId, layer => Rebuild(layer, opacity: opacity));

    /// <summary>Replaces the tone; four levels are only valid for CD projects.</summary>
    public static ProjectWorkspace SetTone(ProjectWorkspace workspace, ToneProfile tone)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (tone.Levels == ToneProfile.FourLevels && workspace.Media is not CdState)
        {
            throw new ArgumentException("Four tone levels are only available for CD projects.", nameof(tone));
        }

        return workspace with { Tone = new ToneState(tone) };
    }

    /// <summary>Sets or clears (null/blank) the folder whose files are written to the disc.</summary>
    public static ProjectWorkspace SetDataDirectory(ProjectWorkspace workspace, string? dataDirectory)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        DeliveryState delivery = workspace.Delivery;
        return workspace with
        {
            Delivery = new DeliveryState(
                delivery.OutputPath,
                string.IsNullOrWhiteSpace(dataDirectory) ? string.Empty : dataDirectory.Trim(),
                delivery.VolumeLabel,
                delivery.FilesAtOuterRing),
        };
    }

    /// <summary>Sets the file generation writes, which Preview also reads back.</summary>
    public static ProjectWorkspace SetOutputPath(ProjectWorkspace workspace, string outputPath)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        DeliveryState delivery = workspace.Delivery;
        return workspace with
        {
            Delivery = new DeliveryState(
                outputPath.Trim(),
                delivery.DataDirectory,
                delivery.VolumeLabel,
                delivery.FilesAtOuterRing),
        };
    }

    public static int IndexOf(IReadOnlyList<DesignLayerState> layers, string layerId)
    {
        for (int index = 0; index < layers.Count; index++)
        {
            if (string.Equals(layers[index].Id, layerId, StringComparison.Ordinal))
            {
                return index;
            }
        }

        throw new ArgumentException($"Layer '{layerId}' does not exist.", nameof(layerId));
    }

    private static ProjectWorkspace Update(
        ProjectWorkspace workspace,
        string layerId,
        Func<DesignLayerState, DesignLayerState> change)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        List<DesignLayerState> layers = [.. workspace.Design.Layers];
        int index = IndexOf(layers, layerId);
        layers[index] = change(layers[index]);
        return WithLayers(workspace, layers);
    }

    private static ProjectWorkspace WithLayers(ProjectWorkspace workspace, IEnumerable<DesignLayerState> layers)
        => workspace with { Design = new DesignState(layers) };

    private static DesignLayerState Rebuild(
        DesignLayerState layer,
        string? id = null,
        string? name = null,
        bool? visible = null,
        LayerTransform? transform = null,
        double? opacity = null)
    {
        LayerTransform placement = transform ?? LayerTransform.Of(layer);
        return new DesignLayerState(
            id ?? layer.Id,
            name ?? layer.Name,
            layer.ImagePath,
            visible ?? layer.Visible,
            placement.TranslateXMm,
            placement.TranslateYMm,
            placement.RotationDeg,
            placement.Scale,
            opacity ?? layer.Opacity);
    }

    private static string RequireName(string? name)
        => string.IsNullOrWhiteSpace(name)
            ? throw new ArgumentException("A layer name is required.", nameof(name))
            : name.Trim();
}
