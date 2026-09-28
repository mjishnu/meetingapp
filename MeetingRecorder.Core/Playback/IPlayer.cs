namespace MeetingRecorder.Core.Playback;

public interface IPlayer : IDisposable
{
    event EventHandler? Ended;

    event EventHandler<string>? Failed;

    bool IsPlaying { get; }

    void Play(string path);

    void Stop();
}
