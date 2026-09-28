using MeetingRecorder.Core.Playback;
using Microsoft.UI.Dispatching;
using Serilog;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace MeetingRecorder.Services;

/// <summary>
/// One player instance per playback, disposed as soon as it ends or is stopped.
/// </summary>
internal sealed partial class MediaPlayerService(DispatcherQueue dispatcher) : IPlayer
{
    private MediaPlayer? _player;

    public event EventHandler? Ended;
    public event EventHandler<string>? Failed;

    public bool IsPlaying => _player is not null;

    public void Play(string path)
    {
        Stop();
        var player = new MediaPlayer
        {
            AudioCategory = MediaPlayerAudioCategory.Media,
            AutoPlay = true,
        };
        player.MediaEnded += (s, _) => OnUi(s, () =>
        {
            Stop();
            Ended?.Invoke(this, EventArgs.Empty);
        });
        player.MediaFailed += (s, e) => OnUi(s, () =>
        {
            Log.Warning("Playback failed: {Error} {ErrorMessage} (0x{HResult:X8})", e.Error, e.ErrorMessage, e.ExtendedErrorCode?.HResult);
            Stop();
            Failed?.Invoke(this, "This recording couldn't be played. The file may be damaged or in use.");
        });
        _player = player;
        try
        {
            player.Source = MediaSource.CreateFromUri(new Uri(path));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not open {Path} for playback", path);
            Stop();
            Failed?.Invoke(this, "This recording couldn't be opened.");
        }
    }

    public void Stop()
    {
        var player = _player;
        _player = null;
        if (player is null) return;
        try
        {
            player.Pause();
            player.Source = null;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Stopping playback failed");
        }
        player.Dispose();
    }

    private void OnUi(MediaPlayer sender, Action action) => dispatcher.TryEnqueue(() =>
    {
        if (ReferenceEquals(sender, _player)) action();
    });

    public void Dispose() => Stop();
}
