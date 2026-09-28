namespace MeetingRecorder.ViewModels;
internal static class SampleRecordings
{
    public static IReadOnlyList<RecordingItemViewModel> Create(DateTime today)
    {
        var yesterday = today.AddDays(-1);
        return
        [
            Make(1, "Product design review", "Ideas for a simpler onboarding experience.", today.AddHours(10).AddMinutes(30), 24, 18),
            Make(2, "Product design review", "Ideas for a simpler onboarding experience.", today.AddHours(10).AddMinutes(30), 24, 18),
            Make(3, "Product design review", "Ideas for a simpler onboarding experience.", today.AddHours(10).AddMinutes(30), 24, 18),
            Make(4, "Product design review", "Ideas for a simpler onboarding experience.", today.AddHours(10).AddMinutes(30), 24, 18),
            Make(5, "Product design review", "Ideas for a simpler onboarding experience.", yesterday.AddHours(10).AddMinutes(30), 24, 18),
            Make(6, "Product design review", "Ideas for a simpler onboarding experience.", yesterday.AddHours(10).AddMinutes(30), 24, 18),
            Make(7, "Product design review", "Ideas for a simpler onboarding experience.", yesterday.AddHours(10).AddMinutes(30), 24, 18),

        ];
    }

    private static RecordingItemViewModel Make(int n, string title, string description, DateTime local, int minutes, int seconds) =>
        RecordingItemViewModel.Sample($"sample{n}", title, description, new DateTimeOffset(local), new TimeSpan(0, minutes, seconds));
}
