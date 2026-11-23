using System.Globalization;
using System.Text.Json;
using StreamChat.Core.Emotes;

namespace StreamChat.Core.Chat;

/// <summary>
/// События локального WebSocket TikFinity: {"event": "...", "data": {...}}.
/// Поля не документированы самой TikFinity, поэтому принимаем плоский и вложенный вид.
/// </summary>
public static class TikFinityParser
{
    // серия одинаковых подарков: событие на каждый, итог с repeatEnd
    private const string SeriesGift = "1";
    private const int MaxStickers = 10;

    public static ChatEvent? Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var data)) return null;

            var kind = Read(root, "event");
            var text = kind switch
            {
                "chat" => Read(data, "comment"),
                "gift" => Gift(data),
                "subscribe" => Subscription(data),
                _ => "",
            };
            var stickers = kind switch
            {
                "chat" => Stickers(data),
                // стикер подписчика: только из полей стикера, в событии ещё аватар и значки на том же CDN
                "emote" when EmoteImage(data) is { } url => new Dictionary<string, string> { ["[стикер-1]"] = url },
                _ => [],
            };
            // место стикера в тексте у версий TikFinity разное, ставим в конец
            foreach (var word in stickers.Keys) text = text.Length == 0 ? word : $"{text} {word}";
            if (text.Length == 0) return null;

            var mark = kind switch
            {
                "gift" => MessageMark.Paid,
                "subscribe" => MessageMark.Subscription,
                _ => MessageMark.None,
            };
            var login = Field(data, "user", "uniqueId");
            var author = UntrustedText.Name(Field(data, "user", "nickname"), login);
            var body = UntrustedText.Clean(text, UntrustedText.MaxText);
            if (body.Length == 0) return null;
            // ник из невидимых знаков в TikTok обычен, а деньги без имени терять нельзя
            if (author.Length == 0)
            {
                if (mark == MessageMark.None) return null;
                author = UntrustedText.Anonymous;
            }

            var message = new ChatMessage(Platform.TikTok, author, body)
            {
                Mark = mark,
                // у подарка и подписки нет комментария зрителя: весь текст и есть сумма
                Amount = mark == MessageMark.None ? "" : body,
                Id = Field(data, "common", "msgId"),
                AuthorId = Field(data, "user", "userId"),
                Login = login,
            };
            return new Posted(stickers.Count == 0 ? message : message with { Emotes = stickers });
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    public static long? ParseViewers(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (Read(root, "event") != "roomUser" || !root.TryGetProperty("data", out var data)) return null;
            return long.TryParse(Read(data, "viewerCount"), NumberStyles.None, CultureInfo.InvariantCulture, out var count) ? count : null;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private static string Gift(JsonElement data)
    {
        if (Read(data, "giftType") == SeriesGift && Read(data, "repeatEnd") is "false" or "0") return "";

        var name = UntrustedText.Clean(Field(data, "giftDetails", "giftName"), UntrustedText.MaxAuthor);
        var many = int.TryParse(Read(data, "repeatCount"), NumberStyles.None, CultureInfo.InvariantCulture, out var count) && count > 1;
        var gift = name.Length > 0 ? $"подарок: {name}" : "подарок";
        return many ? $"{gift} ×{count}" : gift;
    }

    private static string Subscription(JsonElement data) =>
        int.TryParse(Read(data, "subMonth"), NumberStyles.None, CultureInfo.InvariantCulture, out var months) && months > 1
            ? $"подписка, {months} мес."
            : "подписка";

    private static Dictionary<string, string> Stickers(JsonElement data)
    {
        var stickers = new Dictionary<string, string>();
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("emotes", out var list)) return stickers;
        if (list.ValueKind != JsonValueKind.Array) return stickers;

        foreach (var entry in list.EnumerateArray().Take(MaxStickers))
        {
            if (FindImage(entry) is { } url) stickers[$"[стикер-{stickers.Count + 1}]"] = url;
        }

        return stickers;
    }

    // вложенность у версий разная, глубину ограничивает JsonDocument
    private static string? EmoteImage(JsonElement data)
    {
        var flat = Read(data, "emoteImageUrl");
        if (EmoteImages.IsAllowed(flat)) return flat;
        return data.ValueKind == JsonValueKind.Object && data.TryGetProperty("emote", out var emote) ? FindImage(emote) : null;
    }

    private static string? FindImage(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String when element.GetString() is { } text && EmoteImages.IsAllowed(text) => text,
        JsonValueKind.Object => element.EnumerateObject().Select(p => FindImage(p.Value)).FirstOrDefault(url => url is not null),
        JsonValueKind.Array => element.EnumerateArray().Select(FindImage).FirstOrDefault(url => url is not null),
        _ => null,
    };

    private static string Field(JsonElement data, string group, string name)
    {
        var flat = Read(data, name);
        if (flat.Length > 0) return flat;
        return data.ValueKind == JsonValueKind.Object && data.TryGetProperty(group, out var nested) ? Read(nested, name) : "";
    }

    private static string Read(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value)) return "";
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            // как написано: 19-значный id не помещается в double
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => "",
        };
    }
}
