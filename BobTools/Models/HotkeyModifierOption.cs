namespace BobTools.Models;

public sealed record HotkeyModifierOption(uint Value, string DisplayName)
{
    public override string ToString() => DisplayName;
}