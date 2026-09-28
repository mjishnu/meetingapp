using Serilog;

namespace MeetingRecorder.Core.Detection;

public sealed class MicUsageMonitorOptions
{
    public string? SelfKey { get; init; }

    public TimeSpan Debounce { get; init; } = TimeSpan.FromSeconds(1.5);

    public TimeSpan ReopenGrace { get; init; } = TimeSpan.FromSeconds(5);
}

public sealed class MicUsageMonitor : IMicUsageMonitor
{
    private enum Status
    {
        Pending,
        Prompted,
        Dismissed,
        Accepted,
        SeenWhileRecording,
        Self,
    }

    private sealed class Tracked(MicSession session, DateTimeOffset eligibleAt)
    {
        public MicSession Session { get; } = session;
        public DateTimeOffset EligibleAt { get; } = eligibleAt;
        public Status Status { get; set; }
    }

    private readonly IMicUsageSource _source;
    private readonly TimeProvider _time;
    private readonly MicUsageMonitorOptions _options;
    private readonly Lock _sync = new();
    private readonly Dictionary<string, Tracked> _active = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (DateTimeOffset EndedAt, Status Status)> _recentlyEnded = new(StringComparer.OrdinalIgnoreCase);
    private readonly ITimer _debounceTimer;
    private bool _suppress;
    private bool _started;
    private bool _disposed;

    public MicUsageMonitor(IMicUsageSource source, TimeProvider time, MicUsageMonitorOptions options)
    {
        _source = source;
        _time = time;
        _options = options;
        _debounceTimer = time.CreateTimer(_ => Evaluate(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public event EventHandler<MicSession>? SessionDetected;
    public event EventHandler<MicSession>? SessionEnded;

    public bool SuppressPrompts
    {
        get { lock (_sync) return _suppress; }
        set
        {
            lock (_sync)
            {
                _suppress = value;
                if (value)
                {
                    foreach (var t in _active.Values.Where(t => t.Status is Status.Pending or Status.Prompted))
                        t.Status = Status.SeenWhileRecording;
                }
            }
            if (!value) Evaluate();
        }
    }

    public void Start()
    {
        lock (_sync)
        {
            if (_started) return;
            _started = true;
        }
        _source.Changed += OnSourceChanged;
        _source.Start();
        Evaluate();
    }

    public void Dismiss(MicSession session) => SetStatus(session, Status.Dismissed);

    public void Accept(MicSession session) => SetStatus(session, Status.Accepted);

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _source.Changed -= OnSourceChanged;
        _debounceTimer.Dispose();
    }

    private void SetStatus(MicSession session, Status status)
    {
        lock (_sync)
        {
            if (_active.TryGetValue(session.AppKey, out var t) && t.Session.StartFileTime == session.StartFileTime)
                t.Status = status;
            else
                _recentlyEnded[session.AppKey] = (_time.GetUtcNow(), status);
        }
        Log.Information("Mic session {App} marked {Status}", session.RawName, status);
    }

    private void OnSourceChanged(object? sender, EventArgs e) => Evaluate();

    internal void Evaluate()
    {
        IReadOnlyList<MicAppUsage> snapshot;
        try
        {
            snapshot = _source.ReadSnapshot();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Reading mic usage failed");
            return;
        }

        var detected = new List<MicSession>();
        var ended = new List<MicSession>();
        lock (_sync)
        {
            if (_disposed) return;
            var now = _time.GetUtcNow();
            var inUse = new Dictionary<string, MicAppUsage>(StringComparer.OrdinalIgnoreCase);
            foreach (var u in snapshot)
                if (u.InUse) inUse[u.Key] = u;

            foreach (var (key, tracked) in _active.ToList())
            {
                if (inUse.TryGetValue(key, out var u) && u.LastUsedTimeStart == tracked.Session.StartFileTime) continue;
                _active.Remove(key);
                _recentlyEnded[key] = (now, tracked.Status);
                if (tracked.Status == Status.Prompted) ended.Add(tracked.Session);
            }

            foreach (var (key, stale) in _recentlyEnded.ToList())
                if (now - stale.EndedAt > _options.ReopenGrace) _recentlyEnded.Remove(key);

            foreach (var (key, u) in inUse)
            {
                if (_active.ContainsKey(key)) continue;
                var session = new MicSession(key, u.LastUsedTimeStart, AppNames.FriendlyName(key), AppNames.RawName(key));
                var tracked = new Tracked(session, EligibleAt(u.LastUsedTimeStart, now)) { Status = InitialStatus(key) };
                _active[key] = tracked;
                Log.Information("Mic session started: {App} ({Status})", session.RawName, tracked.Status);
            }

            DateTimeOffset? nextCheck = null;
            foreach (var t in _active.Values.Where(t => t.Status == Status.Pending))
            {
                if (now >= t.EligibleAt)
                {
                    t.Status = Status.Prompted;
                    detected.Add(t.Session);
                }
                else if (nextCheck is null || t.EligibleAt < nextCheck)
                {
                    nextCheck = t.EligibleAt;
                }
            }
            if (nextCheck is { } due)
                _debounceTimer.Change(due - now, Timeout.InfiniteTimeSpan);
        }

        foreach (var s in ended) SessionEnded?.Invoke(this, s);
        foreach (var s in detected)
        {
            Log.Information("Prompting for mic session: {App}", s.RawName);
            SessionDetected?.Invoke(this, s);
        }
    }

    private Status InitialStatus(string key)
    {
        if (_options.SelfKey is { } self && AppNames.IsSameKey(key, self)) return Status.Self;
        if (_suppress) return Status.SeenWhileRecording;
        if (_recentlyEnded.TryGetValue(key, out var previous) &&
            previous.Status is Status.Dismissed or Status.Accepted or Status.SeenWhileRecording)
            return previous.Status;
        return Status.Pending;
    }

    private DateTimeOffset EligibleAt(long startFileTime, DateTimeOffset now)
    {
        DateTimeOffset started;
        try
        {
            started = DateTimeOffset.FromFileTime(startFileTime);
        }
        catch (ArgumentOutOfRangeException)
        {
            started = now;
        }
        var eligible = started + _options.Debounce;
        if (eligible < now) return now;
        var latest = now + _options.Debounce;
        return eligible > latest ? latest : eligible;
    }
}
