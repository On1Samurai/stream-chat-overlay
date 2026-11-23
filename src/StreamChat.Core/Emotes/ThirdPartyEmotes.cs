using System.Text.Json;
using System.Text.RegularExpressions;

namespace StreamChat.Core.Emotes;

/// <summary>Эмоуты 7TV, BTTV и FFZ: в чате Twitch они приходят обычными словами.</summary>
public sealed partial class ThirdPartyEmotes : IDisposable
{
    private readonly HttpClient _http = new(new SocketsHttpHandler { UseCookies = false })
    {
        Timeout = TimeSpan.FromSeconds(20),
        // у больших каналов список 7TV больше 2 МБ
        MaxResponseContentBufferSize = 10 * 1024 * 1024,
    };

    [GeneratedRegex(@"\A[A-Za-z0-9]{1,40}\z")]
    private static partial Regex SafeId();

    /// <summary>Общие и канальные наборы одним словарём. Недоступный сервис пропускается.</summary>
    /// <returns>null, если не ответил ни один. Complete false: ответили не все, стоит повторить позже.</returns>
    public async Task<(Dictionary<string, string> Emotes, bool Complete)?> LoadAsync(string roomId, CancellationToken cancel)
    {
        if (!SafeId().IsMatch(roomId)) return null;

        // позже в списке значит главнее: канал важнее общих, 7TV важнее BTTV и FFZ
        (string Url, Func<string, Dictionary<string, string>> Parse)[] sources =
        [
            ("https://api.frankerfacez.com/v1/set/global", ParseFfz),
            ("https://api.betterttv.net/3/cached/emotes/global", ParseBttv),
            ("https://7tv.io/v3/emote-sets/global", ParseSevenTv),
            ($"https://api.frankerfacez.com/v1/room/id/{roomId}", ParseFfz),
            ($"https://api.betterttv.net/3/cached/users/twitch/{roomId}", ParseBttv),
            ($"https://7tv.io/v3/users/twitch/{roomId}", ParseSevenTv),
        ];
        var bodies = await Task.WhenAll(sources.Select(source => GetAsync(source.Url, cancel))).ConfigureAwait(false);
        if (bodies.All(body => body is null)) return null;

        var all = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < sources.Length; i++)
        {
            if (bodies[i] is not { } body) continue;
            foreach (var (word, url) in sources[i].Parse(body)) all[word] = url;
        }

        return (all, bodies.All(body => body is not null));
    }

    public void Dispose() => _http.Dispose();

    public static Dictionary<string, string> ParseSevenTv(string json) => Parse(json, (root, emotes) =>
    {
        var set = Get(root, "emote_set") is { ValueKind: JsonValueKind.Object } wrapped ? wrapped : root;
        foreach (var emote in Items(Get(set, "emotes")))
        {
            var id = Text(Get(emote, "id"));
            if (!SafeId().IsMatch(id)) continue;
            // список файлов в ответе врёт: _static.gif на CDN нет, а _static.png есть
            var animated = Get(Get(emote, "data"), "animated").ValueKind == JsonValueKind.True;
            Add(emotes, Text(Get(emote, "name")), $"https://cdn.7tv.app/emote/{id}/{(animated ? "2x_static" : "2x")}.png");
        }
    });

    public static Dictionary<string, string> ParseBttv(string json) => Parse(json, (root, emotes) =>
    {
        var lists = root.ValueKind == JsonValueKind.Array
            ? Items(root)
            : Items(Get(root, "channelEmotes")).Concat(Items(Get(root, "sharedEmotes")));
        foreach (var emote in lists)
        {
            var id = Text(Get(emote, "id"));
            if (SafeId().IsMatch(id)) Add(emotes, Text(Get(emote, "code")), $"https://cdn.betterttv.net/emote/{id}/2x.png");
        }
    });

    public static Dictionary<string, string> ParseFfz(string json) => Parse(json, (root, emotes) =>
    {
        var sets = Get(root, "sets");
        if (sets.ValueKind != JsonValueKind.Object) return;

        // в общем ответе есть наборы не для всех, нужные перечислены в default_sets
        var forEveryone = Get(root, "default_sets");
        var wanted = forEveryone.ValueKind == JsonValueKind.Array
            ? Items(forEveryone).Select(id => id.GetRawText()).ToHashSet()
            : null;

        foreach (var set in sets.EnumerateObject())
        {
            if (wanted is not null && !wanted.Contains(set.Name)) continue;
            foreach (var emote in Items(Get(set.Value, "emoticons")))
            {
                var urls = Get(emote, "urls");
                var url = Text(Get(urls, "2"));
                if (url.Length == 0) url = Text(Get(urls, "1"));
                if (EmoteImages.IsAllowed(url)) Add(emotes, Text(Get(emote, "name")), url);
            }
        }
    });

    private async Task<string?> GetAsync(string url, CancellationToken cancel)
    {
        try
        {
            using var response = await _http.GetAsync(url, cancel).ConfigureAwait(false);
            // 404: канал этим сервисом не пользуется
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return "";
            return response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync(cancel).ConfigureAwait(false) : null;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException or ObjectDisposedException)
        {
            return null;
        }
    }

    private static Dictionary<string, string> Parse(string json, Action<JsonElement, Dictionary<string, string>> read)
    {
        var emotes = new Dictionary<string, string>();
        try
        {
            using var document = JsonDocument.Parse(json);
            read(document.RootElement, emotes);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            emotes.Clear();
        }

        return emotes;
    }

    private static JsonElement Get(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : default;

    private static IEnumerable<JsonElement> Items(JsonElement element) =>
        element.ValueKind == JsonValueKind.Array ? element.EnumerateArray() : Enumerable.Empty<JsonElement>();

    private static string Text(JsonElement element) =>
        element.ValueKind == JsonValueKind.String ? element.GetString() ?? "" : "";

    private static void Add(Dictionary<string, string> emotes, string word, string url)
    {
        if (word.Length is > 0 and <= 100 && !word.Contains(' ')) emotes[word] = url;
    }
}
