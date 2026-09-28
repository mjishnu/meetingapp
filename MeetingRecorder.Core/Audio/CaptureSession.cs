using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Serilog;

namespace MeetingRecorder.Core.Audio;

internal sealed class CaptureSession : IAsyncDisposable
{
    public static readonly WaveFormat MixFormat = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
    public static readonly WaveFormat FileFormat = new(48000, 16, 2);

    private static readonly TimeSpan Cushion = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan MaxBuffered = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DriftTrimThreshold = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan PumpInterval = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan HeaderFlushInterval = TimeSpan.FromSeconds(2);

    private readonly string _wavPath;
    private readonly List<RecorderWarning> _warnings = [];
    private readonly Stopwatch _clock = new();
    private readonly ManualResetEventSlim _stopPump = new(false);

    private MMDevice? _micDevice;
    private MMDevice? _renderDevice;
    private WasapiRecorder? _mic;
    private WasapiRecorder? _loopback;
    private CaptureSource? _micSource;
    private CaptureSource? _systemSource;
    private WaveFileWriter? _writer;
    private Thread? _pump;
    private long _framesWritten;
    private volatile RecorderException? _fatal;
    private volatile bool _stopping;
    private bool _systemIsProcessLoopback;

    private CaptureSession(string wavPath) => _wavPath = wavPath;
    public event EventHandler<RecorderException>? Faulted;

    public event EventHandler<RecorderWarning>? Warning;

    public IReadOnlyList<RecorderWarning> Warnings
    {
        get { lock (_warnings) return _warnings.ToArray(); }
    }

    public bool HasSystemAudio => _loopback is not null;

    public string? MicrophoneName { get; private set; }

    public DeviceChoiceReason MicrophoneChoice { get; private set; }

    public TimeSpan Recorded => TimeSpan.FromSeconds(Interlocked.Read(ref _framesWritten) / (double)MixFormat.SampleRate);

    public (float Mic, float System) TakePeaks() => (_micSource?.TakePeak() ?? 0f, _systemSource?.TakePeak() ?? 0f);

    public static async Task<CaptureSession> StartAsync(string wavPath, string? meetingAppKey)
    {
        var session = new CaptureSession(wavPath);
        try
        {
            await session.StartCoreAsync(meetingAppKey).ConfigureAwait(false);
            return session;
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task StartCoreAsync(string? meetingAppKey)
    {
        using var enumerator = new MMDeviceEnumerator();
        if (AudioDeviceLocator.Find(enumerator, DataFlow.Capture, meetingAppKey, fallBackToOtherApps: true) is not { } mic)
        {
            throw new RecorderException(RecorderErrorKind.NoMicrophone,
                "No microphone was found. Connect one, or choose an input device in Sound settings, then try again.");
        }
        _micDevice = mic.Device;
        MicrophoneChoice = mic.Reason;
        try
        {
            MicrophoneName = mic.Device.FriendlyName;
            _mic = new WasapiRecorderBuilder().WithDevice(mic.Device).WithSharedMode().Build();
        }
        catch (Exception ex)
        {
            throw RecorderErrorMapper.ForMicrophoneStart(ex);
        }
        _micSource = new CaptureSource("mic", _mic.WaveFormat, MixFormat, Cushion, MaxBuffered);
        Log.Information("Mic: {Microphone} ({MicrophoneChoice}) {WaveFormat}", MicrophoneName, MicrophoneChoice, _mic.WaveFormat);

        await StartSystemAudioAsync(enumerator, meetingAppKey).ConfigureAwait(false);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_wavPath)!);
            _writer = new WaveFileWriter(_wavPath, FileFormat);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw RecorderErrorMapper.ForWrite(ex);
        }

        _mic.DataAvailable += OnMicData;
        _mic.RecordingStopped += OnMicStopped;
        if (_loopback is not null)
        {
            _loopback.DataAvailable += OnLoopbackData;
            _loopback.RecordingStopped += OnLoopbackStopped;
        }

        try
        {
            _mic.StartRecording();
        }
        catch (Exception ex)
        {
            throw RecorderErrorMapper.ForMicrophoneStart(ex);
        }
        try
        {
            _loopback?.StartRecording();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Loopback start failed; recording microphone only");
            DisposeLoopback();
            AddWarning(new RecorderWarning(RecorderWarningKind.NoSystemAudioDevice,
                "System audio can't be captured right now, so only your microphone is being recorded."));
        }

