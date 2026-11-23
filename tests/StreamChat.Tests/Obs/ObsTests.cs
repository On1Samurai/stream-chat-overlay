using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using StreamChat.Core.Chat;
using StreamChat.Core.Obs;

namespace StreamChat.Tests.Obs;

public sealed class ObsClientTests : IDisposable
{
    private readonly LocalServer _server = new();
    private readonly ConcurrentQueue<ObsSnapshot> _snapshots = new();
    private readonly ConcurrentQueue<SourceStatus> _statuses = new();

    public void Dispose() => _server.Dispose();

    private Task Run(string password = "", string microphone = "") =>
        new ObsClient(new Uri($"ws://localhost:{_server.Port}/"), password, microphone, poll: TimeSpan.FromMilliseconds(30))
            .RunAsync(_snapshots.Enqueue, _statuses.Enqueue, _server.Stop);

    private async Task<WebSocket> Connect(string hello = """{"op":0,"d":{"rpcVersion":1}}""")
    {
        var socket = await _server.Socket();
        await _server.Send(socket, hello);
        Assert.Equal(1, (await _server.Read(socket)).GetProperty("op").GetInt32());
        await _server.Send(socket, """{"op":2,"d":{"negotiatedRpcVersion":1}}""");
        return socket;
    }

    private async Task Serve(WebSocket socket, bool muted, bool active) =>
        await Serve(socket, (type, _) => (true, Data(type, muted, active)));

    private static string Data(string type, bool muted, bool active) => type switch
    {
        "GetStreamStatus" => """{"outputActive":true,"outputReconnecting":false,"outputDuration":3723000,"outputSkippedFrames":5,"outputTotalFrames":1000}""",
        "GetRecordStatus" => """{"outputActive":true,"outputPaused":false}""",
        "GetInputList" => """{"inputs":[{"inputName":"Игра","unversionedInputKind":"game_capture"},{"inputName":"Микрофон","unversionedInputKind":"wasapi_input_capture"}]}""",
        "GetInputMute" => muted ? """{"inputMuted":true}""" : """{"inputMuted":false}""",
        _ => active ? """{"videoActive":true}""" : """{"videoActive":false}""",
    };

    private async Task Serve(WebSocket socket, Func<string, JsonElement, (bool Ok, string Data)> answer)
    {
        while (_snapshots.IsEmpty)
        {
            var request = (await _server.Read(socket)).GetProperty("d");
            var type = request.GetProperty("requestType").GetString()!;
            var id = request.GetProperty("requestId").GetString()!;
            var (ok, data) = answer(type, request);
            await _server.Send(socket, """{"op":5,"d":{"eventType":"X"}}""");
            var status = ok ? """{"result":true,"code":100}""" : """{"result":false,"code":207,"comment":"not ready"}""";
            await _server.Send(socket,
                """{"op":7,"d":{"requestId":"ID","requestStatus":STATUS,"responseData":""".Replace("ID", id).Replace("STATUS", status)
                + (ok ? data : "null") + "}}");
        }
    }

    [Fact]
    public async Task Reads_stream_record_and_microphone()
    {
        _ = Run();
        using var socket = await Connect();
        await Serve(socket, muted: false, active: true);
        Assert.True(_snapshots.TryPeek(out var snapshot));
        Assert.Equal(new ObsSnapshot(true, false, TimeSpan.FromSeconds(3723), 5, 1000, true, false, MicState.On), snapshot);
    }

    [Theory]
    [InlineData(true, true, MicState.Muted)]
    [InlineData(false, false, MicState.NotInScene)]
    public async Task Microphone_state(bool muted, bool active, MicState expected)
    {
        _ = Run();
        using var socket = await Connect();
        await Serve(socket, muted, active);
        Assert.True(_snapshots.TryPeek(out var snapshot));
        Assert.Equal(expected, snapshot!.Mic);
    }

    [Fact]
    public async Task Failed_mute_request_is_not_shown_as_an_open_microphone()
    {
        _ = Run();
        using var socket = await Connect();
        await Serve(socket, (type, _) => (type != "GetInputMute", Data(type, false, true)));
        Assert.True(_snapshots.TryPeek(out var snapshot));
        Assert.Equal(MicState.Unknown, snapshot!.Mic);
    }

