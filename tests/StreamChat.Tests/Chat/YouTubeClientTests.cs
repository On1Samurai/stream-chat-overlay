using System.Runtime.CompilerServices;
using Grpc.Core;
using StreamChat.Core;
using StreamChat.Core.Chat;
using Youtube.Api.V3;
using Type = Youtube.Api.V3.LiveChatMessageSnippet.Types.TypeWrapper.Types.Type;

namespace StreamChat.Tests.Chat;

public class YouTubeClientTests
{
    private sealed class FakeApi : IYouTubeApi
    {
        public Queue<LookupResult> Lookups = new([new LookupResult("chat", LookupProblem.None)]);
        public Queue<Func<IEnumerable<LiveChatMessageListResponse>>> Connections = new();
        public List<string?> Tokens = [];
        public int LookupCount;

        public Task<LookupResult> FindChatAsync(string videoId, string key, CancellationToken cancel)
        {
            LookupCount++;
            return Task.FromResult(Lookups.Dequeue());
        }

        public async IAsyncEnumerable<LiveChatMessageListResponse> StreamAsync(
            string chatId, string key, string? pageToken, [EnumeratorCancellation] CancellationToken cancel)
        {
            Tokens.Add(pageToken);
            await Task.Yield();
            // кончились сценарии: эфир завершён, клиент должен остановиться
            if (!Connections.TryDequeue(out var connection)) throw Error(StatusCode.FailedPrecondition);
            foreach (var response in connection()) yield return response;
        }
    }

    private readonly FakeApi _api = new();
    private readonly List<ChatEvent> _events = [];
    private readonly List<SourceStatus> _statuses = [];
    private readonly List<TimeSpan> _waits = [];

