using MeetingRecorder.Core.Formatting;
using MeetingRecorder.Core.Recordings;
using Serilog;

namespace MeetingRecorder.Core.Audio;

internal sealed class RecordingSaver(string id, string partialWavPath, DateTimeOffset startedAt, string? sourceApp,
    RecordingFinalizer finalizer, IRecordingStore store)
{
    private FinalizedRecording? _finalized;
    private bool _discarded;

    public bool IsSaved { get; private set; }

    public bool CanRetry => !IsSaved && !_discarded && (_finalized is not null || File.Exists(partialWavPath));

    public async Task<(RecordingEntry Entry, RecorderWarning? Warning)> SaveAsync(IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (IsSaved) throw new InvalidOperationException("Recording already saved.");
        try
        {
            _finalized ??= await Task.Run(() => finalizer.Finalize(id, partialWavPath, progress), cancellationToken).ConfigureAwait(false);
        }
        catch (RecorderException ex) when (ex.Kind == RecorderErrorKind.NothingRecorded)
        {
            _discarded = true;
            TryDelete(partialWavPath);
            throw;
        }

        var entry = new RecordingEntry(id, RecordingText.DefaultTitle(startedAt), startedAt,
            _finalized.Duration, _finalized.FileName, _finalized.Format, sourceApp);
        try
        {
            await store.AddAsync(entry, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error(ex, "Indexing the recording failed");
            throw RecorderErrorMapper.ForWrite(ex);
        }
        IsSaved = true;
        return (entry, _finalized.Warning);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Warning(ex, "Could not delete {Path}", path); }
    }
}
