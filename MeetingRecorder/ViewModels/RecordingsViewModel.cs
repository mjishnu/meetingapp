using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MeetingRecorder.Core.Audio;
using MeetingRecorder.Core.Formatting;
using MeetingRecorder.Core.Playback;
using MeetingRecorder.Core.Recordings;
using MeetingRecorder.Core.Settings;
using Serilog;

namespace MeetingRecorder.ViewModels;

/// <summary>The recordings list: loading, grouping, samples, playback and crash recovery.</summary>
public sealed partial class RecordingsViewModel : ObservableObject
{
    private readonly IRecordingStore _store;
    private readonly IPlayer _player;
    private readonly SettingsStore _settingsStore;
    private readonly AppSettings _settings;
    private readonly PartialRecovery _recovery;
    private readonly List<RecordingItemViewModel> _real = [];
    private readonly IReadOnlyList<RecordingItemViewModel> _samples = SampleRecordings.Create(DateTime.Today);
    private readonly Queue<string> _orphans = new();
    private RecordingItemViewModel? _playing;

    public RecordingsViewModel(IRecordingStore store, IPlayer player, SettingsStore settingsStore, AppSettings settings, PartialRecovery recovery)
    {
        _store = store;
        _player = player;
        _settingsStore = settingsStore;
        _settings = settings;
        _recovery = recovery;
        ShowSamples = !settings.HideSamples;
        _player.Ended += (_, _) => ResetPlaying();
        _player.Failed += (_, message) =>
        {
            ResetPlaying();
            PlaybackError = message;
        };
        Rebuild();
    }

    public event EventHandler<RecordingItemViewModel>? ScrollIntoViewRequested;

    public event EventHandler? RecordRequested;

