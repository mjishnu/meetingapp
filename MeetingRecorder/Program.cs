using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace MeetingRecorder;

/// <summary>
/// Custom entry point for single-instance behaviour so there are never 2 detectors.
/// </summary>
public static partial class Program
{
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(IntPtr hWnd);

    [STAThread]
    private static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        var instance = AppInstance.FindOrRegisterForKey("MeetingRecorder");
        if (!instance.IsCurrent)
        {
            var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
            // Redirect on a worker thread since STA thread will deadlock
            Task.Run(() => instance.RedirectActivationToAsync(activation).AsTask()).Wait(TimeSpan.FromSeconds(5));
            // move focus to the open window
            var targetProcess = Process.GetProcessById((int)instance.ProcessId);
            if (targetProcess.MainWindowHandle != IntPtr.Zero)
                SetForegroundWindow(targetProcess.MainWindowHandle);

            return 0;
        }

        Application.Start(_ =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            var app = new App();
            instance.Activated += (_, _) => app.OnRedirectedActivation();
        });
        return 0;
    }
}