    [Fact]
    public async Task One_muted_microphone_of_two_is_reported()
    {
        _ = Run();
        using var socket = await Connect();
        await Serve(socket, (type, request) => (true, type switch
        {
            "GetInputList" => """{"inputs":[{"inputName":"A","unversionedInputKind":"wasapi_input_capture"},{"inputName":"B","unversionedInputKind":"wasapi_input_capture"}]}""",
            "GetInputMute" => request.GetProperty("requestData").GetProperty("inputName").GetString() == "A" ? """{"inputMuted":true}""" : """{"inputMuted":false}""",
            _ => Data(type, false, true),
        }));
        Assert.True(_snapshots.TryPeek(out var snapshot));
        Assert.Equal(MicState.Partial, snapshot!.Mic);
    }

    // OBS отвечает NotReady, например при смене коллекции сцен: это не «эфира нет»
    [Fact]
    public async Task Refused_status_request_skips_the_round_and_keeps_the_connection()
    {
        _ = Run();
        using var socket = await Connect();
        var refused = 0;
        await Serve(socket, (type, _) => (type != "GetStreamStatus" || refused++ > 0, Data(type, false, true)));
        Assert.True(_snapshots.TryPeek(out var snapshot));
        Assert.True(snapshot!.Streaming);
        Assert.DoesNotContain(_statuses, s => s.Health == SourceHealth.Waiting);
    }

    // вектор посчитан по алгоритму из protocol.md obs-websocket
    [Fact]
    public void Password_answer_follows_the_protocol()
    {
        const string salt = "lM1GncleQOaCu9lT1yeUZhFYnqhsLLP1G5lAGo3ixaI=";
        const string challenge = "+IxH4CnCiqpX1rM9scsNynZzbOe4KhDeYcTNS3PDaeY=";
        Assert.Equal("1Ct943GAT+6YQUUX47Ia/ncufilbe6+oD6lY+5kaCu4=", ObsClient.Answer("supersecretpassword", salt, challenge));
    }

    [Fact]
    public async Task Password_is_never_sent_as_is()
    {
        _ = Run(password: "secret");
        using var socket = await _server.Socket();
        await _server.Send(socket, """{"op":0,"d":{"rpcVersion":1,"authentication":{"challenge":"c","salt":"s"}}}""");
        var identify = (await _server.Read(socket)).ToString();
        Assert.Contains("authentication", identify);
        Assert.DoesNotContain("secret", identify);
    }

    [Fact]
    public async Task Wrong_password_is_explained_and_stops()
    {
        var run = Run(password: "wrong");
        using var socket = await _server.Socket();
        await _server.Send(socket, """{"op":0,"d":{"rpcVersion":1,"authentication":{"challenge":"c","salt":"s"}}}""");
        await _server.Read(socket);
        await socket.CloseOutputAsync((WebSocketCloseStatus)4009, "Authentication failed.", _server.Stop);
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("пароль", _statuses.Last().Text);
    }

    [Fact]
    public async Task Missing_password_is_explained()
    {
        var run = Run();
        using var socket = await _server.Socket();
        await _server.Send(socket, """{"op":0,"d":{"rpcVersion":1,"authentication":{"challenge":"c","salt":"s"}}}""");
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("пароль", _statuses.Last().Text);
    }
}

public class ObsBoardTests
{
    private static TimeSpan At(double seconds) => TimeSpan.FromSeconds(seconds);

    private static ObsSnapshot Live(MicState mic = MicState.On, long skipped = 0, long total = 1000, bool reconnecting = false) =>
        new(true, reconnecting, TimeSpan.FromSeconds(3723), skipped, total, false, false, mic);

    private static StatusItem Item(ObsBoard board, string label, double now = 0) => board.Items(At(now)).Single(i => i.Label == label);

    [Fact]
    public void Live_shows_duration()
    {
        var board = new ObsBoard();
        board.Update(Live(), At(0));
        Assert.Equal(new StatusItem("ЭФИР", "1:02:03", StatusLevel.Good), Item(board, "ЭФИР"));
    }

