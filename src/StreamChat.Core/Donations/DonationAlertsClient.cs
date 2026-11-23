using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using StreamChat.Core.Chat;

namespace StreamChat.Core.Donations;

/// <summary>
/// Официальный API DonationAlerts: профиль, токен канала, Centrifugo.
/// Из профиля берём только номер и токен сокета, почту не читаем.
/// </summary>
public sealed class DonationAlertsClient(Uri? api = null, Uri? socket = null, TimeSpan? retry = null)
{
    private const int MaxMessage = 256 * 1024;
    private const int FailuresUntilError = 5;
    private static readonly TimeSpan PingEvery = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan MaxRetry = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(20);

    // лимит 60 запросов в минуту общий на всех пользователей приложения: короткие сессии не сбрасывают паузу
    private static readonly TimeSpan StableSession = TimeSpan.FromMinutes(1);

    private readonly Uri _api = api ?? new Uri("https://www.donationalerts.com/api/v1/");
    private readonly Uri _socket = socket ?? new Uri("wss://centrifugo.donationalerts.com/connection/websocket");
    private readonly TimeSpan _retry = retry ?? TimeSpan.FromSeconds(3);

    private enum End { Lost, Unauthorized, RateLimited }

    private readonly Lock _seenLock = new();

    // номер последнего известного доната: по нему после обрыва показываются пропущенные
    private long? _lastSeen;
    private TimeSpan _retryAfter;

