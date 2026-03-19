namespace BobTools.Models;

public sealed record HotkeyKeyOption(uint Value, string DisplayName)
{
    public override string ToString() => DisplayName;
}