using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MeetingRecorder.Core.Audio;
using MeetingRecorder.Core.Detection;
using MeetingRecorder.Core.Formatting;
using MeetingRecorder.Core.Recordings;
using Microsoft.UI.Dispatching;
using Serilog;

namespace MeetingRecorder.ViewModels;

public enum RecordingState
{
    Starting,
    Recording,
    Saving,
    Saved,
    Failed,
}

/// <summary>
/// Drives the recording window: Starting → Recording → Saving → Saved, or → Failed at any step.
/// </summary>
public sealed partial class RecordingSessionViewModel : ObservableObject
{
    private const float SilenceThreshold = 0.003f;  
    private static readonly TimeSpan SilentHintAfter = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SavedAutoClose = TimeSpan.FromSeconds(4);

    private readonly Func<MicSession?, IRecorder> _recorderFactory;
    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueueTimer _timer;
    private IRecorder? _recorder;
    private IRecorder? _pendingSave; 
    private DateTime _lastMicActivity;
    private bool _stopWhenStarted;
    private bool _closeWhenDone;
    private int _lastTitleSecond = -1;

    public RecordingSessionViewModel(Func<MicSession?, IRecorder> recorderFactory, MicSession? source, DispatcherQueue dispatcher)
    {
        _recorderFactory = recorderFactory;
        _dispatcher = dispatcher;
        Source = source;
        _timer = dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(100);
        _timer.Tick += (_, _) => OnTick();
        WindowTitle = "Starting… · Meeting Recorder";
    }

    public event EventHandler<RecordingEntry>? Saved;

    public event EventHandler<RecordingEntry>? ShowInListRequested;

    public event EventHandler? CloseRequested;

    public event EventHandler<bool>? ActiveChanged;

    public MicSession? Source { get; }

    public string? SourceApp => Source?.AppName;

    public string SourceText => SourceApp is null ? "Microphone and system audio" : $"From {SourceApp}";

    [ObservableProperty]
    public partial string MicrophoneDescription { get; set; } = "Microphone";

    [ObservableProperty]
    public partial string RetryText { get; set; } = "Retry";

