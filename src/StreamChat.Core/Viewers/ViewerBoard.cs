using System.Globalization;

namespace StreamChat.Core.Viewers;

public enum ViewerState { Unknown, Live, Offline }

public sealed record ViewerReading(ViewerState State, long Count = 0);

/// <summary>Последнее известное число зрителей по площадкам.</summary>
public sealed class ViewerBoard
{
    // дольше показывать старое число нечестно
    private static readonly TimeSpan Fresh = TimeSpan.FromMinutes(3);

    private static readonly NumberFormatInfo Grouped = new() { NumberGroupSeparator = " ", NumberDecimalDigits = 0 };

    private readonly Dictionary<Platform, (ViewerReading Reading, TimeSpan At)> _last = [];

    public void Set(Platform platform, ViewerReading reading, TimeSpan now)
    {
        // один неудачный запрос не должен гасить число
        if (reading.State == ViewerState.Unknown) return;
        _last[platform] = (reading, now);
    }

    public void Forget(Platform platform) => _last.Remove(platform);

    public string Text(Platform platform, TimeSpan now) => Current(platform, now) switch
    {
        { State: ViewerState.Live } live => Format(live.Count),
        { State: ViewerState.Offline } => "офлайн",
        _ => "—",
    };

    public string Total(IEnumerable<Platform> platforms, TimeSpan now)
    {
        var live = platforms.Select(platform => Current(platform, now)).Where(r => r is { State: ViewerState.Live }).ToArray();
        return live.Length == 0 ? "—" : Format(live.Sum(r => (decimal)r!.Count));
    }

    private ViewerReading? Current(Platform platform, TimeSpan now) =>
        _last.TryGetValue(platform, out var last) && now - last.At < Fresh ? last.Reading : null;

    private static string Format(decimal count) => count.ToString("N0", Grouped);
}
