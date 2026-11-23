using System.Collections.Concurrent;
using System.Net;
using StreamChat.Core;
using StreamChat.Core.Chat;
using StreamChat.Core.Donations;

namespace StreamChat.Tests.Donations;

public sealed class DonationAlertsClientTests : IDisposable
{
    private readonly LocalServer _api = new();
    private readonly LocalServer _socket = new();
    private readonly ConcurrentQueue<ChatEvent> _events = new();
    private readonly ConcurrentQueue<SourceStatus> _statuses = new();

    public void Dispose()
    {
        _api.Dispose();
        _socket.Dispose();
    }

    private Task Run() => new DonationAlertsClient(new Uri($"http://localhost:{_api.Port}/api/v1/"),
            new Uri($"ws://localhost:{_socket.Port}/"), retry: TimeSpan.FromMilliseconds(30))
        .RunAsync("token", new ChatSink(_events.Enqueue, _statuses.Enqueue), _api.Stop);

    [Fact]
    public async Task Follows_the_documented_steps_and_delivers_a_donation()
    {
        _ = Run();
        var user = await _api.Next();
        Assert.Equal("/api/v1/user/oauth", user.Request.Url!.AbsolutePath);
        Assert.Equal("Bearer token", user.Request.Headers["Authorization"]);
        await _api.Reply(user, 200, """{"data":{"id":3,"email":"x@example.com","socket_connection_token":"socket-token"}}""");

        using var socket = await _socket.Socket();
        Assert.Equal("socket-token", (await _socket.Read(socket)).GetProperty("params").GetProperty("token").GetString());
        await _socket.Send(socket, """{"id":1,"result":{"client":"client-id","version":"2.2.1"}}""");

        var subscribe = await _api.Next();
        Assert.Equal("/api/v1/centrifuge/subscribe", subscribe.Request.Url!.AbsolutePath);
        var body = await new StreamReader(subscribe.Request.InputStream).ReadToEndAsync();
        Assert.Contains("$alerts:donation_3", body.Replace("\\u0024", "$"));
        Assert.Contains("client-id", body);
        await _api.Reply(subscribe, 200, """{"channels":[{"channel":"$alerts:donation_3","token":"channel-token"}]}""");

        var join = await _socket.Read(socket);
        Assert.Equal((1, "channel-token"), (join.GetProperty("method").GetInt32(), join.GetProperty("params").GetProperty("token").GetString()));
        await _socket.Send(socket, """{"result":{"type":1,"channel":"$alerts:donation_3","data":{}}}""");
        await _socket.Send(socket, """{"result":{"channel":"$alerts:donation_3","data":{"seq":1,"data":{"id":30530030,"username":"Ivan","message_type":"text","message":"Hello!","amount":500,"currency":"RUB"}}}}""");

        await _socket.Until(() => !_events.IsEmpty && _statuses.Any(s => s.Health == SourceHealth.Connected));
        var message = Assert.IsType<Posted>(_events.First()).Message;
        Assert.Equal(("Ivan", "[500 RUB] Hello!", "[500 RUB]", MessageMark.Donation, Platform.DonationAlerts),
            (message.Author, message.Text, message.Amount, message.Mark, message.Platform));
    }

    [Fact]
    public async Task Revoked_token_asks_to_log_in_again()
    {
        var run = Run();
        await _api.Reply(await _api.Next(), 401, """{"message":"Unauthenticated."}""");
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("войдите заново", _statuses.Last().Text);
    }

    [Fact]
    public async Task Rate_limit_is_named()
    {
        _ = Run();
        var user = await _api.Next();
        user.Response.Headers.Add("Retry-After", "1");
        await _api.Reply(user, 429, "{}");
        await _api.Until(() => _statuses.Any(s => s.Text.Contains("лимит")));
    }

