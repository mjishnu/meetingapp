using MeetingRecorder.Services;
using MeetingRecorder.ViewModels;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MeetingRecorder.Views;

public sealed partial class MainWindow : Window
{
    private const double DefaultWidth = 1060, DefaultHeight = 760;
    private const double MinWidth = 480, MinHeight = 480;

    public MainWindow(RecordingsViewModel viewModel)
    {
        InitializeComponent();
        Page = new RecordingsPage(viewModel);
        NavView.Content = Page;

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon("Assets/AppIcon.ico");
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;

        if (AppWindow.Presenter is OverlappedPresenter presenter)
            WindowSizing.ApplyLimitsEpx(this, presenter, MinWidth, MinHeight);
        WindowSizing.ResizeEpx(this, DefaultWidth, DefaultHeight);
    }

    public RecordingsPage Page { get; }

    public void BringToFront()
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } p) p.Restore();
        Activate();
    }
    public async Task<bool> ConfirmQuitWhileRecordingAsync()
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "A recording is in progress",
            Content = "Stop the recording and save it before quitting?",
            PrimaryButtonText = "Stop, save and quit",
            CloseButtonText = "Keep recording",
            DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
    private void OnNavDisplayModeChanged(NavigationView sender, NavigationViewDisplayModeChangedEventArgs args) =>
        AppTitleBar.IsPaneToggleButtonVisible = args.DisplayMode != NavigationViewDisplayMode.Expanded;

    private void OnPaneToggleRequested(TitleBar sender, object args) => NavView.IsPaneOpen = !NavView.IsPaneOpen;
}
