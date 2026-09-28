using CommunityToolkit.Mvvm.ComponentModel;
using MeetingRecorder.Core.Formatting;
using MeetingRecorder.Core.Recordings;

namespace MeetingRecorder.ViewModels;

/// <summary>One row in the recordings list: a saved recording or a sample.</summary>
public sealed partial class RecordingItemViewModel : ObservableObject
{
    private RecordingItemViewModel(string id, string title, string? description, DateTimeOffset createdAt,
        TimeSpan duration, bool isSample, RecordingEntry? entry)
    {
        Id = id;
        Title = title;
        Description = description ?? string.Empty;
        CreatedAt = createdAt;
        Duration = duration;
        IsSample = isSample;
        Entry = entry;
        MetaText = RecordingText.DayAndTime(createdAt.LocalDateTime, DateTime.Today);
    }

    public static RecordingItemViewModel FromEntry(RecordingEntry entry) =>
        new(entry.Id, entry.Title, null, entry.CreatedAt, entry.Duration, isSample: false, entry);

    public static RecordingItemViewModel Sample(string id, string title, string description, DateTimeOffset createdAt, TimeSpan duration) =>
        new(id, title, description, createdAt, duration, isSample: true, entry: null);

    public string Id { get; }

    public string Title { get; }

    public string Description { get; }

    public bool HasDescription => Description.Length > 0;

    public DateTimeOffset CreatedAt { get; }

    public TimeSpan Duration { get; }

    public string DurationText => RecordingText.Duration(Duration);

    public string MetaText { get; }

    public bool IsSample { get; }

    public bool CanPlay => !IsSample;

    public RecordingEntry? Entry { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayButtonName), nameof(PlayToolTip))]
    public partial bool IsPlaying { get; set; }

    [ObservableProperty]
    public partial bool IsHighlighted { get; set; }

    public string PlayButtonName => IsSample
        ? $"{Title}, sample entry, {RecordingText.DurationSpoken(Duration)}"
        : $"{(IsPlaying ? "Stop" : "Play")} {Title}, {RecordingText.DurationSpoken(Duration)}";

    public string PlayToolTip => IsSample ? "Sample entry" : IsPlaying ? "Stop" : "Play";

    public override string ToString() =>
        $"{Title}{(HasDescription ? ", " + Description : string.Empty)}, {RecordingText.DurationSpoken(Duration)}, {MetaText}{(IsSample ? ", sample" : string.Empty)}";

    public void RefreshPlayingState() => OnPropertyChanged(nameof(IsPlaying));
}
