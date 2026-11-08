using DiscImageStudio.App.Avalonia.Localization;
using DiscImageStudio.App.Avalonia.ViewModels;
using DiscImageStudio.Application.Ports;
using DiscImageStudio.Application.Projects;
using DiscImageStudio.Application.Results;
using DiscImageStudio.Application.Settings;
using DiscImageStudio.Core;
using Xunit;

namespace DiscImageStudio.PortableArchitectureTests;

public sealed class ProjectSettingsTests
{
    private static readonly ProjectAssetService Assets = new(new PhysicalFileSystem(), new PhysicalDirectoryScanner());

    [Fact]
    public void Inspect_resolves_relative_paths_against_the_project_folder()
    {
        string directory = CreateTempDirectory();
        File.WriteAllBytes(Path.Combine(directory, "art.png"), [1]);
        ProjectWorkspace workspace = Workspace(
            [Layer("relative", "art.png"), Layer("absolute", Path.Combine(directory, "gone.png"))],
            dataDirectory: Path.Combine(directory, "data"),
            calibration: "session.json");

        IReadOnlyList<ProjectAssetReference> assets = Assets.Inspect(workspace, Path.Combine(directory, "disc.discproj"));

        ProjectAssetReference relative = assets.Single(asset => asset.Key == "relative");
        Assert.Equal(Path.Combine(directory, "art.png"), relative.ResolvedPath);
        Assert.True(relative.Exists);
        Assert.False(assets.Single(asset => asset.Key == "absolute").Exists);
        Assert.Contains(assets, asset => asset.Kind == ProjectAssetKind.DataDirectory && !asset.Exists);
        Assert.Equal(
            Path.Combine(directory, "session.json"),
            assets.Single(asset => asset.Kind == ProjectAssetKind.CalibrationSession).ResolvedPath);
    }

    [Fact]
    public void Relink_stores_an_absolute_path_and_requires_an_item_of_the_same_kind()
    {
        string directory = CreateTempDirectory();
        string replacement = Path.Combine(directory, "new.png");
        File.WriteAllBytes(replacement, [1]);
        ProjectWorkspace workspace = Workspace([Layer("l1", Path.Combine(directory, "old.png"))]);
        ProjectAssetReference asset = Assets.Inspect(workspace, null).Single();

        ProjectWorkspace relinked = Assets.Relink(workspace, asset, replacement);

        Assert.Equal(replacement, relinked.Design.Layers.Single().ImagePath);
        Assert.Equal(Path.Combine(directory, "old.png"), workspace.Design.Layers.Single().ImagePath);
        Assert.Throws<ArgumentException>(() => Assets.Relink(workspace, asset, directory));
        Assert.Throws<ArgumentException>(() => Assets.Relink(workspace, asset, Path.Combine(directory, "missing.png")));
    }

    [Fact]
    public void Folder_search_classifies_matches_and_never_follows_symbolic_links()
    {
        string directory = CreateTempDirectory();
        string search = Path.Combine(directory, "search");
        string outside = Path.Combine(directory, "outside");
        Directory.CreateDirectory(Path.Combine(search, "a"));
        Directory.CreateDirectory(Path.Combine(search, "b"));
        Directory.CreateDirectory(outside);
        File.WriteAllBytes(Path.Combine(search, "a", "exact.png"), [1]);
        File.WriteAllBytes(Path.Combine(search, "a", "twice.png"), [1]);
        File.WriteAllBytes(Path.Combine(search, "b", "twice.png"), [1]);
        File.WriteAllBytes(Path.Combine(search, "b", "Cased.png"), [1]);
        File.WriteAllBytes(Path.Combine(outside, "linked.png"), [1]);
        Directory.CreateSymbolicLink(Path.Combine(search, "escape"), outside);
        string old = Path.Combine(directory, "old");
        ProjectWorkspace workspace = Workspace(
        [
            Layer("exact", Path.Combine(old, "exact.png")),
            Layer("twice", Path.Combine(old, "twice.png")),
            Layer("cased", Path.Combine(old, "cased.png")),
            Layer("linked", Path.Combine(old, "linked.png")),
        ]);

        RelinkSearchResult result = Assets.FindInFolder(Assets.Inspect(workspace, null), search);

        RelinkProposal Proposal(string key) => result.Proposals.Single(proposal => proposal.Asset.Key == key);
        Assert.Equal(RelinkMatch.Exact, Proposal("exact").Match);
        Assert.Equal(Path.Combine(search, "a", "exact.png"), Proposal("exact").CandidatePath);
        Assert.Equal(RelinkMatch.Ambiguous, Proposal("twice").Match);
        Assert.Equal(2, Proposal("twice").CandidateCount);
        Assert.Null(Proposal("twice").CandidatePath);
        Assert.Equal(RelinkMatch.CaseDiffers, Proposal("cased").Match);
        Assert.Equal(RelinkMatch.NotFound, Proposal("linked").Match);
        Assert.False(result.ReachedScanLimit);
    }

