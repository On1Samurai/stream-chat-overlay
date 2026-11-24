using System.Globalization;

namespace StreamChat.Core.Obs;

/// <summary>Что из состояния OBS показать и насколько это срочно.</summary>
public sealed class ObsBoard
{
    private const double DropWarning = 1;
    private const double DropAlarm = 5;
    private static readonly TimeSpan Fresh = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DropWindow = TimeSpan.FromSeconds(30);

    private readonly Queue<(TimeSpan At, long Skipped, long Total)> _frames = new();
    private ObsSnapshot? _last;
    private TimeSpan _lastAt;

    public void Update(ObsSnapshot snapshot, TimeSpan now)
    {
        // счётчики OBS начинаются заново с каждым эфиром
        if (!snapshot.Streaming || (_last is { } before && (snapshot.Total < before.Total || snapshot.Skipped < before.Skipped)))
            _frames.Clear();
        if (snapshot.Streaming) _frames.Enqueue((now, snapshot.Skipped, snapshot.Total));
        while (_frames.Count > 0 && now - _frames.Peek().At > DropWindow) _frames.Dequeue();

        _last = snapshot;
        _lastAt = now;
    }

    public void Forget()
    {
        _last = null;
        _frames.Clear();
    }

    /// <summary>OBS ответила недавно: без этого «эфира нет» нельзя утверждать.</summary>
    public bool IsKnown(TimeSpan now) => Current(now) is not null;

    /// <summary>Идёт эфир или запись.</summary>
    public bool IsOnAir(TimeSpan now) => Current(now) is { } s && (s.Streaming || (s.Recording && !s.RecordPaused));

    /// <summary>OBS известна и нет ни эфира, ни записи (пауза записи тоже запись).</summary>
    public bool IsIdle(TimeSpan now) => Current(now) is { } s && !s.Streaming && !s.Reconnecting && !s.Recording;

    public IReadOnlyList<StatusItem> Items(TimeSpan now)
    {
        // старые данные нельзя выдавать за «всё хорошо»
        if (Current(now) is not { } state) return [new("OBS", "нет связи", StatusLevel.Warning)];

        var items = new List<StatusItem>
        {
            state.Reconnecting ? new("ЭФИР", "переподключение", StatusLevel.Bad)
            : state.Streaming ? new("ЭФИР", Format(state.Duration), StatusLevel.Good)
            : new("ЭФИР", "нет", StatusLevel.Neutral),
        };
        if (state.Recording)
        {
            items.Add(state.RecordPaused
                ? new("ЗАПИСЬ", "пауза", StatusLevel.Warning)
                : new("ЗАПИСЬ", "идёт", StatusLevel.Good));
        }

        var audience = state.Streaming || state.Recording;
        items.Add(state.Mic switch
        {
            MicState.On => new("МИК", "вкл", StatusLevel.Good),
            MicState.Muted => new("МИК", "ВЫКЛЮЧЕН", audience ? StatusLevel.Bad : StatusLevel.Warning),
            MicState.NotInScene => new("МИК", "нет в сцене", StatusLevel.Warning),
            MicState.Partial => new("МИК", "частично выкл", StatusLevel.Warning),
            MicState.Unknown => new("МИК", "?", StatusLevel.Warning),
            _ => new("МИК", "не найден", StatusLevel.Warning),
        });

        var lost = LostPercent();
        if (lost >= DropWarning)
        {
            var text = lost.ToString("0.#", CultureInfo.InvariantCulture) + "%";
            items.Add(new("ПОТЕРИ", text, lost >= DropAlarm ? StatusLevel.Bad : StatusLevel.Warning));
        }

        return items;
    }

    private ObsSnapshot? Current(TimeSpan now) => _last is { } last && now - _lastAt < Fresh ? last : null;

    private double LostPercent()
    {
        if (_frames.Count < 2) return 0;
        var first = _frames.Peek();
        var latest = _frames.Last();
        var total = latest.Total - first.Total;
        return total <= 0 ? 0 : 100d * (latest.Skipped - first.Skipped) / total;
    }

    private static string Format(TimeSpan time) => $"{(long)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}";
}
