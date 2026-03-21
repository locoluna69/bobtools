namespace BobTools.Services;

[Flags]
public enum HotkeyRegistrationMode
{
    None = 0,
    WindowMessage = 1,
    KeyboardHook = 2
}

public readonly record struct HotkeyRegistrationResult(
    HotkeyRegistrationMode Mode,
    int? RegisterHotKeyErrorCode,
    int? KeyboardHookErrorCode)
{
    public bool IsActive => Mode != HotkeyRegistrationMode.None;

    public bool HasWindowMessageRegistration => Mode.HasFlag(HotkeyRegistrationMode.WindowMessage);

    public bool HasKeyboardHookRegistration => Mode.HasFlag(HotkeyRegistrationMode.KeyboardHook);
}