    [Fact]
    public void User_presets_round_trip_and_an_unreadable_file_is_never_overwritten()
    {
        string path = Path.Combine(CreateTempDirectory(), DiscPresetJsonStore.FileName);
        UserPresetCatalog catalog = new(path);
        DvdState media = new(23.5, 58, 2_295_104, 0.001, 0, 0);

        ApplicationResult<ProjectPresetDefinition> saved = catalog.SaveUserPreset("My DVD", "Measured", media);

        Assert.True(saved.Succeeded);
        ProjectPresetDefinition loaded = Assert.Single(catalog.LoadUserPresets());
        Assert.Equal("My DVD", loaded.DisplayName);
        Assert.Equal(OpticalDiscFamily.Dvd, loaded.Family);
        Assert.Equal(23.5, loaded.Media.InnerRadiusMm);
        Assert.True(catalog.DeleteUserPreset(loaded.Id).Succeeded);
        Assert.Empty(catalog.LoadUserPresets());

        File.WriteAllText(path, "{ hand edited, not json");
        ApplicationResult<ProjectPresetDefinition> refused = catalog.SaveUserPreset("Other", "x", media);

        Assert.Equal(ApplicationErrorCode.PresetReadFailed, refused.Error?.Code);
        Assert.Equal("{ hand edited, not json", File.ReadAllText(path));
        Assert.Equal(ApplicationErrorCode.InvalidRequest, catalog.SaveUserPreset(" ", "x", media).Error?.Code);
    }

    [Fact]
    public void Applying_a_preset_keeps_the_family_and_CD_output_options()
    {
        CdState current = new(24.5, 56.8, 359_849, writeCue: false, interleave: true);
        CdState preset = new(22, 58, 333_000, writeCue: true, interleave: false);

        CdState applied = Assert.IsType<CdState>(UserPresetCatalog.ApplyGeometry(current, preset));

        Assert.Equal((22d, 58d, 333_000L), (applied.InnerRadiusMm, applied.OuterRadiusMm, applied.Sectors));
        Assert.False(applied.WriteCue);
        Assert.True(applied.Interleave);
        Assert.Throws<ArgumentException>(() => UserPresetCatalog.ApplyGeometry(current, new DvdState(24, 58, 2_295_104, 0, 0, 0)));
    }

    [Fact]
    public async Task Settings_edit_a_draft_until_apply_and_cancel_discards_it()
    {
        (ProjectLifecycleService projects, ProjectSettingsViewModel settings, _) = await CreateSettingsAsync();

        await settings.OpenAsync();
        settings.Name = "Renamed";
        settings.VolumeLabel = "NEW_LABEL";
        settings.CancelCommand.Execute(null);

        Assert.Equal("Settings project", projects.Current!.Identity.Name);
        Assert.False(projects.Dirty.IsDirty);

        await settings.OpenAsync();
        settings.ApplyCommand.Execute(null);
        Assert.False(projects.Dirty.IsDirty);

        await settings.OpenAsync();
        settings.Name = "Renamed";
        settings.VolumeLabel = "NEW_LABEL";
        settings.ApplyCommand.Execute(null);

        Assert.False(settings.IsOpen);
        Assert.Equal("Renamed", projects.Current!.Identity.Name);
        Assert.Equal("NEW_LABEL", projects.Current.Delivery.VolumeLabel);
        Assert.True(projects.Dirty.IsDirty);
    }