        _clock.Start();
        _pump = new Thread(PumpLoop) { IsBackground = true, Name = "Audio pump", Priority = ThreadPriority.AboveNormal };
        _pump.Start();
    }

    private async Task StartSystemAudioAsync(MMDeviceEnumerator enumerator, string? meetingAppKey)
    {
        if (!enumerator.TryGetDefaultAudioEndpoint(DataFlow.Render, Role.Console, out var anyRender) || anyRender is null)
        {
            AddWarning(new RecorderWarning(RecorderWarningKind.NoSystemAudioDevice,
                "No speakers or headphones were found, so only your microphone is being recorded."));
            return;
        }
        anyRender.Dispose();

        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            try
            {
                _loopback = await new WasapiRecorderBuilder()
                    .WithProcessLoopback((uint)Environment.ProcessId, ProcessLoopbackMode.ExcludeTargetProcessTree)
                    .WithFormat(MixFormat)
                    .BuildAsync().ConfigureAwait(false);
                _systemIsProcessLoopback = true;
                Log.Information("System audio: process loopback (all apps except this one) {WaveFormat}", _loopback.WaveFormat);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Process loopback unavailable; falling back to device loopback");
            }
        }

        if (_loopback is null)
        {
            try
            {
                if (AudioDeviceLocator.Find(enumerator, DataFlow.Render, meetingAppKey, fallBackToOtherApps: false) is { } render)
                {
                    _renderDevice = render.Device;
                    _loopback = new WasapiRecorderBuilder().WithDevice(render.Device).WithLoopbackCapture().WithPollingSync().Build();
                    Log.Information("System audio: loopback of {Device} ({Reason}) {WaveFormat}", render.Device.FriendlyName, render.Reason, _loopback.WaveFormat);
                }
            }
            catch (Exception fallbackEx)
            {
                Log.Warning(fallbackEx, "Loopback capture unavailable; recording microphone only");
                _loopback = null;
            }
        }

        if (_loopback is null)
        {
            AddWarning(new RecorderWarning(RecorderWarningKind.NoSystemAudioDevice,
                "System audio can't be captured on this device, so only your microphone is being recorded."));
            return;
        }
        _systemSource = new CaptureSource("system", _loopback.WaveFormat, MixFormat, Cushion, MaxBuffered);
    }

    private void OnMicData(ReadOnlySpan<byte> data, AudioClientBufferFlags flags, long devicePosition, long qpc)
    {
        if ((flags & AudioClientBufferFlags.Silent) != 0) _micSource!.AddSilence(data.Length);
        else _micSource!.Add(data);
    }

    private void OnLoopbackData(ReadOnlySpan<byte> data, AudioClientBufferFlags flags, long devicePosition, long qpc)
    {
        if ((flags & AudioClientBufferFlags.Silent) != 0) _systemSource?.AddSilence(data.Length);
        else _systemSource?.Add(data);
    }

    private void OnMicStopped(object? sender, StoppedEventArgs e)
    {
        if (_stopping) return;
        Log.Error(e.Exception, "Microphone capture stopped unexpectedly");
        Fault(RecorderErrorMapper.MicrophoneDisconnected(e.Exception));
    }

    private void OnLoopbackStopped(object? sender, StoppedEventArgs e)
    {
        if (_stopping) return;
        Log.Warning(e.Exception, "System audio capture stopped unexpectedly; continuing with microphone only");
        var warning = new RecorderWarning(RecorderWarningKind.SystemAudioDeviceLost, _systemIsProcessLoopback
            ? "System audio stopped being captured. The microphone is still being recorded."
            : "Your speakers or headphones were disconnected. The microphone is still being recorded; system audio is not.");
        AddWarning(warning);
        Warning?.Invoke(this, warning);
    }

    private void Fault(RecorderException ex)
    {
        if (_fatal is not null) return;
        _fatal = ex;
        Faulted?.Invoke(this, ex);
    }

    private void AddWarning(RecorderWarning w)
    {
        lock (_warnings) _warnings.Add(w);
    }

    private void PumpLoop()
    {
        try
        {
            PumpLoopCore();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Audio pump crashed");
            Fault(new RecorderException(RecorderErrorKind.Unknown, "Recording stopped because of an unexpected audio error.", ex));
        }
    }

    private void PumpLoopCore()
    {
        int maxFrames = MixFormat.SampleRate;
        var mic = new float[maxFrames * 2];
        var sys = new float[maxFrames * 2];
        var pcm = new byte[maxFrames * 4];
        var lastHeaderFlush = TimeSpan.Zero;
        long cushionFrames = (long)(Cushion.TotalSeconds * MixFormat.SampleRate);

        while (!_stopPump.Wait(PumpInterval))
        {
            if (_fatal is not null) break;
            long target = (long)(_clock.Elapsed.TotalSeconds * MixFormat.SampleRate) - cushionFrames;
            if (!WriteUpTo(target, mic, sys, pcm)) break;

            TrimDrift(_micSource);
            TrimDrift(_systemSource);

            if (_clock.Elapsed - lastHeaderFlush >= HeaderFlushInterval)
            {
                lastHeaderFlush = _clock.Elapsed;
                try { _writer!.Flush(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Fault(RecorderErrorMapper.ForWrite(ex)); break; }
            }
        }
    }

    private bool WriteUpTo(long targetFrames, float[] mic, float[] sys, byte[] pcm)
    {
        int maxFrames = mic.Length / 2;
        while (true)
        {
            long pending = targetFrames - Interlocked.Read(ref _framesWritten);
            if (pending <= 0) return true;
            int frames = (int)Math.Min(pending, maxFrames);
            int samples = frames * 2;

            _micSource!.Output.Read(mic.AsSpan(0, samples));
            if (_systemSource is not null) _systemSource.Output.Read(sys.AsSpan(0, samples));
            else Array.Clear(sys, 0, samples);

            for (int i = 0; i < samples; i++)
            {
                short s = (short)(Mixer.SoftClip(mic[i] + sys[i]) * short.MaxValue);
                pcm[i * 2] = (byte)s;
                pcm[i * 2 + 1] = (byte)(s >> 8);
            }
            try
            {
                _writer!.Write(pcm.AsSpan(0, samples * 2));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Error(ex, "WAV write failed");
                Fault(RecorderErrorMapper.ForWrite(ex));
                return false;
            }
            catch (ArgumentException ex)
            {
                Log.Warning(ex, "WAV size limit reached");
                Fault(new RecorderException(RecorderErrorKind.WriteFailed,
                    "The recording reached the maximum length (about 6 hours) and was stopped.", ex));
                return false;
            }
            Interlocked.Add(ref _framesWritten, frames);
        }
    }

    private static void TrimDrift(CaptureSource? source)
    {
        if (source is null) return;
        if (source.Buffered > Cushion + DriftTrimThreshold)
        {
            Log.Information("Drift: trimming {Source} buffer from {BufferedMs:F0} ms", source.Name, source.Buffered.TotalMilliseconds);
            source.TrimTo(Cushion);
        }
    }

    public void Stop()
    {
        if (_stopping) return;
        _stopping = true;
        try { _mic?.StopRecording(); } catch (Exception ex) { Log.Warning(ex, "Mic stop failed"); }
        try { _loopback?.StopRecording(); } catch (Exception ex) { Log.Warning(ex, "Loopback stop failed"); }

        _stopPump.Set();
        _pump?.Join();

        if (_fatal is null && _writer is not null && _micSource is not null)
        {
            long tail = (long)(_micSource.Buffered.TotalSeconds * MixFormat.SampleRate);
            var mic = new float[MixFormat.SampleRate * 2];
            var sys = new float[MixFormat.SampleRate * 2];
            var pcm = new byte[MixFormat.SampleRate * 4];
            WriteUpTo(Interlocked.Read(ref _framesWritten) + Math.Min(tail, MixFormat.SampleRate), mic, sys, pcm);
        }

        try
        {
            _writer?.Dispose();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error(ex, "Closing WAV failed");
            _fatal ??= RecorderErrorMapper.ForWrite(ex);
        }
        _writer = null;
        Log.Information("Capture stopped: {Recorded} written, mic underruns {MicUnderruns}, system underruns {SystemUnderruns}",
            Recorded, _micSource?.Underruns, _systemSource?.Underruns);
    }

    public RecorderException? FatalError => _fatal;

    private void DisposeLoopback()
    {
        if (_loopback is null) return;
        _loopback.DataAvailable -= OnLoopbackData;
        _loopback.RecordingStopped -= OnLoopbackStopped;
        try { _loopback.Dispose(); } catch (Exception ex) { Log.Warning(ex, "Loopback dispose failed"); }
        _loopback = null;
        _systemSource = null;
    }

    public async ValueTask DisposeAsync()
    {
        _stopping = true;
        _stopPump.Set();
        if (_mic is not null)
        {
            _mic.DataAvailable -= OnMicData;
            _mic.RecordingStopped -= OnMicStopped;
            try { await _mic.DisposeAsync().ConfigureAwait(false); } catch (Exception ex) { Log.Warning(ex, "Mic dispose failed"); }
            _mic = null;
        }
        DisposeLoopback();
        _micDevice?.Dispose();
        _renderDevice?.Dispose();
        _micDevice = _renderDevice = null;
        try { _writer?.Dispose(); } catch (IOException) { }
        _writer = null;
        _stopPump.Dispose();
    }
}
