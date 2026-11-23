using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Grpc.Core;
using Grpc.Net.Client;
using StreamChat.Core.Viewers;
using Youtube.Api.V3;

namespace StreamChat.Core.Chat;

public enum LookupProblem { None, BadKey, ApiDisabled, KeyRestricted, Quota, NotFound, NotLiveVideo, NotStarted, Ended, ChatDisabled, Unavailable }

public sealed record LookupResult(string? ChatId, LookupProblem Problem);

public interface IYouTubeApi
{
    Task<LookupResult> FindChatAsync(string videoId, string key, CancellationToken cancel);

    IAsyncEnumerable<LiveChatMessageListResponse> StreamAsync(string chatId, string key, string? pageToken, CancellationToken cancel);
}

/// <summary>Официальный API. Ключ только в заголовке: адрес попадает в ошибки и журналы прокси.</summary>
public sealed class YouTubeApi : IYouTubeApi, IDisposable
{
    private const string KeyHeader = "x-goog-api-key";

    private readonly HttpClient _http;
    private readonly GrpcChannel _channel;
    private readonly V3DataLiveChatMessageService.V3DataLiveChatMessageServiceClient _chat;

    public YouTubeApi(string rest = "https://www.googleapis.com", string grpc = "https://youtube.googleapis.com")
    {
        // без перенаправлений: заголовок с ключом ушёл бы на чужой адрес
        _http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false })
        {
            BaseAddress = new Uri(rest),
            Timeout = TimeSpan.FromSeconds(20),
            MaxResponseContentBufferSize = 1024 * 1024,
        };
        _channel = GrpcChannel.ForAddress(grpc, new GrpcChannelOptions
        {
            HttpHandler = new SocketsHttpHandler
            {
                UseCookies = false,
                ConnectTimeout = TimeSpan.FromSeconds(20),
                KeepAlivePingDelay = TimeSpan.FromSeconds(60),
                KeepAlivePingTimeout = TimeSpan.FromSeconds(20),
            },
            DisposeHttpClient = true,
        });
        _chat = new V3DataLiveChatMessageService.V3DataLiveChatMessageServiceClient(_channel);
    }

    public async Task<LookupResult> FindChatAsync(string videoId, string key, CancellationToken cancel)
    {
        return await GetVideoAsync(videoId, key, cancel).ConfigureAwait(false) is { } answer
            ? ParseLookup(answer.Status, answer.Body)
            : new(null, LookupProblem.Unavailable);
    }

    /// <summary>1 единица квоты за запрос.</summary>
    public async Task<ViewerReading> GetViewersAsync(string videoId, string key, CancellationToken cancel) =>
        await GetVideoAsync(videoId, key, cancel).ConfigureAwait(false) is { } answer
            ? ParseViewers(answer.Status, answer.Body)
            : new(ViewerState.Unknown);

    private async Task<(int Status, string Body)?> GetVideoAsync(string videoId, string key, CancellationToken cancel)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"/youtube/v3/videos?part=liveStreamingDetails&id={Uri.EscapeDataString(videoId)}");
        request.Headers.TryAddWithoutValidation(KeyHeader, key);
        try
        {
            using var response = await _http.SendAsync(request, cancel).ConfigureAwait(false);
            return ((int)response.StatusCode, await response.Content.ReadAsStringAsync(cancel).ConfigureAwait(false));
        }
        catch (Exception e) when (e is HttpRequestException or IOException
                                  || (e is OperationCanceledException && !cancel.IsCancellationRequested))
        {
            return null;
        }
    }

    public async IAsyncEnumerable<LiveChatMessageListResponse> StreamAsync(
        string chatId, string key, string? pageToken, [EnumeratorCancellation] CancellationToken cancel)
    {
        var request = new LiveChatMessageListRequest { LiveChatId = chatId };
        request.Part.Add(["id", "snippet", "authorDetails"]);
        if (!string.IsNullOrEmpty(pageToken)) request.PageToken = pageToken;

        using var call = _chat.StreamList(request, new Metadata { { KeyHeader, key } }, cancellationToken: cancel);
        await foreach (var response in call.ResponseStream.ReadAllAsync(cancel).ConfigureAwait(false))
            yield return response;
    }

    public void Dispose()
    {
        _http.Dispose();
        _channel.Dispose();
    }

    public static LookupResult ParseLookup(int status, string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return new(null, LookupProblem.Unavailable);
            if (status != 200) return new(null, Problem(Reasons(root)));

            if (!root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                return new(null, LookupProblem.Unavailable);
            if (items.GetArrayLength() == 0) return new(null, LookupProblem.NotFound);
            if (!items[0].TryGetProperty("liveStreamingDetails", out var live)) return new(null, LookupProblem.NotLiveVideo);

            if (live.TryGetProperty("activeLiveChatId", out var chat) && chat.GetString() is { Length: > 0 } id)
                return new(id, LookupProblem.None);
            if (live.TryGetProperty("actualEndTime", out _)) return new(null, LookupProblem.Ended);
            return new(null, live.TryGetProperty("actualStartTime", out _) ? LookupProblem.ChatDisabled : LookupProblem.NotStarted);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            return new(null, LookupProblem.Unavailable);
        }
    }

    public static ViewerReading ParseViewers(int status, string json)
    {
        var unknown = new ViewerReading(ViewerState.Unknown);
        if (status != 200) return unknown;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("items", out var items) || items.GetArrayLength() == 0) return unknown;
            if (!items[0].TryGetProperty("liveStreamingDetails", out var live)) return unknown;
            if (live.TryGetProperty("actualEndTime", out _) || !live.TryGetProperty("actualStartTime", out _))
                return new(ViewerState.Offline);

            // нет поля: владелец скрыл число или зрителей ноль, API их не различает
            if (!live.TryGetProperty("concurrentViewers", out var viewers)) return unknown;
            var text = viewers.ValueKind == JsonValueKind.String ? viewers.GetString() : viewers.GetRawText();
            return long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var count)
                ? new(ViewerState.Live, count)
                : unknown;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            return unknown;
        }
    }

    private static HashSet<string> Reasons(JsonElement root)
    {
        var reasons = new HashSet<string>();
        if (!root.TryGetProperty("error", out var error)) return reasons;
        foreach (var list in new[] { "errors", "details" })
        {
            if (!error.TryGetProperty(list, out var entries)) continue;
            foreach (var entry in entries.EnumerateArray())
            {
                if (entry.TryGetProperty("reason", out var reason) && reason.GetString() is { } text) reasons.Add(text);
            }
        }

        return reasons;
    }

    private static LookupProblem Problem(HashSet<string> reasons)
    {
        if (reasons.Overlaps(["API_KEY_INVALID", "keyInvalid", "keyExpired", "API_KEY_EXPIRED"])) return LookupProblem.BadKey;
        if (reasons.Overlaps(["accessNotConfigured", "SERVICE_DISABLED"])) return LookupProblem.ApiDisabled;
        // rateLimitExceeded это «слишком часто», повтор с паузой, а не кончившаяся квота
        if (reasons.Overlaps(["quotaExceeded", "dailyLimitExceeded"])) return LookupProblem.Quota;
        if (reasons.Any(r => r.StartsWith("API_KEY_", StringComparison.Ordinal) && r.EndsWith("_BLOCKED", StringComparison.Ordinal)))
            return LookupProblem.KeyRestricted;
        return LookupProblem.Unavailable;
    }
}
