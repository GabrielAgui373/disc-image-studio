using DiscImageStudio.Core.Calibration;
using DiscImageStudio.ImageIO;

namespace DiscImageStudio.Application.Calibration;

public enum CalibrationEdge
{
    /// <summary>The physical outer edge of the disc.</summary>
    Outer,

    /// <summary>The physical edge of the centre hole.</summary>
    Hole,
}

/// <summary>
/// The state of step 2 (Task 12.3): the photo, the handles on the outer edge and on the hole,
/// the physical disc size, the fitted rectification and whether the user confirmed it. The
/// rules are the WPF wizard's: any change to the photo, the handles or the disc size drops the
/// rectification, its confirmation and the traced curve points, because they all depend on it.
/// Orientation edits (mirror, quarter turns) change the photo itself, so the session format
/// stays the one the WPF host reads.
/// </summary>
public sealed class CalibrationPhotoStep
{
    /// <summary>Radii of the two disc sizes the session accepts.</summary>
    public static IReadOnlyList<double> DiscRadiiMm { get; } = [60, 40];

    private CalibrationPoint[] _outer = [];
    private CalibrationPoint[] _hole = [];
    private CalibrationCrossTrace[] _traces = [];
    private (BgraRaster Raster, string Png)? _png;
    private bool _pendingConfirmation;

    public CalibrationPhoto? Photo { get; private set; }

    public IReadOnlyList<CalibrationPoint> Outer => _outer;

    public IReadOnlyList<CalibrationPoint> Hole => _hole;

    public double PhysicalOuterRadiusMm { get; private set; } = 60;

    public CalibrationRectification? Rectification { get; private set; }

    /// <summary>The user checked the rectified view and moved on to tracing.</summary>
    public bool Confirmed { get; private set; }

    /// <summary>Curve points from step 3 (or a restored session); dropped with the rectification.</summary>
    public IReadOnlyList<CalibrationCrossTrace> Traces => _traces;

    public int TracePointCount => _traces.Sum(trace => trace.Points.Count);

    /// <summary>Bumped by every change; a rectification computed for an older revision is stale.</summary>
    public long Revision { get; private set; }

    public bool HasPhoto => Photo is not null;

    public bool CanRectify => Photo is not null && _outer.Length >= 5 && _hole.Length >= 5;

    /// <summary>Raised after every change.</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<CalibrationPoint> Points(CalibrationEdge edge) => edge == CalibrationEdge.Outer ? _outer : _hole;

    public void Clear()
    {
        Photo = null;
        _outer = [];
        _hole = [];
        _traces = [];
        _png = null;
        PhysicalOuterRadiusMm = 60;
        Rectification = null;
        Confirmed = false;
        _pendingConfirmation = false;
        Touch();
    }

    /// <summary>A new photo with the suggested handles; everything measured before is dropped.</summary>
    public void Import(CalibrationPhoto photo, int handlesPerEdge = 8)
    {
        ArgumentNullException.ThrowIfNull(photo);
        CalibrationPhotoContours suggested = CalibrationPhotoLoader.SuggestContours(photo, handlesPerEdge);
        Photo = photo;
        _outer = [.. suggested.Outer];
        _hole = [.. suggested.Hole];
        _traces = [];
        Rectification = null;
        Confirmed = false;
        _pendingConfirmation = false;
        Touch();
    }

    /// <summary>
    /// Restores a saved photo session. The rectification is not stored, so a confirmed session
    /// needs <see cref="ApplyRectification"/> before it counts as confirmed again.
    /// </summary>
    /// <param name="decoded">The session's photo when the caller already decoded it off the UI thread.</param>
    public void Restore(CalibrationPhotoSession session, CalibrationPhoto? decoded = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        Photo = decoded ?? CalibrationPhotoLoader.FromSession(session);
        _png = (Photo.Raster, session.ImagePngBase64);
        _outer = [.. session.OuterBoundary];
        _hole = [.. session.HoleBoundary];
        _traces = [.. session.Traces];
        PhysicalOuterRadiusMm = session.PhysicalOuterRadiusMm;
        Rectification = null;
        Confirmed = false;
        _pendingConfirmation = session.RectificationConfirmed;
        Touch();
    }

    /// <summary>True after <see cref="Restore"/> of a session whose rectification was confirmed.</summary>
    public bool RestoredAsConfirmed => _pendingConfirmation;

