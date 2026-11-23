using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace StreamChat.Tests;

/// <summary>Настоящие HTTP и WebSocket на localhost вместо чужого сервера.</summary>
internal sealed class LocalServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(20));

    public LocalServer()
    {
        Port = FreePort();
        _listener.Prefixes.Add($"http://localhost:{Port}/");
        _listener.Start();
    }

    public int Port { get; }
    public CancellationToken Stop => _stop.Token;

    public static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    public Task<HttpListenerContext> Next() => _listener.GetContextAsync().WaitAsync(Stop);

    public async Task<WebSocket> Socket() => (await (await Next()).AcceptWebSocketAsync(null)).WebSocket;

    public async Task<JsonElement> Read(WebSocket socket)
    {
        var buffer = new byte[16 * 1024];
        var result = await socket.ReceiveAsync(buffer, Stop);
        return JsonDocument.Parse(Encoding.UTF8.GetString(buffer, 0, result.Count)).RootElement.Clone();
    }

    public Task Send(WebSocket socket, string json) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, Stop);

    public async Task Reply(HttpListenerContext context, int status, string json)
    {
        context.Response.StatusCode = status;
        await context.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(json), Stop);
        context.Response.Close();
    }

    public async Task Until(Func<bool> condition)
    {
        while (!condition()) await Task.Delay(10, Stop);
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Close();
    }
}