    public ObservableCollection<RecordingGroup> Groups { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    public partial bool IsLoading { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSampleNoticeVisible), nameof(CanShowSamples))]
    public partial bool ShowSamples { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPlaybackError))]
    public partial string? PlaybackError { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRecoverable))]
    public partial string? RecoverableMessage { get; set; }

    [ObservableProperty]
    public partial bool IsRecordingActive { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRecoveryError))]
    public partial string? RecoveryError { get; set; }

    public bool HasPlaybackError => !string.IsNullOrEmpty(PlaybackError);

    public bool HasRecoverable => !string.IsNullOrEmpty(RecoverableMessage);

    public bool HasRecoveryError => !string.IsNullOrEmpty(RecoveryError);

    public bool IsEmpty => !IsLoading && Groups.Count == 0;

    public bool IsSampleNoticeVisible => ShowSamples && _samples.Count > 0;

    public bool CanShowSamples => !ShowSamples;

    public async Task LoadAsync()
    {
        try
        {
            var result = await _store.LoadAsync();
            _real.Clear();
            _real.AddRange(result.Entries.Select(RecordingItemViewModel.FromEntry));
            foreach (var orphan in result.OrphanedPartials) _orphans.Enqueue(orphan);
            UpdateRecoverableMessage();
            if (result.IndexWasCorrupt)
                RecoveryError = "The recordings list was damaged and has been rebuilt from the files on disk.";
            Log.Information("Loaded {Count} recordings (dropped {Dropped}, re-indexed {Reindexed}, partials {Partials})",
                result.Entries.Count, result.DroppedMissing, result.Reindexed, result.OrphanedPartials.Count);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Loading recordings failed");
            RecoveryError = "Your recordings couldn't be loaded. Try restarting Meeting Recorder.";
        }
        finally
        {
            IsLoading = false;
            Rebuild();
        }
    }

    public void AddSaved(RecordingEntry entry, bool highlight)
    {
        var item = RecordingItemViewModel.FromEntry(entry);
        _real.RemoveAll(r => r.Id == entry.Id);
        _real.Add(item);

        var day = entry.CreatedAt.LocalDateTime.Date;
        var group = Groups.FirstOrDefault(g => g.Day == day);
        if (group is null)
        {
            group = new RecordingGroup(day, [item]);
            int gi = 0;
            while (gi < Groups.Count && Groups[gi].Day > day) gi++;
            Groups.Insert(gi, group);
        }
        else
        {
            int i = 0;
            while (i < group.Count && RecordingGroup.Compare(group[i], item) < 0) i++;
            group.Insert(i, item);
        }
        OnPropertyChanged(nameof(IsEmpty));
        if (highlight) Highlight(item);
    }

    public void Highlight(string id)
    {
        var item = _real.FirstOrDefault(r => r.Id == id);
        if (item is not null) Highlight(item);
    }

    private void Highlight(RecordingItemViewModel item)
    {
        foreach (var other in _real.Where(r => r.IsHighlighted)) other.IsHighlighted = false;
        item.IsHighlighted = true;
        ScrollIntoViewRequested?.Invoke(this, item);
        _ = ClearHighlightLaterAsync(item);
    }

    private static async Task ClearHighlightLaterAsync(RecordingItemViewModel item)
    {
        await Task.Delay(TimeSpan.FromSeconds(4));
        item.IsHighlighted = false;
    }

    [RelayCommand]
    private void Record() => RecordRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void TogglePlayback(RecordingItemViewModel? item)
    {
        if (item is null || !item.CanPlay || item.Entry is null) return;
        PlaybackError = null;
        if (item.IsPlaying)
        {
            _player.Stop();
            ResetPlaying();
            return;
        }

        ResetPlaying();
        var path = _store.GetFullPath(item.Entry);
        if (!File.Exists(path))
        {
            PlaybackError = $"\"{item.Title}\" can't be played because its file is missing.";
            item.RefreshPlayingState();
            return;
        }
        _playing = item;
        item.IsPlaying = true;
        _player.Play(path);
    }

    private void ResetPlaying()
    {
        if (_playing is { } p)
        {
            p.IsPlaying = false;
            p.RefreshPlayingState();
        }
        _playing = null;
    }

    [RelayCommand]
    private void HideSamples() => SetSamplesVisible(false);

    [RelayCommand]
    private void ShowSampleRecordings() => SetSamplesVisible(true);

    private void SetSamplesVisible(bool visible)
    {
        if (ShowSamples == visible) return;
        ShowSamples = visible;
        _settings.HideSamples = !visible;
        _ = Task.Run(() => _settingsStore.Save(_settings));
        Rebuild();
    }

    [RelayCommand]
    private void DismissPlaybackError() => PlaybackError = null;

    [RelayCommand]
    private void DismissRecoveryError() => RecoveryError = null;

    [RelayCommand]
    private async Task RecoverAsync()
    {
        if (!_orphans.TryPeek(out var path)) return;
        try
        {
            var entry = await _recovery.RecoverAsync(path);
            _orphans.Dequeue();
            if (entry is not null) AddSaved(entry, highlight: true);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Recovering partial failed");
            _orphans.Dequeue();
            RecoveryError = ex is RecorderException r ? r.Message : "The unfinished recording couldn't be recovered.";
        }
        UpdateRecoverableMessage();
    }

    [RelayCommand]
    private void DiscardRecoverable()
    {
        if (!_orphans.TryDequeue(out var path)) return;
        _recovery.Discard(path);
        UpdateRecoverableMessage();
    }

    private void UpdateRecoverableMessage()
    {
        if (!_orphans.TryPeek(out var path))
        {
            RecoverableMessage = null;
            return;
        }
        string when;
        try
        {
            var created = File.GetCreationTime(path);
            when = $" from {RecordingText.DayAndTime(created, DateTime.Today).Replace(" · ", " at ")}";
        }
        catch (IOException)
        {
            when = string.Empty;
        }
        RecoverableMessage = $"Meeting Recorder closed while recording{when}. You can recover what was captured.";
    }

    private void Rebuild()
    {
        var items = ShowSamples ? _real.Concat(_samples) : _real;
        var groups = items
            .GroupBy(i => i.CreatedAt.LocalDateTime.Date)
            .OrderByDescending(g => g.Key)
            .Select(g =>
            {
                var sorted = g.ToList();
                sorted.Sort(RecordingGroup.Compare);
                return new RecordingGroup(g.Key, sorted);
            })
            .ToList();

        Groups.Clear();
        foreach (var g in groups) Groups.Add(g);
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(IsSampleNoticeVisible));
    }
}