    // донаты во время обрыва приходят списком после переподключения, история до запуска не показывается
    [Fact]
    public async Task Donations_missed_during_a_reconnect_are_shown()
    {
        _ = Run();
        for (var session = 1; session <= 2; session++)
        {
            await _api.Reply(await _api.Next(), 200, """{"data":{"id":3,"socket_connection_token":"t"}}""");
            var socket = await _socket.Socket();
            await _socket.Read(socket);
            await _socket.Send(socket, """{"id":1,"result":{"client":"c"}}""");
            await _api.Reply(await _api.Next(), 200, """{"channels":[{"channel":"$alerts:donation_3","token":"x"}]}""");
            await _socket.Read(socket);
            await _socket.Send(socket, """{"result":{"type":1,"channel":"$alerts:donation_3","data":{}}}""");

            var list = await _api.Next();
            Assert.Equal("/api/v1/alerts/donations", list.Request.Url!.AbsolutePath);
            var data = session == 1
                ? """[{"id":5,"username":"old","amount":1,"currency":"RUB","message":""}]"""
                : """[{"id":6,"username":"new","amount":2,"currency":"RUB","message":""},{"id":5,"username":"old","amount":1,"currency":"RUB","message":""}]""";
            await _api.Reply(list, 200, """{"data":""" + data + "}");

            if (session == 1)
            {
                await Task.Delay(100, _api.Stop);
                Assert.Empty(_events);
                // Abort у HttpListener соединение не рвёт
                await socket.CloseOutputAsync(System.Net.WebSockets.WebSocketCloseStatus.EndpointUnavailable, null, _api.Stop);
            }
            else
            {
                await _socket.Until(() => !_events.IsEmpty);
                Assert.Equal("new", Assert.IsType<Posted>(Assert.Single(_events)).Message.Author);
            }
        }
    }

    [Fact]
    public async Task Server_error_is_retried()
    {
        _ = Run();
        await _api.Reply(await _api.Next(), 500, "{}");
        await _api.Reply(await _api.Next(), 500, "{}");
        Assert.Contains(_statuses, s => s.Health == SourceHealth.Waiting);
    }
}

public class DonationParserTests
{
    private static ChatMessage? Parse(string donation) =>
        (DonationParser.Parse("""{"result":{"channel":"$alerts:donation_3","data":{"seq":2,"data":DATA}}}""".Replace("DATA", donation)) as Posted)?.Message;

    [Fact]
    public void Fractional_amount() =>
        Assert.Equal("[10.5 USD]", Parse("""{"id":1,"username":"A","amount":10.50,"currency":"USD","message":""}""")!.Text);

    [Fact]
    public void Amount_as_text() =>
        Assert.Equal("[100 RUB] ok", Parse("""{"id":1,"username":"A","amount":"100.00","currency":"RUB","message":"ok"}""")!.Text);

    [Fact]
    public void Nameless_donation() =>
        Assert.Equal("Аноним", Parse("""{"id":1,"username":null,"amount":1,"currency":"RUB"}""")!.Author);

    [Fact]
    public void Voice_message_is_not_a_link() =>
        Assert.Equal("[50 RUB] голосовое сообщение",
            Parse("""{"id":1,"username":"A","amount":50,"currency":"RUB","message_type":"audio","message":"https://cdn.example/a.mp3"}""")!.Text);

    [Theory]
    [InlineData("""{"id":1,"username":"A","currency":"RUB"}""")]
    [InlineData("""{"id":1,"amount":-5,"currency":"RUB"}""")]
    [InlineData("""{"id":1,"amount":1e40,"currency":"RUB"}""")]
    [InlineData("""{"id":1,"amount":5,"currency":"<script>"}""")]
    [InlineData("7")]
    public void Garbage_is_ignored(string donation) => Assert.Null(Parse(donation));

    [Fact]
    public void Service_frames_are_ignored() => Assert.Null(DonationParser.Parse("""{"id":2,"result":{}}"""));
}

