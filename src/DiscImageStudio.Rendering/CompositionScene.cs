using System.Collections.ObjectModel;

namespace DiscImageStudio.Rendering;

/// <summary>
/// One layer of a composition. Placement is physical: millimetres from the disc centre
/// (x to the right, y downwards) and a clockwise rotation in degrees about the layer centre.
/// The layer's size at <see cref="Scale"/> 1 is derived from its image aspect ratio; see
/// <see cref="CompositionScene.ContentOuterRadiusMm"/>.
/// </summary>
public sealed record CompositionLayer
{
    public CompositionLayer(
        string id,
        string imagePath,
        double translateXMm,
        double translateYMm,
        double rotationDeg,
        double scale,
        bool visible = true,
        double opacity = 1.0)
    {
        Id = string.IsNullOrWhiteSpace(id) ? throw new ArgumentException("A layer id is required.", nameof(id)) : id;
        ImagePath = string.IsNullOrWhiteSpace(imagePath)
            ? throw new ArgumentException("A layer image path is required.", nameof(imagePath))
            : imagePath;
        TranslateXMm = Finite(translateXMm, nameof(translateXMm));
        TranslateYMm = Finite(translateYMm, nameof(translateYMm));
        RotationDeg = Finite(rotationDeg, nameof(rotationDeg));
        Scale = double.IsFinite(scale) && scale > 0
            ? scale
            : throw new ArgumentOutOfRangeException(nameof(scale), "Scale must be positive and finite.");
        Opacity = double.IsFinite(opacity) && opacity is >= 0 and <= 1
            ? opacity
            : throw new ArgumentOutOfRangeException(nameof(opacity), "Opacity must be between 0 and 1.");
        Visible = visible;
    }

    public string Id { get; }

    public string ImagePath { get; }

    public double TranslateXMm { get; }

    public double TranslateYMm { get; }

    public double RotationDeg { get; }

    public double Scale { get; }

    public bool Visible { get; }

    /// <summary>Multiplier from 0 to 1. Project files do not persist opacity yet, so hosts pass 1.</summary>
    public double Opacity { get; }

    private static double Finite(double value, string name)
        => double.IsFinite(value) ? value : throw new ArgumentOutOfRangeException(name, "The value must be finite.");
}

/// <summary>
/// An immutable layer stack painted bottom-first onto a transparent square canvas whose
/// centre is the disc centre and whose side spans 2 × <see cref="CanvasOuterRadiusMm"/>.
/// </summary>
public sealed record CompositionScene
{
    public CompositionScene(
        double canvasOuterRadiusMm,
        double contentOuterRadiusMm,
        IEnumerable<CompositionLayer> layers)
    {
        CanvasOuterRadiusMm = Positive(canvasOuterRadiusMm, nameof(canvasOuterRadiusMm));
        ContentOuterRadiusMm = Positive(contentOuterRadiusMm, nameof(contentOuterRadiusMm));
        CompositionLayer[] copy = layers?.ToArray() ?? throw new ArgumentNullException(nameof(layers));
        if (copy.Any(layer => layer is null))
        {
            throw new ArgumentException("Layers cannot contain null entries.", nameof(layers));
        }

        Layers = new ReadOnlyCollection<CompositionLayer>(copy);
    }

    /// <summary>Half the physical side of the canvas: the radius the generated image spans.</summary>
    public double CanvasOuterRadiusMm { get; }

    /// <summary>At scale 1 the larger image dimension spans 2 × this radius (the WPF fit rule).</summary>
    public double ContentOuterRadiusMm { get; }

    /// <summary>Bottom-first paint order, matching <c>DesignState.Layers</c>.</summary>
    public IReadOnlyList<CompositionLayer> Layers { get; }

    private static double Positive(double value, string name)
        => double.IsFinite(value) && value > 0
            ? value
            : throw new ArgumentOutOfRangeException(name, "The radius must be positive and finite.");
}
