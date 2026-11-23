using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using StreamChat.Core.Chat;

namespace StreamChat.Tests.Chat;

public sealed class TikFinityClientTests : IDisposable
{
    private const string Message = """{"event":"chat","data":{"comment":"hi","uniqueId":"v"}}""";

    private readonly HttpListener _server = new();
    private readonly Uri _address;
    private readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(15));
    private readonly ConcurrentQueue<ChatEvent> _events = new();
    private readonly ConcurrentQueue<SourceStatus> _statuses = new();

    public TikFinityClientTests()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        _server.Prefixes.Add($"http://localhost:{port}/");
        _address = new Uri($"ws://localhost:{port}/");
    }

    public void Dispose()
    {
        _stop.Cancel();
        _server.Close();
    }

    private Task Run() => new TikFinityClient(_address, TimeSpan.FromMilliseconds(40))
        .RunAsync(new ChatSink(_events.Enqueue, _statuses.Enqueue), _stop.Token);

    private async Task<WebSocket> Accept() =>
        (await (await _server.GetContextAsync().WaitAsync(_stop.Token)).AcceptWebSocketAsync(null)).WebSocket;

    private Task Send(WebSocket socket, string text, bool end = true) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, end, _stop.Token);

    private async Task Until(Func<bool> condition)
    {
        while (!condition()) await Task.Delay(10, _stop.Token);
    }

    [Fact]
    public async Task Waits_until_tikfinity_is_started()
    {
        _ = Run();
        await Until(() => _statuses.Any(s => s.Health == SourceHealth.Waiting));
        _server.Start();
        using var socket = await Accept();
        await Until(() => _statuses.Any(s => s.Health == SourceHealth.Connected));
    }

    [Fact]
    public async Task Waiting_is_reported_once()
    {
        _ = Run();
        await Until(() => _statuses.Any(s => s.Health == SourceHealth.Waiting));
        await Task.Delay(300, _stop.Token);
        Assert.Single(_statuses, s => s.Health == SourceHealth.Waiting);
    }

    [Fact]
    public async Task Delivers_split_frames()
    {
        _server.Start();
        _ = Run();
        using var socket = await Accept();
        await Send(socket, Message[..20], end: false);
        await Send(socket, Message[20..]);
        await Until(() => _events.Count == 1);
    }

    [Fact]
    public async Task Huge_event_is_skipped_without_dropping_the_connection()
    {
        _server.Start();
        _ = Run();
        using var socket = await Accept();
        await Send(socket, new string('x', 2 * 1024 * 1024));
        await Send(socket, Message);
        await Until(() => _events.Count == 1);
        Assert.DoesNotContain(_statuses, s => s.Health == SourceHealth.Waiting);
    }

    [Fact]
    public async Task Reconnects_when_tikfinity_restarts()
    {
        _server.Start();
        _ = Run();
        var first = await Accept();
        await first.CloseOutputAsync(WebSocketCloseStatus.EndpointUnavailable, null, _stop.Token);
        using var second = await Accept();
        await Until(() => _statuses.Count(s => s.Health == SourceHealth.Connected) == 2);
    }

    [Fact]
    public async Task Reports_viewer_count()
    {
        long seen = -1;
        _server.Start();
        _ = new TikFinityClient(_address, TimeSpan.FromMilliseconds(40))
            .RunAsync(new ChatSink(_events.Enqueue, _statuses.Enqueue, Viewers: count => Interlocked.Exchange(ref seen, count)), _stop.Token);
        using var socket = await Accept();
        await Send(socket, """{"event":"roomUser","data":{"viewerCount":55}}""");
        await Until(() => Interlocked.Read(ref seen) == 55);
        Assert.Empty(_events);
    }

    [Fact]
    public async Task Lost_tikfinity_becomes_a_visible_error()
    {
        _server.Start();
        _ = Run();
        var socket = await Accept();
        await Until(() => _statuses.Any(s => s.Health == SourceHealth.Connected));
        await socket.CloseOutputAsync(WebSocketCloseStatus.EndpointUnavailable, null, _stop.Token);
        _server.Stop();
        await Until(() => _statuses.Any(s => s.Health == SourceHealth.Error));
    }

    [Fact]
    public async Task Cancel_stops_the_client()
    {
        var run = Run();
        await Until(() => !_statuses.IsEmpty);
        _stop.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
