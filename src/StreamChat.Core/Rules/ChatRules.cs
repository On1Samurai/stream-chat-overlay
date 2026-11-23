using System.Text.RegularExpressions;
using StreamChat.Core.Chat;

namespace StreamChat.Core.Rules;

/// <summary>Что скрыть и что выделить.</summary>
public sealed partial class ChatRules
{
    private const string Hidden = "[текст скрыт фильтром]";
    private const int RememberedTexts = 2000;
    private static readonly TimeSpan RepeatWindow = TimeSpan.FromSeconds(30);

    // без схемы ссылку выдаёт только известная зона: иначе "1.2.3" и "и т.д." стали бы ссылками
    [GeneratedRegex(@"https?://|\bwww\.|[\p{L}\p{N}]\.(?:com|ru|su|рф|by|ua|kz|net|org|io|gg|ly|me|tv|cc|co|xyz|link|site|online|shop|store|top|club|pro|info|app|dev)(?![\p{L}\p{N}])", RegexOptions.IgnoreCase)]
    private static partial Regex Link();

    private readonly Regex? _mention;
    private readonly Regex? _blocked;
    private readonly bool _hideLinks;
    private readonly bool _hideRepeats;
    private readonly Dictionary<string, TimeSpan> _recent = [];

    public ChatRules(AppSettings settings)
    {
        _hideLinks = settings.HideLinks;
        _hideRepeats = settings.HideRepeats;

        var names = Split(settings.MentionNames).Select(name => name.TrimStart('@')).ToList();
        if (ChatLinks.TwitchChannel(settings.TwitchChannel) is { } channel) names.Add(channel);
        _mention = AnyWord(names, allowPrefix: false);
        _blocked = AnyWord(Split(settings.BlockedWords), allowPrefix: true);
    }

    /// <returns>null: не показывать.</returns>
    public ChatMessage? Check(ChatMessage message, TimeSpan now)
    {
        var text = message.Text;
        var unwanted = _blocked?.IsMatch(text) == true || (_hideLinks && Link().IsMatch(text));

        if (OverlayState.IsMoney(message))
        {
            // деньги фильтр не прячет, только слова. Amount это часть без комментария зрителя
            if (!unwanted || message.Amount == text) return message;
            return message with { Text = message.Amount.Length > 0 ? $"{message.Amount} {Hidden}" : Hidden };
        }

        if (unwanted) return null;
        if (_hideRepeats && message.Mark is MessageMark.None or MessageMark.FirstMessage && IsRepeat(text, now)) return null;

        // метки остаются всегда: по ним работает защита денег, а рисовать ли их, решает окно
        if (message.Mark == MessageMark.Raid) return message;
        return _mention?.IsMatch(text) == true ? message with { Mark = MessageMark.Mention } : message;
    }

    private bool IsRepeat(string text, TimeSpan now)
    {
        var key = string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();
        if (_recent.TryGetValue(key, out var seen) && now - seen < RepeatWindow) return true;
        if (_recent.Count >= RememberedTexts) _recent.Clear();
        _recent[key] = now;
        return false;
    }

    private static string[] Split(string list) =>
        list.Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static Regex? AnyWord(IEnumerable<string> words, bool allowPrefix)
    {
        var patterns = new List<string>();
        foreach (var word in words)
        {
            var prefix = allowPrefix && word.EndsWith('*');
            var body = (prefix ? word.TrimEnd('*') : word).Trim();
            // одна буква спрятала бы половину чата
            if (body.Length < 2) continue;
            patterns.Add(prefix ? Regex.Escape(body) : Regex.Escape(body) + @"(?![\p{L}\p{N}_])");
        }

        return patterns.Count == 0
            ? null
            : new Regex($@"(?<![\p{{L}}\p{{N}}_])(?:{string.Join('|', patterns)})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
