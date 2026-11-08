using Avalonia.Media;
using DiscImageStudio.App.Avalonia.Localization;
using DiscImageStudio.App.Avalonia.Platform;
using DiscImageStudio.App.Avalonia.ViewModels;
using DiscImageStudio.Application.Ports;
using DiscImageStudio.Application.Projects;
using DiscImageStudio.Application.Settings;
using DiscImageStudio.Core;
using Xunit;

namespace DiscImageStudio.PortableArchitectureTests;

public sealed class RecentProjectsTests
{
    private static readonly byte[] TinyPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    [Fact]
    public void Store_deduplicates_normalized_paths_with_platform_case_rules()
    {
        string directory = CreateTempDirectory();
        JsonRecentProjectStore store = new(Path.Combine(directory, "recent.json"));
        string project = Path.Combine(directory, "Disc.discproj");

        store.Record(Entry("First", Path.Combine(directory, ".", "Disc.discproj"), minutes: 1));
        store.Record(Entry("Second", project, minutes: 2));
        store.Record(Entry("Other case", Path.Combine(directory, "disc.discproj"), minutes: 3));

        IReadOnlyList<RecentProjectEntry> entries = store.Load();
        Assert.Equal(Path.GetFullPath(project), entries.Single(entry => entry.Name == "Second").ProjectPath);
        Assert.DoesNotContain(entries, entry => entry.Name == "First");
        if (ProjectPaths.Comparison == StringComparison.Ordinal)
        {
            // Linux: Disc.discproj and disc.discproj are two different projects.
            Assert.Equal(["Other case", "Second"], entries.Select(entry => entry.Name));
        }
        else
        {
            Assert.Equal(["Other case"], entries.Select(entry => entry.Name));
        }
    }

    [Fact]
    public void Store_recovers_from_a_corrupt_history_file()
    {
        string path = Path.Combine(CreateTempDirectory(), "recent.json");
        File.WriteAllText(path, "[{ \"name\": ");

        Assert.Empty(new JsonRecentProjectStore(path).Load());
    }

    [Fact]
    public async Task Lifecycle_records_created_and_saved_projects_newest_first()
    {
        string directory = CreateTempDirectory();
        JsonRecentProjectStore store = new(Path.Combine(directory, "recent.json"));
        MutableClock clock = new(new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.Zero));
        ProjectLifecycleService lifecycle = CreateLifecycle(store, clock);
        string first = Path.Combine(directory, "first.discproj");
        string second = Path.Combine(directory, "second.discproj");

        Assert.True((await lifecycle.CreateProjectAsync(Request(first, "First disc"))).Succeeded);
        clock.UtcNow = clock.UtcNow.AddMinutes(5);
        Assert.True((await lifecycle.SaveProjectAsAsync(second)).Succeeded);

