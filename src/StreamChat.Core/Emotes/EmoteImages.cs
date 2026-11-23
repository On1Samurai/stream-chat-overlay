namespace StreamChat.Core.Emotes;

/// <summary>
/// Адреса картинок приходят извне, поэтому качаем только с известных CDN:
/// иначе зритель заставит программу открыть свой адрес и узнает IP стримера.
/// </summary>
public sealed class EmoteImages : IDisposable
{
    private const int MaxBytes = 1024 * 1024;

    private static readonly string[] Hosts = ["static-cdn.jtvnw.net", "cdn.7tv.app", "cdn.betterttv.net", "cdn.frankerfacez.com"];
    private static readonly string[] TikTokHosts = [".tiktokcdn.com", ".tiktokcdn-us.com", ".tiktokcdn-eu.com"];

    private readonly HttpClient _http = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, MaxConnectionsPerServer = 6 })
    {
        Timeout = TimeSpan.FromSeconds(15),
        MaxResponseContentBufferSize = MaxBytes,
    };

    public static bool IsAllowed(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var address)
        && address.Scheme == Uri.UriSchemeHttps
        && address.IsDefaultPort
        && address.UserInfo.Length == 0
        && (Hosts.Contains(address.Host) || TikTokHosts.Any(host => address.Host.EndsWith(host, StringComparison.Ordinal)));

    /// <summary>PNG, GIF или WebP. Прочее декодерам Windows не отдаём.</summary>
    public static bool IsKnownFormat(byte[] bytes) =>
        StartsWith(bytes, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A])
            || StartsWith(bytes, "GIF87a"u8)
            || StartsWith(bytes, "GIF89a"u8)
            || (bytes.Length >= 12 && StartsWith(bytes, "RIFF"u8) && bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8));

    /// <returns>null, если картинки нет или она не годится.</returns>
    public async Task<byte[]?> DownloadAsync(string url, CancellationToken cancel)
    {
        if (!IsAllowed(url)) return null;
        try
        {
            using var response = await _http.GetAsync(url, cancel).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            var bytes = await response.Content.ReadAsByteArrayAsync(cancel).ConfigureAwait(false);
            return IsKnownFormat(bytes) ? bytes : null;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException or ObjectDisposedException)
        {
            return null;
        }
    }

    public void Dispose() => _http.Dispose();

    private static bool StartsWith(byte[] bytes, ReadOnlySpan<byte> prefix) => bytes.AsSpan().StartsWith(prefix);
}
