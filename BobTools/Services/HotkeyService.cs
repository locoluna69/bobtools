using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using BobTools.Interop;

namespace BobTools.Services;

public sealed class HotkeyService : IHotkeyService
{
    private const int HotkeyId = 9001;
    private const long DuplicateTriggerWindowMilliseconds = 150;
    private IntPtr _windowHandle;
    private IntPtr _keyboardHookHandle;
    private bool _isRegistered;
    private uint _registeredModifiers;
    private uint _registeredVirtualKey;
    private int _activeVirtualKey;
    private long _lastTriggerTicks;
    private SynchronizationContext? _synchronizationContext;
    private readonly NativeMethods.LowLevelKeyboardProc _keyboardHookProc;

    public event EventHandler? HotkeyPressed;

    public HotkeyService()
    {
        _keyboardHookProc = KeyboardHookCallback;
    }

    public HotkeyRegistrationResult Register(IntPtr windowHandle, uint modifiers, uint virtualKey)
    {
        Unregister();

        _windowHandle = windowHandle;
        _registeredModifiers = modifiers;
        _registeredVirtualKey = virtualKey;
        _synchronizationContext = SynchronizationContext.Current;

        HotkeyRegistrationMode registrationMode = HotkeyRegistrationMode.None;
        int? keyboardHookErrorCode = null;
        int? registerHotKeyErrorCode = null;

        if (TryInstallKeyboardHook(out var hookErrorCode))
        {
            registrationMode |= HotkeyRegistrationMode.KeyboardHook;
        }
        else
        {
            keyboardHookErrorCode = hookErrorCode;
        }

        var ok = NativeMethods.RegisterHotKey(windowHandle, HotkeyId, modifiers | NativeMethods.MOD_NOREPEAT, virtualKey);
        if (ok)
        {
            _isRegistered = true;
            registrationMode |= HotkeyRegistrationMode.WindowMessage;
        }
        else
        {
            registerHotKeyErrorCode = Marshal.GetLastWin32Error();
        }

        if (registrationMode == HotkeyRegistrationMode.None)
        {
            throw new Win32Exception(
                registerHotKeyErrorCode ?? keyboardHookErrorCode ?? Marshal.GetLastWin32Error(),
                "Unable to register any global hotkey mechanism.");
        }

        return new HotkeyRegistrationResult(registrationMode, registerHotKeyErrorCode, keyboardHookErrorCode);
    }

    public bool ProcessWindowMessage(int msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg != NativeMethods.WM_HOTKEY || wParam.ToInt32() != HotkeyId)
        {
            return false;
        }

        NotifyHotkeyPressed(_registeredVirtualKey);
        return true;
    }

    public void Unregister()
    {
        if (!_isRegistered || _windowHandle == IntPtr.Zero)
        {
            UninstallKeyboardHook();
            ResetRegistrationState();
            return;
        }

        NativeMethods.UnregisterHotKey(_windowHandle, HotkeyId);
        _isRegistered = false;
        UninstallKeyboardHook();
        ResetRegistrationState();
    }

    public void Dispose()
    {
        Unregister();
    }

    private bool TryInstallKeyboardHook(out int errorCode)
    {
        errorCode = 0;
        var moduleHandle = NativeMethods.GetModuleHandle(null);
        _keyboardHookHandle = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _keyboardHookProc, moduleHandle, 0);

        if (_keyboardHookHandle != IntPtr.Zero)
        {
            return true;
        }

        errorCode = Marshal.GetLastWin32Error();
        return false;
    }

    private void UninstallKeyboardHook()
    {
        if (_keyboardHookHandle == IntPtr.Zero)
        {
            return;
        }

        NativeMethods.UnhookWindowsHookEx(_keyboardHookHandle);
        _keyboardHookHandle = IntPtr.Zero;
    }

    private void ResetRegistrationState()
    {
        _windowHandle = IntPtr.Zero;
        _registeredModifiers = 0;
        _registeredVirtualKey = 0;
        _activeVirtualKey = 0;
        Interlocked.Exchange(ref _lastTriggerTicks, 0);
    }

    private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _registeredVirtualKey != 0)
        {
            var message = wParam.ToInt32();
            var keyboardData = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);

            if (message is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN)
            {
                if (ShouldTriggerFromKeyboardHook(keyboardData.vkCode))
                {
                    NotifyHotkeyPressed(keyboardData.vkCode);
                }
            }
            else if (message is NativeMethods.WM_KEYUP or NativeMethods.WM_SYSKEYUP)
            {
                if (_activeVirtualKey != 0 && !IsRegisteredHotkeyCurrentlyPressed())
                {
                    _activeVirtualKey = 0;
                }
            }
        }

        return NativeMethods.CallNextHookEx(_keyboardHookHandle, nCode, wParam, lParam);
    }

    private bool ShouldTriggerFromKeyboardHook(uint virtualKey)
    {
        if (virtualKey != _registeredVirtualKey)
        {
            return false;
        }

        if (!AreConfiguredModifiersPressed())
        {
            return false;
        }

        return _activeVirtualKey != (int)virtualKey || !IsRegisteredHotkeyCurrentlyPressed();
    }

    private void NotifyHotkeyPressed(uint virtualKey)
    {
        if (_registeredVirtualKey == 0 || virtualKey != _registeredVirtualKey)
        {
            return;
        }

        var now = Environment.TickCount64;
        var previous = Interlocked.Read(ref _lastTriggerTicks);
        if (previous != 0 && now - previous < DuplicateTriggerWindowMilliseconds)
        {
            return;
        }

        _activeVirtualKey = (int)virtualKey;
        Interlocked.Exchange(ref _lastTriggerTicks, now);

        var handler = HotkeyPressed;
        if (handler is null)
        {
            return;
        }

        if (_synchronizationContext is not null)
        {
            _synchronizationContext.Post(_ => handler(this, EventArgs.Empty), null);
            return;
        }

        handler(this, EventArgs.Empty);
    }

    private bool IsRegisteredHotkeyCurrentlyPressed()
    {
        return IsVirtualKeyPressed((int)_registeredVirtualKey) && AreConfiguredModifiersPressed();
    }

    private bool AreConfiguredModifiersPressed()
    {
        var currentModifiers = GetPressedModifiers();
        return currentModifiers == _registeredModifiers;
    }

    private static uint GetPressedModifiers()
    {
        uint modifiers = 0;

        if (IsVirtualKeyPressed(NativeMethods.VK_MENU))
        {
            modifiers |= NativeMethods.MOD_ALT;
        }

        if (IsVirtualKeyPressed(NativeMethods.VK_CONTROL))
        {
            modifiers |= NativeMethods.MOD_CONTROL;
        }

        if (IsVirtualKeyPressed(NativeMethods.VK_SHIFT))
        {
            modifiers |= NativeMethods.MOD_SHIFT;
        }

        if (IsVirtualKeyPressed(NativeMethods.VK_LWIN) || IsVirtualKeyPressed(NativeMethods.VK_RWIN))
        {
            modifiers |= NativeMethods.MOD_WIN;
        }

        return modifiers;
    }

    private static bool IsVirtualKeyPressed(int virtualKey)
    {
        return (NativeMethods.GetAsyncKeyState(virtualKey) & 0x8000) != 0;
    }
}
