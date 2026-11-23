using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace StreamChat.Core.Updates;

public enum UpdateState { Failed, NoReleases, UpToDate, Available }

public sealed record UpdateResult(UpdateState State, string Version = "");

/// <summary>Только сообщает о новой версии: ничего не качает и не запускает.</summary>
public sealed partial class UpdateChecker : IDisposable
{
    /// <summary>Адрес постоянный: ссылке из ответа сервера не верим.</summary>
    public const string DownloadPage = "https://github.com/On1Samurai/stream-chat-overlay/releases/latest";

    private const string Latest = "https://api.github.com/repos/On1Samurai/stream-chat-overlay/releases/latest";

    private readonly HttpClient _http = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false })
    {
        Timeout = TimeSpan.FromSeconds(20),
        MaxResponseContentBufferSize = 1024 * 1024,
    };

    [GeneratedRegex(@"\Av?(\d{1,5})\.(\d{1,5})\.(\d{1,5})(?:-rc(\d{1,5}))?\z", RegexOptions.CultureInvariant)]
    private static partial Regex Version();

    public UpdateChecker() => _http.DefaultRequestHeaders.UserAgent.ParseAdd("StreamChat"); // без него GitHub отвечает 403

    public async Task<UpdateResult> CheckAsync(string current, CancellationToken cancel)
    {
        try
        {
            using var response = await _http.GetAsync(Latest, cancel).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancel).ConfigureAwait(false);
            return Parse(current, (int)response.StatusCode, body);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException or ObjectDisposedException)
        {
            return new(UpdateState.Failed);
        }
    }

    public void Dispose() => _http.Dispose();

    public static UpdateResult Parse(string current, int status, string json)
    {
        if (status == 404) return new(UpdateState.NoReleases);
        if (status != 200) return new(UpdateState.Failed);
        try
        {
            using var document = JsonDocument.Parse(json);
            var tag = document.RootElement.GetProperty("tag_name").GetString() ?? "";
            if (Number(tag) is not { } latest || Number(current) is not { } mine) return new(UpdateState.Failed);
            return latest.CompareTo(mine) > 0 ? new(UpdateState.Available, tag.TrimStart('v')) : new(UpdateState.UpToDate);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return new(UpdateState.Failed);
        }
    }

    private static (int, int, int, int)? Number(string text)
    {
        var match = Version().Match(text);
        if (!match.Success) return null;
        int Part(int group) => int.Parse(match.Groups[group].ValueSpan, CultureInfo.InvariantCulture);
        // готовый выпуск новее любого своего rc
        return (Part(1), Part(2), Part(3), match.Groups[4].Success ? Part(4) : int.MaxValue);
    }
}
