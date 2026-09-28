namespace MeetingRecorder.Core.Recordings;

public interface IRecordingStore
{
    IReadOnlyList<RecordingEntry> Entries { get; }

    Task<StoreLoadResult> LoadAsync(CancellationToken cancellationToken = default);

    Task AddAsync(RecordingEntry entry, CancellationToken cancellationToken = default);

    Task RemoveAsync(string id, CancellationToken cancellationToken = default);

    string GetFullPath(RecordingEntry entry);
}

public sealed record StoreLoadResult(
    IReadOnlyList<RecordingEntry> Entries,
    int DroppedMissing,
    int Reindexed,
    IReadOnlyList<string> OrphanedPartials,
    bool IndexWasCorrupt);

public interface IAudioFileProbe
{
    TimeSpan? TryGetDuration(string path);
}
