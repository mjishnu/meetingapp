using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using Serilog;

namespace MeetingRecorder.Core.Detection;

public sealed partial class RegistryMicUsageSource : IMicUsageSource
{
    public const string MicrophoneKeyPath = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";

    private const int RegNotifyChangeName = 0x1;
    private const int RegNotifyChangeLastSet = 0x4;
    private const int RegNotifyThreadAgnostic = 0x10000000;

    private readonly TimeSpan _safetyPoll;
    private readonly ManualResetEvent _stop = new(false);
    private Thread? _thread;

    public RegistryMicUsageSource(TimeSpan? safetyPoll = null) => _safetyPoll = safetyPoll ?? TimeSpan.FromSeconds(5);

    public event EventHandler? Changed;

    public void Start()
    {
        if (_thread is not null) return;
        _thread = new Thread(WatchLoop) { IsBackground = true, Name = "Mic usage watcher", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    public IReadOnlyList<MicAppUsage> ReadSnapshot()
    {
        var result = new List<MicAppUsage>();
        using var root = Registry.CurrentUser.OpenSubKey(MicrophoneKeyPath, writable: false);
        if (root is null) return result;
        ReadChildren(root, "", result);
        using var nonPackaged = root.OpenSubKey("NonPackaged", writable: false);
        if (nonPackaged is not null) ReadChildren(nonPackaged, MicAppUsage.NonPackagedPrefix, result);
        return result;
    }

    private static void ReadChildren(RegistryKey parent, string prefix, List<MicAppUsage> into)
    {
        foreach (var name in parent.GetSubKeyNames())
        {
            if (prefix.Length == 0 && name.Equals("NonPackaged", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                using var key = parent.OpenSubKey(name, writable: false);
                if (key is null) continue;
                into.Add(new MicAppUsage(prefix + name, ReadFileTime(key, "LastUsedTimeStart"), ReadFileTime(key, "LastUsedTimeStop")));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
            }
        }
    }

    private static long ReadFileTime(RegistryKey key, string name) => key.GetValue(name) switch
    {
        long l => l,
        int i => i,
        byte[] { Length: 8 } b => BitConverter.ToInt64(b),
        _ => 0,
    };

    private void WatchLoop()
    {
        using var changed = new AutoResetEvent(false);
        var handles = new WaitHandle[] { _stop, changed };
        RegistryKey? root = null;
        try
        {
            while (true)
            {
                root ??= Registry.CurrentUser.OpenSubKey(MicrophoneKeyPath, writable: false);
                bool armed = false;
                if (root is not null)
                {
                    int rc = RegNotifyChangeKeyValue(root.Handle, watchSubtree: true,
                        RegNotifyChangeName | RegNotifyChangeLastSet | RegNotifyThreadAgnostic,
                        changed.SafeWaitHandle, asynchronous: true);
                    armed = rc == 0;
                    if (!armed)
                    {
                        Log.Warning("RegNotifyChangeKeyValue failed ({ResultCode}); falling back to polling", rc);
                        root.Dispose();
                        root = null;
                    }
                }

                int signalled = WaitHandle.WaitAny(handles, _safetyPoll);
                if (signalled == 0) return;
                try
                {
                    Changed?.Invoke(this, EventArgs.Empty);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Mic usage change handler threw");
                }
            }
        }
        finally
        {
            root?.Dispose();
        }
    }

    public void Dispose()
    {
        _stop.Set();
        _thread?.Join(TimeSpan.FromSeconds(2));
        _stop.Dispose();
    }

    [LibraryImport("advapi32.dll")]
    private static partial int RegNotifyChangeKeyValue(SafeRegistryHandle hKey,
        [MarshalAs(UnmanagedType.Bool)] bool watchSubtree, int notifyFilter, SafeWaitHandle hEvent,
        [MarshalAs(UnmanagedType.Bool)] bool asynchronous);
}
