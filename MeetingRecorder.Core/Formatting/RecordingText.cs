using System.Globalization;

namespace MeetingRecorder.Core.Formatting;

public static class RecordingText
{
    public static string DefaultTitle(DateTimeOffset createdAt)
    {
        var culture = CultureInfo.CurrentCulture;
        var local = createdAt.LocalDateTime;
        return $"Recording · {local.ToString("MMM d, yyyy", culture)}, {local.ToString(culture.DateTimeFormat.ShortTimePattern, culture)}";
    }

    public static string Duration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero) duration = TimeSpan.Zero;
        int totalSeconds = (int)Math.Floor(duration.TotalSeconds);
        int h = totalSeconds / 3600, m = totalSeconds / 60 % 60, s = totalSeconds % 60;
        return h > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{h}:{m:00}:{s:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{m:00}:{s:00}");
    }

    public static string Elapsed(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        int totalSeconds = (int)Math.Floor(elapsed.TotalSeconds);
        return string.Create(CultureInfo.InvariantCulture,
            $"{totalSeconds / 3600:00}:{totalSeconds / 60 % 60:00}:{totalSeconds % 60:00}");
    }

    public static string DurationSpoken(TimeSpan duration)
    {
        int totalSeconds = Math.Max(0, (int)Math.Floor(duration.TotalSeconds));
        int h = totalSeconds / 3600, m = totalSeconds / 60 % 60, s = totalSeconds % 60;
        var parts = new List<string>(3);
        if (h > 0) parts.Add(h == 1 ? "1 hour" : $"{h} hours");
        if (m > 0) parts.Add(m == 1 ? "1 minute" : $"{m} minutes");
        if (s > 0 || parts.Count == 0) parts.Add(s == 1 ? "1 second" : $"{s} seconds");
        return string.Join(' ', parts);
    }

    public static string DayGroup(DateTime localDate, DateTime today)
    {
        var culture = CultureInfo.CurrentCulture;
        int daysAgo = (today.Date - localDate.Date).Days;
        return daysAgo switch
        {
            0 => "Today",
            1 => "Yesterday",
            > 1 and < 7 => localDate.ToString("dddd", culture),
            _ => localDate.ToString(localDate.Year == today.Year ? "MMM d" : "MMM d, yyyy", culture),
        };
    }

    public static string DayAndTime(DateTime localDateTime, DateTime today)
    {
        var culture = CultureInfo.CurrentCulture;
        return $"{DayGroup(localDateTime, today)} · {localDateTime.ToString(culture.DateTimeFormat.ShortTimePattern, culture)}";
    }
}
