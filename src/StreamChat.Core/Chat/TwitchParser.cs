using System.Globalization;
using System.Text;

namespace StreamChat.Core.Chat;

public sealed record TwitchLine(string Command, string Text, ChatEvent? Event)
{
    /// <summary>msg-id у NOTICE.</summary>
    public string Kind { get; init; } = "";

    public string RoomId { get; init; } = "";
}

/// <summary>Строки IRC Twitch. Любая строка допустима, исключений нет.</summary>
public static class TwitchParser
{
    private const int MaxEmotesPerMessage = 100;
    private const string ActionPrefix = "\u0001ACTION ";

    public static TwitchLine Parse(string line)
    {
        var rest = line.AsSpan();
        var tags = new Dictionary<string, string>();
        if (rest.StartsWith("@"))
        {
            var space = rest.IndexOf(' ');
            if (space < 0) return new("", "", null);
            foreach (var pair in rest[1..space].ToString().Split(';'))
            {
                var equals = pair.IndexOf('=');
                if (equals > 0) tags[pair[..equals]] = Unescape(pair[(equals + 1)..]);
            }

            rest = rest[(space + 1)..];
        }

        var login = "";
        if (rest.StartsWith(":"))
        {
            var space = rest.IndexOf(' ');
            if (space < 0) return new("", "", null);
            var prefix = rest[1..space];
            var bang = prefix.IndexOf('!');
            if (bang > 0) login = prefix[..bang].ToString();
            rest = rest[(space + 1)..];
        }

        var colon = rest.IndexOf(" :");
        var head = colon < 0 ? rest : rest[..colon];
        var text = colon < 0 ? "" : rest[(colon + 2)..].ToString();
        var commandEnd = head.IndexOf(' ');
        var command = (commandEnd < 0 ? head : head[..commandEnd]).ToString();

        string Tag(string name) => tags.GetValueOrDefault(name, "");

        ChatEvent? change = command switch
        {
            "PRIVMSG" => Post(Tag("display-name"), login, Tag("id"), Tag("user-id"), Unwrap(text), Emotes(Tag("emotes"), Unwrap(text))),
            "USERNOTICE" => Post(Tag("display-name"), Tag("login"), Tag("id"), Tag("user-id"), Join(Tag("system-msg"), text),
                Emotes(Tag("emotes"), text)),
            "CLEARMSG" when Tag("target-msg-id") is { Length: > 0 } id => new Deleted(Platform.Twitch, id),
            "CLEARCHAT" when Tag("target-user-id") is { Length: > 0 } user => new UserCleared(Platform.Twitch, user),
            // без имени в тексте это очистка всего чата, с именем чистить всех нельзя
            "CLEARCHAT" when text.Length == 0 => new Cleared(Platform.Twitch),
            _ => null,
        };
        if (change is Posted posted)
        {
            var bits = Tag("bits");
            var kind = Tag("msg-id");
            var mark = command == "USERNOTICE" ? NoticeMark(kind)
                : bits.Length > 0 || kind is "animated-message" or "gigantified-emote-message" ? MessageMark.Paid
                : Tag("first-msg") == "1" ? MessageMark.FirstMessage
                : MessageMark.None;
            if (mark != MessageMark.None)
            {
                // сумма это часть без комментария: её фильтр слов не прячет
                var amount = mark switch
                {
                    MessageMark.Paid when bits.Length is > 0 and <= 10 && bits.All(char.IsAsciiDigit) => $"[{bits} bits]",
                    MessageMark.Paid => "[платное усиление]",
                    MessageMark.Subscription => UntrustedText.Clean(Tag("system-msg"), UntrustedText.MaxText),
                    _ => "",
                };
                change = new Posted(posted.Message with { Mark = mark, Amount = amount });
            }
        }

        return new(command, text, change) { Kind = Tag("msg-id"), RoomId = Tag("room-id") };
    }

    private static Posted? Post(string displayName, string login, string id, string userId, string text, Dictionary<string, string>? emotes)
    {
        var body = UntrustedText.Clean(text, UntrustedText.MaxText);
        var author = UntrustedText.Name(displayName, login);
        if (author.Length == 0) author = UntrustedText.Anonymous;
        if (body.Length == 0) return null;
        var message = new ChatMessage(Platform.Twitch, author, body) { Id = id, AuthorId = userId, Login = login };
        return new Posted(emotes is null ? message : message with { Emotes = emotes });
    }

    // "25:0-4,12-16/1902:6-10": позиции в кодовых точках, не в char
    private static Dictionary<string, string>? Emotes(string tag, string text)
    {
        if (tag.Length == 0) return null;

        var runes = text.EnumerateRunes().ToArray();
        Dictionary<string, string>? emotes = null;
        foreach (var entry in tag.Split('/', MaxEmotesPerMessage + 1).Take(MaxEmotesPerMessage))
        {
            if (entry.Split(':') is not [var id, var ranges]) continue;
            if (id.Length is 0 or > 100 || !id.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')) continue;

            // одного места хватает: слово у эмоута везде одно
            if (ranges.Split(',')[0].Split('-') is not [var first, var last]) continue;
            if (!int.TryParse(first, NumberStyles.None, CultureInfo.InvariantCulture, out var from)) continue;
            if (!int.TryParse(last, NumberStyles.None, CultureInfo.InvariantCulture, out var to)) continue;
            if (to < from || to >= runes.Length) continue;

            var word = string.Concat(runes[from..(to + 1)].Select(rune => rune.ToString()));
            if (word.Contains(' ')) continue;
            (emotes ??= [])[word] = $"https://static-cdn.jtvnw.net/emoticons/v2/{id}/static/dark/2.0";
        }

        return emotes;
    }

    private static MessageMark NoticeMark(string kind) => kind switch
    {
        "raid" => MessageMark.Raid,
        "sub" or "resub" or "subgift" or "submysterygift" or "giftpaidupgrade" or "anongiftpaidupgrade" or "primepaidupgrade"
            or "standardpayforward" or "communitypayforward"
            => MessageMark.Subscription,
        _ => MessageMark.None,
    };

    private static string Unwrap(string text) =>
        text.StartsWith(ActionPrefix, StringComparison.Ordinal) ? text[ActionPrefix.Length..].TrimEnd('\u0001') : text;

    private static string Join(string system, string comment) =>
        system.Length == 0 ? comment : comment.Length == 0 ? system : $"{system}: {comment}";

    private static string Unescape(string value)
    {
        if (!value.Contains('\\')) return value;
        var result = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '\\')
            {
                result.Append(value[i]);
                continue;
            }

            if (++i == value.Length) break;
            result.Append(value[i] switch { 's' => ' ', ':' => ';', 'r' => '\r', 'n' => '\n', var other => other });
        }

        return result.ToString();
    }
}
