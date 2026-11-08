using DiscImageStudio.Core;

namespace DiscImageStudio.Application.Projects;

/// <summary>Maps the existing on-disk schema to the portable application state.</summary>
public static class ProjectDocumentMapper
{
    public static ProjectWorkspace ToWorkspace(ProjectDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        ProjectJsonStore.Validate(document);

        ProjectGeometry geometry = document.Geometry;
        ProjectOutputs outputs = document.Outputs;
        MediaState media = document.Family switch
        {
            OpticalDiscFamily.Dvd => new DvdState(
                geometry.InnerRadiusMm,
                geometry.OuterRadiusMm,
                geometry.Sectors,
                geometry.PitchLinear,
                geometry.PitchQuadratic,
                geometry.PitchCubic),
            OpticalDiscFamily.BluRay => new BluRayState(
                geometry.InnerRadiusMm,
                geometry.OuterRadiusMm,
                geometry.Sectors,
                document.BluRay?.InvertMapping ?? false),
            _ => new CdState(
                geometry.InnerRadiusMm,
                geometry.OuterRadiusMm,
                geometry.Sectors,
                outputs.WriteCue,
                outputs.Interleave),
        };

        return new ProjectWorkspace(
            new ProjectIdentity(
                document.Name,
                document.CreatedUtc,
                document.ModifiedUtc,
                document.CalibrationSessionPath,
                document.ThumbnailPngBase64),
            new DesignState(document.Layers.Select(ToLayerState)),
            new ToneState(document.Tone ?? ToneProfile.LegacyBinary),
            new DeliveryState(
                outputs.OutputPath,
                outputs.DataDirectory,
                outputs.VolumeLabel,
                outputs.FilesAtOuterRing),
            media,
            document.Measurement is { } measurement
                ? new CalibrationMeasurement(
                    measurement.InnerRadiusMm,
                    measurement.OuterRadiusMm,
                    measurement.Sectors,
                    measurement.PitchLinear,
                    measurement.PitchQuadratic,
                    measurement.PitchCubic,
                    measurement.TargetId,
                    measurement.MeasuredUtc,
                    measurement.RmsErrorMm,
                    measurement.MaxErrorMm)
                : null);
    }

    public static ProjectDocument ToDocument(ProjectWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        MediaState media = workspace.Media;
        ProjectGeometry geometry = media switch
        {
            DvdState dvd => new ProjectGeometry(
                dvd.InnerRadiusMm,
                dvd.OuterRadiusMm,
                dvd.Sectors,
                dvd.PitchLinear,
                dvd.PitchQuadratic,
                dvd.PitchCubic),
            _ => new ProjectGeometry(media.InnerRadiusMm, media.OuterRadiusMm, media.Sectors),
        };
        ProjectOutputs outputs = new(
            workspace.Delivery.OutputPath,
            WriteCue: media is CdState { WriteCue: true },
            workspace.Delivery.DataDirectory,
            workspace.Delivery.VolumeLabel,
            Interleave: media is CdState { Interleave: true },
            workspace.Delivery.FilesAtOuterRing);

        ProjectDocument document = new(
            ProjectJsonStore.CurrentSchemaVersion,
            workspace.Identity.Name,
            FamilyToken(media.Family),
            geometry,
            workspace.Design.Layers.Select(ToProjectLayer).ToArray(),
            outputs,
            workspace.Identity.CalibrationSessionPath,
            workspace.Identity.ThumbnailPngBase64,
            workspace.Identity.CreatedUtc,
            workspace.Identity.ModifiedUtc,
            workspace.Tone.Profile,
            media is BluRayState bluRay
                ? new ProjectBluRaySettings(bluRay.InvertMapping)
                : null,
            workspace.Measurement is { } measurement
                ? new ProjectMeasurement(
                    measurement.InnerRadiusMm,
                    measurement.OuterRadiusMm,
                    measurement.Sectors,
                    measurement.PitchLinear,
                    measurement.PitchQuadratic,
                    measurement.PitchCubic,
                    measurement.TargetId,
                    measurement.MeasuredUtc,
                    measurement.RmsErrorMm,
                    measurement.MaxErrorMm)
                : null);
        document = document with { SchemaVersion = ProjectJsonStore.RequiredSchemaVersion(document) };
        ProjectJsonStore.Validate(document);
        return document;
    }

    private static DesignLayerState ToLayerState(ProjectLayer layer)
        => new(
            layer.Id,
            layer.Name,
            layer.ImagePath,
            layer.Visible,
            layer.TranslateXMm,
            layer.TranslateYMm,
            layer.RotationDeg,
            layer.Scale,
            layer.Opacity ?? 1.0);

    private static ProjectLayer ToProjectLayer(DesignLayerState layer)
        => new(
            layer.Id,
            layer.Name,
            layer.ImagePath,
            layer.Visible,
            layer.TranslateXMm,
            layer.TranslateYMm,
            layer.RotationDeg,
            layer.Scale,
            // Full opacity is the default and is not written (keeps schema 2 files).
            layer.Opacity < 1 ? layer.Opacity : null);

    private static string FamilyToken(OpticalDiscFamily family)
        => family switch
        {
            OpticalDiscFamily.Dvd => ProjectDocument.DvdFamily,
            OpticalDiscFamily.BluRay => ProjectDocument.BluRayFamily,
            _ => ProjectDocument.CdFamily,
        };
}
