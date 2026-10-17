using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using DofusCompanion.Application.Imaging;

namespace DofusCompanion.App.Services;

public sealed class CaptureException(string message) : Exception(message);

/// <summary>
/// One-shot, user-triggered capture of a monitor's pixels through GDI BitBlt (the same mechanism as the
/// "Print Screen" key). It reads screen pixels only; it never accesses the game process.
/// Known limitation: games in exclusive fullscreen may come out black; use borderless/windowed mode.
/// </summary>
public sealed class ScreenCaptureService
{
    /// <summary>Captures the monitor that contains <paramref name="reference"/> (or the nearest one).</summary>
    public RasterImage CaptureMonitorOf(Window reference)
    {
        var hwnd = new WindowInteropHelper(reference).Handle;
        var monitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var info = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (!NativeMethods.GetMonitorInfo(monitor, ref info))
            throw new CaptureException("Unable to read monitor information.");

        var r = info.rcMonitor;
        return Capture(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
    }

    /// <summary>Captures a rectangle in physical screen pixels.</summary>
    public static RasterImage Capture(int x, int y, int width, int height)
    {
        if (width <= 0 || height <= 0) throw new CaptureException("Empty capture area.");

        var screenDc = NativeMethods.GetDC(0);
        if (screenDc == 0) throw new CaptureException("Screen device context unavailable.");
        var memDc = NativeMethods.CreateCompatibleDC(screenDc);
        nint dib = 0, old = 0;
        try
        {
            var header = new NativeMethods.BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
                biWidth = width,
                biHeight = -height, // top-down rows
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0, // BI_RGB
            };
            dib = NativeMethods.CreateDIBSection(screenDc, ref header, 0, out var bits, 0, 0);
            if (dib == 0 || bits == 0) throw new CaptureException("Could not allocate the capture buffer.");
            old = NativeMethods.SelectObject(memDc, dib);

            if (!NativeMethods.BitBlt(memDc, 0, 0, width, height, screenDc, x, y, NativeMethods.SRCCOPY | NativeMethods.CAPTUREBLT))
                throw new CaptureException($"Screen copy failed (Win32 error {Marshal.GetLastWin32Error()}).");

            var pixels = new byte[width * height * 4];
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255; // GDI leaves alpha undefined
            return new RasterImage(width, height, pixels);
        }
        finally
        {
            if (old != 0) NativeMethods.SelectObject(memDc, old);
            if (dib != 0) NativeMethods.DeleteObject(dib);
            NativeMethods.DeleteDC(memDc);
            _ = NativeMethods.ReleaseDC(0, screenDc);
        }
    }

    /// <summary>Asks Windows to leave the window out of screen captures (Windows 10 2004+). Returns false if unsupported.</summary>
    public static bool ExcludeFromCapture(Window window, bool exclude)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        return hwnd != 0 && NativeMethods.SetWindowDisplayAffinity(hwnd, exclude ? NativeMethods.WDA_EXCLUDEFROMCAPTURE : NativeMethods.WDA_NONE);
    }
}
