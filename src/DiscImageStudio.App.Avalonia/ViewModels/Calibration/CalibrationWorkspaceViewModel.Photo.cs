using System.Globalization;
using PhotoBitmap = global::Avalonia.Media.Imaging.Bitmap;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiscImageStudio.App.Avalonia.Design;
using DiscImageStudio.App.Avalonia.Platform;
using DiscImageStudio.Application.Calibration;
using DiscImageStudio.Application.Ports;
using DiscImageStudio.Core.Calibration;

namespace DiscImageStudio.App.Avalonia.ViewModels.Calibration;

public enum CalibrationStep
{
    Target,
    Photo,
    Curve,
}

/// <summary>
/// Step 2 (Task 12.3): the photo of the burned target, the handles on its physical edges, the
/// rectification and its review. The photo belongs to the session linked to the project: it is
/// written into that <c>.calibration.json</c> when the user saves or confirms, and kept as a
/// recovery draft in between, so a crash or a forced quit loses nothing.
/// </summary>
public sealed partial class CalibrationWorkspaceViewModel
{
    private static readonly FilePickerType PhotoFileType = new("Photo", ["*.jpg", "*.jpeg", "*.png", "*.bmp", "*.gif"], ["image/jpeg", "image/png"]);
    private static readonly TimeSpan DraftDelay = TimeSpan.FromSeconds(1.5);

    private readonly CalibrationPhotoStep _photo = new();
    private readonly CalibrationFitCoordinator _fitters = new();
    private CalibrationRecoveryStore? _recovery;
    private Debouncer? _draft;
    private CancellationTokenSource? _photoWork;

    /// <summary>Target id whose photo state is loaded in <see cref="_photo"/>.</summary>
    private string? _photoKey;

    /// <summary>The session the photo belongs to and its file; saves and drafts go there.</summary>
    private CalibrationSession? _photoOwner;
    private string? _photoOwnerPath;

    /// <summary>The photo state differs from the session file.</summary>
    private bool _photoUnsaved;

    private bool _loadingPhoto;

    public CalibrationPhotoCanvasViewModel PhotoCanvas => _photoCanvas ??= CreatePhotoCanvas();

