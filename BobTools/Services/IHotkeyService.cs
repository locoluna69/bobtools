namespace BobTools.Services;

public interface IHotkeyService : IDisposable
{
    event EventHandler? HotkeyPressed;
    HotkeyRegistrationResult Register(IntPtr windowHandle, uint modifiers, uint virtualKey);
    void Unregister();
    bool ProcessWindowMessage(int msg, IntPtr wParam, IntPtr lParam);
}
