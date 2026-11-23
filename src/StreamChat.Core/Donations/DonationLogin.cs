using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace StreamChat.Core.Donations;

/// <summary>
/// Вход OAuth implicit: секрета приложения в exe быть не может. Токен приходит в части адреса после #,
/// страница сразу стирает её и отдаёт токен этой программе на localhost.
/// </summary>
public sealed class DonationLogin
{
    public const int DefaultPort = 21214;

    /// <summary>Приложение StreamChat в DonationAlerts. Номер публичный, секрет программе не нужен.</summary>
    public const string AppClientId = "23372";

    private const int MaxRequest = 16 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    private readonly string _clientId;
    private readonly int _port;

    public DonationLogin(string clientId, int port = DefaultPort)
    {
        _clientId = clientId;
        _port = port;
        State = RandomNumberGenerator.GetHexString(32, lowercase: true);
    }

    public static string RedirectAddress => $"http://localhost:{DefaultPort}/callback";

    public string State { get; }

    public Uri Link => new("https://www.donationalerts.com/oauth/authorize"
        + $"?client_id={Uri.EscapeDataString(_clientId)}"
        + $"&redirect_uri={Uri.EscapeDataString($"http://localhost:{_port}/callback")}"
        + "&response_type=token"
        // index нужен, чтобы после обрыва связи догнать пропущенные донаты
        + $"&scope={Uri.EscapeDataString("oauth-user-show oauth-donation-subscribe oauth-donation-index")}"
        + $"&state={State}");

    public static bool IsClientId(string id) => id.Length is > 0 and <= 12 && id.All(char.IsAsciiDigit);

    /// <returns>Токен или null при отмене.</returns>
    /// <exception cref="SocketException">Порт занят.</exception>
    public Task<string?> WaitAsync(CancellationToken cancel)
    {
        // слушать начинаем сразу, до открытия браузера. Браузер может пойти и на ::1
        var listeners = new List<TcpListener>();
        var ipv4 = new TcpListener(IPAddress.Loopback, _port);
        ipv4.Start();
        listeners.Add(ipv4);
        try
        {
            var ipv6 = new TcpListener(IPAddress.IPv6Loopback, _port);
            ipv6.Start();
            listeners.Add(ipv6);
        }
        catch (SocketException e) when (e.SocketErrorCode is SocketError.AddressFamilyNotSupported or SocketError.AddressNotAvailable)
        {
            // IPv6 выключен, хватит IPv4
        }
        catch (SocketException)
        {
            // [::1] занят чужой программой: браузер отдал бы токен ей
            ipv4.Stop();
            throw;
        }

        return ListenAsync(listeners, cancel);
    }

    private async Task<string?> ListenAsync(List<TcpListener> listeners, CancellationToken cancel)
    {
        var result = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stopped = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        await using var registration = cancel.Register(() =>
        {
            // порт освобождается прямо в Cancel: вход могут начать заново тем же щелчком.
            // Сначала отмена приёма, иначе упавший accept примут за случайный сбой и пойдут на новый круг
            stopped.Cancel();
            foreach (var listener in listeners) listener.Stop();
            result.TrySetResult(null);
        });
        foreach (var listener in listeners) _ = AcceptAsync(listener, result, stopped.Token);
        try
        {
            return await result.Task.ConfigureAwait(false);
        }
        finally
        {
            await stopped.CancelAsync().ConfigureAwait(false);
            foreach (var listener in listeners) listener.Stop();
        }
    }

    private async Task AcceptAsync(TcpListener listener, TaskCompletionSource<string?> result, CancellationToken cancel)
    {
        try
        {
            while (!cancel.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(cancel).ConfigureAwait(false);
                }
                catch (SocketException) when (!cancel.IsCancellationRequested)
                {
                    // сброшенное в очереди соединение не должно останавливать вход
                    continue;
                }

                // не ждём: браузер открывает и пустые соединения про запас
                _ = ServeAsync(client, result, cancel);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException
                                      or InvalidOperationException)
        {
            // слушатель остановлен
        }
    }

