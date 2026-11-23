using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StreamChat.Core.Chat;

namespace StreamChat.Core.Obs;

/// <summary>
/// obs-websocket 5. Только читает состояние, ничем в OBS не управляет.
/// GetSourceActive.videoActive это obs_source_active: для звукового источника значит «в активной сцене».
/// </summary>
public sealed class ObsClient(Uri address, string password, string microphone, TimeSpan? poll = null)
{
    private const int MaxMessage = 4 * 1024 * 1024;
    private const int AuthenticationFailed = 4009;
    private static readonly TimeSpan AnswerTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Retry = TimeSpan.FromSeconds(3);
    private static readonly string[] MicrophoneKinds = ["wasapi_input_capture", "pulse_input_capture", "coreaudio_input_capture"];

    private readonly TimeSpan _poll = poll ?? TimeSpan.FromSeconds(2);
    private long _requestId;

    private enum End { Lost, BadPassword, NeedsPassword }

    public static string Answer(string password, string salt, string challenge) => Hash(Hash(password + salt) + challenge);

    public async Task RunAsync(Action<ObsSnapshot> onSnapshot, Action<SourceStatus> onStatus, CancellationToken cancel)
    {
        var waiting = false;
        onStatus(new(SourceHealth.Connecting, "ищу OBS"));
        try
        {
            while (true)
            {
                var end = await SessionAsync(
                    snapshot => onSnapshot(snapshot),
                    () =>
                    {
                        waiting = false;
                        onStatus(new(SourceHealth.Connected, "подключено"));
                    },
                    cancel).ConfigureAwait(false);
                cancel.ThrowIfCancellationRequested();

                switch (end)
                {
                    case End.BadPassword:
                        onStatus(new(SourceHealth.Error, "пароль сервера WebSocket не подходит"));
                        return;
                    case End.NeedsPassword:
                        onStatus(new(SourceHealth.Error, "нужен пароль сервера WebSocket из OBS"));
                        return;
                }

                if (!waiting)
                {
                    waiting = true;
                    onStatus(new(SourceHealth.Waiting, "нет связи: запустите OBS и включите сервер WebSocket"));
                }

                await Task.Delay(Retry, cancel).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
        }
    }

    private async Task<End> SessionAsync(Action<ObsSnapshot> onSnapshot, Action onConnected, CancellationToken cancel)
    {
        using var socket = new ClientWebSocket();
        socket.Options.Proxy = null;
        try
        {
            using (var opening = CancellationTokenSource.CreateLinkedTokenSource(cancel))
            {
                opening.CancelAfter(AnswerTimeout);
                await socket.ConnectAsync(address, opening.Token).ConfigureAwait(false);
                if (await WebSocketText.ReadAsync(socket, MaxMessage, opening.Token).ConfigureAwait(false) is not { } hello) return End.Lost;

                string? answer = null;
                using (var document = JsonDocument.Parse(hello))
                {
                    if (document.RootElement.GetProperty("d").TryGetProperty("authentication", out var auth))
                    {
                        if (password.Length == 0) return End.NeedsPassword;
                        answer = Answer(password, auth.GetProperty("salt").GetString() ?? "", auth.GetProperty("challenge").GetString() ?? "");
                    }
                }

                // события не нужны: состояние спрашиваем сами
                var identify = answer is null
                    ? JsonSerializer.Serialize(new { op = 1, d = new { rpcVersion = 1, eventSubscriptions = 0 } })
                    : JsonSerializer.Serialize(new { op = 1, d = new { rpcVersion = 1, eventSubscriptions = 0, authentication = answer } });
                await WebSocketText.SendAsync(socket, identify, opening.Token).ConfigureAwait(false);

                if (await WebSocketText.ReadAsync(socket, MaxMessage, opening.Token).ConfigureAwait(false) is null)
                    return (int?)socket.CloseStatus == AuthenticationFailed ? End.BadPassword : End.Lost;
            }

            onConnected();
            while (true)
            {
                using (var round = CancellationTokenSource.CreateLinkedTokenSource(cancel))
                {
                    round.CancelAfter(AnswerTimeout);
                    // OBS иногда отказывает, например при смене коллекции сцен: раунд пропускаем, связь держим
                    if (await SnapshotAsync(socket, round.Token).ConfigureAwait(false) is { } snapshot) onSnapshot(snapshot);
                }

                await Task.Delay(_poll, cancel).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            return End.Lost;
        }
        catch (Exception e) when (e is WebSocketException or IOException or HttpRequestException or JsonException
                                      or InvalidOperationException or KeyNotFoundException)
        {
            return End.Lost;
        }
    }

    private async Task<ObsSnapshot?> SnapshotAsync(WebSocket socket, CancellationToken cancel)
    {
        if (await AskAsync(socket, "GetStreamStatus", null, cancel).ConfigureAwait(false) is not { } stream) return null;
        if (await AskAsync(socket, "GetRecordStatus", null, cancel).ConfigureAwait(false) is not { } record) return null;
        var list = await AskAsync(socket, "GetInputList", null, cancel).ConfigureAwait(false);

        var names = new List<string>();
        if (list is { } answer && answer.TryGetProperty("inputs", out var inputs) && inputs.ValueKind == JsonValueKind.Array)
        {
            foreach (var input in inputs.EnumerateArray())
            {
                var name = Text(input, "inputName");
                var wanted = microphone.Length > 0
                    ? name == microphone
                    : MicrophoneKinds.Contains(Text(input, "unversionedInputKind"));
                if (wanted && name.Length > 0) names.Add(name);
            }
        }

        int on = 0, muted = 0, unknown = 0;
        foreach (var name in names.Take(8))
        {
            var mute = await AskAsync(socket, "GetInputMute", new { inputName = name }, cancel).ConfigureAwait(false);
            var active = await AskAsync(socket, "GetSourceActive", new { sourceName = name }, cancel).ConfigureAwait(false);
            // отказ OBS это «неизвестно», а не «включён»
            if (mute is not { } muteState || active is not { } activeState) unknown++;
            else if (!Flag(activeState, "videoActive")) continue;
            else if (Flag(muteState, "inputMuted")) muted++;
            else on++;
        }

        var mic = list is null ? MicState.Unknown
            : names.Count == 0 ? MicState.None
            : on > 0 && muted > 0 ? MicState.Partial
            : unknown > 0 ? MicState.Unknown
            : muted > 0 ? MicState.Muted
            : on > 0 ? MicState.On
            : MicState.NotInScene;

        return new ObsSnapshot(
            Flag(stream, "outputActive"), Flag(stream, "outputReconnecting"),
            TimeSpan.FromMilliseconds(Math.Clamp(Number(stream, "outputDuration"), 0, (long)TimeSpan.FromDays(60).TotalMilliseconds)),
            Number(stream, "outputSkippedFrames"), Number(stream, "outputTotalFrames"),
            Flag(record, "outputActive"), Flag(record, "outputPaused"), mic);
    }

    /// <returns>null: OBS отказал в запросе.</returns>
    /// <exception cref="IOException">Соединение закрыто.</exception>
    private async Task<JsonElement?> AskAsync(WebSocket socket, string type, object? data, CancellationToken cancel)
    {
        var id = (++_requestId).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var request = data is null
            ? JsonSerializer.Serialize(new { op = 6, d = new { requestType = type, requestId = id } })
            : JsonSerializer.Serialize(new { op = 6, d = new { requestType = type, requestId = id, requestData = data } });
        await WebSocketText.SendAsync(socket, request, cancel).ConfigureAwait(false);

        while (await WebSocketText.ReadAsync(socket, MaxMessage, cancel).ConfigureAwait(false) is { } text)
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.GetProperty("op").GetInt32() != 7) continue;
            var body = root.GetProperty("d");
            if (Text(body, "requestId") != id) continue;
            if (!body.TryGetProperty("requestStatus", out var status) || !Flag(status, "result")) return null;
            return body.TryGetProperty("responseData", out var response) && response.ValueKind == JsonValueKind.Object
                ? response.Clone()
                : JsonSerializer.SerializeToElement(new { });
        }

        throw new IOException("OBS closed the connection");
    }

    private static bool Flag(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static long Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) ? (long)number : 0;

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static string Hash(string text) => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
