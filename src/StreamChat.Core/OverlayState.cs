using StreamChat.Core.Chat;
using StreamChat.Core.Rules;

namespace StreamChat.Core;

/// <summary>Решает, должно ли окно быть на экране. Время передаётся снаружи, монотонное.</summary>
public sealed class OverlayState
{
    private const int RememberedIds = 5000;

    // за это время деньги точно успевают заметить, раньше поток чата их не вытеснит
    private static readonly TimeSpan MoneyOnScreen = TimeSpan.FromSeconds(30);

    private readonly List<(ChatMessage Message, TimeSpan Added)> _entries = [];
    private readonly HashSet<(Platform, string)> _seen = [];
    private readonly Queue<(Platform, string)> _seenOrder = new();
    private ChatRules? _rules;
    private HashSet<string> _ignored = new(StringComparer.OrdinalIgnoreCase);
    private TimeSpan _lastActivity;
    private bool _hidden;
    private bool _blocked;
    private int _maxMessages = 6;
    private int _hideAfterSeconds = 15;
    private TimeSpan _now;

    public IReadOnlyList<ChatMessage> Messages => _entries.ConvertAll(entry => entry.Message);

    /// <summary>Донаты, платные сообщения и подписки: не пропадают из-за потока, фильтров и списка скрытых.</summary>
    public static bool IsMoney(ChatMessage message) =>
        message.Mark is MessageMark.Paid or MessageMark.Donation or MessageMark.Subscription;
    public bool Editing { get; private set; }

    /// <summary>Открыты настройки: чат виден как образец.</summary>
    public bool Previewing { get; set; }

    /// <summary>Счётчик зрителей от тишины в чате не зависит.</summary>
    public bool CountersEnabled { get; set; }

    public bool ShouldShowCounters => CountersEnabled && !_blocked && (Editing || !_hidden);

    public void Apply(ChatEvent change, TimeSpan now)
    {
        _now = now;
        switch (change)
        {
            // сначала повтор: он не должен будить окно, скрытое тишиной
            case Posted(var message) when IsNew(message) && !IsIgnored(message):
                if ((_rules is null ? message : _rules.Check(message, now)) is not { } shown) break;
                // новая версия того же сообщения встаёт на его место, а не второй строкой
                var index = shown.Revision.Length == 0 ? -1
                    : _entries.FindIndex(e => e.Message.Platform == shown.Platform && e.Message.Id == shown.Id);
                if (index < 0)
                {
                    Add(shown, now);
                    break;
                }

                _entries[index] = (shown, now);
                _lastActivity = now;
                break;
            case Deleted(var platform, var id):
                _entries.RemoveAll(e => e.Message.Platform == platform && e.Message.Id == id);
                break;
            case UserCleared(var platform, var author):
                _entries.RemoveAll(e => e.Message.Platform == platform && e.Message.AuthorId == author);
                break;
            case Cleared(var platform):
                _entries.RemoveAll(e => e.Message.Platform == platform);
                break;
        }
    }

    public void SetRules(ChatRules rules) => _rules = rules;

    public void SetIgnored(string names)
    {
        _ignored = new HashSet<string>(
            names.Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(name => name.TrimStart('@')),
            StringComparer.OrdinalIgnoreCase);
        _entries.RemoveAll(e => IsIgnored(e.Message));
    }

    public void Configure(int maxMessages, int hideAfterSeconds)
    {
        _maxMessages = maxMessages;
        _hideAfterSeconds = hideAfterSeconds;
        Trim();
    }

    public void Add(ChatMessage message, TimeSpan now)
    {
        _now = now;
        _entries.Add((message, now));
        Trim();
        _lastActivity = now;
    }

    public void ToggleHidden(TimeSpan now)
    {
        // клавиша переключает то, что видно: после автоскрытия она показывает
        var silent = !ShouldShow(now) && !ShouldShowCounters;
        if (_hidden || _blocked || silent)
        {
            Reveal(now);
            return;
        }

        _hidden = true;
        Editing = false;
    }

    public void ToggleEditing(TimeSpan now)
    {
        Editing = !Editing;
        Reveal(now);
    }

    public void Reveal(TimeSpan now)
    {
        // пока чат был спрятан, деньги никто не видел: их полминуты начинаются сейчас
        if (_hidden || _blocked)
        {
            for (var i = 0; i < _entries.Count; i++)
                if (IsMoney(_entries[i].Message)) _entries[i] = (_entries[i].Message, now);
        }

        _hidden = false;
        _blocked = false;
        _lastActivity = now;
        _now = now;
    }

    /// <returns>true только для первого сбоя, чтобы не повторять уведомление.</returns>
    public bool Block()
    {
        var first = !_blocked;
        _blocked = true;
        Editing = false;
        return first;
    }

    public bool ShouldShow(TimeSpan now)
    {
        if (_blocked) return false;
        if (Editing || (Previewing && !_hidden)) return true;
        return !_hidden && _entries.Count > 0
            && (_hideAfterSeconds == 0 || now - _lastActivity < TimeSpan.FromSeconds(_hideAfterSeconds));
    }

    private bool IsNew(ChatMessage message)
    {
        if (message.Id.Length == 0) return true;
        var key = (message.Platform, message.Revision.Length == 0 ? message.Id : $"{message.Id}\n{message.Revision}");
        if (!_seen.Add(key)) return false;
        _seenOrder.Enqueue(key);
        if (_seenOrder.Count > RememberedIds) _seen.Remove(_seenOrder.Dequeue());
        return true;
    }

    private bool IsIgnored(ChatMessage message) =>
        !IsMoney(message)
        && (_ignored.Contains(message.Author.TrimStart('@')) || _ignored.Contains(message.Login.TrimStart('@')));

    private void Trim()
    {
        while (_entries.Count > _maxMessages)
        {
            // защищённых не больше половины экрана, иначе волна подарков полностью закроет чат
            var shielded = _entries.Count(IsProtected);
            var victim = shielded > Math.Max(1, _maxMessages / 2)
                ? _entries.FindIndex(IsProtected)
                : _entries.FindIndex(e => !IsProtected(e));
            _entries.RemoveAt(victim >= 0 ? victim : 0);
        }
    }

    private bool IsProtected((ChatMessage Message, TimeSpan Added) entry) =>
        IsMoney(entry.Message) && (_hidden || _blocked || _now - entry.Added < MoneyOnScreen);
}
