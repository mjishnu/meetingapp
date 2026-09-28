using MeetingRecorder.Core.Detection;
using MeetingRecorder.Interop;
using MeetingRecorder.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.UI.ViewManagement;

namespace MeetingRecorder.Views;
public sealed partial class PromptWindow : Window
{
    private const double WidthEpx = 360, HeightEpx = 116, MarginEpx = 12;
    private static readonly TimeSpan AutoHideAfter = TimeSpan.FromSeconds(20);

    private readonly DispatcherQueueTimer _autoHide;
    private readonly UISettings _uiSettings = new();

    public PromptWindow()
    {
        InitializeComponent();

        var presenter = OverlappedPresenter.CreateForToolWindow();
        presenter.IsAlwaysOnTop = true;
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.SetBorderAndTitleBar(hasBorder: false, hasTitleBar: false);
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false;
        AppWindow.Title = "Meeting Recorder";

        var hwnd = WindowSizing.Hwnd(this);
        NativeMethods.MakeNonActivating(hwnd);
        NativeMethods.RemoveNonClientFrame(hwnd);
        NativeMethods.UseRoundedCorners(hwnd);

        _autoHide = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _autoHide.Interval = AutoHideAfter;
        _autoHide.IsRepeating = false;
        _autoHide.Tick += (_, _) => Finish(timedOut: true);

        AppWindow.Closing += (_, e) =>
        {
            if (!AllowClose)
            {
                e.Cancel = true;
                Finish(timedOut: false);
            }
        };
    }
    public bool AllowClose { get; set; }

    public MicSession? Session { get; private set; }

    public bool IsShowing => Session is not null;

    public event EventHandler<MicSession>? RecordRequested;
    public event EventHandler<MicSession>? Dismissed;

    public void Show(MicSession session)
    {
        Session = session;
        PromptTitle.Text = session.PromptText;
        PromptDetail.Text = session.AppName is null ? $"{session.RawName} · Record this meeting?" : "Record this meeting?";

        WindowSizing.PlaceBottomRightOfForegroundMonitor(this, WidthEpx, HeightEpx, MarginEpx);
        AppWindow.Show(activateWindow: false);
        AppWindow.MoveInZOrderAtTop();

        if (_uiSettings.AnimationsEnabled)
        {
            EnterStoryboard.Begin();
        }
        else
        {
            ContentGrid.Opacity = 1;
            ContentSlide.X = 0;
        }
        _autoHide.Stop();
        _autoHide.Start();
    }
    public void HideSilently()
    {
        _autoHide.Stop();
        Session = null;
        AppWindow.Hide();
    }

    private void OnRecordClick(object sender, RoutedEventArgs e)
    {
        var session = Session;
        HideSilently();
        if (session is not null) RecordRequested?.Invoke(this, session);
    }

    private void OnDismissClick(object sender, RoutedEventArgs e) => Finish(timedOut: false);

    private void Finish(bool timedOut)
    {
        var session = Session;
        HideSilently();
        if (session is null) return;
        Serilog.Log.Information(timedOut ? "Prompt timed out (treated as dismiss)" : "Prompt dismissed");
        Dismissed?.Invoke(this, session);
    }
}