    /// <summary>Moves one handle, clamped to the photo.</summary>
    public bool MoveHandle(CalibrationEdge edge, int index, CalibrationPoint position)
    {
        ArgumentNullException.ThrowIfNull(position);
        CalibrationPoint[] points = edge == CalibrationEdge.Outer ? _outer : _hole;
        if (Photo is not { } photo || (uint)index >= (uint)points.Length || !double.IsFinite(position.X) || !double.IsFinite(position.Y))
        {
            return false;
        }

        CalibrationPoint clamped = new(Math.Clamp(position.X, 0, photo.Width - 1), Math.Clamp(position.Y, 0, photo.Height - 1));
        if (points[index] == clamped)
        {
            return false;
        }

        CalibrationPoint[] copy = [.. points];
        copy[index] = clamped;
        if (edge == CalibrationEdge.Outer)
        {
            _outer = copy;
        }
        else
        {
            _hole = copy;
        }

        Invalidate();
        return true;
    }

    public bool SetDiscRadius(double radiusMm)
    {
        if (!DiscRadiiMm.Contains(radiusMm))
        {
            throw new ArgumentOutOfRangeException(nameof(radiusMm), "A disc is 12 cm (60 mm) or 8 cm (40 mm).");
        }

        if (PhysicalOuterRadiusMm == radiusMm)
        {
            return false;
        }

        PhysicalOuterRadiusMm = radiusMm;
        Invalidate();
        return true;
    }

    /// <summary>Mirrors the photo left ↔ right (a photo taken through the other side of the disc).</summary>
    public void MirrorHorizontally()
    {
        if (Photo is not { } photo)
        {
            return;
        }

        int width = photo.Width;
        int height = photo.Height;
        byte[] source = photo.Raster.Pixels;
        byte[] pixels = new byte[source.Length];
        for (int y = 0; y < height; y++)
        {
            int row = y * width * 4;
            for (int x = 0; x < width; x++)
            {
                Buffer.BlockCopy(source, row + (x * 4), pixels, row + ((width - 1 - x) * 4), 4);
            }
        }

        Reorient(
            new CalibrationPhoto(new BgraRaster(width, height, pixels), photo.OriginalWidth, photo.OriginalHeight, photo.FromSession),
            point => new CalibrationPoint(width - 1 - point.X, point.Y));
    }

    /// <summary>Turns the photo a quarter turn clockwise.</summary>
    public void RotateClockwise()
    {
        if (Photo is not { } photo)
        {
            return;
        }

        int width = photo.Width;
        int height = photo.Height;
        byte[] source = photo.Raster.Pixels;
        byte[] pixels = new byte[source.Length];
        // (x, y) → (height − 1 − y, x) in a height × width image.
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                Buffer.BlockCopy(source, ((y * width) + x) * 4, pixels, ((x * height) + (height - 1 - y)) * 4, 4);
            }
        }

        Reorient(
            new CalibrationPhoto(new BgraRaster(height, width, pixels), photo.OriginalHeight, photo.OriginalWidth, photo.FromSession),
            point => new CalibrationPoint(height - 1 - point.Y, point.X));
    }

    /// <summary>Keeps a rectification only if it was computed for the current revision.</summary>
    public bool ApplyRectification(CalibrationRectification rectification, long revision)
    {
        ArgumentNullException.ThrowIfNull(rectification);
        if (revision != Revision || Photo is null)
        {
            return false;
        }

        Rectification = rectification;
        if (_pendingConfirmation)
        {
            // A restored, confirmed session is confirmed again once its rectification is back.
            Confirmed = true;
            _pendingConfirmation = false;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool Confirm()
    {
        if (Rectification is null || Confirmed)
        {
            return false;
        }

        Confirmed = true;
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Back to editing the edges; the rectification stays until something changes.</summary>
    public void Reopen()
    {
        if (Confirmed)
        {
            Confirmed = false;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>The photo part of the session file; <see langword="null"/> without a photo.</summary>
    public CalibrationPhotoSession? ToSession()
    {
        if (Photo is not { } photo)
        {
            return null;
        }

        if (_png is not { } cached || !ReferenceEquals(cached.Raster, photo.Raster))
        {
            _png = (photo.Raster, photo.ToSessionPng());
        }

        return new CalibrationPhotoSession(
            _png.Value.Png,
            _outer,
            _hole,
            PhysicalOuterRadiusMm,
            Confirmed || (_pendingConfirmation && _traces.Length > 0),
            Confirmed || _pendingConfirmation ? _traces : []);
    }

    private void Reorient(CalibrationPhoto photo, Func<CalibrationPoint, CalibrationPoint> map)
    {
        Photo = photo;
        _outer = [.. _outer.Select(map)];
        _hole = [.. _hole.Select(map)];
        Invalidate();
    }

    private void Invalidate()
    {
        Rectification = null;
        Confirmed = false;
        _pendingConfirmation = false;
        _traces = [];
        Touch();
    }

    private void Touch()
    {
        Revision++;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
