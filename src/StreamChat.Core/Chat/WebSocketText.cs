using System.Net.WebSockets;
using System.Text;

namespace StreamChat.Core.Chat;

internal static class WebSocketText
{
    /// <returns>null: сервер закрыл соединение.</returns>
    /// <exception cref="IOException">Сообщение больше предела.</exception>
    public static async Task<string?> ReadAsync(WebSocket socket, int max, CancellationToken cancel)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            var part = await socket.ReceiveAsync(buffer, cancel).ConfigureAwait(false);
            if (part.MessageType == WebSocketMessageType.Close) return null;
            if (message.Length + part.Count > max) throw new IOException("message is too large");
            message.Write(buffer, 0, part.Count);
            if (part.EndOfMessage) return Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
        }
    }

    public static Task SendAsync(WebSocket socket, string text, CancellationToken cancel) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, cancel);
}