    private Task Run(int interval = 15)
    {
        var client = new YouTubeClient(_api, TimeSpan.FromSeconds(interval), (wait, _) =>
        {
            _waits.Add(wait);
            return Task.CompletedTask;
        });
        return client.RunAsync("dQw4w9WgXcQ", "key", new ChatSink(_events.Add, _statuses.Add), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static RpcException Error(StatusCode code) => new(new Status(code, "test"));

    private static IEnumerable<LiveChatMessageListResponse> Fails(StatusCode code)
    {
        throw Error(code);
#pragma warning disable CS0162 // нужен, чтобы метод был итератором
        yield break;
#pragma warning restore CS0162
    }

    private static LiveChatMessageListResponse Page(string token, params LiveChatMessage[] items)
    {
        var response = new LiveChatMessageListResponse { NextPageToken = token };
        response.Items.AddRange(items);
        return response;
    }

    private static LiveChatMessage Item(Type type, string id = "m") => new()
    {
        Id = id,
        Snippet = new LiveChatMessageSnippet
        {
            Type = type,
            DisplayMessage = "привет",
        },
        AuthorDetails = new LiveChatMessageAuthorDetails { ChannelId = "channel", DisplayName = "@viewer" },
    };

    [Fact]
    public async Task Delivers_messages()
    {
        _api.Connections.Enqueue(() => [Page("a", Item(Type.TextMessageEvent))]);
        await Run();
        var message = Assert.IsType<Posted>(Assert.Single(_events)).Message;
        Assert.Equal(new ChatMessage(Platform.YouTube, "@viewer", "привет") { Id = "m", AuthorId = "channel", Login = "@viewer" }, message);
    }

    [Fact]
    public async Task Reopens_from_the_last_page_token()
    {
        _api.Connections.Enqueue(() => [Page("a"), Page("b")]);
        _api.Connections.Enqueue(() => [Page("c")]);
        await Run();
        Assert.Equal([null, "b", "c"], _api.Tokens);
        Assert.Equal(1, _api.LookupCount);
    }

    [Fact]
    public async Task Short_connections_are_spaced_to_save_quota()
    {
        _api.Connections.Enqueue(() => [Page("a")]);
        await Run(interval: 20);
        Assert.InRange(Assert.Single(_waits).TotalSeconds, 19, 20);
    }

    [Fact]
    public async Task Normal_stream_end_is_not_shown_as_a_failure()
    {
        _api.Connections.Enqueue(() => [Page("a")]);
        _api.Connections.Enqueue(() => [Page("b")]);
        await Run();
        Assert.DoesNotContain(_statuses, s => s.Health == SourceHealth.Waiting);
    }

    [Fact]
    public async Task Network_errors_back_off_and_keep_the_token()
    {
        _api.Connections.Enqueue(() => [Page("a")]);
        for (var i = 0; i < 3; i++) _api.Connections.Enqueue(() => Fails(StatusCode.Unavailable));
        await Run();
        var retries = _waits.Skip(1).ToArray();
        Assert.True(retries[0] < retries[1] && retries[1] < retries[2]);
        Assert.Equal("a", _api.Tokens[^1]);
    }

    // по документации streamList RESOURCE_EXHAUSTED это «слишком часто», а не квота
    [Fact]
    public async Task Too_frequent_reopening_is_retried_soon()
    {
        _api.Connections.Enqueue(() => Fails(StatusCode.ResourceExhausted));
        _api.Connections.Enqueue(() => [Page("a")]);
        await Run();
        Assert.DoesNotContain(_statuses, s => s.Text.Contains("квота"));
        Assert.Contains(TimeSpan.FromSeconds(15), _waits);
        Assert.DoesNotContain(TimeSpan.FromMinutes(5), _waits);
    }

    [Fact]
    public async Task Repeated_exhaustion_is_reported_as_quota()
    {
        for (var i = 0; i < 3; i++) _api.Connections.Enqueue(() => Fails(StatusCode.ResourceExhausted));
        await Run();
        Assert.Contains(TimeSpan.FromMinutes(5), _waits);
        Assert.Contains(_statuses, s => s.Health == SourceHealth.Error && s.Text.Contains("квота"));
    }

    [Fact]
    public async Task Gift_combo_updates_keep_one_id_with_a_new_revision()
    {
        LiveChatMessage Gift(int combo)
        {
            var item = Item(Type.GiftEvent, "g");
            item.Snippet.GiftDetails = new LiveChatGiftDetails { GiftName = "Rose", ComboCount = combo, JewelsAmount = 10 };
            return item;
        }

        _api.Connections.Enqueue(() => [Page("a", Gift(1), Gift(2))]);
        await Run();
        var gifts = _events.OfType<Posted>().Select(p => p.Message).ToArray();
        Assert.Equal([("g", "1"), ("g", "2")], gifts.Select(m => (m.Id, m.Revision)));
        Assert.All(gifts, m => Assert.Equal((MessageMark.Paid, m.Text), (m.Mark, m.Amount)));
    }

    [Fact]
    public async Task Invisible_name_of_a_paying_viewer_becomes_anonymous()
    {
        var item = Item(Type.SuperChatEvent);
        item.AuthorDetails.DisplayName = "\u3164";
        item.SnippetSuperChat();
        _api.Connections.Enqueue(() => [Page("a", item)]);
        await Run();
        Assert.Equal("Аноним", Assert.IsType<Posted>(Assert.Single(_events)).Message.Author);
    }

    [Fact]
    public async Task History_from_before_the_connection_is_not_shown_as_new()
    {
        var old = Item(Type.TextMessageEvent, "old");
        old.Snippet.PublishedAt = "2020-01-01T00:00:00Z";
        var fresh = Item(Type.TextMessageEvent, "fresh");
        fresh.Snippet.PublishedAt = DateTimeOffset.UtcNow.ToString("O");
        _api.Connections.Enqueue(() => [Page("a", old, fresh)]);
        await Run();
        Assert.Equal("fresh", Assert.IsType<Posted>(Assert.Single(_events)).Message.Id);
    }

    [Fact]
    public async Task Wrong_pc_clock_does_not_hide_live_chat()
    {
        var live = Item(Type.TextMessageEvent, "live");
        live.Snippet.PublishedAt = "2020-01-01T00:00:00Z";
        _api.Connections.Enqueue(() => [Page("a"), Page("b", live)]);
        await Run();
        Assert.Equal("live", Assert.IsType<Posted>(Assert.Single(_events)).Message.Id);
    }

    [Fact]
    public async Task Invalid_key_in_stream_is_named_and_not_retried()
    {
        _api.Connections.Enqueue(() => [Page("a")]);
        _api.Connections.Enqueue(() => throw new RpcException(
            new Status(StatusCode.InvalidArgument, "API key not valid. Please pass a valid API key.")));
        await Run();
        Assert.Equal(2, _api.Tokens.Count);
        Assert.Contains("ключ", _statuses[^1].Text);
    }

    [Fact]
    public async Task Long_outage_becomes_a_visible_error()
    {
        for (var i = 0; i < 5; i++) _api.Connections.Enqueue(() => Fails(StatusCode.Unavailable));
        await Run();
        Assert.Contains(_statuses, s => s.Health == SourceHealth.Error && s.Text.Contains("нет связи"));
    }

    [Fact]
    public async Task Stale_token_is_dropped()
    {
        _api.Connections.Enqueue(() => [Page("a")]);
        _api.Connections.Enqueue(() => Fails(StatusCode.InvalidArgument));
        await Run();
        Assert.Equal([null, "a", null], _api.Tokens);
    }

    [Theory]
    [InlineData(StatusCode.PermissionDenied)]
    [InlineData(StatusCode.Unauthenticated)]
    [InlineData(StatusCode.NotFound)]
    [InlineData(StatusCode.InvalidArgument)]
    public async Task Fatal_errors_stop_without_spending_quota(StatusCode code)
    {
        _api.Connections.Enqueue(() => Fails(code));
        await Run();
        Assert.Single(_api.Tokens);
        Assert.Equal(SourceHealth.Error, _statuses[^1].Health);
    }

    [Fact]
    public async Task Offline_marker_stops_the_client()
    {
        _api.Connections.Enqueue(() => [new LiveChatMessageListResponse { OfflineAt = "2026-11-22T10:00:00Z" }]);
        await Run();
        Assert.Single(_api.Tokens);
        Assert.Contains("законч", _statuses[^1].Text);
    }

    [Fact]
    public async Task Chat_ended_event_stops_the_client()
    {
        _api.Connections.Enqueue(() => [Page("a", Item(Type.ChatEndedEvent))]);
        await Run();
        Assert.Single(_api.Tokens);
    }

    [Fact]
    public async Task Tombstone_deletes_message()
    {
        _api.Connections.Enqueue(() => [Page("a", Item(Type.Tombstone, "gone"))]);
        await Run();
        Assert.Equal(new Deleted(Platform.YouTube, "gone"), Assert.Single(_events));
    }

    [Fact]
    public async Task Ban_clears_the_banned_user_and_not_the_moderator()
    {
        var item = Item(Type.UserBannedEvent);
        item.Snippet.UserBannedDetails = new LiveChatUserBannedMessageDetails
        {
            BannedUserDetails = new ChannelProfileDetails { ChannelId = "bad" },
        };
        _api.Connections.Enqueue(() => [Page("a", item)]);
        await Run();
        Assert.Equal(new UserCleared(Platform.YouTube, "bad"), Assert.Single(_events));
    }

    [Fact]
    public async Task Super_chat_shows_amount_and_comment()
    {
        var item = Item(Type.SuperChatEvent);
        item.Snippet.SuperChatDetails = new LiveChatSuperChatDetails { AmountDisplayString = "$5.00", UserComment = "спасибо" };
        _api.Connections.Enqueue(() => [Page("a", item)]);
        await Run();
        var message = Assert.IsType<Posted>(Assert.Single(_events)).Message;
        Assert.Equal(("[$5.00] спасибо", "[$5.00]", MessageMark.Paid), (message.Text, message.Amount, message.Mark));
    }

    [Theory]
    [InlineData(LookupProblem.BadKey)]
    [InlineData(LookupProblem.ApiDisabled)]
    [InlineData(LookupProblem.KeyRestricted)]
    [InlineData(LookupProblem.NotFound)]
    [InlineData(LookupProblem.NotLiveVideo)]
    [InlineData(LookupProblem.Ended)]
    [InlineData(LookupProblem.ChatDisabled)]
    public async Task Fatal_lookup_problem_is_explained_and_not_retried(LookupProblem problem)
    {
        _api.Lookups = new([new LookupResult(null, problem)]);
        await Run();
        Assert.Equal(1, _api.LookupCount);
        Assert.Empty(_api.Tokens);
        Assert.NotEqual("", _statuses[^1].Text);
        Assert.Equal(SourceHealth.Error, _statuses[^1].Health);
    }

    [Theory]
    [InlineData(LookupProblem.NotStarted)]
    [InlineData(LookupProblem.Unavailable)]
    [InlineData(LookupProblem.Quota)]
    public async Task Temporary_lookup_problem_is_retried(LookupProblem problem)
    {
        _api.Lookups = new([new LookupResult(null, problem), new LookupResult("chat", LookupProblem.None)]);
        await Run();
        Assert.Equal(2, _api.LookupCount);
    }
}

internal static class YouTubeTestItems
{
    public static void SnippetSuperChat(this LiveChatMessage item) =>
        item.Snippet.SuperChatDetails = new LiveChatSuperChatDetails { AmountDisplayString = "$5.00", UserComment = "спасибо" };
}
