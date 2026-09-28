using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MeetingRecorder.Interop;

/// <summary>
/// The Win32 pieces the Windows App SDK doesn't expose (record popup).
/// </summary>
internal static partial class NativeMethods
{
    public const int GWL_EXSTYLE = -20;
    public const long WS_EX_NOACTIVATE = 0x08000000L;
    public const uint WM_MOUSEACTIVATE = 0x0021;
    public const nint MA_NOACTIVATE = 3;
    public const uint WM_NCCALCSIZE = 0x0083;
    public const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010, SWP_FRAMECHANGED = 0x0020;
    public const uint MONITOR_DEFAULTTONEAREST = 2;
    public const int MDT_EFFECTIVE_DPI = 0;
    public const uint DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    public const int DWMWCP_ROUND = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [LibraryImport("user32.dll")]
    public static partial uint GetDpiForWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    public static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    public static partial nint GetWindowLongPtr(nint hwnd, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    public static partial nint SetWindowLongPtr(nint hwnd, int index, nint value);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int cx, int cy, uint flags);

    [LibraryImport("user32.dll")]
    public static partial nint MonitorFromPoint(POINT pt, uint flags);

    [LibraryImport("shcore.dll")]
    public static partial int GetDpiForMonitor(nint monitor, int dpiType, out uint dpiX, out uint dpiY);

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmSetWindowAttribute(nint hwnd, uint attribute, ref int value, int size);

    [LibraryImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool SetWindowSubclass(nint hwnd,
        delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nuint, nuint, nint> subclassProc, nuint idSubclass, nuint refData);

    [LibraryImport("comctl32.dll")]
    public static partial nint DefSubclassProc(nint hwnd, uint msg, nint wParam, nint lParam);

    /// <summary>
    /// Subclass procedure for the prompt window: a click must never activate it, so the meeting app keeps focus.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    public static nint NoActivateSubclassProc(nint hwnd, uint msg, nint wParam, nint lParam, nuint id, nuint refData)
    {
        if (msg == WM_MOUSEACTIVATE) return MA_NOACTIVATE;
        return DefSubclassProc(hwnd, msg, wParam, lParam);
    }

    public static unsafe void MakeNonActivating(nint hwnd)
    {
        var ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, (nint)((long)ex | WS_EX_NOACTIVATE));
        SetWindowSubclass(hwnd, &NoActivateSubclassProc, 1, 0);
    }

    /// <summary>Subclass procedure for the prompt window</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    public static nint NoFrameSubclassProc(nint hwnd, uint msg, nint wParam, nint lParam, nuint id, nuint refData)
    {
        // wParam != 0: returning 0 keeps the proposed window rect as the client rect (no non-client area).
        if (msg == WM_NCCALCSIZE && wParam != 0) return 0;
        return DefSubclassProc(hwnd, msg, wParam, lParam);
    }

    /// <summary>
    /// remove the fixed non-client frame (title bar, borders) from a window, so it can be drawn borderless.
    /// </summary>
    public static unsafe void RemoveNonClientFrame(nint hwnd)
    {
        SetWindowSubclass(hwnd, &NoFrameSubclassProc, 2, 0);
        SetWindowPos(hwnd, 0, 0, 0, 0, 0, SWP_FRAMECHANGED | SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    /// <summary>Borderless windows lose Windows 11 rounded corners; ask DWM for them back.</summary>
    public static void UseRoundedCorners(nint hwnd)
    {
        int preference = DWMWCP_ROUND;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
    }

    /// <summary>DPI scale (1.0 = 96 dpi) of the monitor containing a physical-pixel point.</summary>
    public static double ScaleForPoint(int x, int y)
    {
        var monitor = MonitorFromPoint(new POINT { X = x, Y = y }, MONITOR_DEFAULTTONEAREST);
        return GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out var dpi, out _) == 0 ? dpi / 96.0 : 1.0;
    }

    public static double ScaleForWindow(nint hwnd)
    {
        var dpi = GetDpiForWindow(hwnd);
        return dpi == 0 ? 1.0 : dpi / 96.0;
    }
}
