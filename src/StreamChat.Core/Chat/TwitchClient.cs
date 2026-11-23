using System.Net.WebSockets;
using System.Text;

namespace StreamChat.Core.Chat;

/// <summary>Чат Twitch только на чтение, анонимно.</summary>
public sealed class TwitchClient(Uri? address = null, TimeSpan? retry = null, TimeSpan? joinTimeout = null, TimeSpan? keepAlive = null)
{
    private const int MaxFrame = 256 * 1024;

    // после стольких неудач подряд это уже не заминка, стример должен увидеть
    private const int FailuresUntilError = 5;

    private static readonly Uri Official = new("wss://irc-ws.chat.twitch.tv:443");
    private static readonly TimeSpan MaxRetry = TimeSpan.FromSeconds(30);

    private readonly Uri _address = address ?? Official;
    private readonly TimeSpan _retry = retry ?? TimeSpan.FromSeconds(1);
    private readonly TimeSpan _joinTimeout = joinTimeout ?? TimeSpan.FromSeconds(15);

    private readonly TimeSpan _keepAlive = keepAlive ?? TimeSpan.FromSeconds(20);

    private enum End { Lost, Reconnect, NotFound, Suspended }

    public async Task RunAsync(string channel, ChatSink sink, CancellationToken cancel)
    {
        var failures = 0;
        sink.Status(new(SourceHealth.Connecting, "подключаюсь"));
        try
        {
            while (true)
            {
                var (end, joined) = await ConnectAsync(channel, sink, cancel).ConfigureAwait(false);
                cancel.ThrowIfCancellationRequested();
                if (joined) failures = 0;

                if (end == End.Reconnect && joined) continue;

                failures++;
                var wait = Backoff.After(failures, _retry, MaxRetry);
                switch (end)
                {
                    case End.NotFound:
                        sink.Status(new(SourceHealth.Error, "канал не найден, проверьте имя"));
                        wait = MaxRetry;
                        break;
                    case End.Suspended:
                        // так Twitch отвечает и на удалённый канал, поэтому не сдаёмся
                        sink.Status(new(SourceHealth.Error, "канал не существует или заблокирован, проверьте имя"));
                        wait = failures == 1 ? _retry : MaxRetry;
                        break;
                    default:
                        sink.Status(failures >= FailuresUntilError
                            ? new(SourceHealth.Error, "нет связи с Twitch, переподключаюсь")
                            : new(SourceHealth.Waiting, "нет связи, переподключаюсь"));
                        break;
                }

                await Task.Delay(wait, cancel).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
        }
    }

    private async Task<(End End, bool Joined)> ConnectAsync(string channel, ChatSink sink, CancellationToken cancel)
    {
        using var socket = new ClientWebSocket();
        // обрыв сети иначе не виден до шести минут: столько молчит сам Twitch
        socket.Options.KeepAliveInterval = _keepAlive;
        socket.Options.KeepAliveTimeout = _keepAlive;

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        deadline.CancelAfter(_joinTimeout);

        var welcomed = false;
        var joined = false;
        try
        {
            await socket.ConnectAsync(_address, deadline.Token).ConfigureAwait(false);
            // у входа свой срок, медленное подключение его не съедает
            deadline.CancelAfter(_joinTimeout);
            await Send("CAP REQ :twitch.tv/tags twitch.tv/commands").ConfigureAwait(false);
            await Send($"NICK justinfan{Random.Shared.Next(10_000, 99_999)}").ConfigureAwait(false);
            await Send($"JOIN #{channel}").ConfigureAwait(false);

            var buffer = new byte[16 * 1024];
            using var frame = new MemoryStream();
            while (true)
            {
                var part = await socket.ReceiveAsync(buffer, deadline.Token).ConfigureAwait(false);
                if (part.MessageType == WebSocketMessageType.Close) return (End.Lost, joined);
                if (frame.Length + part.Count > MaxFrame) return (End.Lost, joined);
                frame.Write(buffer, 0, part.Count);
                if (!part.EndOfMessage) continue;

                var lines = Encoding.UTF8.GetString(frame.GetBuffer(), 0, (int)frame.Length)
                    .Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
                frame.SetLength(0);

                foreach (var raw in lines)
                {
                    var line = TwitchParser.Parse(raw);
                    switch (line.Command)
                    {
                        case "PING":
                            await Send($"PONG :{line.Text}").ConfigureAwait(false);
                            break;
                        case "001":
                            welcomed = true;
                            break;
                        case "RECONNECT":
                            return (End.Reconnect, joined);
                        case "NOTICE" when line.Kind is "msg_channel_suspended" or "tos_ban":
                            return (End.Suspended, joined);
                        // у несуществующего канала ROOMSTATE не приходит
                        case "ROOMSTATE" when !joined:
                            joined = true;
                            deadline.CancelAfter(Timeout.InfiniteTimeSpan);
                            sink.Status(new(SourceHealth.Connected, "подключено"));
                            if (line.RoomId.Length > 0) sink.Room?.Invoke(line.RoomId);
                            break;
                    }

                    if (line.Event is { } change) sink.Event(change);
                }
            }
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            // "не найден" только если Twitch принял вход и промолчал на JOIN
            return (welcomed && !joined ? End.NotFound : End.Lost, joined);
        }
        catch (Exception e) when (e is WebSocketException or IOException or HttpRequestException)
        {
            return (End.Lost, joined);
        }

        Task Send(string text) =>
            socket.SendAsync(Encoding.UTF8.GetBytes(text + "\r\n"), WebSocketMessageType.Text, true, deadline.Token);
    }
}