    private async Task ServeAsync(TcpClient client, TaskCompletionSource<string?> result, CancellationToken cancel)
    {
        using var _ = client;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            deadline.CancelAfter(RequestTimeout);
            var stream = client.GetStream();

            var buffer = new byte[MaxRequest];
            var length = 0;
            int headEnd;
            while ((headEnd = buffer.AsSpan(0, length).IndexOf("\r\n\r\n"u8)) < 0)
            {
                if (length == buffer.Length) return;
                var read = await stream.ReadAsync(buffer.AsMemory(length), deadline.Token).ConfigureAwait(false);
                if (read == 0) return;
                length += read;
            }

            var head = Encoding.ASCII.GetString(buffer, 0, headEnd).Split("\r\n");
            string Header(string name) => head.Skip(1)
                .FirstOrDefault(line => line.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase))?[(name.Length + 1)..].Trim() ?? "";

            var origin = $"http://localhost:{_port}";
            // Host проверяем из-за подмены DNS
            var ownHost = Header("Host") == $"localhost:{_port}";

            if (ownHost && (head[0].StartsWith("GET /callback ", StringComparison.Ordinal) || head[0].StartsWith("GET /callback?", StringComparison.Ordinal)))
            {
                var nonce = RandomNumberGenerator.GetHexString(16);
                await RespondAsync(stream, "200 OK", Page(nonce),
                    $"Content-Security-Policy: default-src 'none'; script-src 'nonce-{nonce}'; connect-src 'self'\r\n", deadline.Token).ConfigureAwait(false);
                return;
            }

            if (!ownHost || !head[0].StartsWith("POST /token ", StringComparison.Ordinal))
            {
                await RespondAsync(stream, "404 Not Found", "", "", deadline.Token).ConfigureAwait(false);
                return;
            }

            if (Header("Origin") != origin)
            {
                await RespondAsync(stream, "403 Forbidden", "", "", deadline.Token).ConfigureAwait(false);
                return;
            }

            if (!int.TryParse(Header("Content-Length"), NumberStyles.None, CultureInfo.InvariantCulture, out var size)) return;
            var bodyStart = headEnd + 4;
            if (size > buffer.Length - bodyStart) return;
            while (length < bodyStart + size)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(length), deadline.Token).ConfigureAwait(false);
                if (read == 0) return;
                length += read;
            }

            var form = new Dictionary<string, string>();
            foreach (var pair in Encoding.UTF8.GetString(buffer, bodyStart, size).Split('&'))
            {
                if (pair.Split('=', 2) is [var key, var value]) form[key] = Uri.UnescapeDataString(value.Replace('+', ' '));
            }

            // без state любой сайт подсунул бы свой токен, и донаты показывались бы чужие
            var token = form.GetValueOrDefault("access_token", "");
            if (form.GetValueOrDefault("state") != State || token.Length is 0 or > 8000)
            {
                await RespondAsync(stream, "403 Forbidden", "", "", deadline.Token).ConfigureAwait(false);
                return;
            }

            await RespondAsync(stream, "204 No Content", "", "", deadline.Token).ConfigureAwait(false);
            result.TrySetResult(token);
        }
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            // браузер бросил соединение
        }
    }

    private static async Task RespondAsync(NetworkStream stream, string status, string body, string headers, CancellationToken cancel)
    {
        var content = Encoding.UTF8.GetBytes(body);
        var head = $"HTTP/1.1 {status}\r\n{headers}Content-Type: text/html; charset=utf-8\r\n"
            + (status.StartsWith("204", StringComparison.Ordinal) ? "" : $"Content-Length: {content.Length}\r\n")
            + "Cache-Control: no-store\r\nReferrer-Policy: no-referrer\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), cancel).ConfigureAwait(false);
        await stream.WriteAsync(content, cancel).ConfigureAwait(false);
    }

    private static string Page(string nonce) => $$"""
        <!doctype html><html lang="ru"><meta charset="utf-8"><title>StreamChat</title>
        <p id="result">Завершаю вход…</p>
        <script nonce="{{nonce}}">
        const data = location.hash.slice(1);
        history.replaceState(null, "", "/callback");
        const say = text => document.getElementById("result").textContent = text;
        if (!data.includes("access_token=")) say("Вход отменён. Закройте вкладку и повторите из StreamChat.");
        else fetch("/token", { method: "POST", body: data })
          .then(response => say(response.ok ? "Готово. Закройте вкладку." : "Не получилось. Начните вход заново из StreamChat."))
          .catch(() => say("StreamChat не отвечает. Начните вход заново."));
        </script></html>
        """;
}
