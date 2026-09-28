# Meeting Recorder

A small Windows 11 app that notices when another app starts using the microphone (a Zoom call, for
example), offers to record, and saves the mic and system audio to a local file you can play back
from the list.

## Build and run

You need Visual Studio 2026 with the **WiUI application development and .Net Desktop development** workload and the .NET 10 SDK
(installed with VS 2026). The Windows App SDK comes from NuGet on restore.

1. Open `MeetingRecorder.slnx` in the repo root.
2. Set **MeetingRecorder** as the startup project.
3. Pick **Debug** or **Release** and **x64**.
4. Press **F5**.

Or from a terminal:

```powershell
dotnet build MeetingRecorder.slnx
dotnet publish src\MeetingRecorder\MeetingRecorder.csproj -c Release -r win-x64
```

Release publishes use Native AOT, so publishing also needs the **Desktop development with C++**
workload. The output folder is self-contained: copy it anywhere and run `MeetingRecorder.exe`. There's
no installer and no .NET install needed on the target machine.

The app keeps its data in `%LOCALAPPDATA%\MeetingRecorder\` (recordings, `recordings.json`,
`settings.json` and `logs\`).

## Framework

WinUI 3 on the Windows App SDK, with C# / .NET 10 and CommunityToolkit.Mvvm. It's Microsoft's current
native UI stack, so Mica, the title bar, Fluent typography, light/dark themes and DPI scaling come
built in. `AppWindow` also covers most of what the prompt window needs (tool window, always on top,
show without activating).

The app is unpackaged and self-contained so it runs from a zip without trusting a certificate.

Audio uses NAudio 3 for WASAPI capture, loopback, resampling and Media Foundation encoding.

The code is split into two projects:

- `MeetingRecorder.Core` has no UI code: detection, audio, storage, settings and logging.
- `MeetingRecorder` is the WinUI app: views, view models, and `RecordingCoordinator`, which connects
  detection → prompt → recording window → list.

## Detection

Windows records which apps are using the mic under this registry key:

```
HKCU\Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone
```

It's the same data that drives the mic icon in the taskbar. An app is using the mic when its
`LastUsedTimeStop` is 0.

- `RegistryMicUsageSource` waits on `RegNotifyChangeKeyValue` on its own thread, so it uses no CPU while
  idle. It also re-reads the key every 5 s in case a notification is missed.
- `MicUsageMonitor` treats each *(app, start time)* pair as one session:
  - Each session is offered only once.
  - It waits 1.5 s before prompting, so a quick mic check doesn't trigger a prompt.
  - Once you dismiss a session, it isn't offered again. Muting in Zoom doesn't start a new session.
    If an app closes and reopens the mic within 5 s, the earlier decision still applies.
- The app's own recording is ignored: it compares each entry with its own exe path, and no prompts
  appear while it's recording.
- Zoom, Teams, Slack, Discord, Webex, the main browsers and a few others are shown by name. Any other
  app gets a generic "An app is using your microphone" message.

The prompt is a small borderless window in the bottom-right corner. It uses `WS_EX_NOACTIVATE` and
handles `WM_MOUSEACTIVATE`, so clicking Record or Dismiss doesn't take focus from the meeting. It hides
itself after 20 s.

Detection doesn't depend on the main window, so it keeps working while the window is minimised.

## Audio and storage

- **Microphone.** The app records from the mic the meeting app is actually using.
  `AudioDeviceLocator` finds it by matching audio sessions to the detected app. If there's no match,
  it uses the Windows default mic.
- **System audio.** Process loopback captures everything other apps are playing on any output device,
  excluding this app. That means playing an old recording while recording doesn't end up in the new
  file.
- **Mixing.** Both sources are converted to 48 kHz stereo float, summed through a soft limiter, and
  written as a 16-bit WAV. The WAV header is updated every 2 s, so a crash still leaves a playable
  file.
- **Stopping.** The WAV is encoded to AAC (128 kbps, `.m4a`, roughly 1 MB per minute) using the
  Windows Media Foundation encoder. The result is checked, then moved into `Recordings\`. Only then
  does the row appear in the list. If encoding fails, the WAV is saved instead.
- **The list.** `recordings.json` is the index. It's written to a temp file and swapped in with
  `File.Replace`. On startup it's checked against the files on disk, and any unfinished recording is
  offered for recovery.
- **Errors.** If mic access is denied, the device is missing or saving fails, you get a plain message
  with Retry and a link to the relevant Settings page. A failed recording never shows up as saved.

## Known limitations

- **It detects mic use, not meetings.** Any app that opens the mic triggers the prompt: dictation, a
  voice memo, a browser tab. Browsers only show up as "Chrome" or "Edge", so there's no way to tell
  which site it is. The registry key it reads isn't officially documented either. Windows could change it.
- **Use headphones.** On speakers, the mic picks up the other person's voice as well as the system
  audio, so they appear twice in the recording with a slight echo. Windows has echo cancellation that
  NAudio can hook into. That's what I'd add next.
- **System audio means all of it.** If music or a YouTube video is playing during the call, it gets
  recorded too.

## Performance

Measured on the same machine as below, using a Release build.

- **Startup:** about 2.2 s from process start to the first rendered frame (1.9 s to the window
  activating). The app writes these times to its log at startup, measured from `Process.StartTime`.
- **Idle memory:** around 40 MB, read from Task Manager with the app idle and monitoring.
- **Idle CPU:** close to zero. The detection thread sleeps until the registry changes.

## Tested on

- Windows 11 Pro 25H2 (build 26200)
- AMD Ryzen 9 5900HX, 16 GB RAM
- 2560×1440 display at 150 % scaling

## Time spent

Around 3 days.
