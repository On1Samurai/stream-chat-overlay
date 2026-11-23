using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using StreamChat.Core.Chat;

namespace StreamChat.Tests.Chat;

// настоящий WebSocket на localhost, подменён только сервер Twitch
public sealed class TwitchClientTests : IDisposable
{
    private readonly HttpListener _server = new();
    private readonly Uri _address;
    private readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(15));
    private readonly ConcurrentQueue<ChatEvent> _events = new();
    private readonly ConcurrentQueue<SourceStatus> _statuses = new();

    public TwitchClientTests()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        _server.Prefixes.Add($"http://localhost:{port}/");
        _server.Start();
        _address = new Uri($"ws://localhost:{port}/");
    }

    public void Dispose()
    {
        _stop.Cancel();
        _server.Close();
    }

    private Task Run(TimeSpan? joinTimeout = null) =>
        new TwitchClient(_address, retry: TimeSpan.FromMilliseconds(30), joinTimeout: joinTimeout ?? TimeSpan.FromSeconds(5))
            .RunAsync("chan", new ChatSink(_events.Enqueue, _statuses.Enqueue), _stop.Token);

    private async Task<WebSocket> AcceptAndJoin(bool confirm = true)
    {
        var socket = (await (await _server.GetContextAsync().WaitAsync(_stop.Token)).AcceptWebSocketAsync(null)).WebSocket;
        while (!(await Read(socket)).Contains("JOIN #chan", StringComparison.Ordinal))
        {
        }

        if (confirm) await Send(socket, "@room-id=1 :tmi.twitch.tv ROOMSTATE #chan\r\n");
        return socket;
    }

    private async Task<string> Read(WebSocket socket)
    {
        var buffer = new byte[4096];
        var result = await socket.ReceiveAsync(buffer, _stop.Token);
        return Encoding.UTF8.GetString(buffer, 0, result.Count);
    }

    private Task Send(WebSocket socket, string text) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, _stop.Token);

    private async Task Until(Func<bool> condition)
    {
        while (!condition()) await Task.Delay(10, _stop.Token);
    }

    [Fact]
    public async Task Connects_and_delivers_messages()
    {
        _ = Run();
        using var socket = await AcceptAndJoin();
        await Send(socket, "@id=1 :a!a@a PRIVMSG #chan :раз\r\n@id=2 :b!b@b PRIVMSG #chan :два\r\n");
        await Until(() => _events.Count == 2);
        Assert.Contains(_statuses, s => s.Health == SourceHealth.Connected);
    }

    [Fact]
    public async Task Answers_ping()
    {
        _ = Run();
        using var socket = await AcceptAndJoin();
        await Send(socket, "PING :check\r\n");
        Assert.Equal("PONG :check\r\n", await Read(socket));
    }

    [Fact]
    public async Task Reconnects_when_twitch_asks()
    {
        _ = Run();
        using var first = await AcceptAndJoin();
        await Send(first, ":tmi.twitch.tv RECONNECT\r\n");
        using var second = await AcceptAndJoin();
        await Send(second, "@id=3 :a!a@a PRIVMSG #chan :после\r\n");
        await Until(() => _events.Count == 1);
    }

    [Fact]
    public async Task Reconnects_after_connection_loss()
    {
        _ = Run();
        var first = await AcceptAndJoin();
        await first.CloseOutputAsync(WebSocketCloseStatus.EndpointUnavailable, null, _stop.Token);
        using var second = await AcceptAndJoin();
        await Until(() => _statuses.Count(s => s.Health == SourceHealth.Connected) == 2);
    }

    // так выглядит пропавший интернет: соединение открыто, ответов нет
    [Fact]
    public async Task Reconnects_when_the_network_goes_silent()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var address = new Uri($"ws://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/");
        _ = new TwitchClient(address, retry: TimeSpan.FromMilliseconds(30), keepAlive: TimeSpan.FromMilliseconds(300))
            .RunAsync("chan", new ChatSink(_events.Enqueue, _statuses.Enqueue), _stop.Token);

        using var first = await listener.AcceptTcpClientAsync(_stop.Token);
        var stream = first.GetStream();
        var buffer = new byte[4096];
        var request = Encoding.ASCII.GetString(buffer, 0, await stream.ReadAsync(buffer, _stop.Token));
        var key = request.Split("\r\n").Single(l => l.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase)).Split(':', 2)[1].Trim();
        var accept = Convert.ToBase64String(System.Security.Cryptography.SHA1.HashData(
            Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            $"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n"), _stop.Token);

        var server = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = true });
        await Send(server, "@room-id=1 :tmi.twitch.tv ROOMSTATE #chan\r\n");

        // дальше сервер не читает и на проверки связи не отвечает
        using var second = await listener.AcceptTcpClientAsync(_stop.Token);
        Assert.Contains(_statuses, s => s.Health == SourceHealth.Waiting);
    }

    [Fact]
    public async Task Unknown_channel_is_reported()
    {
        _ = Run(TimeSpan.FromMilliseconds(300));
        using var socket = await AcceptAndJoin(confirm: false);
        await Send(socket, ":tmi.twitch.tv 001 justinfan :Welcome, GLHF!\r\n");
        await Until(() => _statuses.Any(s => s.Health == SourceHealth.Error));
        Assert.Contains("не найден", _statuses.Last(s => s.Health == SourceHealth.Error).Text);
    }

    [Fact]
    public async Task Silent_server_is_a_connection_problem_and_not_a_wrong_name()
    {
        _ = Run(TimeSpan.FromMilliseconds(300));
        using var socket = await AcceptAndJoin(confirm: false);
        await Until(() => _statuses.Any(s => s.Health is SourceHealth.Waiting or SourceHealth.Error));
        Assert.DoesNotContain(_statuses, s => s.Text.Contains("не найден"));
    }

    [Fact]
    public async Task Suspended_or_deleted_channel_is_explained_and_retried()
    {
        _ = Run();
        using var socket = await AcceptAndJoin(confirm: false);
        await Send(socket, "@msg-id=msg_channel_suspended :tmi.twitch.tv NOTICE #chan :no\r\n");
        await Until(() => _statuses.Any(s => s.Health == SourceHealth.Error));
        Assert.Contains("не существует", _statuses.Last().Text);
        using var second = await AcceptAndJoin();
    }

    [Fact]
    public async Task Long_outage_becomes_a_visible_error()
    {
        _server.Stop();
        _ = Run();
        await Until(() => _statuses.Any(s => s.Health == SourceHealth.Error));
        Assert.True(_statuses.Count(s => s.Health == SourceHealth.Waiting) >= 4);
        Assert.Contains("нет связи", _statuses.Last().Text);
    }

    [Fact]
    public async Task Cancel_stops_the_client()
    {
        var run = Run();
        using var socket = await AcceptAndJoin();
        _stop.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
