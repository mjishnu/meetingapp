using MeetingRecorder.Interop;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace MeetingRecorder.Services;

/// <summary>Sizes windows in effective pixels. AppWindow works in physical pixels, so everything goes through the DPI scale.</summary>
internal static class WindowSizing
{
    public static nint Hwnd(Window window) => Win32Interop.GetWindowFromWindowId(window.AppWindow.Id);

    public static void ResizeEpx(Window window, double widthEpx, double heightEpx)
    {
        var scale = NativeMethods.ScaleForWindow(Hwnd(window));
        window.AppWindow.Resize(new SizeInt32((int)Math.Ceiling(widthEpx * scale), (int)Math.Ceiling(heightEpx * scale)));
    }

    public static void ApplyLimitsEpx(Window window, OverlappedPresenter presenter, double minW, double minH, double? maxW = null, double? maxH = null)
    {
        void Apply()
        {
            var scale = NativeMethods.ScaleForWindow(Hwnd(window));
            presenter.PreferredMinimumWidth = (int)Math.Ceiling(minW * scale);
            presenter.PreferredMinimumHeight = (int)Math.Ceiling(minH * scale);
            if (maxW is { } mw) presenter.PreferredMaximumWidth = (int)Math.Ceiling(mw * scale);
            if (maxH is { } mh) presenter.PreferredMaximumHeight = (int)Math.Ceiling(mh * scale);
        }

        Apply();
        if (window.Content is FrameworkElement root)
        {
            double lastScale = 0;
            root.Loaded += (_, _) =>
            {
                lastScale = root.XamlRoot.RasterizationScale;
                root.XamlRoot.Changed += (s, _) =>
                {
                    if (Math.Abs(s.RasterizationScale - lastScale) < 0.001) return;
                    lastScale = s.RasterizationScale;
                    Apply();
                };
            };
        }
    }

    /// <summary>Places a window at the bottom-right of the work area of the monitor showing the foreground window.</summary>
    public static void PlaceBottomRightOfForegroundMonitor(Window window, double widthEpx, double heightEpx, double marginEpx)
    {
        var foreground = NativeMethods.GetForegroundWindow();
        DisplayArea display = foreground != 0
            ? DisplayArea.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(foreground), DisplayAreaFallback.Primary)
            : DisplayArea.Primary;
        var work = display.WorkArea; 
        // Move onto the target monitor first so any WM_DPICHANGED rescale happens before we size for that DPI.
        window.AppWindow.Move(new PointInt32(work.X + work.Width / 2, work.Y + work.Height / 2));
        var scale = NativeMethods.ScaleForPoint(work.X + work.Width / 2, work.Y + work.Height / 2);
        int w = (int)Math.Ceiling(widthEpx * scale);
        int h = (int)Math.Ceiling(heightEpx * scale);
        int m = (int)Math.Round(marginEpx * scale);
        window.AppWindow.MoveAndResize(new RectInt32(work.X + work.Width - w - m, work.Y + work.Height - h - m, w, h));
    }
}
