using System.Globalization;

namespace StreamChat.Core.Viewers;

/// <summary>
/// Официальный Helix не отдаёт число зрителей без токена, поэтому открытый сервис DecAPI.
/// Он видит имя канала и IP, отсюда отдельная галочка в настройках.
/// </summary>
public sealed class TwitchViewers : IDisposable
{
    private readonly HttpClient _http = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false })
    {
        BaseAddress = new Uri("https://decapi.me"),
        Timeout = TimeSpan.FromSeconds(15),
        MaxResponseContentBufferSize = 4096,
    };

    /// <param name="channel">Имя, уже проверенное ChatLinks.</param>
    public async Task<ViewerReading> GetAsync(string channel, CancellationToken cancel)
    {
        try
        {
            using var response = await _http.GetAsync($"/twitch/viewercount/{channel}", cancel).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return new(ViewerState.Unknown);
            return Parse(channel, await response.Content.ReadAsStringAsync(cancel).ConfigureAwait(false));
        }
        catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException or ObjectDisposedException)
        {
            return new(ViewerState.Unknown);
        }
    }

    // ответ текстом: число или "<канал> is offline"
    public static ViewerReading Parse(string channel, string body)
    {
        var text = body.Trim();
        if (long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var count)) return new(ViewerState.Live, count);
        return string.Equals(text, $"{channel} is offline", StringComparison.OrdinalIgnoreCase)
            ? new(ViewerState.Offline)
            : new(ViewerState.Unknown);
    }

    public void Dispose() => _http.Dispose();
}
