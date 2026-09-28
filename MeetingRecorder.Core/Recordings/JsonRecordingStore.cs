using System.Text.Json;
using System.Text.Json.Serialization;
using MeetingRecorder.Core.Formatting;
using Serilog;

namespace MeetingRecorder.Core.Recordings;

public sealed class JsonRecordingStore : IRecordingStore
{
    private static readonly string[] AudioExtensions = [".m4a", ".wav"];

    private readonly AppPaths _paths;
    private readonly IAudioFileProbe _probe;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _sync = new();
    private List<RecordingEntry> _entries = [];

    public JsonRecordingStore(AppPaths paths, IAudioFileProbe probe)
    {
        _paths = paths;
        _probe = probe;
    }

    public IReadOnlyList<RecordingEntry> Entries
    {
        get { lock (_sync) return _entries.ToArray(); }
    }

    public string GetFullPath(RecordingEntry entry) => Path.Combine(_paths.RecordingsDirectory, entry.FileName);

    public async Task<StoreLoadResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => LoadCore(cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task AddAsync(RecordingEntry entry, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var next = _entries.Where(e => e.Id != entry.Id).Append(entry).OrderByDescending(e => e.CreatedAt).ToList();
            await Task.Run(() => WriteIndex(next), cancellationToken).ConfigureAwait(false);
            lock (_sync) _entries = next;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RemoveAsync(string id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var next = _entries.Where(e => e.Id != id).ToList();
            await Task.Run(() => WriteIndex(next), cancellationToken).ConfigureAwait(false);
            lock (_sync) _entries = next;
        }
        finally
        {
            _gate.Release();
        }
    }

    private StoreLoadResult LoadCore(CancellationToken cancellationToken)
    {
        _paths.EnsureCreated();
        bool corrupt = false;
        List<RecordingEntry> indexed = [];

        if (File.Exists(_paths.IndexFile))
        {
            try
            {
                using var stream = File.OpenRead(_paths.IndexFile);
                var doc = JsonSerializer.Deserialize(stream, StoreJsonContext.Default.IndexDocument);
                indexed = doc?.Recordings?.Where(IsValid).ToList() ?? [];
            }
            catch (JsonException ex)
            {
                corrupt = true;
                var aside = _paths.IndexFile + $".corrupt-{DateTime.Now:yyyyMMddHHmmss}";
                Log.Error(ex, "recordings.json is corrupt; moving it to {Backup} and rebuilding from disk", Path.GetFileName(aside));
                TryMove(_paths.IndexFile, aside);
            }
        }

        var present = indexed.Where(e => File.Exists(GetFullPath(e))).ToList();
        int dropped = indexed.Count - present.Count;
        if (dropped > 0) Log.Warning("Reconcile: dropped {Count} index {Noun} with missing files", dropped, dropped == 1 ? "entry" : "entries");

        var known = new HashSet<string>(present.Select(e => e.FileName), StringComparer.OrdinalIgnoreCase);
        int reindexed = 0;
        foreach (var file in new DirectoryInfo(_paths.RecordingsDirectory).EnumerateFiles())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!AudioExtensions.Contains(file.Extension, StringComparer.OrdinalIgnoreCase)) continue;
            if (known.Contains(file.Name) || file.Name.Contains(".encoding", StringComparison.OrdinalIgnoreCase)) continue;
            var id = Path.GetFileNameWithoutExtension(file.Name);
            if (present.Any(e => e.Id == id)) continue;

            var duration = _probe.TryGetDuration(file.FullName);
            if (duration is null || duration <= TimeSpan.Zero)
            {
                Log.Warning("Reconcile: ignoring unreadable file {File}", file.Name);
                continue;
            }
            var created = new DateTimeOffset(file.CreationTime);
            present.Add(new RecordingEntry(id, RecordingText.DefaultTitle(created), created, duration.Value, file.Name,
                file.Extension.Equals(".wav", StringComparison.OrdinalIgnoreCase) ? RecordingFormat.Wav : RecordingFormat.M4a));
            reindexed++;
            Log.Information("Reconcile: re-indexed {File} ({Duration})", file.Name, duration);
        }

        present = present.OrderByDescending(e => e.CreatedAt).ToList();
        if (dropped > 0 || reindexed > 0 || corrupt) WriteIndex(present);

        var partials = Directory.EnumerateFiles(_paths.PartialDirectory, "*.wav").ToList();
        lock (_sync) _entries = present;
        return new StoreLoadResult(present, dropped, reindexed, partials, corrupt);
    }

    private void WriteIndex(List<RecordingEntry> entries)
    {
        Directory.CreateDirectory(_paths.Root);
        var temp = _paths.IndexFile + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, new IndexDocument { Version = 1, Recordings = entries }, StoreJsonContext.Default.IndexDocument);
            stream.Flush(flushToDisk: true);
        }
        if (File.Exists(_paths.IndexFile))
            File.Replace(temp, _paths.IndexFile, destinationBackupFileName: null, ignoreMetadataErrors: true);
        else
            File.Move(temp, _paths.IndexFile);
    }

    private static bool IsValid(RecordingEntry? e) =>
        e is not null && !string.IsNullOrWhiteSpace(e.Id) && !string.IsNullOrWhiteSpace(e.FileName)
        && e.FileName.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    private static void TryMove(string from, string to)
    {
        try { File.Move(from, to, overwrite: true); }
        catch (IOException ex) { Log.Warning(ex, "Could not move {Path}", from); }
    }

    internal sealed class IndexDocument
    {
        public int Version { get; set; }
        public List<RecordingEntry>? Recordings { get; set; }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(JsonRecordingStore.IndexDocument))]
internal sealed partial class StoreJsonContext : JsonSerializerContext;
