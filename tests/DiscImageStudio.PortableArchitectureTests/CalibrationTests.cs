using System.Security.Cryptography;
using System.Text.RegularExpressions;
using DiscImageStudio.Application.Calibration;
using DiscImageStudio.Application.Ports;
using DiscImageStudio.Application.Projects;
using DiscImageStudio.Cd;
using DiscImageStudio.Core;
using DiscImageStudio.Core.Calibration;
using DiscImageStudio.Generation;
using DiscImageStudio.ImageIO;
using Xunit;

namespace DiscImageStudio.PortableArchitectureTests;

/// <summary>The calibration workflow of Task 12: core, sessions, photo, curve, application and report.</summary>
public sealed class CalibrationTests : IDisposable
{
    private static readonly CalibrationParameters CdParameters = new(CalibrationDiscKind.Cd, 23, 58, 20_000, 1200);
    private static readonly CalibrationParameters DvdParameters = new(CalibrationDiscKind.Dvd, 24, 58, 2_297_888, 133.3);
    private readonly string _directory = Directory.CreateTempSubdirectory("dis-calibration-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    // ---- Sessions and recovery ----

    [Fact]
    public void A_session_round_trips_in_the_strict_wpf_format()
    {
        CalibrationSession session = CalibrationTargetFactory.NewSession(new DvdState(24, 58, 2_295_104, 0, 0, 0));
        string path = Path.Combine(_directory, "s.calibration.json");
        CalibrationSessionStore.Save(path, session);
        CalibrationSession loaded = CalibrationSessionStore.Load(path);
        Assert.Equal(CalibrationSessionJson.Serialize(session), CalibrationSessionJson.Serialize(loaded));
        CalibrationSessionJson.Deserialize(File.ReadAllText(path));
    }

    [Fact]
    public void Broken_and_foreign_sessions_get_stable_codes()
    {
        string json = CalibrationSessionJson.Serialize(CalibrationTargetFactory.NewSession(new CdState(23, 58, 330_000, true, true)));
        Assert.Equal("calibration.session.unsupportedVersion", Code(() => CalibrationSessionStore.Parse(json.Replace("\"schemaVersion\": 6", "\"schemaVersion\": 5"))));
        Assert.Equal("calibration.session.unsupportedVersion", Code(() => CalibrationSessionStore.Parse(json.Replace("\"schemaVersion\": 6", "\"schemaVersion\": 7"))));
        Assert.Equal("calibration.session.corrupt", Code(() => CalibrationSessionStore.Parse(json[..(json.Length / 2)])));
        Assert.Equal("calibration.session.corrupt", Code(() => CalibrationSessionStore.Parse("{\"hello\":1}")));
        Assert.Equal("calibration.session.corrupt", Code(() => CalibrationSessionStore.Parse(json.Replace("\"cdInterleave\"", "\"future\": 1, \"cdInterleave\""))));
        Assert.Equal("calibration.session.invalid", Code(() => CalibrationSessionStore.Parse(json.Replace("\"innerRadiusMm\": 23,", "\"innerRadiusMm\": -1,"))));
        Assert.Equal("calibration.session.notFound", Code(() => CalibrationSessionStore.Load(Path.Combine(_directory, "missing.calibration.json"))));
    }

    [Fact]
    public void Recovery_drafts_are_listed_restored_checked_and_private()
    {
        CalibrationRecoveryStore store = new(Path.Combine(_directory, "recovery"), new FixedClock());
        CalibrationSession session = CalibrationTargetFactory.NewSession(new CdState(23, 58, 330_000, true, true));
        CalibrationDraftInfo draft = store.Save(session, "/s.calibration.json", "/p.discproj");
        Assert.Contains(store.List(), info => info.Key == draft.Key);
        Assert.Equal(CalibrationSessionJson.Serialize(session), CalibrationSessionJson.Serialize(store.Load(draft.Key)));

        File.AppendAllText(Path.Combine(store.Directory, draft.Key + ".calibration.json"), " ");
        Assert.Equal("calibration.recovery.mismatch", Code(() => store.Load(draft.Key)));
        Assert.Throws<ArgumentException>(() => store.Load("../../etc/passwd"));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(store.Directory, draft.Key + ".draft.json")));
        }

        store.Discard(draft.Key);
        Assert.Empty(store.List());
    }

    // ---- Renderer and generation ----

    [Fact]
    public void The_disc_view_is_binary_and_white_outside_the_band()
    {
        CalibrationTarget target = CalibrationTarget.Create(CdParameters);
        GrayRaster view = CalibrationPatternRenderer.RenderDiscView(target, 60, 300);
        Assert.All(view.Pixels, value => Assert.True(value is 0 or 255));
        Assert.Equal(255, view.Pixels[0]);
        Assert.Contains((byte)0, view.Pixels);
    }

    [Fact]
    public async Task A_cd_target_matches_the_engine_and_publishes_its_snapshot()
    {
        CalibrationSession session = CalibrationTargetFactory.NewSession(new CdState(23, 58, 3_000, true, true));
        DiscGenerationService service = new();
        GenerationPlan plan = await service.PlanAsync(new GenerateCalibrationTargetRequest(session, CdArtifactFormat.WaveAudio));
        string output = Path.Combine(_directory, "target.wav");
        PublishedArtifact published = await service.PublishFileAsync(plan, output);

        string engine = Path.Combine(_directory, "engine", "target.wav");
        Directory.CreateDirectory(Path.GetDirectoryName(engine)!);
        CalibrationParameters p = session.Target.Parameters;
        CdTrackGenerator.GeneratePattern(session.Target.Sample, engine, new CdDiscParameters(p.InnerRadiusMm, p.OuterRadiusMm, p.Sectors, p.LinearDensity, 0, p.OuterRadiusMm), session.CdInterleave);

        Assert.Equal(Hash(engine), Hash(output));
        Assert.Equal(Hash(output), published.Artifact.Sha256);
        Assert.Equal(session.Target.Id, CalibrationSessionStore.Load(Path.ChangeExtension(output, ".calibration.json")).Target.Id);
        Assert.Contains(published.Companions, file => file.Kind == "calibration");
    }

    // ---- Photo ----

    [Fact]
    public void A_large_photo_is_reduced_and_mapped_back_to_the_original()
    {
        string path = Photo(3000, 2000);
        CalibrationPhoto photo = CalibrationPhotoLoader.Load(path, 1600);
        Assert.Equal((1600, 1067), (photo.Width, photo.Height));
        Assert.True(photo.IsReduced);
        CalibrationPoint back = photo.ToOriginal(photo.FromOriginal(new(1234.5, 432.25)));
        Assert.Equal(1234.5, back.X, 9);
        Assert.Equal(432.25, back.Y, 9);
        Assert.Equal("calibration.photo.notFound", Code(() => CalibrationPhotoLoader.Load(Path.Combine(_directory, "none.png"))));
    }

    [Fact]
    public void The_viewport_round_trips_at_any_zoom_and_scaling()
    {
        foreach (double scaling in new[] { 1.0, 1.5, 2.0 })
        {
            foreach (double zoom in new[] { 0.5, 3.7, 40 })
            {
                CalibrationPhotoViewport viewport = new();
                viewport.Resize(913, 587, scaling);
                viewport.ShowPhoto(1600, 1200);
                viewport.ZoomAt(zoom, new(300, 200));
                viewport.PanBy(-57, 33);
                CalibrationPoint world = new(812.25, 77.5);
                CalibrationPoint back = viewport.ViewToWorld(viewport.DeviceToView(viewport.ViewToDevice(viewport.WorldToView(world))));
                Assert.Equal(world.X, back.X, 9);
                Assert.Equal(world.Y, back.Y, 9);
            }
        }

        CalibrationPhotoViewport fit = new();
        fit.Resize(800, 600);
        fit.ShowPhoto(1600, 1200);
        CalibrationPoint anchor = new(123, 456);
        CalibrationPoint before = fit.ViewToWorld(anchor);
        fit.ZoomAt(3.3, anchor);
        Assert.Equal(before.X, fit.ViewToWorld(anchor).X, 9);
        Assert.Equal(1, fit.HitTest([new(100, 100), new(103, 100)], fit.WorldToView(new(102.4, 100)), 12));
    }

    [Fact]
    public async Task Photo_step_rules_follow_the_wpf_wizard()
    {
        CalibrationPhotoStep step = await RectifiedStep();
        long revision = step.Revision;
        Assert.True(step.Confirm());

        // Curve points: radial order, physical limits.
        Assert.Equal(0, step.AddTracePoint(new(40, 1)));
        Assert.Equal(0, step.AddTracePoint(new(25, 1)));
        Assert.Equal(1, step.AddTracePoint(new(30, 1)));
        Assert.Equal(-1, step.AddTracePoint(new(5, 0)));
        Assert.Equal(-1, step.AddTracePoint(new(0, 61)));
        Assert.Equal(revision, step.Revision);
        Assert.Equal(3, step.ToSession()!.Traces.Single().Points.Count);

        // Changing an edge drops the rectification, the confirmation and the curve.
        step.MoveHandle(CalibrationEdge.Outer, 0, new(step.Outer[0].X + 2, step.Outer[0].Y));
        Assert.Null(step.Rectification);
        Assert.False(step.Confirmed);
        Assert.Equal(0, step.TracePointCount);
        Assert.Equal(-1, step.AddTracePoint(new(30, 1)));
    }

    [Fact]
    public void Mirror_and_rotate_move_pixels_and_handles_together()
    {
        CalibrationPhotoStep step = new();
        step.Import(CalibrationPhotoLoader.Load(Photo(90, 70)));
        CalibrationPhoto original = step.Photo!;
        CalibrationPoint handle = step.Outer[0];
        step.MirrorHorizontally();
        Assert.Equal(new CalibrationPoint(89 - handle.X, handle.Y), step.Outer[0]);
        step.MirrorHorizontally();
        Assert.Equal(original.Raster.Pixels, step.Photo!.Raster.Pixels);
        step.RotateClockwise();
        Assert.Equal((70, 90), (step.Photo!.Width, step.Photo.Height));
        Assert.Equal(new CalibrationPoint(69 - handle.Y, handle.X), step.Outer[0]);
        for (int i = 0; i < 3; i++)
        {
            step.RotateClockwise();
        }

        Assert.Equal(original.Raster.Pixels, step.Photo!.Raster.Pixels);
    }

    [Fact]
    public async Task A_confirmed_session_is_confirmed_again_once_rectified()
    {
        CalibrationPhotoStep step = await RectifiedStep();
        step.Confirm();
        CalibrationPhotoSession saved = step.ToSession()!;
        CalibrationPhotoStep restored = new();
        restored.Restore(saved);
        Assert.True(restored.RestoredAsConfirmed);
        Assert.False(restored.Confirmed);
        using CalibrationFitCoordinator fitters = new();
        long revision = restored.Revision;
        restored.ApplyRectification(await fitters.RectifyAsync(restored.Photo!, restored.Outer, restored.Hole, 60), revision);
        Assert.True(restored.Confirmed);
    }

    // ---- Fitting ----

    [Fact]
    public async Task A_dense_trace_recovers_the_measured_radii()
    {
        CalibrationTarget target = CalibrationTarget.Create(DvdParameters);
        CalibrationParameters actual = DvdParameters with { InnerRadiusMm = 24.01, OuterRadiusMm = 57.988 };
        Func<double, CalibrationPoint> curve = CalibrationCrossPrediction.CreateMapping(target, actual, 0.3);
        CalibrationPoint[] points = [.. Enumerable.Range(0, 200).Select(i => curve(24.5 + (33 * i / 199.0)))];
        using CalibrationFitCoordinator fitters = new();
        CalibrationFitOutcome outcome = await fitters.FitAsync(new(target, [new CalibrationCrossTrace(0, points)], CalibrationTargetFactory.DefaultFitOptions(target)));
        CalibrationCandidate best = outcome.Candidates[0];
        Assert.True(best.ResidualAccepted);
        Assert.Equal(actual.InnerRadiusMm, best.Fit.Parameters.InnerRadiusMm, 3);
        Assert.Equal(actual.OuterRadiusMm, best.Fit.Parameters.OuterRadiusMm, 3);

        CalibrationPrediction prediction = await fitters.PredictAsync(target, best.Fit, points);
        Assert.Empty(prediction.OutlierIndices);
    }

    [Fact]
    public async Task The_newest_fit_cancels_the_older_one()
    {
        CalibrationTarget target = CalibrationTarget.Create(DvdParameters);
        Func<double, CalibrationPoint> curve = CalibrationCrossPrediction.CreateMapping(target, DvdParameters with { InnerRadiusMm = 24.01 }, 0.3);
        CalibrationPoint[] points = [.. Enumerable.Range(0, 200).Select(i => { CalibrationPoint q = curve(24.5 + (33 * i / 199.0)); return new CalibrationPoint(q.X + (0.01 * Math.Sin(i)), q.Y); })];
        CalibrationFitRequest request = new(target, [new CalibrationCrossTrace(0, points)], CalibrationTargetFactory.DefaultFitOptions(target));
        using CalibrationFitCoordinator fitters = new();
        Task<CalibrationFitOutcome> older = fitters.FitAsync(request);
        Task<CalibrationFitOutcome> newer = fitters.FitAsync(request);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => older);
        Assert.True((await newer).Result.Succeeded);
    }

    [Fact]
    public void Every_fitter_message_has_a_code()
    {
        string calibration = Path.Combine(RepositoryRoot(), "src", "DiscImageStudio.Core", "Calibration");
        string[] validation = ["十字线最多包含四条臂。", "曲线编号无效或重复。", "曲线点数量超出限制。", "曲线点坐标无效，请使用矫正后以盘心为原点的坐标。"];
        string[] messages = [.. new[] { "CalibrationCrossFitter.cs", "CalibrationCubicFitter.cs" }
            .SelectMany(file => Regex.Matches(File.ReadAllText(Path.Combine(calibration, file)), "\"([^\"]*[\\u4e00-\\u9fff][^\"]*)\"").Select(match => match.Groups[1].Value))
            .Where(message => message.Length > 12 && !validation.Contains(message))
            .Distinct()];
        Assert.NotEmpty(messages);
        Assert.All(messages, message => Assert.NotEqual("other", CalibrationFitMessages.Code(message)));
    }

    // ---- Applying and reporting ----

    [Fact]
    public void Applying_sets_the_geometry_records_the_measurement_and_round_trips_as_schema_4()
    {
        ProjectWorkspace project = Workspace(new DvdState(24, 58, 2_295_104, 0, 0, 0));
        CalibrationTarget target = CalibrationTarget.Create(CalibrationTargetFactory.ParametersFor(project.Media));
        CalibrationFitCandidate candidate = new(target.Parameters with { InnerRadiusMm = 24.013, OuterRadiusMm = 57.99 }, 0.3, 0.05, MaxErrorMm: 0.2);
        ProjectWorkspace applied = CalibrationEdits.ApplyMeasurement(project, target.Id, candidate, DateTimeOffset.UnixEpoch);
        Assert.Equal(24.013, applied.Media.InnerRadiusMm);
        Assert.Equal(24.013, applied.MeasuredMedia!.InnerRadiusMm);
        Assert.Null(project.MeasuredMedia);

        string path = Path.Combine(_directory, "p.discproj");
        ProjectJsonStore.Save(path, ProjectDocumentMapper.ToDocument(applied));
        Assert.Contains("\"schemaVersion\": 4", File.ReadAllText(path));
        Assert.Equal(applied.Measurement, ProjectDocumentMapper.ToWorkspace(ProjectJsonStore.Load(path)).Measurement);
        ProjectJsonStore.Save(path, ProjectDocumentMapper.ToDocument(CalibrationEdits.ClearMeasurement(applied)));
        Assert.Contains("\"schemaVersion\": 2", File.ReadAllText(path));

        Assert.Equal(
            "calibration.apply.familyMismatch",
            Code(() => CalibrationEdits.ApplyMeasurement(Workspace(new CdState(23, 58, 330_000, true, true)), target.Id, candidate, DateTimeOffset.UnixEpoch)));
    }

    [Fact]
    public async Task The_report_has_no_photo_or_paths_unless_asked()
    {
        CalibrationPhotoStep step = await RectifiedStep();
        step.Confirm();
        step.AddTracePoint(new(30, 1));
        CalibrationTarget target = CalibrationTarget.Create(CdParameters);
        CalibrationSession session = new(target, [], CalibrationTargetFactory.DefaultFitOptions(target), true, step.ToSession());
        CalibrationReportInput input = new(session, step, null, null, null, new CalibrationDecision(null, false, false, null, null), "/home/someone/s.calibration.json", "/home/someone/p.discproj");
        string plain = CalibrationReport.ToJson(input, DateTimeOffset.UnixEpoch, includePersonal: false);
        string personal = CalibrationReport.ToJson(input, DateTimeOffset.UnixEpoch, includePersonal: true);
        Assert.DoesNotContain("imagePngBase64", plain);
        Assert.DoesNotContain("/home/", plain);
        Assert.Contains("imagePngBase64", personal);
        Assert.Contains("/home/someone/s.calibration.json", personal);
        Assert.Contains(CalibrationReport.FormatName, plain);
        Assert.Contains("\"pointCount\": 1", plain);
    }

    // ---- Helpers ----

    private async Task<CalibrationPhotoStep> RectifiedStep()
    {
        CalibrationPhotoStep step = new();
        step.Import(CalibrationPhotoLoader.Load(Photo(800, 800)));
        for (int i = 0; i < 8; i++)
        {
            double angle = 2 * Math.PI * i / 8;
            step.MoveHandle(CalibrationEdge.Outer, i, new(400 + (360 * Math.Cos(angle)), 400 + (360 * Math.Sin(angle))));
            step.MoveHandle(CalibrationEdge.Hole, i, new(400 + (45 * Math.Cos(angle)), 400 + (45 * Math.Sin(angle))));
        }

        using CalibrationFitCoordinator fitters = new();
        long revision = step.Revision;
        CalibrationRectification rectification = await fitters.RectifyAsync(step.Photo!, step.Outer, step.Hole, 60);
        Assert.True(step.ApplyRectification(rectification, revision));
        step.MoveHandle(CalibrationEdge.Hole, 0, new(step.Hole[0].X + 1, step.Hole[0].Y));
        Assert.False(step.ApplyRectification(rectification, revision));
        step.MoveHandle(CalibrationEdge.Hole, 0, new(step.Hole[0].X - 1, step.Hole[0].Y));
        revision = step.Revision;
        Assert.True(step.ApplyRectification(await fitters.RectifyAsync(step.Photo!, step.Outer, step.Hole, 60), revision));
        return step;
    }

    private string Photo(int width, int height)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int i = 0; i < width * height; i++)
        {
            pixels[i * 4] = (byte)(i % width);
            pixels[(i * 4) + 1] = (byte)(i / width % 256);
            pixels[(i * 4) + 2] = 90;
            pixels[(i * 4) + 3] = 255;
        }

        string path = Path.Combine(_directory, $"photo-{width}x{height}.png");
        RasterCodec.SavePng(path, width, height, pixels, false);
        return path;
    }

    private static ProjectWorkspace Workspace(MediaState media)
        => new(
            new ProjectIdentity("P", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch),
            new DesignState(),
            new ToneState(ToneProfile.LegacyBinary),
            new DeliveryState("/tmp/p.iso", null, "P"),
            media);

    private static string Code(Action action)
    {
        try
        {
            action();
            return "none";
        }
        catch (CalibrationException exception)
        {
            return exception.Code;
        }
    }

    private static string Hash(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static string RepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DiscImageStudio.Linux.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Repository root not found.");
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);
    }
}
