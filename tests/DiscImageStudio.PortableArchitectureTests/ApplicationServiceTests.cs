using DiscImageStudio.Application.Jobs;
using DiscImageStudio.Application.Navigation;
using DiscImageStudio.Application.Ports;
using DiscImageStudio.Application.Projects;
using DiscImageStudio.Application.Results;
using DiscImageStudio.Core;
using Xunit;

namespace DiscImageStudio.PortableArchitectureTests;

public sealed class ApplicationServiceTests
{
    [Fact]
    public void Dirty_state_ignores_ephemeral_changes_and_tracks_persistent_origin()
    {
        DirtyState dirty = new();
        DateTimeOffset now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        Assert.False(dirty.MarkDirty("zoom", "design-canvas", now, ChangePersistence.Ephemeral));
        Assert.False(dirty.IsDirty);
        Assert.True(dirty.MarkDirty("layer-moved", "design-canvas", now));
        Assert.True(dirty.IsDirty);
        Assert.Equal("layer-moved", dirty.Reasons.Single().Code);
        Assert.Equal("design-canvas", dirty.Reasons.Single().Origin);

        dirty.AcceptChanges();
        Assert.False(dirty.IsDirty);
        Assert.Equal(1, dirty.Revision);
    }

    [Fact]
    public void Navigation_requires_an_open_project_only_for_project_workspaces()
    {
        NavigationState navigation = new();

        ApplicationResult rejected = navigation.NavigateTo(WorkspacePage.Design, hasOpenProject: false);
        ApplicationResult settings = navigation.NavigateTo(WorkspacePage.Settings, hasOpenProject: false);

        Assert.False(rejected.Succeeded);
        Assert.Equal(ApplicationErrorCode.NavigationRequiresProject, rejected.Error?.Code);
        Assert.True(settings.Succeeded);
        Assert.Equal(WorkspacePage.Settings, navigation.Current);
        Assert.True(navigation.NavigateTo(WorkspacePage.Preview, hasOpenProject: true).Succeeded);
    }

    [Fact]
    public async Task Canceled_unsaved_prompt_keeps_project_and_dirty_state()
    {
        string directory = CreateTempDirectory();
        string path = Path.Combine(directory, "project.discproj");
        QueueInteraction interaction = new(UnsavedChangesDecision.Cancel);
        ProjectLifecycleService lifecycle = CreateLifecycle(interaction);
        ApplicationResult<ProjectWorkspace> created = await lifecycle.CreateProjectAsync(CreateRequest(path));
        Assert.True(created.Succeeded);
        lifecycle.MarkDirty("tone-changed", "tone-panel");

        ApplicationResult close = await lifecycle.CloseProjectAsync();

        Assert.False(close.Succeeded);
        Assert.Equal(ApplicationErrorCode.UserCanceled, close.Error?.Code);
        Assert.True(lifecycle.HasOpenProject);
        Assert.True(lifecycle.Dirty.IsDirty);
        Assert.Equal(WorkspacePage.Design, lifecycle.Navigation.Current);
    }

    [Fact]
    public async Task Save_as_updates_timestamp_path_and_clears_dirty_state()
    {
        string directory = CreateTempDirectory();
        string path = Path.Combine(directory, "project.discproj");
        string secondPath = Path.Combine(directory, "copy.discproj");
        TestClock clock = new(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero));
        ProjectLifecycleService lifecycle = CreateLifecycle(new QueueInteraction(), clock);
        Assert.True((await lifecycle.CreateProjectAsync(CreateRequest(path))).Succeeded);
        ProjectSnapshot snapshot = lifecycle.CaptureProjectSnapshot().Value;
        Assert.True(lifecycle.ApplyProjectSnapshot(snapshot).Succeeded);
        clock.UtcNow = clock.UtcNow.AddHours(2);

        ApplicationResult<ProjectWorkspace> saved = await lifecycle.SaveProjectAsAsync(secondPath);

