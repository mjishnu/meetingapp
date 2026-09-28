using System.Runtime.InteropServices;

namespace MeetingRecorder.Core.Audio;

public enum RecorderErrorKind
{
    MicrophoneAccessDenied,
    NoMicrophone,
    MicrophoneInUseExclusively,
    MicrophoneDisconnected,
    DiskFull,
    WriteFailed,
    NothingRecorded,
    Unknown,
}

public sealed class RecorderException(RecorderErrorKind kind, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public RecorderErrorKind Kind { get; } = kind;
}

public enum RecorderWarningKind
{
    NoSystemAudioDevice,
    SystemAudioDeviceLost,
    SavedAsWav,
}

public sealed record RecorderWarning(RecorderWarningKind Kind, string Message);

public static class RecorderErrorMapper
{
    internal const int E_ACCESSDENIED = unchecked((int)0x80070005);
    internal const int E_NOTFOUND = unchecked((int)0x80070490);
    internal const int AUDCLNT_E_DEVICE_INVALIDATED = unchecked((int)0x88890004);
    internal const int AUDCLNT_E_DEVICE_IN_USE = unchecked((int)0x8889000A);
    internal const int AUDCLNT_E_SERVICE_NOT_RUNNING = unchecked((int)0x88890010);
    internal const int ERROR_DISK_FULL = unchecked((int)0x80070070);
    internal const int ERROR_HANDLE_DISK_FULL = unchecked((int)0x80070027);

    public static RecorderException ForMicrophoneStart(Exception ex) => ex switch
    {
        RecorderException r => r,
        UnauthorizedAccessException => AccessDenied(ex),
        COMException { HResult: E_ACCESSDENIED } => AccessDenied(ex),
        COMException { HResult: E_NOTFOUND } => NoMicrophone(ex),
        COMException { HResult: AUDCLNT_E_DEVICE_INVALIDATED } => NoMicrophone(ex),
        COMException { HResult: AUDCLNT_E_DEVICE_IN_USE } => new RecorderException(RecorderErrorKind.MicrophoneInUseExclusively,
            "Another app is using your microphone in exclusive mode, so it can't be shared. Close that app or turn off exclusive mode in Sound settings, then try again.", ex),
        COMException { HResult: AUDCLNT_E_SERVICE_NOT_RUNNING } => new RecorderException(RecorderErrorKind.Unknown,
            "The Windows audio service isn't running. Restart your PC or start the \"Windows Audio\" service, then try again.", ex),
        _ => new RecorderException(RecorderErrorKind.Unknown, "The microphone couldn't be started. Check your sound settings and try again.", ex),
    };

    public static RecorderException ForWrite(Exception ex) => IsDiskFull(ex)
        ? new RecorderException(RecorderErrorKind.DiskFull, "Your disk is full, so the recording was stopped. Free up some space and try again.", ex)
        : new RecorderException(RecorderErrorKind.WriteFailed, "The recording couldn't be written to disk. Check that the drive is available and try again.", ex);

    public static bool IsDiskFull(Exception ex) => ex.HResult is ERROR_DISK_FULL or ERROR_HANDLE_DISK_FULL;

    public static RecorderException MicrophoneDisconnected(Exception? ex) => new(RecorderErrorKind.MicrophoneDisconnected,
        "Your microphone was disconnected, so recording stopped.", ex);

    private static RecorderException AccessDenied(Exception ex) => new(RecorderErrorKind.MicrophoneAccessDenied,
        "Meeting Recorder doesn't have permission to use your microphone. Turn on \"Let desktop apps access your microphone\" in Privacy settings, then try again.", ex);

    private static RecorderException NoMicrophone(Exception ex) => new(RecorderErrorKind.NoMicrophone,
        "No microphone was found. Connect one, or choose an input device in Sound settings, then try again.", ex);
}
