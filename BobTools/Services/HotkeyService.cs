using System.ComponentModel;
using System.Runtime.InteropServices;
using BobTools.Interop;

namespace BobTools.Services;

public sealed class HotkeyService : IHotkeyService
{
    private const int HotkeyId = 9001;
    private IntPtr _windowHandle;
    private bool _isRegistered;

    public event EventHandler? HotkeyPressed;

    public void Register(IntPtr windowHandle, uint modifiers, uint virtualKey)
    {
        Unregister();
        _windowHandle = windowHandle;
        var ok = NativeMethods.RegisterHotKey(windowHandle, HotkeyId, modifiers | NativeMethods.MOD_NOREPEAT, virtualKey);
        if (!ok)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to register global hotkey.");
        }

        _isRegistered = true;
    }

    public bool ProcessWindowMessage(int msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg != NativeMethods.WM_HOTKEY || wParam.ToInt32() != HotkeyId)
        {
            return false;
        }

        HotkeyPressed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void Unregister()
    {
        if (!_isRegistered || _windowHandle == IntPtr.Zero)
        {
            return;
        }

        NativeMethods.UnregisterHotKey(_windowHandle, HotkeyId);
        _isRegistered = false;
    }

    public void Dispose() => Unregister();
}
