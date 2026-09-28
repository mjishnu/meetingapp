namespace MeetingRecorder.Core.Recordings;

public enum RecordingFormat
{
    M4a,
    Wav,
}

public sealed record RecordingEntry(
    string Id,
    string Title,
    DateTimeOffset CreatedAt,
    TimeSpan Duration,
    string FileName,
    RecordingFormat Format,
    string? SourceApp = null);