public sealed class DonationLoginTests : IDisposable
{
    private readonly int _port = LocalServer.FreePort();
    private readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(15));
    private readonly HttpClient _browser = new();

    public void Dispose()
    {
        _stop.Cancel();
        _browser.Dispose();
    }

    private async Task<HttpResponseMessage> Post(string body, string? origin = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"http://localhost:{_port}/token") { Content = new StringContent(body) };
        request.Headers.Add("Origin", origin ?? $"http://localhost:{_port}");
        return await _browser.SendAsync(request, _stop.Token);
    }

    [Fact]
    public void Link_asks_only_for_reading_donations()
    {
        var link = new DonationLogin("12345", _port).Link;
        Assert.Equal("www.donationalerts.com", link.Host);
        var query = Uri.UnescapeDataString(link.Query).Replace('+', ' ');
        Assert.Contains("response_type=token", query);
        Assert.Contains("scope=oauth-user-show oauth-donation-subscribe oauth-donation-index", query);
        Assert.Contains($"redirect_uri=http://localhost:{_port}/callback", query);
    }

    [Fact]
    public void Program_signs_in_as_the_registered_application()
    {
        Assert.Equal("23372", DonationLogin.AppClientId);
        Assert.True(DonationLogin.IsClientId(DonationLogin.AppClientId));
        // этот адрес записан у приложения в DonationAlerts: поменять здесь значит сломать вход всем
        Assert.Equal("http://localhost:21214/callback", DonationLogin.RedirectAddress);
        Assert.Contains("client_id=23372&", new DonationLogin(DonationLogin.AppClientId).Link.Query);
    }

    [Theory]
    [InlineData("")]
    [InlineData("12a")]
    [InlineData("1&scope=all")]
    public void Client_id_is_digits_only(string id) => Assert.False(DonationLogin.IsClientId(id));

    [Fact]
    public async Task Token_is_accepted_once_with_the_right_state()
    {
        var login = new DonationLogin("12345", _port);
        var token = login.WaitAsync(_stop.Token);

        var page = await _browser.GetAsync($"http://localhost:{_port}/callback", _stop.Token);
        Assert.Contains("replaceState", await page.Content.ReadAsStringAsync(_stop.Token));
        Assert.Contains("no-store", page.Headers.CacheControl!.ToString());

        Assert.Equal(HttpStatusCode.NoContent, (await Post($"access_token=abc.def&state={login.State}")).StatusCode);
        Assert.Equal("abc.def", await token);
    }

    [Fact]
    public async Task Other_sites_cannot_plant_a_token()
    {
        var login = new DonationLogin("12345", _port);
        var token = login.WaitAsync(_stop.Token);
        Assert.Equal(HttpStatusCode.Forbidden, (await Post($"access_token=evil&state={login.State}", "https://evil.example")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Post("access_token=evil&state=wrong")).StatusCode);
        Assert.False(token.IsCompleted);
    }

    [Fact]
    public async Task Idle_browser_connection_does_not_block_the_login()
    {
        var login = new DonationLogin("12345", _port);
        var token = login.WaitAsync(_stop.Token);
        using var idle = new System.Net.Sockets.TcpClient();
        await idle.ConnectAsync(IPAddress.Loopback, _port, _stop.Token);
        await Post($"access_token=ok&state={login.State}").WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("ok", await token);
    }

    [Fact]
    public async Task Login_can_be_restarted_at_once()
    {
        using var first = new CancellationTokenSource();
        var abandoned = new DonationLogin("12345", _port).WaitAsync(first.Token);
        first.Cancel();

        var login = new DonationLogin("12345", _port);
        var token = login.WaitAsync(_stop.Token);
        await Post($"access_token=second&state={login.State}");
        Assert.Equal("second", await token);
        Assert.Null(await abandoned);
    }

    [Fact]
    public void Busy_ipv6_port_is_reported_instead_of_ignored()
    {
        using var squatter = new System.Net.Sockets.TcpListener(IPAddress.IPv6Loopback, _port);
        try
        {
            squatter.Start();
        }
        catch (System.Net.Sockets.SocketException)
        {
            return; // на этой машине нет IPv6, проверять нечего
        }

        // порт занимается сразу при вызове, ещё до ожидания
        Assert.Throws<System.Net.Sockets.SocketException>(() => { _ = new DonationLogin("12345", _port).WaitAsync(_stop.Token); });
    }
}
