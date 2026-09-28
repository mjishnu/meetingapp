using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MeetingRecorder.Core.Detection;

internal static partial class ProcessKeys
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int ErrorSuccess = 0;

    public static string? KeyFor(int processId)
    {
        using var process = OpenProcess(ProcessQueryLimitedInformation, false, (uint)processId);
        if (process.IsInvalid) return null;

        if (PackageFamilyName(process) is { } family) return family;
        return ImagePath(process) is { } path ? AppNames.KeyForExecutable(path) : null;
    }

    private static unsafe string? PackageFamilyName(SafeProcessHandle process)
    {
        uint length = 256;
        char* buffer = stackalloc char[(int)length];
        return GetPackageFamilyName(process, ref length, buffer) == ErrorSuccess && length > 1
            ? new string(buffer, 0, (int)length - 1)
            : null;
    }

    private static unsafe string? ImagePath(SafeProcessHandle process)
    {
        uint length = 1024;
        char* buffer = stackalloc char[(int)length];
        return QueryFullProcessImageName(process, 0, buffer, ref length) ? new string(buffer, 0, (int)length) : null;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, char* exeName, ref uint size);

    [LibraryImport("kernel32.dll")]
    private static unsafe partial int GetPackageFamilyName(SafeProcessHandle process, ref uint length, char* name);
}
