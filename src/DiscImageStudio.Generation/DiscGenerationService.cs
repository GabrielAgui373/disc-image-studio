using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using DiscImageStudio.BluRay;
using DiscImageStudio.Cd;
using DiscImageStudio.Core;
using DiscImageStudio.Dvd;
using DiscImageStudio.ImageIO;

namespace DiscImageStudio.Generation;

public sealed partial class DiscGenerationService : IDiscGenerationService
{
    private const long MaximumSourceBytes = 256L * 1024 * 1024;
    private const long MaximumSourcePixels = 64L * 1024 * 1024;
    private readonly Func<DateTimeOffset> _utcNow;

    public DiscGenerationService(Func<DateTimeOffset>? utcNow = null)
        => _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);

    public Task<GenerationPlan> PlanAsync(
        GenerateDiscRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Task.Run(() => Plan(request, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// Generates and publishes <paramref name="outputPath"/> with its sidecars, replacing an
    /// existing output as this method always did. Use <see cref="PublishFileAsync"/> to choose the
    /// replace policy and get the published files back.
    /// </summary>
    public Task GenerateFileAsync(
        GenerationPlan plan,
        string outputPath,
        IProgress<GenerationProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => PublishFileAsync(
            plan,
            outputPath,
            new ArtifactPublishOptions(ArtifactReplacePolicy.Replace),
            progress,
            cancellationToken);

    public Task GenerateStreamAsync(
        GenerationPlan plan,
        Stream output,
        IProgress<GenerationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(output);
        return Task.Run(() => GenerateStream(plan, output, progress, cancellationToken), cancellationToken);
    }

    private GenerationPlan Plan(GenerateDiscRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // A calibration target is drawn from its session's pattern; there is no source image.
        if (request is not GenerateCalibrationTargetRequest)
        {
            ValidateSource(request.SourceImagePath);
        }

        try
        {
            request.Tone.Validate("tone");
            cancellationToken.ThrowIfCancellationRequested();
            GenerationPlan plan = request switch
            {
                GenerateCdRequest cd => PlanCd(cd),
                GenerateDvdRequest dvd => PlanDvd(dvd),
                GenerateBluRayRequest bluRay => PlanBluRay(bluRay),
                GenerateCalibrationTargetRequest calibration => PlanCalibrationTarget(calibration),
                _ => throw Validation(
                    "generation.request.unsupported",
                    nameof(request),
                    ("requestType", request.GetType().FullName)),
            };
            cancellationToken.ThrowIfCancellationRequested();
            return plan;
        }
        catch (GenerationValidationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or IOException)
        {
            throw Validation(
                "generation.plan.invalid",
                "request",
                exception,
                ("reason", exception.Message));
        }
    }

    private GenerationPlan PlanCd(GenerateCdRequest request)
    {
        ArgumentNullException.ThrowIfNull(request.Parameters);
        request.Parameters.Validate();
        bool hasData = !string.IsNullOrWhiteSpace(request.DataDirectory);
        if (hasData != (request.Format == CdArtifactFormat.Mode1Iso))
        {
            throw Validation(
                "generation.cd.format_data_mismatch",
                nameof(request.Format),
                ("format", request.Format),
                ("hasDataDirectory", hasData));
        }

        ValidateDataDirectory(request.DataDirectory, request.FilePlacement);
        if (request.Format != CdArtifactFormat.Mode1Iso)
        {
            long header = request.Format == CdArtifactFormat.WaveAudio ? 44 : 0;
            return new GenerationPlan(
                request,
                request.Family,
                GenerationFilesystem.CdDa,
                new GenerationRange(0, request.Parameters.Sectors),
                checked(request.Parameters.TotalBytes + header),
                0,
                0,
                GenerationCapability.SequentialStream
                    | GenerationCapability.MetadataSidecar
                    | (request.Format == CdArtifactFormat.WaveAudio && request.WriteCue
                        ? GenerationCapability.CueSheet
                        : GenerationCapability.None),
                request.Format == CdArtifactFormat.WaveAudio && request.WriteCue
                    ? [new("metadata", ".json", true), new("cue", ".cue", true)]
                    : [new("metadata", ".json", true)],
                $"CD-DA: {request.Parameters.Sectors} sectors, {request.Parameters.TotalBytes} audio bytes",
                new CdGenerationPlanDetails(request, null));
        }

        CdDataLayoutPlan layout = CdDataImageGenerator.PrepareLayout(
            request.DataDirectory!,
            request.Parameters,
            RequireVolumeLabel(request.VolumeLabel, "volumeLabel"),
            request.FilePlacement == FilePlacement.OuterRing,
            _utcNow());
        return new GenerationPlan(
            request,
            request.Family,
            GenerationFilesystem.Iso9660Joliet,
            new GenerationRange(layout.DrawingStartLba, layout.DrawingEndLbaExclusive),
            layout.ContentLength,
            layout.FileCount,
            layout.FileBytes,
            GenerationCapability.SequentialStream
                | GenerationCapability.Filesystem
                | GenerationCapability.MetadataSidecar,
            [new("metadata", ".json", true)],
            $"CD Mode 1 ISO9660/Joliet: {layout.FileCount} files, drawing LBA {layout.DrawingStartLba}..{layout.DrawingEndLbaExclusive - 1}, {layout.ContentLength} bytes",
            new CdGenerationPlanDetails(request, layout));
    }

    private GenerationPlan PlanDvd(GenerateDvdRequest request)
    {
        ArgumentNullException.ThrowIfNull(request.Options);
        request.Options.Validate();
        if (!request.Tone.IsDvdCompatible)
        {
            throw Validation("generation.tone.binary_required", "tone.levels", ("family", "dvd"));
        }

        ValidateDataDirectory(request.DataDirectory, request.FilePlacement);
        DvdHybridStreamingPlan? layout = string.IsNullOrWhiteSpace(request.DataDirectory)
            ? null
            : DvdStreamingGenerator.PrepareHybrid(
                request.DataDirectory,
                request.Options,
                RequireVolumeLabel(request.VolumeLabel, "volumeLabel"),
                request.FilePlacement == FilePlacement.OuterRing,
                _utcNow());
        GenerationRange range = layout is null
            ? new GenerationRange(0, request.Options.TotalSectors)
            : new GenerationRange(layout.DrawingStartLba, layout.DrawingEndLbaExclusive);
        return new GenerationPlan(
            request,
            request.Family,
            layout is null ? GenerationFilesystem.None : GenerationFilesystem.Iso9660Joliet,
            range,
            request.Options.ContentLength,
            layout?.FileCount ?? 0,
            layout?.FileBytes ?? 0,
            GenerationCapability.SequentialStream
                | GenerationCapability.MetadataSidecar
                | (layout is null ? GenerationCapability.None : GenerationCapability.Filesystem),
            [new("metadata", ".json", true)],
            layout is null
                ? $"DVD drawing: {request.Options.TotalSectors} sectors, {request.Options.ContentLength} bytes"
                : $"DVD ISO9660/Joliet: {layout.FileCount} files, drawing LBA {range.StartSector}..{range.EndSectorExclusive - 1}, {request.Options.ContentLength} bytes",
            new DvdGenerationPlanDetails(request, layout));
    }

    private GenerationPlan PlanBluRay(GenerateBluRayRequest request)
    {
        ArgumentNullException.ThrowIfNull(request.Parameters);
        request.Parameters.Validate();
        if (!request.Tone.IsDvdCompatible)
        {
            throw Validation("generation.tone.binary_required", "tone.levels", ("family", "bluray"));
        }

        ValidateDataDirectory(request.DataDirectory, request.FilePlacement);
        BluRayIsoPlan layout = BluRayIsoGenerator.PrepareDrawing(
            request.Parameters,
            request.DataDirectory,
            RequireVolumeLabel(request.VolumeLabel, "volumeLabel"),
            timestamp: _utcNow(),
            filesAtOuterRing: request.FilePlacement == FilePlacement.OuterRing);
        return new GenerationPlan(
            request,
            request.Family,
            layout.HasFileSystem ? GenerationFilesystem.Udf201 : GenerationFilesystem.None,
            new GenerationRange(layout.DrawingStartSector, layout.DrawingEndSectorExclusive),
            layout.ContentLength,
            layout.FileCount,
            layout.FileBytes,
            GenerationCapability.SequentialStream
                | GenerationCapability.MetadataSidecar
                | (layout.HasFileSystem ? GenerationCapability.Filesystem : GenerationCapability.None),
            [new("metadata", ".json", true)],
            layout.HasFileSystem
                ? $"Blu-ray UDF 2.01: {layout.FileCount} files, drawing LBA {layout.DrawingStartSector}..{layout.DrawingEndSectorExclusive - 1}, {layout.ContentLength} bytes"
                : $"Blu-ray drawing-only: {layout.TotalSectors} sectors, {layout.ContentLength} bytes",
            new BluRayGenerationPlanDetails(request, layout));
    }

    private static void GenerateStream(
        GenerationPlan plan,
        Stream output,
        IProgress<GenerationProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!output.CanWrite)
        {
            throw Validation("generation.output.not_writable", "output");
        }

        cancellationToken.ThrowIfCancellationRequested();
        switch (plan.Details)
        {
            case CdGenerationPlanDetails cd:
                GenerateCd(plan, cd, output, progress, cancellationToken);
                break;
            case DvdGenerationPlanDetails dvd:
                GenerateDvd(plan, dvd, output, progress, cancellationToken);
                break;
            case BluRayGenerationPlanDetails bluRay:
                GenerateBluRay(plan, bluRay, output, progress, cancellationToken);
                break;
            case CalibrationTargetPlanDetails calibration:
                GenerateCalibrationTarget(plan, calibration, output, progress, cancellationToken);
                break;
            default:
                throw Validation("generation.plan.unsupported", nameof(plan));
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private static void GenerateCd(
        GenerationPlan plan,
        CdGenerationPlanDetails details,
        Stream output,
        IProgress<GenerationProgress>? progress,
        CancellationToken cancellationToken)
    {
        GenerateCdRequest request = details.Request;
        Stopwatch stopwatch = Stopwatch.StartNew();
        if (request.Format == CdArtifactFormat.WaveAudio)
        {
            WriteWaveHeader(output, request.Parameters.TotalBytes);
        }

        long prefix = request.Format == CdArtifactFormat.WaveAudio ? 44 : 0;
        Action<CdProgress> report = value => progress?.Report(new GenerationProgress(
            "write",
            checked(prefix + value.CompletedBytes),
            plan.EstimatedBytes,
            value.Elapsed,
            request.Format == CdArtifactFormat.Mode1Iso
                ? "generation.cd_data.write"
                : "generation.cd_audio.write"));
        if (request.Format == CdArtifactFormat.Mode1Iso)
        {
            CdDataImageGenerator.GenerateToStream(
                request.SourceImagePath,
                output,
                request.Parameters,
                details.DataLayout!,
                report,
                cancellationToken,
                request.Tone);
        }
        else
        {
            CdTrackGenerator.GenerateToStream(
                request.SourceImagePath,
                output,
                request.Parameters,
                request.Interleave,
                report,
                cancellationToken,
                audioByteOrder: request.AudioByteOrder,
                tone: request.Tone);
        }

        progress?.Report(new GenerationProgress(
            "complete",
            plan.EstimatedBytes,
            plan.EstimatedBytes,
            stopwatch.Elapsed,
            "generation.complete"));
    }

    private static void GenerateDvd(
        GenerationPlan plan,
        DvdGenerationPlanDetails details,
        Stream output,
        IProgress<GenerationProgress>? progress,
        CancellationToken cancellationToken)
    {
        Action<DvdStreamingProgress> report = value => progress?.Report(new GenerationProgress(
            "write",
            value.CompletedBytes,
            value.TotalBytes,
            value.Elapsed,
            "generation.dvd.write"));
        if (details.HybridLayout is null)
        {
            DvdStreamingGenerator.Generate(
                details.Request.SourceImagePath,
                output,
                details.Request.Options,
                report,
                cancellationToken);
        }
        else
        {
            DvdStreamingGenerator.GenerateHybrid(
                details.Request.SourceImagePath,
                output,
                details.Request.Options,
                details.HybridLayout,
                report,
                cancellationToken);
        }

        progress?.Report(new GenerationProgress(
            "complete",
            plan.EstimatedBytes,
            plan.EstimatedBytes,
            TimeSpan.Zero,
            "generation.complete"));
    }

    private static void GenerateBluRay(
        GenerationPlan plan,
        BluRayGenerationPlanDetails details,
        Stream output,
        IProgress<GenerationProgress>? progress,
        CancellationToken cancellationToken)
    {
        GenerateBluRayRequest request = details.Request;
        BluRayGray8Raster raster = LoadBluRayRaster(
            request.SourceImagePath,
            request.Tone,
            cancellationToken);
        BluRayImagePayloadMapping mapping = new(
            request.Parameters,
            raster,
            new BluRayRunLengthMapping(request.InvertMapping));
        InlineProgress<BluRayStreamingProgress>? adapter = progress is null
            ? null
            : new InlineProgress<BluRayStreamingProgress>(value => progress.Report(new GenerationProgress(
                "write",
                value.CompletedBytes,
                value.TotalBytes,
                value.Elapsed,
                "generation.bluray.write")));
        BluRayIsoGenerator.GenerateToStream(
            output,
            details.IsoLayout,
            mapping,
            adapter,
            cancellationToken);
        progress?.Report(new GenerationProgress(
            "complete",
            plan.EstimatedBytes,
            plan.EstimatedBytes,
            TimeSpan.Zero,
            "generation.complete"));
    }

    /// <summary>
    /// Writes the plan's sidecars next to <paramref name="locationPath"/>, describing the artifact
    /// at <paramref name="contentPath"/>. Publishing stages them beside a staged artifact with the
    /// final file name, so the recorded paths are the published ones.
    /// </summary>
    private void WriteSidecars(GenerationPlan plan, string contentPath, string locationPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        switch (plan.Details)
        {
            case CdGenerationPlanDetails cd:
                WriteCdSidecars(cd, contentPath, locationPath);
                break;
            case DvdGenerationPlanDetails dvd:
                WriteDvdSidecar(dvd, locationPath);
                break;
            case BluRayGenerationPlanDetails bluRay:
                BluRayIsoMetadata.FromPlan(
                        bluRay.IsoLayout,
                        bluRay.Request.SourceImagePath,
                        contentPath,
                        bluRay.Request.InvertMapping,
                        bluRay.Request.Tone,
                        _utcNow())
                    .Save(locationPath);
                break;
            case CalibrationTargetPlanDetails calibration:
                WriteCalibrationSidecars(calibration, contentPath, locationPath);
                break;
        }
    }

    private static void WriteCdSidecars(CdGenerationPlanDetails details, string contentPath, string locationPath)
    {
        GenerateCdRequest request = details.Request;
        if (request.Format == CdArtifactFormat.Mode1Iso)
        {
            CdDataLayoutPlan layout = details.DataLayout!;
            CdDataImageMetadata.Save(
                locationPath,
                new CdDataImageMetadata(
                    request.Parameters,
                    layout.DrawingStartLba,
                    layout.DrawingEndLbaExclusive,
                    layout.FilesAtOuterRing,
                    request.Tone.Normalized()));
            return;
        }

        CdTrackMetadata.Save(
            locationPath,
            new CdTrackMetadata(
                Path.GetFullPath(request.SourceImagePath),
                contentPath,
                request.Parameters.InnerRadiusMm,
                request.Parameters.OuterRadiusMm,
                request.Parameters.Sectors,
                request.Parameters.LinearVelocityMmPerSecond,
                request.Parameters.StartAngleRadians * 180.0 / Math.PI,
                request.Parameters.ImageOuterRadiusMm,
                request.Interleave,
                request.Tone.DitherStrength,
                request.Tone.Levels,
                request.Tone.Brightness,
                request.Tone.SrgbLinearization));
        if (request.Format == CdArtifactFormat.WaveAudio && request.WriteCue)
        {
            File.WriteAllText(
                Path.ChangeExtension(locationPath, ".cue"),
                $"FILE \"{Path.GetFileName(contentPath)}\" WAVE\r\n"
                    + "  TRACK 01 AUDIO\r\n"
                    + "    INDEX 01 00:00:00\r\n",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        }
    }

    private static void WriteDvdSidecar(DvdGenerationPlanDetails details, string outputPath)
    {
        DvdHybridStreamingPlan? layout = details.HybridLayout;
        DvdStreamingOptions options = details.Request.Options;
        DvdImageMetadata.Save(
            outputPath,
            new DvdImageMetadata(
                options.TotalSectors,
                options.InnerRadiusMm,
                options.OuterRadiusMm,
                options.PitchLinear,
                options.PitchQuadratic,
                options.PitchCubic,
                layout?.FilesAtOuterRing ?? false,
                layout?.DrawingStartLba ?? 0,
                layout?.DrawingEndLbaExclusive ?? options.TotalSectors,
                layout is not null));
    }

    private static string ValidateOutputPath(GenerationPlan plan, string outputPath)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            throw Validation("generation.output.required", nameof(outputPath));
        }

        string target = Path.GetFullPath(outputPath);
        if (!string.IsNullOrEmpty(plan.Request.SourceImagePath)
            && target.Equals(Path.GetFullPath(plan.Request.SourceImagePath), StringComparison.OrdinalIgnoreCase))
        {
            throw Validation("generation.output.overwrites_source", nameof(outputPath));
        }

        string expectedExtension = plan.Details switch
        {
            CdGenerationPlanDetails { Request.Format: CdArtifactFormat.WaveAudio } => ".wav",
            CdGenerationPlanDetails { Request.Format: CdArtifactFormat.RawAudio } => ".raw",
            CalibrationTargetPlanDetails { Request.Family: OpticalDiscFamily.CompactDisc, Request.CdFormat: CdArtifactFormat.WaveAudio } => ".wav",
            CalibrationTargetPlanDetails { Request.Family: OpticalDiscFamily.CompactDisc } => ".raw",
            _ => ".iso",
        };
        if (!Path.GetExtension(target).Equals(expectedExtension, StringComparison.OrdinalIgnoreCase))
        {
            throw Validation(
                "generation.output.extension",
                nameof(outputPath),
                ("expected", expectedExtension));
        }

        string? dataDirectory = plan.Request switch
        {
            GenerateCdRequest cd => cd.DataDirectory,
            GenerateDvdRequest dvd => dvd.DataDirectory,
            GenerateBluRayRequest bluRay => bluRay.DataDirectory,
            _ => null,
        };
        if (!string.IsNullOrWhiteSpace(dataDirectory) && IsWithinDirectory(target, dataDirectory))
        {
            throw Validation("generation.output.inside_source_directory", nameof(outputPath));
        }

        return target;
    }

    private static bool IsWithinDirectory(string path, string directory)
    {
        string fullPath = Path.GetFullPath(path);
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        return fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static void ValidateSource(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw Validation("generation.source.required", "sourceImagePath");
        }

        if (!File.Exists(path))
        {
            throw Validation(
                "generation.source.not_found",
                "sourceImagePath",
                ("path", path));
        }

        try
        {
            RasterCodec.ValidateSource(path, MaximumSourceBytes, MaximumSourcePixels);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or IOException)
        {
            throw Validation(
                "generation.source.invalid_image",
                "sourceImagePath",
                exception,
                ("reason", exception.Message));
        }
    }

    private static void ValidateDataDirectory(string? dataDirectory, FilePlacement placement)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory))
        {
            if (placement == FilePlacement.OuterRing)
            {
                throw Validation(
                    "generation.files.outer_requires_directory",
                    "filePlacement");
            }

            return;
        }

        if (!Directory.Exists(dataDirectory))
        {
            throw Validation(
                "generation.files.directory_not_found",
                "dataDirectory",
                ("path", dataDirectory));
        }
    }

    private static string RequireVolumeLabel(string value, string field)
        => string.IsNullOrWhiteSpace(value)
            ? throw Validation("generation.volume_label.required", field)
            : value;

    private static void WriteWaveHeader(Stream output, long audioBytes)
    {
        if (audioBytes <= 0 || audioBytes > uint.MaxValue - 36L)
        {
            throw Validation("generation.cd.wave_too_large", "parameters.sectors");
        }

        Span<byte> header = stackalloc byte[44];
        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], checked((uint)(audioBytes + 36)));
        "WAVEfmt "u8.CopyTo(header[8..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(header[20..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(header[22..], 2);
        BinaryPrimitives.WriteUInt32LittleEndian(header[24..], 44100);
        BinaryPrimitives.WriteUInt32LittleEndian(header[28..], 176400);
        BinaryPrimitives.WriteUInt16LittleEndian(header[32..], 4);
        BinaryPrimitives.WriteUInt16LittleEndian(header[34..], 16);
        "data"u8.CopyTo(header[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[40..], checked((uint)audioBytes));
        output.Write(header);
    }

    private static BluRayGray8Raster LoadBluRayRaster(
        string path,
        ToneProfile tone,
        CancellationToken cancellationToken)
    {
        BgraRaster frame = RasterCodec.LoadBgra32(path, MaximumSourceBytes, MaximumSourcePixels);
        int width = frame.Width;
        int height = frame.Height;
        byte[] gray = new byte[checked(width * height)];
        for (int pixel = 0; pixel < gray.Length; pixel++)
        {
            if ((pixel & 0xFFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            int offset = pixel * 4;
            int weighted = 2126 * frame.Pixels[offset + 2]
                + 7152 * frame.Pixels[offset + 1]
                + 722 * frame.Pixels[offset];
            int alpha = frame.Pixels[offset + 3];
            byte luminance = (byte)((weighted * alpha
                + 2_550_000 * (255 - alpha)
                + 1_275_000) / 2_550_000);
            gray[pixel] = tone.IsLegacyBinary
                ? luminance
                : tone.UseWhite(luminance, pixel) ? (byte)255 : (byte)0;
        }

        return new BluRayGray8Raster(width, height, gray);
    }

    private static GenerationValidationException Validation(
        string code,
        string field,
        params (string Key, object? Value)[] parameters)
        => Validation(code, field, innerException: null, parameters);

    private static GenerationValidationException Validation(
        string code,
        string field,
        Exception? innerException,
        params (string Key, object? Value)[] parameters)
        => new(
            new GenerationValidationError(
                code,
                field,
                parameters.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal)),
            innerException);

    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
