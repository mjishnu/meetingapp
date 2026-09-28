using MeetingRecorder.Core.Recordings;
using NAudio.MediaFoundation;
using NAudio.Wave;
using Serilog;

namespace MeetingRecorder.Core.Audio;

public sealed record FinalizedRecording(string FileName, RecordingFormat Format, TimeSpan Duration, RecorderWarning? Warning);

public sealed class RecordingFinalizer(AppPaths paths, IAudioFileProbe probe)
{
    private const int AacBitrate = 128_000;
    private static readonly Lock MfInit = new();
    private static bool _mfStarted;

    public FinalizedRecording Finalize(string id, string partialWavPath, IProgress<double>? progress)
    {
        TimeSpan wavDuration;
        using (var reader = new WaveFileReader(partialWavPath))
            wavDuration = reader.TotalTime;
        if (wavDuration < TimeSpan.FromMilliseconds(250))
            throw new RecorderException(RecorderErrorKind.NothingRecorded, "The recording was too short to save.");

        Directory.CreateDirectory(paths.RecordingsDirectory);
        var finalM4a = Path.Combine(paths.RecordingsDirectory, id + ".m4a");
        var tempM4a = Path.Combine(paths.PartialDirectory, id + ".encoding.m4a");

        try
        {
            EnsureMediaFoundation();
            TryDelete(tempM4a);
            using (var reader = new WaveFileReader(partialWavPath))
            {
                var tracked = new ProgressWaveProvider(reader, reader.Length, progress);
                MediaFoundationEncoder.EncodeToAac(tracked, tempM4a, AacBitrate);
            }

            var encodedDuration = Verify(tempM4a, wavDuration);
            File.Move(tempM4a, finalM4a, overwrite: false);
            TryDelete(partialWavPath);
            progress?.Report(1.0);
            Log.Information("Saved {File} ({Duration}, {SizeKB} KB)", Path.GetFileName(finalM4a), encodedDuration, new FileInfo(finalM4a).Length / 1024);
            return new FinalizedRecording(Path.GetFileName(finalM4a), RecordingFormat.M4a, encodedDuration, null);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "AAC encode failed; keeping the WAV as the saved recording");
            TryDelete(tempM4a);
            var finalWav = Path.Combine(paths.RecordingsDirectory, id + ".wav");
            try
            {
                File.Move(partialWavPath, finalWav, overwrite: false);
            }
            catch (Exception moveEx) when (moveEx is IOException or UnauthorizedAccessException)
            {
                throw RecorderErrorMapper.ForWrite(moveEx);
            }
            return new FinalizedRecording(Path.GetFileName(finalWav), RecordingFormat.Wav, wavDuration,
                new RecorderWarning(RecorderWarningKind.SavedAsWav,
                    "The recording was saved as a WAV file because converting it to a smaller format failed."));
        }
    }

    private TimeSpan Verify(string path, TimeSpan expected)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length == 0)
            throw new InvalidDataException("Encoded file is missing or empty.");
        var duration = probe.TryGetDuration(path)
                       ?? throw new InvalidDataException("Encoded file cannot be opened.");
        if (duration <= TimeSpan.Zero || Math.Abs((duration - expected).TotalSeconds) > Math.Max(1.0, expected.TotalSeconds * 0.02))
            throw new InvalidDataException($"Encoded duration {duration} does not match recorded {expected}.");
        return duration;
    }

    internal static void EnsureMediaFoundation()
    {
        lock (MfInit)
        {
            if (_mfStarted) return;
            MediaFoundationApi.Startup();
            _mfStarted = true;
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Warning(ex, "Could not delete {Path}", path); }
    }

    private sealed class ProgressWaveProvider(IWaveProvider source, long totalBytes, IProgress<double>? progress) : IWaveProvider
    {
        private long _read;
        private int _lastPercent = -1;

        public WaveFormat WaveFormat => source.WaveFormat;

        public int Read(Span<byte> buffer)
        {
            int n = source.Read(buffer);
            _read += n;
            if (progress is not null && totalBytes > 0)
            {
                int percent = (int)(_read * 100 / totalBytes);
                if (percent != _lastPercent)
                {
                    _lastPercent = percent;
                    progress.Report(Math.Min(0.99, percent / 100.0));
                }
            }
            return n;
        }
    }
}

public sealed class MediaFoundationProbe : IAudioFileProbe
{
    public TimeSpan? TryGetDuration(string path)
    {
        try
        {
            RecordingFinalizer.EnsureMediaFoundation();
            using var reader = new MediaFoundationReader(path);
            return reader.TotalTime;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not probe {File}", Path.GetFileName(path));
            return null;
        }
    }
}

public static class WavRepair
{
    public static bool TryRepair(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            using var br = new BinaryReader(fs, System.Text.Encoding.ASCII, leaveOpen: true);
            using var bw = new BinaryWriter(fs, System.Text.Encoding.ASCII, leaveOpen: true);
            if (fs.Length < 44 || new string(br.ReadChars(4)) != "RIFF") return false;
            br.ReadInt32();
            if (new string(br.ReadChars(4)) != "WAVE") return false;

            int blockAlign = 4;
            while (fs.Position + 8 <= fs.Length)
            {
                var id = new string(br.ReadChars(4));
                int size = br.ReadInt32();
                long bodyStart = fs.Position;
                if (id == "fmt ")
                {
                    br.ReadInt16(); br.ReadInt16(); br.ReadInt32(); br.ReadInt32();
                    blockAlign = Math.Max((int)br.ReadInt16(), 1);
                    fs.Position = bodyStart + size + (size & 1);
                }
                else if (id == "data")
                {
                    long available = fs.Length - bodyStart;
                    available -= available % blockAlign;
                    if (available > uint.MaxValue - 64) available = uint.MaxValue - 64;
                    fs.Position = bodyStart - 4;
                    bw.Write((uint)available);
                    fs.Position = 4;
                    bw.Write((uint)(bodyStart + available - 8));
                    fs.SetLength(bodyStart + available);
                    return available > 0;
                }
                else
                {
                    fs.Position = bodyStart + size + (size & 1);
                }
            }
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "WAV repair failed for {Path}", path);
            return false;
        }
    }
}