    [Fact]
    public void Offline_is_neutral()
    {
        var board = new ObsBoard();
        board.Update(new ObsSnapshot(false, false, TimeSpan.Zero, 0, 0, false, false, MicState.On), At(0));
        Assert.Equal(new StatusItem("ЭФИР", "нет", StatusLevel.Neutral), Item(board, "ЭФИР"));
    }

    [Fact]
    public void Muted_microphone_on_air_is_an_alarm()
    {
        var board = new ObsBoard();
        board.Update(Live(MicState.Muted), At(0));
        Assert.Equal(new StatusItem("МИК", "ВЫКЛЮЧЕН", StatusLevel.Bad), Item(board, "МИК"));
    }

    [Fact]
    public void Muted_microphone_while_only_recording_is_an_alarm()
    {
        var board = new ObsBoard();
        board.Update(new ObsSnapshot(false, false, TimeSpan.Zero, 0, 0, true, false, MicState.Muted), At(0));
        Assert.Equal(StatusLevel.Bad, Item(board, "МИК").Level);
    }

    // сцена «Перерыв» пресета: микрофона нет намеренно
    [Theory]
    [InlineData(MicState.Unknown, "?")]
    [InlineData(MicState.Partial, "частично выкл")]
    public void Unclear_microphone_is_a_warning(MicState mic, string text)
    {
        var board = new ObsBoard();
        board.Update(Live(mic), At(0));
        Assert.Equal(new StatusItem("МИК", text, StatusLevel.Warning), Item(board, "МИК"));
    }

    [Fact]
    public void Microphone_outside_the_scene_is_not_an_alarm()
    {
        var board = new ObsBoard();
        board.Update(Live(MicState.NotInScene), At(0));
        Assert.Equal(new StatusItem("МИК", "нет в сцене", StatusLevel.Warning), Item(board, "МИК"));
    }

    [Theory]
    [InlineData(2, null)]
    [InlineData(30, StatusLevel.Warning)]
    [InlineData(100, StatusLevel.Bad)]
    public void Dropped_frames_of_the_last_half_minute(long lost, StatusLevel? level)
    {
        var board = new ObsBoard();
        board.Update(Live(skipped: 50, total: 10_000), At(0));
        board.Update(Live(skipped: 50 + lost, total: 11_000), At(20));
        Assert.Equal(level, board.Items(At(20)).SingleOrDefault(i => i.Label == "ПОТЕРИ")?.Level);
    }

    [Fact]
    public void Old_drops_are_forgotten()
    {
        var board = new ObsBoard();
        board.Update(Live(skipped: 0, total: 1000), At(0));
        board.Update(Live(skipped: 500, total: 2000), At(2));
        for (var t = 4; t <= 40; t += 2) board.Update(Live(skipped: 500, total: 2000 + t * 60), At(t));
        Assert.DoesNotContain(board.Items(At(40)), i => i.Label == "ПОТЕРИ");
    }

    [Fact]
    public void Reconnecting_is_an_alarm()
    {
        var board = new ObsBoard();
        board.Update(Live(reconnecting: true), At(0));
        Assert.Equal(StatusLevel.Bad, Item(board, "ЭФИР").Level);
    }

    [Fact]
    public void Silence_from_obs_is_not_shown_as_all_good()
    {
        var board = new ObsBoard();
        board.Update(Live(), At(0));
        Assert.Equal([new StatusItem("OBS", "нет связи", StatusLevel.Warning)], board.Items(At(11)));
        Assert.False(board.IsOnAir(At(11)));
    }

    [Fact]
    public void Silent_obs_is_unknown_not_off_air()
    {
        var board = new ObsBoard();
        Assert.False(board.IsKnown(At(0)));
        board.Update(Live(), At(0));
        Assert.True(board.IsKnown(At(1)));
        Assert.False(board.IsKnown(At(11)));
    }

    [Fact]
    public void Knows_when_on_air()
    {
        var board = new ObsBoard();
        board.Update(Live(), At(0));
        Assert.True(board.IsOnAir(At(1)));
    }
}