    private CalibrationPhotoCanvasViewModel? _photoCanvas;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTargetStep), nameof(IsPhotoStep))]
    public partial CalibrationStep CurrentStep { get; private set; } = CalibrationStep.Target;

    public bool IsTargetStep => CurrentStep == CalibrationStep.Target;

    public bool IsPhotoStep => CurrentStep == CalibrationStep.Photo;

    /// <summary>The photo step works on the session linked to the project.</summary>
    public bool CanUsePhotoStep => _linkedSession is not null && _loadedLinkPath is not null;

    [ObservableProperty]
    public partial bool IsPhotoBusy { get; private set; }

    [ObservableProperty]
    public partial string PhotoBusyText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string PhotoInfo { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string RectificationText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string PhotoError { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string PhotoSaveText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasRecoveryDraft { get; private set; }

    [ObservableProperty]
    public partial string RecoveryText { get; private set; } = string.Empty;

    public bool HasPhoto => _photo.HasPhoto;

    public bool HasRectification => _photo.Rectification is not null;

    public bool IsRectificationConfirmed => _photo.Confirmed;

    public bool IsDisc12 => _photo.PhysicalOuterRadiusMm == 60;

    public bool IsDisc8 => _photo.PhysicalOuterRadiusMm == 40;

    public bool HasPhotoError => PhotoError.Length > 0;

    // ---- Labels ----

    public string PhotoTitle => _text["calibration.photo.title"];

    public string PhotoDescription => _text["calibration.photo.description"];

    public string ImportPhotoLabel => _text["calibration.photo.import"];

    public string DiscSizeLabel => _text["calibration.photo.discSize"];

    public string Disc12Label => _text["calibration.photo.disc12"];

    public string Disc8Label => _text["calibration.photo.disc8"];

    public string OrientationLabel => _text["calibration.photo.orientation"];

    public string MirrorLabel => _text["calibration.photo.mirror"];

    public string RotateLabel => _text["calibration.photo.rotate"];

    public string RectifyLabel => _text["calibration.photo.rectify"];

    public string ShowPhotoLabel => _text["calibration.photo.showPhoto"];

    public string ShowRectifiedLabel => _text["calibration.photo.showRectified"];

    public string ConfirmLabel => _text["calibration.photo.confirm"];

    public string ReopenLabel => _text["calibration.photo.reopen"];

    public string SaveSessionLabel => _text["calibration.photo.save"];

    public string ConfirmedText => _text["calibration.photo.confirmed"];

    public string RestoreDraftLabel => _text["calibration.recovery.restore"];

    public string DiscardDraftLabel => _text["calibration.recovery.discard"];

    public string NeedsSessionText => _text["calibration.photo.needsSession"];

    public string FitLabel => _text["calibration.photo.fit"];

    public string CancelPhotoWorkLabel => _text["export.run.cancel"];

    /// <summary>Called once by the shell: where recovery drafts live.</summary>
    public void AttachRecovery(CalibrationRecoveryStore recovery)
    {
        _recovery = recovery ?? throw new ArgumentNullException(nameof(recovery));
        _draft = new Debouncer(DraftDelay, () => _ = WriteDraftAsync());
    }

    // ---- Steps ----

    [RelayCommand]
    private void ShowTargetStep() => CurrentStep = CalibrationStep.Target;

    [RelayCommand(CanExecute = nameof(CanUsePhotoStep))]
    private async Task ShowPhotoStepAsync()
    {
        CurrentStep = CalibrationStep.Photo;
        await EnsurePhotoLoadedAsync();
    }

    partial void OnCurrentStepChanged(CalibrationStep value) => RefreshPhotoState();

    /// <summary>The linked session changed: drop the photo of another target, keep this one's.</summary>
    private void OnLinkedSessionChanged()
    {
        ShowPhotoStepCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanUsePhotoStep));
        if (_photoKey is not null && _photoKey != _linkedSession?.Target.Id)
        {
            ResetPhoto();
        }

        if (!CanUsePhotoStep && IsPhotoStep)
        {
            CurrentStep = CalibrationStep.Target;
        }
    }

    private void ResetPhoto()
    {
        _photoWork?.Cancel();
        _draft?.Cancel();
        FlushDraft();
        _photoKey = null;
        _photoOwner = null;
        _photoOwnerPath = null;
        _photoUnsaved = false;
        _loadingPhoto = true;
        try
        {
            _photo.Clear();
        }
        finally
        {
            _loadingPhoto = false;
        }

        PhotoCanvas.SetPhotoBitmap(null);
        PhotoCanvas.SetRectifiedBitmap(null);
        PhotoCanvas.ShowRectified = false;
        HasRecoveryDraft = false;
        PhotoInfo = string.Empty;
        PhotoError = string.Empty;
        PhotoSaveText = string.Empty;
        RefreshPhotoState();
    }

    /// <summary>Loads the linked session's photo, and offers a newer recovery draft.</summary>
    private async Task EnsurePhotoLoadedAsync()
    {
        if (_linkedSession is not { } session || _loadedLinkPath is not { } path || _photoKey == session.Target.Id)
        {
            return;
        }

        _photoKey = session.Target.Id;
        _photoOwner = session;
        _photoOwnerPath = path;
        HasRecoveryDraft = false;
        if (_recovery?.List().FirstOrDefault(draft => draft.Key == session.Target.Id) is { } draft
            && draft.SavedUtc > File.GetLastWriteTimeUtc(path))
        {
            HasRecoveryDraft = true;
            RecoveryText = string.Format(
                CultureInfo.CurrentUICulture,
                _text["calibration.recovery.text"],
                draft.SavedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentUICulture),
                draft.TracePointCount);
        }

        if (session.Photo is { } photo)
        {
            await RestorePhotoAsync(photo);
        }

        PhotoSaveText = session.Photo is null ? string.Empty : string.Format(CultureInfo.CurrentUICulture, _text["calibration.photo.savedIn"], Path.GetFileName(path));
    }

    [RelayCommand]
    private async Task RestoreDraftAsync()
    {
        if (_recovery is null || _photoOwner is not { } session)
        {
            return;
        }

        HasRecoveryDraft = false;
        try
        {
            CalibrationSession draft = await Task.Run(() => _recovery.Load(session.Target.Id));
            if (draft.Photo is { } photo)
            {
                await RestorePhotoAsync(photo);
                _photoUnsaved = true;
                PhotoSaveText = _text["calibration.photo.unsaved"];
            }
        }
        catch (CalibrationException exception)
        {
            PhotoError = ErrorText(exception);
        }
    }

    [RelayCommand]
    private void DiscardDraft()
    {
        if (_photoOwner is { } session)
        {
            _recovery?.Discard(session.Target.Id);
        }

        HasRecoveryDraft = false;
    }

    // ---- Photo ----

    [RelayCommand]
    private async Task ImportPhotoAsync()
    {
        IReadOnlyList<string> files = await _filePicker.OpenFilesAsync(new OpenFilePickerRequest(
            _text["calibration.photo.pickerTitle"],
            SuggestedDirectory: ProjectDirectory(),
            FileTypes: [PhotoFileType]));
        if (files.Count > 0)
        {
            await ImportPhotoFromAsync(files[0]);
        }
    }

    /// <summary>Loads a photo file (also used by the harness, which has no picker).</summary>
    public async Task ImportPhotoFromAsync(string path)
    {
        PhotoError = string.Empty;
        CancellationToken token = BeginPhotoWork(_text["calibration.photo.reading"]);
        try
        {
            CalibrationPhoto photo = await Task.Run(() => CalibrationPhotoLoader.Load(path, CalibrationPhotoLoader.DefaultMaximumSide, token), token);
            PhotoBitmap bitmap = await Task.Run(() => (PhotoBitmap)CompositionBitmap.ToBitmap(photo.Raster), token);
            _photo.Import(photo);
            PhotoCanvas.SetPhotoBitmap(bitmap);
            PhotoCanvas.SetRectifiedBitmap(null);
            PhotoCanvas.ShowRectified = false;
            PhotoCanvas.FitViewCommand.Execute(null);
            PhotoInfo = PhotoSizeText(photo);
        }
        catch (OperationCanceledException)
        {
        }
        catch (CalibrationException exception)
        {
            PhotoError = ErrorText(exception);
        }
        finally
        {
            EndPhotoWork();
        }
    }

    [RelayCommand]
    private void UseDisc12() => _photo.SetDiscRadius(60);

    [RelayCommand]
    private void UseDisc8() => _photo.SetDiscRadius(40);

    [RelayCommand]
    private async Task MirrorPhotoAsync()
    {
        _photo.MirrorHorizontally();
        await RefreshPhotoBitmapAsync();
    }

    [RelayCommand]
    private async Task RotatePhotoAsync()
    {
        _photo.RotateClockwise();
        await RefreshPhotoBitmapAsync();
        PhotoCanvas.FitViewCommand.Execute(null);
    }

    [RelayCommand]
    private async Task RectifyAsync()
    {
        if (!_photo.CanRectify || _photo.Photo is not { } photo)
        {
            return;
        }

        PhotoError = string.Empty;
        long revision = _photo.Revision;
        CancellationToken token = BeginPhotoWork(_text["calibration.photo.rectifying"]);
        try
        {
            CalibrationRectification rectification = await _fitters.RectifyAsync(
                photo, _photo.Outer, _photo.Hole, _photo.PhysicalOuterRadiusMm, cancellationToken: token);
            PhotoBitmap bitmap = await Task.Run(() => (PhotoBitmap)CompositionBitmap.ToBitmap(rectification.Rectified), token);
            if (_photo.ApplyRectification(rectification, revision))
            {
                PhotoCanvas.SetRectifiedBitmap(bitmap);
                PhotoCanvas.ShowRectified = true;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (CalibrationException exception)
        {
            PhotoError = exception.Code == "calibration.photo.rectifyFailed"
                ? _text["calibration.photo.rectifyFailed"]
                : ErrorText(exception);
        }
        finally
        {
            EndPhotoWork();
        }
    }

    [RelayCommand]
    private void CancelPhotoWork() => _photoWork?.Cancel();

    [RelayCommand]
    private void ShowPhoto() => PhotoCanvas.ShowRectified = false;

    [RelayCommand]
    private void ShowRectifiedView()
    {
        if (HasRectification)
        {
            PhotoCanvas.ShowRectified = true;
        }
    }

    /// <summary>The rectified disc looks right: mark it confirmed and save the session.</summary>
    [RelayCommand]
    private async Task ConfirmRectificationAsync()
    {
        if (_photo.Confirm())
        {
            PhotoCanvas.ShowRectified = true;
            await SavePhotoSessionAsync();
        }
    }

    [RelayCommand]
    private void ReopenEdges()
    {
        _photo.Reopen();
        PhotoCanvas.ShowRectified = false;
    }

    [RelayCommand]
    private Task SavePhotoAsync() => SavePhotoSessionAsync();

    /// <summary>Writes the photo state into the linked session file and drops the draft.</summary>
    private async Task SavePhotoSessionAsync()
    {
        if (_photoOwner is not { } session || _photoOwnerPath is not { } path)
        {
            return;
        }

        CalibrationSession updated = session with { Photo = _photo.ToSession() };
        try
        {
            await Task.Run(() => CalibrationSessionStore.Save(path, updated));
            _photoOwner = updated;
            if (_linkedSession?.Target.Id == updated.Target.Id)
            {
                _linkedSession = updated;
            }

            _photoUnsaved = false;
            _draft?.Cancel();
            _recovery?.Discard(session.Target.Id);
            HasRecoveryDraft = false;
            PhotoSaveText = string.Format(CultureInfo.CurrentUICulture, _text["calibration.photo.savedIn"], Path.GetFileName(path));
            LinkedSessionText = SessionText(updated);
        }
        catch (CalibrationException exception)
        {
            PhotoError = ErrorText(exception);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            PhotoError = string.Format(CultureInfo.CurrentUICulture, _text["export.failure.io"], exception.Message);
        }
    }

    // ---- Helpers ----

    private CalibrationPhotoCanvasViewModel CreatePhotoCanvas()
    {
        CalibrationPhotoCanvasViewModel canvas = new(_photo, _text);
        _photo.Changed += (_, _) => OnPhotoChanged();
        return canvas;
    }

    private void OnPhotoChanged()
    {
        if (_photo.Rectification is null)
        {
            PhotoCanvas.SetRectifiedBitmap(null);
            PhotoCanvas.ShowRectified = false;
        }

        if (!_loadingPhoto && _photo.HasPhoto)
        {
            _photoUnsaved = true;
            PhotoSaveText = _text["calibration.photo.unsaved"];
            _draft?.Schedule();
        }

        RefreshPhotoState();
    }

    private async Task RestorePhotoAsync(CalibrationPhotoSession session)
    {
        PhotoError = string.Empty;
        CancellationToken token = BeginPhotoWork(_text["calibration.photo.restoring"]);
        _loadingPhoto = true;
        try
        {
            (CalibrationPhoto decoded, PhotoBitmap bitmap) = await Task.Run(
                () =>
                {
                    CalibrationPhoto restored = CalibrationPhotoLoader.FromSession(session);
                    return (restored, (PhotoBitmap)CompositionBitmap.ToBitmap(restored.Raster));
                },
                token);
            _photo.Restore(session, decoded);
            PhotoCanvas.SetPhotoBitmap(bitmap);
            PhotoCanvas.ShowRectified = false;
            PhotoCanvas.FitViewCommand.Execute(null);
            PhotoInfo = PhotoSizeText(decoded);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (CalibrationException exception)
        {
            PhotoError = ErrorText(exception);
            return;
        }
        finally
        {
            _loadingPhoto = false;
            EndPhotoWork();
        }

        // A confirmed session is rectified again (the transform is not stored) and shown that way.
        if (_photo.RestoredAsConfirmed)
        {
            _loadingPhoto = true;
            try
            {
                await RectifyAsync();
            }
            finally
            {
                _loadingPhoto = false;
            }
        }
    }

    private async Task RefreshPhotoBitmapAsync()
    {
        if (_photo.Photo is { } photo)
        {
            PhotoCanvas.SetPhotoBitmap(await Task.Run(() => (PhotoBitmap)CompositionBitmap.ToBitmap(photo.Raster)));
            PhotoInfo = PhotoSizeText(photo);
        }
    }

    private async Task WriteDraftAsync()
    {
        if (_recovery is not { } recovery || _photoOwner is not { } session || !_photoUnsaved || !_photo.HasPhoto)
        {
            return;
        }

        CalibrationSession draft = session with { Photo = _photo.ToSession() };
        string? sessionPath = _photoOwnerPath;
        string? projectPath = _projects.ProjectFilePath;
        try
        {
            await Task.Run(() => recovery.Save(draft, sessionPath, projectPath));
        }
        catch (Exception exception) when (exception is CalibrationException or IOException or UnauthorizedAccessException)
        {
            // A draft is a safety net; failing to write one must not interrupt the work.
        }
    }

    /// <summary>Writes unsaved photo work as a draft now (before the photo is dropped).</summary>
    private void FlushDraft()
    {
        if (_recovery is not { } recovery || _photoOwner is not { } session || !_photoUnsaved || !_photo.HasPhoto)
        {
            return;
        }

        try
        {
            recovery.Save(session with { Photo = _photo.ToSession() }, _photoOwnerPath, _projects.ProjectFilePath);
        }
        catch (Exception exception) when (exception is CalibrationException or IOException or UnauthorizedAccessException)
        {
        }
    }

    private CancellationToken BeginPhotoWork(string text)
    {
        _photoWork?.Cancel();
        _photoWork = new CancellationTokenSource();
        IsPhotoBusy = true;
        PhotoBusyText = text;
        RefreshPhotoState();
        return _photoWork.Token;
    }

    private void EndPhotoWork()
    {
        IsPhotoBusy = false;
        PhotoBusyText = string.Empty;
        RefreshPhotoState();
    }

    private void RefreshPhotoState()
    {
        PhotoCanvas.CanEdit = IsPhotoStep && _photo.HasPhoto && !IsPhotoBusy && !_photo.Confirmed;
        RectificationText = _photo.Rectification is { } rectification
            ? string.Format(
                CultureInfo.CurrentUICulture,
                _text["calibration.photo.rectification"],
                rectification.Fit.OuterRmsPixels,
                rectification.Fit.HoleRmsPixels)
            : string.Empty;
        OnPropertyChanged(nameof(HasPhoto));
        OnPropertyChanged(nameof(HasRectification));
        OnPropertyChanged(nameof(IsRectificationConfirmed));
        OnPropertyChanged(nameof(IsDisc12));
        OnPropertyChanged(nameof(IsDisc8));
        OnPropertyChanged(nameof(HasPhotoError));
    }

    partial void OnPhotoErrorChanged(string value) => OnPropertyChanged(nameof(HasPhotoError));

    private string PhotoSizeText(CalibrationPhoto photo)
        => photo.IsReduced
            ? string.Format(CultureInfo.CurrentUICulture, _text["calibration.photo.info.reduced"], photo.OriginalWidth, photo.OriginalHeight, photo.Width, photo.Height)
            : string.Format(CultureInfo.CurrentUICulture, _text["calibration.photo.info"], photo.Width, photo.Height);

    private string ErrorText(CalibrationException exception)
    {
        try
        {
            return _text[$"calibration.error.{exception.Code}"];
        }
        catch (KeyNotFoundException)
        {
            return string.Format(CultureInfo.CurrentUICulture, _text["calibration.error.unknown"], exception.Code);
        }
    }
}