    public async Task RunAsync(string token, ChatSink sink, CancellationToken cancel)
    {
        if (token.Length == 0 || token.Any(c => c is <= ' ' or > '~'))
        {
            sink.Status(new(SourceHealth.Error, "вход не выполнен"));
            return;
        }

        using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false })
        {
            BaseAddress = _api,
            Timeout = StepTimeout,
            MaxResponseContentBufferSize = MaxMessage,
        };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        http.DefaultRequestHeaders.Accept.ParseAdd("application/json");

        var failures = 0;
        sink.Status(new(SourceHealth.Connecting, "подключаюсь"));
        try
        {
            while (true)
            {
                var lived = System.Diagnostics.Stopwatch.StartNew();
                var connectedAt = TimeSpan.MinValue;
                var end = await SessionAsync(http, sink, () => connectedAt = lived.Elapsed, cancel).ConfigureAwait(false);
                cancel.ThrowIfCancellationRequested();
                if (end == End.Unauthorized)
                {
                    sink.Status(new(SourceHealth.Error, "доступ отозван или истёк, войдите заново"));
                    return;
                }

                if (connectedAt != TimeSpan.MinValue && lived.Elapsed - connectedAt >= StableSession) failures = 0;
                failures++;
                var wait = Backoff.After(failures, _retry, MaxRetry);
                if (end == End.RateLimited)
                {
                    sink.Status(new(SourceHealth.Waiting, "лимит запросов DonationAlerts, жду"));
                    wait = _retryAfter > wait ? _retryAfter : wait;
                }
                else
                {
                    sink.Status(failures >= FailuresUntilError
                        ? new(SourceHealth.Error, "нет связи с DonationAlerts, переподключаюсь")
                        : new(SourceHealth.Waiting, "нет связи, переподключаюсь"));
                }

                // разброс, чтобы после сбоя DonationAlerts все программы не стучались разом
                // после 429 не раньше срока сервера
                var spread = end == End.RateLimited ? 1.0 : 0.8;
                await Task.Delay(wait * (spread + 0.4 * Random.Shared.NextDouble()), cancel).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
        }
    }

    private async Task<End> SessionAsync(HttpClient http, ChatSink sink, Action onConnected, CancellationToken cancel)
    {
        try
        {
            string channel;
            string socketToken;
            using (var response = await http.GetAsync("user/oauth", cancel).ConfigureAwait(false))
            {
                if (Refused(response) is { } refused) return refused;
                using var profile = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancel).ConfigureAwait(false));
                var data = profile.RootElement.GetProperty("data");
                channel = "$alerts:donation_" + data.GetProperty("id").GetInt64().ToString(CultureInfo.InvariantCulture);
                socketToken = data.GetProperty("socket_connection_token").GetString() ?? "";
            }

            using var socket = new ClientWebSocket();
            using var step = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            step.CancelAfter(StepTimeout);
            await socket.ConnectAsync(_socket, step.Token).ConfigureAwait(false);
            await WebSocketText.SendAsync(socket, JsonSerializer.Serialize(new { @params = new { token = socketToken }, id = 1 }), step.Token).ConfigureAwait(false);

            if (await WebSocketText.ReadAsync(socket, MaxMessage, step.Token).ConfigureAwait(false) is not { } welcome) return End.Lost;
            string client;
            using (var document = JsonDocument.Parse(welcome))
                client = document.RootElement.GetProperty("result").GetProperty("client").GetString() ?? "";

            string channelToken;
            var body = JsonSerializer.Serialize(new { channels = new[] { channel }, client });
            using (var content = new StringContent(body, Encoding.UTF8, "application/json"))
            using (var response = await http.PostAsync("centrifuge/subscribe", content, step.Token).ConfigureAwait(false))
            {
                if (Refused(response) is { } refused) return refused;
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(step.Token).ConfigureAwait(false));
                var channels = document.RootElement.GetProperty("channels");
                if (channels.GetArrayLength() == 0) return End.Lost;
                channelToken = channels[0].GetProperty("token").GetString() ?? "";
            }

            await WebSocketText.SendAsync(socket,
                JsonSerializer.Serialize(new { @params = new { channel, token = channelToken }, method = 1, id = 2 }), step.Token).ConfigureAwait(false);

            using var session = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            // на ping сервер отвечает, значит два пропущенных ответа это мёртвое соединение
            var lastHeard = TimeSpan.Zero;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var pinging = PingAsync(socket, () => clock.Elapsed - lastHeard, session);
            try
            {
                var joined = false;
                while (await WebSocketText.ReadAsync(socket, MaxMessage, joined ? session.Token : step.Token).ConfigureAwait(false) is { } frame)
                {
                    lastHeard = clock.Elapsed;
                    // Centrifugo может склеить несколько ответов через перевод строки
                    foreach (var line in frame.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (!joined)
                        {
                            using var document = JsonDocument.Parse(line);
                            if (document.RootElement.TryGetProperty("error", out _)) return End.Lost;
                            joined = true;
                            step.CancelAfter(Timeout.InfiniteTimeSpan);
                            onConnected();
                            sink.Status(new(SourceHealth.Connected, "подключено"));
                            // параллельно: чтение сокета ответа на список не ждёт
                            // граница берётся сейчас: живой донат во время запроса не должен отрезать пропущенные
                            long? since;
                            lock (_seenLock) since = _lastSeen;
                            _ = CatchUpAsync(http, sink, since, session.Token);
                        }

                        if (DonationParser.Parse(line) is Posted donation)
                        {
                            Remember(donation);
                            sink.Event(donation);
                        }
                    }
                }

                return End.Lost;
            }
            finally
            {
                await session.CancelAsync().ConfigureAwait(false);
                await pinging.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            return End.Lost;
        }
        catch (Exception e) when (e is WebSocketException or HttpRequestException or IOException or JsonException
                                      or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            return End.Lost;
        }
    }

    private End? Refused(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.Unauthorized) return End.Unauthorized;
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            _retryAfter = response.Headers.RetryAfter?.Delta is { } delta && delta < TimeSpan.FromMinutes(10) ? delta : MaxRetry;
            return End.RateLimited;
        }

        return response.IsSuccessStatusCode ? null : End.Lost;
    }

    /// <summary>Донаты, пришедшие пока не было связи. В первый раз только запоминает, что уже было.</summary>
    private async Task CatchUpAsync(HttpClient http, ChatSink sink, long? since, CancellationToken cancel)
    {
        try
        {
            using var response = await http.GetAsync("alerts/donations", cancel).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                // вход до 1.1 не давал права на список донатов
                sink.Status(new(SourceHealth.Connected, "подключено; войдите заново, чтобы видеть донаты, пропущенные при обрыве"));
                return;
            }

            if (!response.IsSuccessStatusCode) return;
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancel).ConfigureAwait(false));

            var listed = new List<(long Id, Posted Donation)>();
            foreach (var item in document.RootElement.GetProperty("data").EnumerateArray())
            {
                if (DonationParser.ParseResource(item) is Posted donation
                    && long.TryParse(donation.Message.Id, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
                    listed.Add((id, donation));
            }

            if (listed.Count == 0) return;
            var newest = listed.Max(entry => entry.Id);
            List<Posted> missed;
            lock (_seenLock)
            {
                missed = since is { } last
                    ? [.. listed.Where(entry => entry.Id > last).OrderBy(entry => entry.Id).Select(entry => entry.Donation)]
                    : [];
                _lastSeen = Math.Max(_lastSeen ?? newest, newest);
            }

            foreach (var donation in missed) sink.Event(donation);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or JsonException or InvalidOperationException
                                      or KeyNotFoundException or OperationCanceledException or ObjectDisposedException)
        {
            // без списка остаются живые донаты, это не повод рвать связь
        }
    }

    private void Remember(Posted donation)
    {
        if (!long.TryParse(donation.Message.Id, NumberStyles.None, CultureInfo.InvariantCulture, out var id)) return;
        lock (_seenLock) _lastSeen = Math.Max(_lastSeen ?? id, id);
    }

    private static async Task PingAsync(WebSocket socket, Func<TimeSpan> silence, CancellationTokenSource session)
    {
        try
        {
            for (var id = 3; ; id++)
            {
                await Task.Delay(PingEvery, session.Token).ConfigureAwait(false);
                if (silence() > PingEvery * 2.5)
                {
                    await session.CancelAsync().ConfigureAwait(false);
                    return;
                }

                await WebSocketText.SendAsync(socket, JsonSerializer.Serialize(new { method = 7, id }), session.Token).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or WebSocketException or IOException or ObjectDisposedException)
        {
            // соединение кончилось, причину увидит цикл чтения
        }
    }
}
