using System.Net.WebSockets;
using System.Text;

namespace StreamChat.Core.Chat;

/// <summary>
/// У TikTok нет официального API чата. Чат получает TikFinity на этом же ПК,
/// программа читает только её локальный порт и к TikTok не обращается.
/// </summary>
public sealed class TikFinityClient(Uri? address = null, TimeSpan? retry = null)
{
    private const int MaxEvent = 1024 * 1024;
    private const int FailuresUntilError = 5;

    private static readonly Uri Local = new("ws://localhost:21213/");

    private readonly Uri _address = address ?? Local;
    private readonly TimeSpan _retry = retry ?? TimeSpan.FromSeconds(3);

    public async Task RunAsync(ChatSink sink, CancellationToken cancel)
    {
        var waiting = false;
        var wasConnected = false;
        var failures = 0;
        sink.Status(new(SourceHealth.Connecting, "ищу TikFinity"));
        try
        {
            while (true)
            {
                try
                {
                    using var socket = new ClientWebSocket();
                    // системный прокси и VPN не должны уводить запрос с этого ПК
                    socket.Options.Proxy = null;
                    using var connecting = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                    connecting.CancelAfter(TimeSpan.FromSeconds(5));
                    await socket.ConnectAsync(_address, connecting.Token).ConfigureAwait(false);

                    waiting = false;
                    wasConnected = true;
                    failures = 0;
                    sink.Status(new(SourceHealth.Connected, "TikFinity на связи, сообщения придут во время эфира"));
                    await ReadAsync(socket, sink, cancel).ConfigureAwait(false);
                }
                catch (Exception e) when (e is WebSocketException or IOException or HttpRequestException
                                          || (e is OperationCanceledException && !cancel.IsCancellationRequested))
                {
                    // не запущена или закрыта, ждём
                }

                cancel.ThrowIfCancellationRequested();
                if (!waiting)
                {
                    waiting = true;
                    sink.Status(new(SourceHealth.Waiting, "нет связи с TikFinity: запустите её на этом ПК"));
                }

                // не запущена это норма, а пропала посреди эфира надо показать
                if (wasConnected && ++failures == FailuresUntilError)
                    sink.Status(new(SourceHealth.Error, "связь с TikFinity потеряна, жду её"));

                await Task.Delay(_retry, cancel).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
        }
    }

    private static async Task ReadAsync(ClientWebSocket socket, ChatSink sink, CancellationToken cancel)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        var tooLarge = false;
        while (true)
        {
            var part = await socket.ReceiveAsync(buffer, cancel).ConfigureAwait(false);
            if (part.MessageType == WebSocketMessageType.Close) return;

            tooLarge |= message.Length + part.Count > MaxEvent;
            if (!tooLarge) message.Write(buffer, 0, part.Count);
            if (!part.EndOfMessage) continue;

            if (!tooLarge)
            {
                var json = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                if (TikFinityParser.Parse(json) is { } change) sink.Event(change);
                else if (sink.Viewers is { } report && TikFinityParser.ParseViewers(json) is { } count) report(count);
            }

            message.SetLength(0);
            tooLarge = false;
        }
    }
}
