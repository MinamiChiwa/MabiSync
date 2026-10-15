namespace MabiSync.Windows;

public static class DesktopCoordinates
{
    public static (int X, int Y) Normalize(int x, int y, int left, int top, int width, int height)
    {
        if (width <= 1 || height <= 1 || x < left || y < top || (long)x >= (long)left + width || (long)y >= (long)top + height) throw new ArgumentOutOfRangeException(nameof(x), "输入坐标超出当前桌面范围。");
        return ((int)Math.Round(((long)x - left) * 65535d / (width - 1)), (int)Math.Round(((long)y - top) * 65535d / (height - 1)));
    }
}
