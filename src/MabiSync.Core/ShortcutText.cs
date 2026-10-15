namespace MabiSync.Core;

public static class ShortcutText
{
    private static readonly Dictionary<ushort, string> Names = new()
    {
        [0x20] = "Space", [0x0D] = "Enter", [0x1B] = "Esc", [0x09] = "Tab", [0x08] = "Backspace",
        [0x21] = "PageUp", [0x22] = "PageDown", [0x23] = "End", [0x24] = "Home", [0x25] = "Left",
        [0x26] = "Up", [0x27] = "Right", [0x28] = "Down", [0x2D] = "Insert", [0x2E] = "Delete",
        [0x6A] = "Multiply", [0x6B] = "Add", [0x6D] = "Subtract", [0x6E] = "Decimal", [0x6F] = "Divide",
        [0xBA] = "Semicolon", [0xBB] = "Equals", [0xBC] = "Comma", [0xBD] = "Minus", [0xBE] = "Period",
        [0xBF] = "Slash", [0xC0] = "Backtick", [0xDB] = "LeftBracket", [0xDC] = "Backslash", [0xDD] = "RightBracket", [0xDE] = "Quote"
    };
    public static KeyBinding FromVirtualKey(ushort key, bool control = false, bool alt = false, bool shift = false)
    {
        if (key == 0x78 || !(key is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A or >= 0x60 and <= 0x69 or >= 0x70 and <= 0x87 || Names.ContainsKey(key)))
            throw new FormatException("此按键不可用；F9保留为本机停止键，请换一个按键。");
        return new(key, control, alt, shift);
    }
    public static KeyBinding? Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var parts = text.Trim().ToUpperInvariant().Split('+', StringSplitOptions.TrimEntries);
        var main = parts[^1]; var mods = parts[..^1];
        if (mods.Distinct().Count() != mods.Length || mods.Any(m => m is not ("CTRL" or "ALT" or "SHIFT"))) throw new FormatException("快捷键示例：F8、Ctrl+1、Space。");
        ushort key = Names.FirstOrDefault(p => p.Value.Equals(main, StringComparison.OrdinalIgnoreCase)).Key;
        if (main == "ESCAPE") key = 0x1b;
        if (main.Length == 1 && char.IsAsciiLetterOrDigit(main[0])) key = main[0];
        else if (main.StartsWith('F') && int.TryParse(main.AsSpan(1), out var number) && number is >= 1 and <= 24) key = (ushort)(0x70 + number - 1);
        else if (main.StartsWith("NUMPAD") && int.TryParse(main.AsSpan(6), out var digit) && digit is >= 0 and <= 9) key = (ushort)(0x60 + digit);
        return FromVirtualKey(key, mods.Contains("CTRL"), mods.Contains("ALT"), mods.Contains("SHIFT"));
    }
    public static string Format(KeyBinding key)
    {
        var name = key.Key switch { >= 0x70 and <= 0x87 => "F" + (key.Key - 0x70 + 1), >= 0x60 and <= 0x69 => "NumPad" + (key.Key - 0x60), _ => Names.GetValueOrDefault(key.Key) ?? ((char)key.Key).ToString() };
        return (key.Control ? "Ctrl+" : "") + (key.Alt ? "Alt+" : "") + (key.Shift ? "Shift+" : "") + name;
    }
}
