using MeetingRecorder.Core.Detection;
using MeetingRecorder.Core.Formatting;
using MeetingRecorder.Core.Recordings;
using Serilog;

namespace MeetingRecorder.Core.Audio;

public sealed class MeetingAudioRecorder : IRecorder
{
    private const long MinimumFreeBytes = 200L * 1024 * 1024;
    private static int _activeCount;

    private readonly AppPaths _paths;
    private readonly RecordingFinalizer _finalizer;
    private readonly IRecordingStore _store;
    private readonly MicSession? _source;
    private readonly string _id = Guid.NewGuid().ToString("N");
    private CaptureSession? _session;
    private RecordingSaver? _saver;
    private IReadOnlyList<RecorderWarning> _captureWarnings = [];
    private RecorderException? _endedBy;
    private DateTimeOffset _startedAt;
    private bool _claimed;
    private bool _stopped;

    public MeetingAudioRecorder(AppPaths paths, RecordingFinalizer finalizer, IRecordingStore store, MicSession? source)
    {
        _paths = paths;
        _finalizer = finalizer;
        _store = store;
        _source = source;
    }

    public event EventHandler<RecorderException>? Faulted;
    public event EventHandler<RecorderWarning>? Warning;

    public TimeSpan Elapsed => _session?.Recorded ?? TimeSpan.Zero;

    public bool HasSystemAudio => _session?.HasSystemAudio ?? false;

    public string? MicrophoneName => _session?.MicrophoneName;

    public DeviceChoiceReason MicrophoneChoice => _session?.MicrophoneChoice ?? DeviceChoiceReason.Default;

    public bool CanRetrySave => _saver?.CanRetry == true;

    public IReadOnlyList<RecorderWarning> Warnings => _session?.Warnings ?? [];

    private string PartialPath => Path.Combine(_paths.PartialDirectory, _id + ".wav");

    public (float Mic, float System) TakePeaks() => _session?.TakePeaks() ?? (0f, 0f);

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_session is not null || _stopped) throw new InvalidOperationException("A recorder can only be started once.");
        if (Interlocked.Exchange(ref _activeCount, 1) == 1)
            throw new RecorderException(RecorderErrorKind.Unknown, "Another recording is already in progress.");
        _claimed = true;

        try
        {
            _session = await Task.Run(async () =>
            {
                _paths.EnsureCreated();
                EnsureFreeSpace();
                return await CaptureSession.StartAsync(PartialPath, _source?.AppKey).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Release();
            TryDelete(PartialPath);
            var mapped = ex as RecorderException ?? RecorderErrorMapper.ForMicrophoneStart(ex);
            Log.Error(ex, "Recording failed to start: {Kind}", mapped.Kind);
            throw mapped;
        }

        _startedAt = DateTimeOffset.Now;
        _session.Faulted += (_, e) => Faulted?.Invoke(this, e);
        _session.Warning += (_, e) => Warning?.Invoke(this, e);
        Log.Information("Recording {RecordingId} started (source: {Source}, mic: {Microphone} ({MicrophoneChoice}), system audio: {HasSystemAudio})",
            _id, _source?.RawName ?? "manual", _session.MicrophoneName, _session.MicrophoneChoice, _session.HasSystemAudio);
    }

    public async Task<RecordingSaveResult> StopAndSaveAsync(IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        if (_saver is null && _session is null) throw new InvalidOperationException("Recording was never started.");
        if (_saver is null && _stopped) throw new InvalidOperationException("Recording already stopped.");
        if (_saver is { IsSaved: true }) throw new InvalidOperationException("Recording already saved.");

        try
        {
            if (_saver is null) await StopCaptureAsync(_session!).ConfigureAwait(false);

            var (entry, warning) = await _saver!.SaveAsync(progress, cancellationToken).ConfigureAwait(false);
            var warnings = _captureWarnings.ToList();
            if (warning is not null) warnings.Add(warning);
            return new RecordingSaveResult(entry, warnings, _endedBy);
        }
        catch (Exception ex) when (ex is not RecorderException and not OperationCanceledException)
        {
            Log.Error(ex, "Saving the recording failed");
            throw RecorderErrorMapper.ForWrite(ex);
        }
    }

    private async Task StopCaptureAsync(CaptureSession session)
    {
        _stopped = true;
        try
        {
            await Task.Run(session.Stop).ConfigureAwait(false);
        }
        finally
        {
            _captureWarnings = session.Warnings;
            _endedBy = session.FatalError;
            await session.DisposeAsync().ConfigureAwait(false);
            Release();
            _saver = new RecordingSaver(_id, PartialPath, _startedAt, _source?.AppName, _finalizer, _store);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_session is not null && !_stopped)
        {
            _stopped = true;
            await Task.Run(_session.Stop).ConfigureAwait(false);
            await _session.DisposeAsync().ConfigureAwait(false);
        }
        Release();
    }

    private void Release()
    {
        if (!_claimed) return;
        _claimed = false;
        Interlocked.Exchange(ref _activeCount, 0);
    }

    private void EnsureFreeSpace()
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(_paths.PartialDirectory));
            if (root is null) return;
            var drive = new DriveInfo(root);
            if (drive.IsReady && drive.AvailableFreeSpace < MinimumFreeBytes)
                throw new RecorderException(RecorderErrorKind.DiskFull,
                    $"There isn't enough free space on drive {drive.Name.TrimEnd('\\')} to record. Free up at least 200 MB and try again.");
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "Free space check failed");
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Warning(ex, "Could not delete {Path}", path); }
    }
}

public sealed class PartialRecovery(RecordingFinalizer finalizer, IRecordingStore store)
{
    public async Task<RecordingEntry?> RecoverAsync(string partialWavPath, CancellationToken cancellationToken = default)
    {
        var id = Path.GetFileNameWithoutExtension(partialWavPath);
        var created = new DateTimeOffset(File.GetCreationTime(partialWavPath));
        var finalized = await Task.Run(() =>
        {
            WavRepair.TryRepair(partialWavPath);
            return finalizer.Finalize(id, partialWavPath, null);
        }, cancellationToken).ConfigureAwait(false);
        var entry = new RecordingEntry(id, RecordingText.DefaultTitle(created) + " (recovered)", created,
            finalized.Duration, finalized.FileName, finalized.Format);
        await store.AddAsync(entry, cancellationToken).ConfigureAwait(false);
        Log.Information("Recovered partial recording {RecordingId}", id);
        return entry;
    }

    public void Discard(string partialWavPath)
    {
        try
        {
            File.Delete(partialWavPath);
            Log.Information("Discarded partial recording {File}", Path.GetFileName(partialWavPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "Could not discard partial");
        }
    }
}