        Assert.True(saved.Succeeded);
        Assert.Equal(Path.GetFullPath(secondPath), lifecycle.ProjectFilePath);
        Assert.Equal(clock.UtcNow, saved.Value.Identity.ModifiedUtc);
        Assert.False(lifecycle.Dirty.IsDirty);
        Assert.True(File.Exists(secondPath));
    }

    [Fact]
    public async Task Opening_an_unsupported_schema_reports_a_stable_error_code()
    {
        string path = Path.Combine(CreateTempDirectory(), "future.discproj");
        ProjectLifecycleService writer = CreateLifecycle(new QueueInteraction());
        Assert.True((await writer.CreateProjectAsync(CreateRequest(path))).Succeeded);
        string json = File.ReadAllText(path);
        Assert.Contains("\"schemaVersion\": 2", json, StringComparison.Ordinal);
        File.WriteAllText(path, json.Replace("\"schemaVersion\": 2", "\"schemaVersion\": 99", StringComparison.Ordinal));
        ProjectLifecycleService reader = CreateLifecycle(new QueueInteraction());

        ApplicationResult<ProjectWorkspace> opened = await reader.OpenProjectAsync(path);

        Assert.False(opened.Succeeded);
        Assert.Equal(ApplicationErrorCode.ProjectSchemaUnsupported, opened.Error?.Code);
        Assert.False(reader.HasOpenProject);
    }

    [Fact]
    public async Task Opening_malformed_json_is_a_read_failure_not_a_schema_failure()
    {
        string path = Path.Combine(CreateTempDirectory(), "broken.discproj");
        File.WriteAllText(path, "{ \"schemaVersion\": 2, ");
        ProjectLifecycleService lifecycle = CreateLifecycle(new QueueInteraction());

        ApplicationResult<ProjectWorkspace> opened = await lifecycle.OpenProjectAsync(path);

        Assert.Equal(ApplicationErrorCode.ProjectReadFailed, opened.Error?.Code);
        Assert.IsType<InvalidDataException>(opened.Error?.Exception);
    }

    [Fact]
    public async Task Exclusive_job_rejects_concurrent_work_and_can_be_canceled()
    {
        JobCoordinator coordinator = new(new TestClock(DateTimeOffset.UtcNow));
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<ApplicationResult<JobCompletion>> first = coordinator.StartJobAsync(
            new JobRequest("generate"),
            async context =>
            {
                started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, context.CancellationToken);
            });
        await started.Task;

        ApplicationResult<JobCompletion> conflict = await coordinator.StartJobAsync(
            new JobRequest("preview"),
            _ => Task.CompletedTask);
        Assert.False(conflict.Succeeded);
        Assert.Equal(ApplicationErrorCode.JobConflict, conflict.Error?.Code);

        Guid runningId = Assert.Single(coordinator.RunningJobIds);
        Assert.True(coordinator.CancelJob(runningId).Succeeded);
        ApplicationResult<JobCompletion> canceled = await first;
        Assert.False(canceled.Succeeded);
        Assert.Equal(ApplicationErrorCode.JobCanceled, canceled.Error?.Code);
        Assert.Empty(coordinator.RunningJobIds);
    }

    [Fact]
    public async Task Shared_jobs_may_run_together()
    {
        JobCoordinator coordinator = new(new TestClock(DateTimeOffset.UtcNow));
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource bothStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int count = 0;
        async Task Work(JobExecutionContext _)
        {
            if (Interlocked.Increment(ref count) == 2)
            {
                bothStarted.SetResult();
            }

            await release.Task;
        }

        Task<ApplicationResult<JobCompletion>> first = coordinator.StartJobAsync(new JobRequest("one", Exclusive: false), Work);
        Task<ApplicationResult<JobCompletion>> second = coordinator.StartJobAsync(new JobRequest("two", Exclusive: false), Work);
        await bothStarted.Task;
        Assert.Equal(2, coordinator.RunningJobIds.Count);
        release.SetResult();

        Assert.True((await first).Succeeded);
        Assert.True((await second).Succeeded);
    }

    private static CreateProjectRequest CreateRequest(string path)
        => new(
            path,
            "Portable project",
            new CdState(24.5, 56.8, 359_849, writeCue: true, interleave: true),
            new DeliveryState("output.wav", string.Empty, "CD_IMAGE"));

    private static ProjectLifecycleService CreateLifecycle(
        IUserInteraction interaction,
        TestClock? clock = null)
        => new(
            new JsonProjectDocumentStore(),
            new PhysicalFileSystem(),
            clock ?? new TestClock(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero)),
            interaction);

    private static string CreateTempDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"disc-app-service-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private sealed class QueueInteraction(params UnsavedChangesDecision[] decisions) : IUserInteraction
    {
        private readonly Queue<UnsavedChangesDecision> _decisions = new(decisions);

        public Task<UnsavedChangesDecision> ConfirmUnsavedChangesAsync(
            UnsavedChangesRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult(_decisions.Count > 0 ? _decisions.Dequeue() : UnsavedChangesDecision.Discard);
    }

    private sealed class TestClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
    }
}
