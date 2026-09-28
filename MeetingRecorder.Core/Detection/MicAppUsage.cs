namespace MeetingRecorder.Core.Detection;

public sealed record MicAppUsage(string Key, long LastUsedTimeStart, long LastUsedTimeStop)
{
    public const string NonPackagedPrefix = @"NonPackaged\";

    public bool InUse => LastUsedTimeStart != 0 && LastUsedTimeStop == 0;

    public bool IsPackaged => !Key.StartsWith(NonPackagedPrefix, StringComparison.OrdinalIgnoreCase);
}

public interface IMicUsageSource : IDisposable
{
    event EventHandler? Changed;

    IReadOnlyList<MicAppUsage> ReadSnapshot();

    void Start();
}

public sealed record MicSession(string AppKey, long StartFileTime, string? AppName, string RawName)
{
    public string PromptText => AppName is null
        ? "An app is using your microphone"
        : $"{AppName} is using your microphone";
}

public interface IMicUsageMonitor : IDisposable
{
    event EventHandler<MicSession>? SessionDetected;

    event EventHandler<MicSession>? SessionEnded;

    bool SuppressPrompts { get; set; }

    void Dismiss(MicSession session);

    void Accept(MicSession session);

    void Start();
}
