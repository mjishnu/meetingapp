using System.Diagnostics;
using MeetingRecorder.Core;
using MeetingRecorder.Core.Audio;
using MeetingRecorder.Core.Detection;
using MeetingRecorder.Core.Logging;
using MeetingRecorder.Core.Recordings;
using MeetingRecorder.Core.Settings;
using MeetingRecorder.Services;
using MeetingRecorder.ViewModels;
using MeetingRecorder.Views;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Serilog;

namespace MeetingRecorder;

/// <summary>Composition root. Detection runs independently of any window, so it keeps working while minimized.</summary>
public partial class App : Application
{
    private MainWindow? _mainWindow;
    private MicUsageMonitor? _monitor;
    private RecordingCoordinator? _coordinator;
    private MediaPlayerService? _player;
    private bool _firstFrameLogged;
    private bool _quitConfirmed;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) => Log.Error(e.Exception, "Unhandled UI exception");
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Log.Fatal(e.ExceptionObject as Exception, "Unhandled exception");
            Log.CloseAndFlush();
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error(e.Exception, "Unobserved task exception");
            e.SetObserved();
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        var paths = AppPaths.Default;
        LogSetup.Initialize(paths.LogsDirectory);
        Log.Information("Meeting Recorder {Version} starting ({ProcessPath})", typeof(App).Assembly.GetName().Version, Environment.ProcessPath);

        var settingsStore = new SettingsStore(paths.SettingsFile);
        var settings = settingsStore.Load();
        var probe = new MediaFoundationProbe();
        var store = new JsonRecordingStore(paths, probe);
        var finalizer = new RecordingFinalizer(paths, probe);
        _player = new MediaPlayerService(dispatcher);
        var recordings = new RecordingsViewModel(store, _player, settingsStore, settings, new PartialRecovery(finalizer, store));

        _mainWindow = new MainWindow(recordings);
        _mainWindow.Activated += OnFirstActivated;
        _mainWindow.AppWindow.Closing += OnMainWindowClosing;
        _mainWindow.Closed += (_, _) => OnMainWindowClosed();
        CompositionTarget.Rendering += OnFirstRendering;
        _mainWindow.Activate();

        // The index loads after the window is up; durations come from the index, so nothing probes files here.
        _ = recordings.LoadAsync();

        _monitor = new MicUsageMonitor(new RegistryMicUsageSource(), TimeProvider.System, new MicUsageMonitorOptions
        {
            SelfKey = Environment.ProcessPath is { } exe ? AppNames.KeyForExecutable(exe) : null,
        });
        _coordinator = new RecordingCoordinator(_monitor,
            source => new MeetingAudioRecorder(paths, finalizer, store, source),
            recordings, _mainWindow, settings, settingsStore, dispatcher);
        _monitor.Start();
    }

    /// <summary>Another launch was redirected here (single instance): bring the main window forward.</summary>
    internal void  OnRedirectedActivation() =>
        _mainWindow?.DispatcherQueue.TryEnqueue(() => _mainWindow.BringToFront());

    private async void OnMainWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_quitConfirmed || _coordinator is not { IsRecording: true }) return;
        args.Cancel = true;
        if (!await _mainWindow!.ConfirmQuitWhileRecordingAsync()) return;
        _quitConfirmed = true;
        await _coordinator.ShutdownAsync();
        _mainWindow.Close();
    }

    private async void OnMainWindowClosed()
    {
        Log.Information("Main window closed; exiting");
        try
        {
            if (_coordinator is not null) await _coordinator.ShutdownAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Shutdown failed");
        }
        _monitor?.Dispose();
        _player?.Dispose();
        await Log.CloseAndFlushAsync();
        Exit();
    }

    private void OnFirstActivated(object sender, WindowActivatedEventArgs args)
    {
        _mainWindow!.Activated -= OnFirstActivated;
        Log.Information("Startup: first Window.Activated after {ElapsedMs:F0} ms", SinceProcessStart());
    }

    private void OnFirstRendering(object? sender, object e)
    {
        if (_firstFrameLogged) return;
        _firstFrameLogged = true;
        CompositionTarget.Rendering -= OnFirstRendering;
        Log.Information("Startup: first frame rendered after {ElapsedMs:F0} ms", SinceProcessStart());
    }

    private static double SinceProcessStart() =>
        (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds;
}
