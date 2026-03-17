using StarCitizenOverlayStarter.Interop;
using System;
using System.Diagnostics;
using System.Drawing;

namespace StarCitizenOverlayStarter.Utils;

internal static class StarCitizenWindowLocator
{
    private const string ProcessName = "StarCitizen";

    public static bool TryGetClientBounds(out Rectangle bounds, out string errorMessage)
    {
        foreach (var process in Process.GetProcessesByName(ProcessName))
        {
            try
            {
                if (process.MainWindowHandle == IntPtr.Zero || !NativeMethods.IsWindowVisible(process.MainWindowHandle))
                {
                    continue;
                }

                if (TryGetClientBounds(process.MainWindowHandle, out bounds))
                {
                    errorMessage = string.Empty;
                    return true;
                }
            }
            finally
            {
                process.Dispose();
            }
        }

        bounds = Rectangle.Empty;
        errorMessage = "StarCitizen.exe is not running or its game window is unavailable.";
        return false;
    }

    private static bool TryGetClientBounds(IntPtr windowHandle, out Rectangle bounds)
    {
        bounds = Rectangle.Empty;

        if (!TryGetMonitorBounds(windowHandle, out var monitorBounds))
        {
            return false;
        }

        if (!NativeMethods.GetClientRect(windowHandle, out var clientRect))
        {
            return TryGetWindowBounds(windowHandle, monitorBounds, out bounds);
        }

        var topLeft = new NativeMethods.POINT
        {
            X = clientRect.Left,
            Y = clientRect.Top
        };

        if (!NativeMethods.ClientToScreen(windowHandle, ref topLeft))
        {
            return TryGetWindowBounds(windowHandle, monitorBounds, out bounds);
        }

        var width = clientRect.Right - clientRect.Left;
        var height = clientRect.Bottom - clientRect.Top;
        if (width <= 0 || height <= 0)
        {
            return TryGetWindowBounds(windowHandle, monitorBounds, out bounds);
        }

        var clientBounds = new Rectangle(topLeft.X, topLeft.Y, width, height);
        var clampedBounds = Rectangle.Intersect(clientBounds, monitorBounds);

        if (clampedBounds.Width <= 0 || clampedBounds.Height <= 0)
        {
            return TryGetWindowBounds(windowHandle, monitorBounds, out bounds);
        }

        bounds = clampedBounds;
        return true;
    }

    private static bool TryGetWindowBounds(IntPtr windowHandle, Rectangle monitorBounds, out Rectangle bounds)
    {
        bounds = Rectangle.Empty;

        if (!NativeMethods.GetWindowRect(windowHandle, out var windowRect))
        {
            return false;
        }

        var windowBounds = Rectangle.FromLTRB(windowRect.Left, windowRect.Top, windowRect.Right, windowRect.Bottom);
        var clampedBounds = Rectangle.Intersect(windowBounds, monitorBounds);
        if (clampedBounds.Width <= 0 || clampedBounds.Height <= 0)
        {
            return false;
        }

        bounds = clampedBounds;
        return true;
    }

    private static bool TryGetMonitorBounds(IntPtr windowHandle, out Rectangle bounds)
    {
        bounds = Rectangle.Empty;

        var monitorHandle = NativeMethods.MonitorFromWindow(windowHandle, NativeMethods.MONITOR_DEFAULTTONEAREST);
        if (monitorHandle == IntPtr.Zero)
        {
            return false;
        }

        var monitorInfo = new NativeMethods.MONITORINFO
        {
            cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>()
        };

        if (!NativeMethods.GetMonitorInfo(monitorHandle, ref monitorInfo))
        {
            return false;
        }

        bounds = Rectangle.FromLTRB(
            monitorInfo.rcMonitor.Left,
            monitorInfo.rcMonitor.Top,
            monitorInfo.rcMonitor.Right,
            monitorInfo.rcMonitor.Bottom);

        return bounds.Width > 0 && bounds.Height > 0;
    }
}