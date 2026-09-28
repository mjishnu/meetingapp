using MeetingRecorder.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MeetingRecorder.Views;

public sealed partial class RecordingsPage : Page
{
    public RecordingsPage(RecordingsViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        ViewModel.ScrollIntoViewRequested += (_, item) => RecordingsList.ScrollIntoView(item, ScrollIntoViewAlignment.Default);
    }

    public RecordingsViewModel ViewModel { get; }
    private void OnPlayClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is RecordingItemViewModel item)
            ViewModel.TogglePlaybackCommand.Execute(item);
    }

    private void OnItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is RecordingItemViewModel { CanPlay: true } item)
            ViewModel.TogglePlaybackCommand.Execute(item);
    }

    private void OnPlaybackErrorClosed(InfoBar sender, InfoBarClosedEventArgs args) => ViewModel.DismissPlaybackErrorCommand.Execute(null);

    private void OnRecoveryErrorClosed(InfoBar sender, InfoBarClosedEventArgs args) => ViewModel.DismissRecoveryErrorCommand.Execute(null);
}
