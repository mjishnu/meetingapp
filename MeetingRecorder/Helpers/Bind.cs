using Microsoft.UI.Xaml;

namespace MeetingRecorder.Helpers;

/// <summary>Static functions for x:Bind.</summary>
public static class Bind
{
    public static Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility Collapsed(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public static bool Not(bool value) => !value;

    public static double Opacity(bool visible) => visible ? 1.0 : 0.0;

    /// <summary>Icons Play (E768) / Stop (E71A)</summary>
    public static string PlayGlyph(bool playing) => playing ? "" : "";

    /// <summary>0..1 meter level → 0..100 ProgressBar value for recording visualisation</summary>
    public static double Percent(double level) => Math.Clamp(level, 0, 1) * 100;
}
