using System.ComponentModel;
using MeetingRecorder.Services;
using MeetingRecorder.ViewModels;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.UI.ViewManagement;

namespace MeetingRecorder.Views;

public sealed partial class RecordingWindow : Window
{
    private const double WidthEpx = 400, HeightEpx = 280;
    private readonly UISettings _uiSettings = new();
    private readonly OverlappedPresenter _presenter;
    private bool _isActive;

    public RecordingWindow(RecordingSessionViewModel viewModel, bool keepOnTop)
    {
        ViewModel = viewModel;
        InitializeComponent();
        Bindings.Update();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(WindowTitleBar);
        AppWindow.SetIcon("Assets/AppIcon.ico");
        AppWindow.Title = viewModel.WindowTitle;
        Root.Loaded += (_, _) => AppWindow.Title = ViewModel.WindowTitle;

        _presenter = OverlappedPresenter.Create();
        _presenter.IsMaximizable = false;
        _presenter.IsMinimizable = true;
        _presenter.IsResizable = true;
        _presenter.IsAlwaysOnTop = keepOnTop;
        AppWindow.SetPresenter(_presenter);
        WindowSizing.ApplyLimitsEpx(this, _presenter, 360, 260, 640, 440);
        KeepOnTopToggle.IsChecked = keepOnTop;

        Activated += (_, e) => _isActive = e.WindowActivationState != WindowActivationState.Deactivated;
        AppWindow.Closing += OnClosing;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        viewModel.CloseRequested += (_, _) => Close();
        Closed += (_, _) =>
        {
            viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            PulseStoryboard.Stop();
        };
    }

    public RecordingSessionViewModel ViewModel { get; }

    public event EventHandler<bool>? KeepOnTopChanged;
    public void ShowNearWorkArea(bool activate)
    {
        WindowSizing.PlaceBottomRightOfForegroundMonitor(this, WidthEpx, HeightEpx, 12);
        if (activate)
        {
            Activate();
        }
        else
        {
            AppWindow.Show(activateWindow: false);
        }
    }

    public void BringToFront()
    {
        if (_presenter.State == OverlappedPresenterState.Minimized) _presenter.Restore();
        Activate();
    }

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (!ViewModel.RequestClose()) args.Cancel = true;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(RecordingSessionViewModel.WindowTitle):
                AppWindow.Title = ViewModel.WindowTitle;
                break;
            case nameof(RecordingSessionViewModel.State):
                UpdatePulse();
                if (ViewModel.IsRecording && _isActive) StopButton.Focus(FocusState.Programmatic);
                break;
        }
    }

    private void UpdatePulse()
    {
        if (ViewModel.IsRecording && _uiSettings.AnimationsEnabled)
        {
            PulseStoryboard.Begin();
        }
        else
        {
            PulseStoryboard.Stop();
            RecordingDot.Opacity = 1;
        }
    }

    private void OnKeepOnTopChanged(object sender, RoutedEventArgs e)
    {
        bool onTop = KeepOnTopToggle.IsChecked == true;
        _presenter.IsAlwaysOnTop = onTop;
        KeepOnTopChanged?.Invoke(this, onTop);
    }
}
