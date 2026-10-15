namespace MabiSync.Windows;

public static class KeyboardInputFlags
{
    // Distinguishes the navigation cluster and keypad divide from non-extended keys.
    public static uint For(ushort key, bool down) =>
        (key is >= 0x21 and <= 0x28 or 0x2D or 0x2E or 0x6F ? 1u : 0u) | (down ? 0u : 2u);
}