    [Fact]
    public async Task Folder_review_applies_only_accepted_matches_to_the_draft()
    {
        (ProjectLifecycleService projects, ProjectSettingsViewModel settings, string directory) = await CreateSettingsAsync();
        string search = Path.Combine(directory, "moved");
        Directory.CreateDirectory(search);
        File.WriteAllBytes(Path.Combine(search, "front.png"), [1]);
        File.WriteAllBytes(Path.Combine(search, "Back.png"), [1]);

        await settings.OpenAsync();
        Assert.Equal(2, settings.MissingAssetCount);
        await settings.ReviewFolderAsync(search);

        Assert.True(settings.IsReviewingProposals);
        RelinkProposalItem cased = settings.Proposals.Single(item => item.Proposal.Match == RelinkMatch.CaseDiffers);
        Assert.False(cased.IsAccepted);
        await settings.ApplyProposalsCommand.ExecuteAsync(null);

        Assert.Equal(Path.Combine(directory, "old", "front.png"), projects.Current!.Design.Layers[0].ImagePath);
        Assert.Equal(1, settings.MissingAssetCount);
        settings.ApplyCommand.Execute(null);

        Assert.Equal(Path.Combine(search, "front.png"), projects.Current!.Design.Layers[0].ImagePath);
        Assert.Equal(Path.Combine(directory, "old", "back.png"), projects.Current.Design.Layers[1].ImagePath);
        Assert.True(File.Exists(Path.Combine(search, "Back.png")));
    }

    private static async Task<(ProjectLifecycleService Projects, ProjectSettingsViewModel Settings, string Directory)> CreateSettingsAsync()
    {
        string directory = CreateTempDirectory();
        ProjectLifecycleService projects = new(
            new JsonProjectDocumentStore(),
            new PhysicalFileSystem(),
            new SystemClock(),
            new DiscardInteraction());
        ApplicationResult<ProjectWorkspace> created = await projects.CreateProjectAsync(new CreateProjectRequest(
            Path.Combine(directory, "settings.discproj"),
            "Settings project",
            new CdState(24.5, 56.8, 359_849, writeCue: true, interleave: true),
            new DeliveryState(Path.Combine(directory, "out.wav"), string.Empty, "CD_IMAGE"),
            new DesignState(
            [
                Layer("front", Path.Combine(directory, "old", "front.png")),
                Layer("back", Path.Combine(directory, "old", "back.png")),
            ])));
        Assert.True(created.Succeeded);
        ProjectSettingsViewModel settings = new(
            projects,
            Assets,
            new UserPresetCatalog(Path.Combine(directory, DiscPresetJsonStore.FileName)),
            new NoFilePicker(),
            new EnglishTextCatalog(),
            _ => []);
        return (projects, settings, directory);
    }

    private static ProjectWorkspace Workspace(
        IEnumerable<DesignLayerState> layers,
        string? dataDirectory = null,
        string? calibration = null)
        => new(
            new ProjectIdentity("Assets", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, calibration),
            new DesignState(layers),
            new ToneState(ToneProfile.Photo),
            new DeliveryState("out.wav", dataDirectory, "LABEL"),
            new CdState(24.5, 56.8, 359_849, writeCue: true, interleave: true));

    private static DesignLayerState Layer(string id, string path) => new(id, id, path, true, 0, 0, 0, 1);

    private static string CreateTempDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"disc-settings-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private sealed class DiscardInteraction : IUserInteraction
    {
        public Task<UnsavedChangesDecision> ConfirmUnsavedChangesAsync(
            UnsavedChangesRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult(UnsavedChangesDecision.Discard);
    }

    private sealed class NoFilePicker : IFilePickerService
    {
        public Task<IReadOnlyList<string>> OpenFilesAsync(OpenFilePickerRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>([]);

        public Task<string?> SaveFileAsync(SaveFilePickerRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);

        public Task<string?> OpenFolderAsync(FolderPickerRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);
    }
}
