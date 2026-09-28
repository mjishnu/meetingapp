using MeetingRecorder.Core.Audio;
using MeetingRecorder.Core.Detection;
using MeetingRecorder.Core.Settings;
using MeetingRecorder.ViewModels;
using MeetingRecorder.Views;
using Microsoft.UI.Dispatching;
using Serilog;

namespace MeetingRecorder.Services;

/// <summary>
/// mic session detected -> prompt -> Record -> recording window -> saved -> row appears.
/// </summary>
internal sealed class RecordingCoordinator
{
    private readonly IMicUsageMonitor _monitor;
    private readonly Func<MicSession?, IRecorder> _recorderFactory;
    private readonly RecordingsViewModel _list;
    private readonly MainWindow _mainWindow;
    private readonly AppSettings _settings;
    private readonly SettingsStore _settingsStore;
    private readonly DispatcherQueue _dispatcher;
    private PromptWindow? _prompt;
    private RecordingWindow? _recordingWindow;

    public RecordingCoordinator(IMicUsageMonitor monitor, Func<MicSession?, IRecorder> recorderFactory, RecordingsViewModel list,
        MainWindow mainWindow, AppSettings settings, SettingsStore settingsStore, DispatcherQueue dispatcher)
    {
        _monitor = monitor;
        _recorderFactory = recorderFactory;
        _list = list;
        _mainWindow = mainWindow;
        _settings = settings;
        _settingsStore = settingsStore;
        _dispatcher = dispatcher;

        _monitor.SessionDetected += (_, s) => _dispatcher.TryEnqueue(() => OnSessionDetected(s));
        _monitor.SessionEnded += (_, s) => _dispatcher.TryEnqueue(() => OnSessionEnded(s));
        _list.RecordRequested += (_, _) => StartFromMainWindow();
    }

    public bool IsRecording => _recordingWindow?.ViewModel.IsActive == true;

    private PromptWindow Prompt
    {
        get
        {
            if (_prompt is null)
            {
                _prompt = new PromptWindow();
                _prompt.RecordRequested += (_, s) =>
                {
                    _monitor.Accept(s);
                    StartRecording(s, activate: false);
                };
                _prompt.Dismissed += (_, s) => _monitor.Dismiss(s);
            }
            return _prompt;
        }
    }

    private void OnSessionDetected(MicSession session)
    {
        if (_recordingWindow is not null)
        {
            // A recording window is open don't stack a prompt on it.
            _monitor.Dismiss(session);
            return;
        }
        if (_prompt?.IsShowing == true) return;
        Log.Information("Showing prompt for {App}", session.RawName);
        Prompt.Show(session);
    }

    private void OnSessionEnded(MicSession session)
    {
        if (_prompt?.Session == session) _prompt.HideSilently();
    }

    private void StartFromMainWindow()
    {
        if (_recordingWindow is not null)
        {
            _recordingWindow.BringToFront();
            return;
        }
        var pending = _prompt?.Session;
        if (pending is not null)
        {
            _monitor.Accept(pending);
            _prompt!.HideSilently();
        }
        StartRecording(pending, activate: true);
    }

    private void StartRecording(MicSession? source, bool activate)
    {
        _monitor.SuppressPrompts = true;
        _list.IsRecordingActive = true;

        var vm = new RecordingSessionViewModel(_recorderFactory, source, _dispatcher);
        var window = new RecordingWindow(vm, _settings.KeepRecordingWindowOnTop);
        _recordingWindow = window;

        vm.Saved += (_, entry) => _list.AddSaved(entry, highlight: true);
        vm.ShowInListRequested += (_, entry) =>
        {
            _mainWindow.BringToFront();
            _list.Highlight(entry.Id);
        };
        vm.ActiveChanged += (_, active) =>
        {
            _monitor.SuppressPrompts = active;
            _list.IsRecordingActive = active;
        };
        window.KeepOnTopChanged += (_, onTop) =>
        {
            _settings.KeepRecordingWindowOnTop = onTop;
            _ = Task.Run(() => _settingsStore.Save(_settings));
        };
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_recordingWindow, window)) _recordingWindow = null;
            _monitor.SuppressPrompts = false;
            _list.IsRecordingActive = false;
        };

        window.ShowNearWorkArea(activate);
        _ = vm.StartAsync();
    }

    public async Task ShutdownAsync()
    {
        if (_recordingWindow is { } window)
        {
            await window.ViewModel.FinishAsync();
            window.Close();
        }
        if (_prompt is not null)
        {
            _prompt.AllowClose = true;
            _prompt.Close();
        }
    }
}