        IReadOnlyList<RecentProjectEntry> entries = store.Load();
        Assert.Equal([Path.GetFullPath(second), Path.GetFullPath(first)], entries.Select(entry => entry.ProjectPath));
        Assert.All(entries, entry => Assert.Equal(ProjectDocument.CdFamily, entry.DiscFamily));
        Assert.Equal(clock.UtcNow, entries[0].LastOpenedUtc);
    }

    [Fact]
    public async Task Probe_reports_each_file_state_without_opening_the_project()
    {
        string directory = CreateTempDirectory();
        JsonRecentProjectStore store = new(Path.Combine(directory, "recent.json"));
        ProjectLifecycleService lifecycle = CreateLifecycle(store, new MutableClock(DateTimeOffset.UnixEpoch));
        string available = Path.Combine(directory, "available.discproj");
        string invalid = Path.Combine(directory, "invalid.discproj");
        string future = Path.Combine(directory, "future.discproj");
        Assert.True((await lifecycle.CreateProjectAsync(Request(available, "Available", Convert.ToBase64String(TinyPng)))).Succeeded);
        File.Copy(available, future);
        File.WriteAllText(future, File.ReadAllText(future).Replace("\"schemaVersion\": 2", "\"schemaVersion\": 42", StringComparison.Ordinal));
        File.WriteAllText(invalid, "{ not json");
        RecentProjectsService service = new(store, new JsonProjectDocumentStore(), new PhysicalFileSystem());

        RecentProjectProbe availableProbe = service.Probe(Entry("Stale name", available));
        Assert.Equal(RecentProjectStatus.Available, availableProbe.Status);
        Assert.Equal("Available", availableProbe.Name);
        Assert.Equal(TinyPng, availableProbe.ThumbnailPng);
        Assert.Equal(RecentProjectStatus.Missing, service.Probe(Entry("Gone", Path.Combine(directory, "gone.discproj"))).Status);
        Assert.Equal(RecentProjectStatus.Invalid, service.Probe(Entry("Broken", invalid)).Status);
        Assert.Equal(RecentProjectStatus.UnsupportedSchema, service.Probe(Entry("Future", future)).Status);
    }

    [Fact]
    public void Thumbnail_decoding_rejects_non_png_and_oversized_payloads()
    {
        Assert.Equal(TinyPng, RecentProjectsService.DecodeThumbnail(Convert.ToBase64String(TinyPng)));
        Assert.Null(RecentProjectsService.DecodeThumbnail(Convert.ToBase64String([1, 2, 3, 4, 5, 6, 7, 8, 9])));
        Assert.Null(RecentProjectsService.DecodeThumbnail("not base64!"));
        byte[] oversized = new byte[RecentProjectsService.MaximumThumbnailBytes + 1];
        TinyPng.CopyTo(oversized, 0);
        Assert.Null(RecentProjectsService.DecodeThumbnail(Convert.ToBase64String(oversized)));
    }

    [Fact]
    public async Task Removing_a_recent_project_only_edits_the_history()
    {
        string directory = CreateTempDirectory();
        JsonRecentProjectStore store = new(Path.Combine(directory, "recent.json"));
        string project = Path.Combine(directory, "keep.discproj");
        Assert.True((await CreateLifecycle(store, new MutableClock(DateTimeOffset.UnixEpoch)).CreateProjectAsync(Request(project, "Keep"))).Succeeded);
        RecentProjectsService service = new(store, new JsonProjectDocumentStore(), new PhysicalFileSystem());
        string text = new EnglishTextCatalog()["recent.removed"];
        RecentProjectsViewModel viewModel = new(service, new FakeDecoder(), new EnglishTextCatalog(), _ => Task.FromResult(true));
        await viewModel.RefreshAsync();
        RecentProjectItem item = Assert.Single(viewModel.Items);

        await viewModel.RemoveCommand.ExecuteAsync(item);

        Assert.Empty(viewModel.Items);
        Assert.Empty(store.Load());
        Assert.True(File.Exists(project));
        Assert.True(viewModel.IsEmpty);
        Assert.Equal(string.Format(System.Globalization.CultureInfo.CurrentUICulture, text, "Keep"), viewModel.Status);
    }

    [Fact]
    public async Task Missing_recent_project_explains_the_state_instead_of_opening()
    {
        string directory = CreateTempDirectory();
        JsonRecentProjectStore store = new(Path.Combine(directory, "recent.json"));
        store.Record(Entry("Moved disc", Path.Combine(directory, "moved.discproj")));
        RecentProjectsService service = new(store, new JsonProjectDocumentStore(), new PhysicalFileSystem());
        int openCalls = 0;
        RecentProjectsViewModel viewModel = new(
            service,
            new FakeDecoder(),
            new EnglishTextCatalog(),
            _ =>
            {
                openCalls++;
                return Task.FromResult(true);
            });
        await viewModel.RefreshAsync();
        RecentProjectItem item = Assert.Single(viewModel.Items);

        await viewModel.OpenCommand.ExecuteAsync(item);

        Assert.Equal(0, openCalls);
        Assert.True(item.HasIssue);
        Assert.Equal("File not found", item.StatusLabel);
        Assert.Contains("Moved disc", viewModel.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Available_recent_project_decodes_its_thumbnail_and_opens()
    {
        string directory = CreateTempDirectory();
        JsonRecentProjectStore store = new(Path.Combine(directory, "recent.json"));
        string project = Path.Combine(directory, "thumb.discproj");
        Assert.True((await CreateLifecycle(store, new MutableClock(DateTimeOffset.UnixEpoch)).CreateProjectAsync(
            Request(project, "Thumb", Convert.ToBase64String(TinyPng)))).Succeeded);
        RecentProjectsService service = new(store, new JsonProjectDocumentStore(), new PhysicalFileSystem());
        FakeDecoder decoder = new();
        string? opened = null;
        RecentProjectsViewModel viewModel = new(
            service,
            decoder,
            new EnglishTextCatalog(),
            path =>
            {
                opened = path;
                return Task.FromResult(true);
            });

        await viewModel.RefreshAsync();
        RecentProjectItem item = Assert.Single(viewModel.Items);
        await viewModel.OpenCommand.ExecuteAsync(item);

        Assert.True(item.IsAvailable);
        Assert.Equal(1, decoder.Calls);
        Assert.Equal(Path.GetFullPath(project), opened);
    }

    private static RecentProjectEntry Entry(string name, string path, int minutes = 0)
        => new(name, ProjectDocument.CdFamily, path, DateTimeOffset.UnixEpoch.AddMinutes(minutes));

    private static CreateProjectRequest Request(string path, string name, string? thumbnail = null)
        => new(
            path,
            name,
            new CdState(24.5, 56.8, 359_849, writeCue: true, interleave: true),
            new DeliveryState("output.wav", string.Empty, "CD_IMAGE"),
            ThumbnailPngBase64: thumbnail);

    private static ProjectLifecycleService CreateLifecycle(IRecentProjectStore store, IClock clock)
        => new(
            new JsonProjectDocumentStore(),
            new PhysicalFileSystem(),
            clock,
            new DiscardInteraction(),
            recentProjects: store);

    private static string CreateTempDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"disc-recent-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private sealed class MutableClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
    }

    private sealed class DiscardInteraction : IUserInteraction
    {
        public Task<UnsavedChangesDecision> ConfirmUnsavedChangesAsync(
            UnsavedChangesRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult(UnsavedChangesDecision.Discard);
    }

    private sealed class FakeDecoder : IThumbnailDecoder
    {
        public int Calls { get; private set; }

        public IImage? Decode(byte[] png, int decodeWidth)
        {
            Calls++;
            return null;
        }
    }
}
