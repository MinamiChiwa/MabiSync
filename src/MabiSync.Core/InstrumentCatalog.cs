namespace MabiSync.Core;

public static class InstrumentCatalog
{
    public static IReadOnlyList<string> Defaults { get; } = Array.AsReadOnly(new[] {
        "鲁特琴","2和弦鲁特琴","3和弦鲁特琴","长笛","小提琴","夏卢莫管","曼陀林",
        "2和弦曼陀林","3和弦曼陀林","木琴","钢琴","2和弦钢琴","3和弦钢琴","大鼓"
    });
    public static IReadOnlyList<string> Merge(IEnumerable<string> fileLines,IEnumerable<string> roomInstruments)
    {
        return Defaults.Concat(fileLines).Concat(roomInstruments).Select(s=>s.Trim())
            .Where(s=>s.Length>0 && !s.StartsWith('#') && s.EnumerateRunes().Count()<=40 && !s.Any(c=>char.IsControl(c) || char.GetUnicodeCategory(c)==System.Globalization.UnicodeCategory.Format))
            .Distinct(StringComparer.Ordinal).Take(256).ToArray();
    }
}
