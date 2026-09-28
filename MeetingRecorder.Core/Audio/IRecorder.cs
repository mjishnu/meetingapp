using MeetingRecorder.Core.Recordings;

namespace MeetingRecorder.Core.Audio;

public interface IRecorder : IAsyncDisposable
{
    event EventHandler<RecorderException>? Faulted;

    event EventHandler<RecorderWarning>? Warning;

    TimeSpan Elapsed { get; }

    bool HasSystemAudio { get; }

    string? MicrophoneName { get; }

    DeviceChoiceReason MicrophoneChoice { get; }

    bool CanRetrySave { get; }

    IReadOnlyList<RecorderWarning> Warnings { get; }

    (float Mic, float System) TakePeaks();

    Task StartAsync(CancellationToken cancellationToken = default);

    Task<RecordingSaveResult> StopAndSaveAsync(IProgress<double>? progress = null, CancellationToken cancellationToken = default);
}

public sealed record RecordingSaveResult(RecordingEntry Entry, IReadOnlyList<RecorderWarning> Warnings, RecorderException? EndedBy);
