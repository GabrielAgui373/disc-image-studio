using System.Globalization;
using DiscImageStudio.Application.Projects;
using DiscImageStudio.Core;
using Xunit;

namespace DiscImageStudio.PortableArchitectureTests;

public sealed class ProjectDocumentMapperTests
{
    public static TheoryData<string> Families =>
    [
        ProjectDocument.CdFamily,
        ProjectDocument.DvdFamily,
        ProjectDocument.BluRayFamily,
    ];

    [Theory]
    [MemberData(nameof(Families))]
    public void Current_project_schema_round_trips_without_semantic_loss(string family)
    {
        ProjectDocument original = CreateDocument(family);

        ProjectWorkspace workspace = ProjectDocumentMapper.ToWorkspace(original);
        ProjectDocument roundTrip = ProjectDocumentMapper.ToDocument(workspace);

        AssertEquivalent(original, roundTrip);
        Assert.Equal(original.Family, workspace.Media.Family);
        Assert.True(workspace.Media switch
        {
            CdState => family == ProjectDocument.CdFamily,
            DvdState => family == ProjectDocument.DvdFamily,
            BluRayState => family == ProjectDocument.BluRayFamily,
            _ => false,
        });
    }

    [Fact]
    public void Layer_paths_are_preserved_without_requiring_assets_to_exist()
    {
        ProjectDocument original = CreateDocument(ProjectDocument.CdFamily) with
        {
            Layers =
            [
                new("relative", "Relative", "assets/missing.png", true, 0, 0, 0, 1),
                new("absolute", "Absolute", "/media/archive/missing.png", true, 1, 2, 3, 0.5),
            ],
        };

        ProjectDocument roundTrip = ProjectDocumentMapper.ToDocument(
            ProjectDocumentMapper.ToWorkspace(original));

        Assert.Equal("assets/missing.png", roundTrip.Layers[0].ImagePath);
        Assert.Equal("/media/archive/missing.png", roundTrip.Layers[1].ImagePath);
    }

    [Fact]
    public void Persisted_path_and_label_text_is_not_silently_normalized()
    {
        ProjectDocument original = CreateDocument(ProjectDocument.CdFamily) with
        {
            Outputs = new ProjectOutputs(" output.wav ", true, " data folder ", " CD IMAGE ", true),
            CalibrationSessionPath = " calibration/session.json ",
        };

        ProjectDocument roundTrip = ProjectDocumentMapper.ToDocument(
            ProjectDocumentMapper.ToWorkspace(original));

        Assert.Equal(original.Outputs, roundTrip.Outputs);
        Assert.Equal(original.CalibrationSessionPath, roundTrip.CalibrationSessionPath);
    }

    [Fact]
    public void Typed_values_are_culture_independent()
    {
        CultureInfo previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("pt-BR");
            ProjectDocument original = CreateDocument(ProjectDocument.DvdFamily) with
            {
                Geometry = new ProjectGeometry(24.5, 58.75, 2_295_104, 0.001, -0.0002, 0.00001),
            };

            ProjectDocument roundTrip = ProjectDocumentMapper.ToDocument(
                ProjectDocumentMapper.ToWorkspace(original));

            Assert.Equal(24.5, roundTrip.Geometry.InnerRadiusMm);
            Assert.Equal(58.75, roundTrip.Geometry.OuterRadiusMm);
            Assert.Equal(0.001, roundTrip.Geometry.PitchLinear);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    [Fact]
    public void Legacy_schema_loads_and_unknown_members_remain_rejected()
    {
        string directory = CreateTempDirectory();
        string path = Path.Combine(directory, "legacy.discproj");
        ProjectJsonStore.Save(path, CreateDocument(ProjectDocument.CdFamily));
        string current = File.ReadAllText(path);
        File.WriteAllText(
            path,
            current.Replace(
                $"\"schemaVersion\": {ProjectJsonStore.CurrentSchemaVersion}",
                "\"schemaVersion\": 1",
                StringComparison.Ordinal));

        ProjectWorkspace workspace = ProjectDocumentMapper.ToWorkspace(ProjectJsonStore.Load(path));
        ProjectJsonStore.Save(path, ProjectDocumentMapper.ToDocument(workspace));
        Assert.Contains(
            $"\"schemaVersion\": {ProjectJsonStore.CurrentSchemaVersion}",
            File.ReadAllText(path),
            StringComparison.Ordinal);

        string unknownPath = Path.Combine(directory, "unknown.discproj");
        File.WriteAllText(
            unknownPath,
            File.ReadAllText(path).Replace("{", "{\"unexpected\":true,", StringComparison.Ordinal));
        Assert.Throws<InvalidDataException>(() => ProjectJsonStore.Load(unknownPath));
    }

    [Fact]
    public void Thumbnail_validation_is_still_owned_by_the_core_schema()
    {
        ProjectDocument invalid = CreateDocument(ProjectDocument.DvdFamily) with
        {
            ThumbnailPngBase64 = new string('A', (512 * 1024) + 1),
        };

        Assert.Throws<InvalidDataException>(() => ProjectDocumentMapper.ToWorkspace(invalid));
    }

    internal static ProjectDocument CreateDocument(string family)
    {
        bool cd = family == ProjectDocument.CdFamily;
        bool dvd = family == ProjectDocument.DvdFamily;
        bool bluRay = family == ProjectDocument.BluRayFamily;
        ProjectGeometry geometry = dvd
            ? new ProjectGeometry(24, 58, 2_295_104, 0.001, -0.0002, 0.00001)
            : bluRay
                ? new ProjectGeometry(24, 58, ProjectDocument.BluRaySectorsPerLayer)
                : new ProjectGeometry(24.5, 56.8, 359_849);
        return new ProjectDocument(
            ProjectJsonStore.CurrentSchemaVersion,
            $"{family} project",
            family,
            geometry,
            [new ProjectLayer("layer", "Artwork", "assets/art.png", true, 1.5, -2.25, 90, 0.8)],
            new ProjectOutputs(
                cd ? "output.wav" : "output.iso",
                WriteCue: cd,
                "payload",
                bluRay ? "BD_IMAGE" : cd ? "CD_IMAGE" : "DISC_IMAGE",
                Interleave: cd,
                FilesAtOuterRing: true),
            "calibration/session.json",
            Convert.ToBase64String([1, 2, 3, 4]),
            new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
            new DateTimeOffset(2026, 2, 3, 4, 5, 6, TimeSpan.Zero),
            new ToneProfile(0.75, 2, 3, true),
            bluRay ? new ProjectBluRaySettings(true) : null);
    }

    private static void AssertEquivalent(ProjectDocument expected, ProjectDocument actual)
    {
        Assert.Equal(expected.SchemaVersion, actual.SchemaVersion);
        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal(expected.DiscFamily, actual.DiscFamily);
        Assert.Equal(expected.Geometry, actual.Geometry);
        Assert.Equal(expected.Outputs, actual.Outputs);
        Assert.Equal(expected.CalibrationSessionPath, actual.CalibrationSessionPath);
        Assert.Equal(expected.ThumbnailPngBase64, actual.ThumbnailPngBase64);
        Assert.Equal(expected.CreatedUtc, actual.CreatedUtc);
        Assert.Equal(expected.ModifiedUtc, actual.ModifiedUtc);
        Assert.Equal(expected.Tone, actual.Tone);
        Assert.Equal(expected.BluRay, actual.BluRay);
        Assert.Equal(expected.Layers, actual.Layers);
    }

    private static string CreateTempDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"disc-app-mapper-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }
}
