using System.Globalization;

namespace MabiSync.Core;

public static class CountdownText
{
    public static int ParseMilliseconds(string text)
    {
        if (!decimal.TryParse(text.Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds) ||
            seconds is < 1 or > 60 || seconds * 1000 != decimal.Truncate(seconds * 1000))
            throw new FormatException("倒计时请填写 1–60 秒，最多三位小数，例如 5 或 2.5。");
        return (int)(seconds * 1000);
    }
}

public sealed record RoomInvitation(Uri Endpoint, string Code)
{
    public override string ToString() => $"MabiSync/1|{Endpoint.AbsoluteUri}|{Code}";
    public static RoomInvitation Parse(string text)
    {
        var parts = text.Trim().Split('|');
        if (parts.Length != 3 || parts[0] != "MabiSync/1" ||
            !Uri.TryCreate(parts[1], UriKind.Absolute, out var uri) || uri.Scheme is not ("ws" or "wss") ||
            string.IsNullOrEmpty(uri.Host) || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 ||
            parts[2].Length != 6 || !parts[2].All(char.IsAsciiLetterOrDigit))
            throw new FormatException("请粘贴完整的 MabiSync 邀请；单独的地址和房间码可在高级连接中填写。");
        return new(uri, parts[2].ToUpperInvariant());
    }
}