    [ObservableProperty]
    public partial bool HasPendingSave { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStarting), nameof(IsRecording), nameof(IsSaving), nameof(IsSaved), nameof(IsFailed), nameof(IsActive))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    public partial RecordingState State { get; set; } = RecordingState.Starting;

    public bool IsStarting => State == RecordingState.Starting;
    public bool IsRecording => State == RecordingState.Recording;
    public bool IsSaving => State == RecordingState.Saving;
    public bool IsSaved => State == RecordingState.Saved;
    public bool IsFailed => State == RecordingState.Failed;
    public bool IsActive => State is RecordingState.Starting or RecordingState.Recording or RecordingState.Saving;

    [ObservableProperty]
    public partial string WindowTitle { get; set; }

    [ObservableProperty]
    public partial string ElapsedText { get; set; } = "00:00:00";

    [ObservableProperty]
    public partial double MicLevel { get; set; }

    [ObservableProperty]
    public partial double SystemLevel { get; set; }

    [ObservableProperty]
    public partial bool HasSystemAudio { get; set; } = true;

    [ObservableProperty]
    public partial bool ShowSilentMicHint { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWarning))]
    public partial string? WarningText { get; set; }

    public bool HasWarning => !string.IsNullOrEmpty(WarningText);

    [ObservableProperty]
    public partial double SaveProgress { get; set; }

    [ObservableProperty]
    public partial string SavedTitle { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSavedNote))]
    public partial string? SavedNote { get; set; }

    public bool HasSavedNote => !string.IsNullOrEmpty(SavedNote);

    [ObservableProperty]
    public partial string ErrorTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ErrorMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool ShowPrivacySettings { get; set; }

    [ObservableProperty]
    public partial bool ShowSoundSettings { get; set; }

    public RecordingEntry? SavedEntry { get; private set; }

    public async Task StartAsync()
    {
        await DiscardPendingSaveAsync();
        SetState(RecordingState.Starting);
        WarningText = null;
        ShowSilentMicHint = false;
        ElapsedText = "00:00:00";
        MicLevel = SystemLevel = 0;

        var recorder = _recorderFactory(Source);
        _recorder = recorder;
        recorder.Faulted += (_, ex) => _dispatcher.TryEnqueue(() => OnFaulted(recorder, ex));
        recorder.Warning += (_, w) => _dispatcher.TryEnqueue(() => WarningText = w.Message);

        try
        {
            await recorder.StartAsync();
        }
        catch (RecorderException ex)
        {
            Fail("Couldn't start recording", ex);
            return;
        }

        HasSystemAudio = recorder.HasSystemAudio;
        MicrophoneDescription = DescribeMicrophone(recorder);
        if (recorder.Warnings.Count > 0) WarningText = recorder.Warnings[^1].Message;
        _lastMicActivity = DateTime.UtcNow;
        SetState(RecordingState.Recording);
        _timer.Start();
        if (_stopWhenStarted) await StopAndSaveAsync(null);
    }

    private bool CanStop() => State == RecordingState.Recording;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private Task StopAsync() => StopAndSaveAsync(null);

    public bool RequestClose()
    {
        switch (State)
        {
            case RecordingState.Starting:
                _stopWhenStarted = true;
                _closeWhenDone = true;
                return false;
            case RecordingState.Recording:
                _closeWhenDone = true;
                _ = StopAndSaveAsync(null);
                return false;
            case RecordingState.Saving:
                _closeWhenDone = true;
                return false;
            default:
                _ = DiscardPendingSaveAsync();
                return true;
        }
    }

    public async Task FinishAsync()
    {
        if (State == RecordingState.Recording) await StopAndSaveAsync(null);
        while (State is RecordingState.Starting or RecordingState.Saving) await Task.Delay(50);
        if (State == RecordingState.Recording) await StopAndSaveAsync(null);
    }

    private async Task StopAndSaveAsync(RecorderException? cause)
    {
        if (State != RecordingState.Recording || _recorder is not { } recorder) return;
        _timer.Stop();
        await SaveAsync(recorder, cause);
    }

    private async Task SaveAsync(IRecorder recorder, RecorderException? cause)
    {
        SaveProgress = 0;
        SetState(RecordingState.Saving);
        var progress = new Progress<double>(p => SaveProgress = p * 100);
        bool keepForRetry = false;
        try
        {
            var result = await recorder.StopAndSaveAsync(progress);
            SavedEntry = result.Entry;
            SavedTitle = $"{result.Entry.Title} · {RecordingText.Duration(result.Entry.Duration)}";
            var notes = new List<string>();
            var endedBy = cause ?? result.EndedBy;
            if (endedBy is not null) notes.Add($"{endedBy.Message} What was recorded has been saved.");
            notes.AddRange(result.Warnings.Where(w => w.Kind == RecorderWarningKind.SavedAsWav).Select(w => w.Message));
            SavedNote = notes.Count > 0 ? string.Join(" ", notes) : null;
            SetState(RecordingState.Saved);
            Saved?.Invoke(this, result.Entry);

            if (_closeWhenDone)
            {
                CloseRequested?.Invoke(this, EventArgs.Empty);
            }
            else if (SavedNote is null)
            {
                _ = AutoCloseAsync();
            }
        }
        catch (RecorderException ex)
        {
            if (ReferenceEquals(_recorder, recorder)) _recorder = null;
            keepForRetry = recorder.CanRetrySave;
            if (keepForRetry)
            {
                _pendingSave = recorder;
                _closeWhenDone = false; 
            }
            Fail("Couldn't save the recording", ex, canRetrySave: keepForRetry);
        }
        finally
        {
            if (ReferenceEquals(_recorder, recorder)) _recorder = null;
            if (!keepForRetry) await recorder.DisposeAsync();
        }
    }

    private async Task DiscardPendingSaveAsync()
    {
        if (_pendingSave is not { } pending) return;
        _pendingSave = null;
        HasPendingSave = false;
        await pending.DisposeAsync();
    }

    private async Task AutoCloseAsync()
    {
        await Task.Delay(SavedAutoClose);
        if (State == RecordingState.Saved) CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnFaulted(IRecorder recorder, RecorderException ex)
    {
        if (!ReferenceEquals(recorder, _recorder)) return;
        Log.Warning("Recording ended by fault: {Kind}", ex.Kind);
        _ = StopAndSaveAsync(ex);
    }

    private void Fail(string title, RecorderException ex, bool canRetrySave = false)
    {
        _timer.Stop();
        ErrorTitle = title;
        ErrorMessage = ex.Message;
        HasPendingSave = canRetrySave;
        RetryText = canRetrySave ? "Try saving again"
            : ex.Kind == RecorderErrorKind.NothingRecorded ? "Record again"
            : "Retry";
        ShowPrivacySettings = ex.Kind == RecorderErrorKind.MicrophoneAccessDenied;
        ShowSoundSettings = ex.Kind is RecorderErrorKind.NoMicrophone or RecorderErrorKind.MicrophoneInUseExclusively
            or RecorderErrorKind.MicrophoneDisconnected;
        SetState(RecordingState.Failed);
        if (_recorder is { } r)
        {
            _recorder = null;
            _ = r.DisposeAsync().AsTask();
        }
        if (_closeWhenDone) CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private Task RetryAsync()
    {
        if (_pendingSave is not { } pending) return StartAsync();
        _pendingSave = null;
        HasPendingSave = false;
        return SaveAsync(pending, cause: null);
    }

    [RelayCommand]
    private async Task OpenPrivacySettingsAsync() =>
        await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:privacy-microphone"));

    [RelayCommand]
    private async Task OpenSoundSettingsAsync() =>
        await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:sound"));

    [RelayCommand]
    private void ShowInList()
    {
        if (SavedEntry is not null) ShowInListRequested?.Invoke(this, SavedEntry);
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Close()
    {
        if (RequestClose()) CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private void SetState(RecordingState state)
    {
        bool wasActive = IsActive;
        State = state;
        WindowTitle = state switch
        {
            RecordingState.Starting => "Starting… · Meeting Recorder",
            RecordingState.Recording => $"Recording {ElapsedText} · Meeting Recorder",
            RecordingState.Saving => "Saving… · Meeting Recorder",
            RecordingState.Saved => "Saved · Meeting Recorder",
            _ => "Recording failed · Meeting Recorder",
        };
        if (wasActive != IsActive || state == RecordingState.Starting) ActiveChanged?.Invoke(this, IsActive);
    }

    private void OnTick()
    {
        if (_recorder is not { } recorder || State != RecordingState.Recording) return;
        var elapsed = recorder.Elapsed;
        ElapsedText = RecordingText.Elapsed(elapsed);
        int second = (int)elapsed.TotalSeconds;
        if (second != _lastTitleSecond)
        {
            _lastTitleSecond = second;
            WindowTitle = $"Recording {ElapsedText} · Meeting Recorder";
        }

        var (mic, system) = recorder.TakePeaks();
        MicLevel = Smooth(MicLevel, ToMeter(mic));
        SystemLevel = Smooth(SystemLevel, ToMeter(system));

        var now = DateTime.UtcNow;
        if (mic > SilenceThreshold) _lastMicActivity = now;
        ShowSilentMicHint = now - _lastMicActivity > SilentHintAfter;
    }

    private string DescribeMicrophone(IRecorder recorder) => recorder.MicrophoneName is not { } name
        ? "Microphone"
        : recorder.MicrophoneChoice switch
        {
            DeviceChoiceReason.MeetingApp => $"{name} · the microphone {SourceApp ?? "your meeting app"} is using",
            DeviceChoiceReason.OtherApp => $"{name} · the microphone another app is using",
            _ => $"{name} · your default microphone",
        };
    internal static double ToMeter(float peak)
    {
        if (peak <= 0) return 0;
        double db = 20 * Math.Log10(peak);
        return Math.Clamp((db + 60) / 60, 0, 1);
    }
    private static double Smooth(double current, double target) => target >= current ? target : Math.Max(target, current - 0.06);
}
