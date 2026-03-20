using BobTools.Interop;
using BobTools.Models;

namespace BobTools.Utils;

internal static class HotkeyCatalog
{
    public const uint DefaultModifiers = NativeMethods.MOD_CONTROL | NativeMethods.MOD_SHIFT;
    public const uint DefaultVirtualKey = 0x79; // F10
    public const uint RecommendedModifiers = DefaultModifiers;
    public const uint RecommendedVirtualKey = DefaultVirtualKey;

    public static IReadOnlyList<HotkeyModifierOption> ModifierOptions { get; } =
    [
        new HotkeyModifierOption(NativeMethods.MOD_ALT, "Alt"),
        new HotkeyModifierOption(NativeMethods.MOD_CONTROL | NativeMethods.MOD_SHIFT, "Ctrl+Shift"),
        new HotkeyModifierOption(NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT, "Ctrl+Alt"),
        new HotkeyModifierOption(NativeMethods.MOD_ALT | NativeMethods.MOD_SHIFT, "Alt+Shift"),
        new HotkeyModifierOption(NativeMethods.MOD_CONTROL | NativeMethods.MOD_SHIFT | NativeMethods.MOD_ALT, "Ctrl+Shift+Alt")
    ];

    public static IReadOnlyList<HotkeyKeyOption> KeyOptions { get; } = BuildKeyOptions();

    public static bool IsSupported(uint modifiers, uint virtualKey)
    {
        return ModifierOptions.Any(option => option.Value == modifiers)
            && KeyOptions.Any(option => option.Value == virtualKey);
    }

    public static string Format(uint modifiers, uint virtualKey)
    {
        var modifierText = ModifierOptions.FirstOrDefault(option => option.Value == modifiers)?.DisplayName;
        var keyText = KeyOptions.FirstOrDefault(option => option.Value == virtualKey)?.DisplayName;

        if (!string.IsNullOrWhiteSpace(modifierText) && !string.IsNullOrWhiteSpace(keyText))
        {
            return $"{modifierText}+{keyText}";
        }

        if (!string.IsNullOrWhiteSpace(keyText))
        {
            return keyText;
        }

        return $"0x{modifiers:X}+VK 0x{virtualKey:X}";
    }

    public static string RecommendedDisplay => Format(RecommendedModifiers, RecommendedVirtualKey);

    private static IReadOnlyList<HotkeyKeyOption> BuildKeyOptions()
    {
        var options = new List<HotkeyKeyOption>();

        for (uint virtualKey = 0x70; virtualKey <= 0x7B; virtualKey++)
        {
            options.Add(new HotkeyKeyOption(virtualKey, $"F{virtualKey - 0x6F}"));
        }

        for (uint virtualKey = 0x41; virtualKey <= 0x5A; virtualKey++)
        {
            options.Add(new HotkeyKeyOption(virtualKey, ((char)virtualKey).ToString()));
        }

        return options;
    }
}