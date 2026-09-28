using System.Collections.ObjectModel;
using MeetingRecorder.Core.Formatting;

namespace MeetingRecorder.ViewModels;

/// <summary>A day's recordings ("Today", "Yesterday", …) for the grouped ListView.</summary>
public sealed partial class RecordingGroup : ObservableCollection<RecordingItemViewModel>
{
    public RecordingGroup(DateTime day, IEnumerable<RecordingItemViewModel> items) : base(items)
    {
        Day = day.Date;
        Header = RecordingText.DayGroup(Day, DateTime.Today);
    }

    public DateTime Day { get; }

    public string Header { get; }

    /// <summary>Order inside a day: real recordings first (newest first), then samples</summary>
    public static int Compare(RecordingItemViewModel a, RecordingItemViewModel b)
    {
        if (a.IsSample != b.IsSample) return a.IsSample ? 1 : -1;
        return b.CreatedAt.CompareTo(a.CreatedAt);
    }
}
