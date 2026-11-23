using System.Diagnostics;
using Grpc.Core;
using Youtube.Api.V3;
using MessageType = Youtube.Api.V3.LiveChatMessageSnippet.Types.TypeWrapper.Types.Type;

namespace StreamChat.Core.Chat;

/// <summary>
/// YouTube сам закрывает streamList через несколько секунд, и каждое открытие стоит 5 единиц из 10 000 в сутки.
/// Поэтому открытия разнесены на interval, а pageToken не даёт потерять сообщения за паузу.
/// </summary>
public sealed class YouTubeClient(IYouTubeApi api, TimeSpan interval, Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    private static readonly TimeSpan QuotaPause = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan BeforeStartPause = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MaxRetry = TimeSpan.FromMinutes(5);

    private const int FailuresUntilError = 5;

    // по документации streamList RESOURCE_EXHAUSTED значит «слишком часто», квота только если не проходит
    private const int ExhaustedUntilQuota = 3;

    // история при подключении приходит снова: старое не выдаём за новое
    private static readonly TimeSpan HistoryGrace = TimeSpan.FromMinutes(1);

    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;

    public async Task RunAsync(string videoId, string key, ChatSink sink, CancellationToken cancel)
    {
        try
        {
            // такой ключ не влезет в заголовок, Google можно не спрашивать
            if (key.Length == 0 || key.Any(c => c is <= ' ' or > '~'))
            {
                sink.Status(Explain(LookupProblem.BadKey));
                return;
            }

            sink.Status(new(SourceHealth.Connecting, "подключаюсь"));
            if (await FindChatAsync(videoId, key, sink, cancel).ConfigureAwait(false) is { } chatId)
                await ReadAsync(chatId, key, sink, cancel).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
        }
        catch (RpcException) when (cancel.IsCancellationRequested)
        {
        }
    }

    private async Task<string?> FindChatAsync(string videoId, string key, ChatSink sink, CancellationToken cancel)
    {
        var failures = 0;
        while (true)
        {
            var (chatId, problem) = await api.FindChatAsync(videoId, key, cancel).ConfigureAwait(false);
            if (problem == LookupProblem.None) return chatId;

            sink.Status(Explain(problem));
            TimeSpan wait;
            switch (problem)
            {
                case LookupProblem.NotStarted:
                    wait = BeforeStartPause;
                    break;
                case LookupProblem.Quota:
                    wait = QuotaPause;
                    break;
                case LookupProblem.Unavailable:
                    wait = Backoff.After(++failures, TimeSpan.FromSeconds(5), MaxRetry);
                    break;
                default:
                    return null;
            }

            await _delay(wait, cancel).ConfigureAwait(false);
        }
    }

    private async Task ReadAsync(string chatId, string key, ChatSink sink, CancellationToken cancel)
    {
        string? token = null;
        var failures = 0;
        var exhausted = 0;
        var since = DateTimeOffset.UtcNow - HistoryGrace;
        var reportedConnected = false;

        while (true)
        {
            cancel.ThrowIfCancellationRequested();
            var opened = Stopwatch.StartNew();
            try
            {
                await foreach (var page in api.StreamAsync(chatId, key, token, cancel).ConfigureAwait(false))
                {
                    ReportConnected();
                    if (!string.IsNullOrEmpty(page.NextPageToken)) token = page.NextPageToken;

                    var ended = !string.IsNullOrEmpty(page.OfflineAt);
                    foreach (var item in page.Items)
                    {
                        if (item.Snippet?.Type == MessageType.ChatEndedEvent) ended = true;
                        else if (Translate(item, since) is { } change) sink.Event(change);
                    }

                    // история только в первой странице; дальше часы ПК не должны отсекать живой чат
                    since = DateTimeOffset.MinValue;

                    if (!ended) continue;
                    sink.Status(new(SourceHealth.Error, "эфир закончился, для нового вставьте новую ссылку"));
                    return;
                }

                // обычное закрытие потока, это не обрыв
                ReportConnected();
                var rest = interval - opened.Elapsed;
                if (rest > TimeSpan.Zero) await _delay(rest, cancel).ConfigureAwait(false);
            }
            catch (RpcException e) when (!cancel.IsCancellationRequested)
            {
                switch (e.StatusCode)
                {
                    case StatusCode.ResourceExhausted when ++exhausted < ExhaustedUntilQuota:
                        sink.Status(new(SourceHealth.Waiting, "YouTube просит подключаться реже, повторю"));
                        reportedConnected = false;
                        await _delay(Backoff.After(exhausted, interval, MaxRetry), cancel).ConfigureAwait(false);
                        break;
                    case StatusCode.ResourceExhausted:
                    case StatusCode.PermissionDenied when e.Status.Detail.Contains("quota", StringComparison.OrdinalIgnoreCase):
                        exhausted = 0;
                        sink.Status(Explain(LookupProblem.Quota));
                        reportedConnected = false;
                        await _delay(QuotaPause, cancel).ConfigureAwait(false);
                        break;
                    case StatusCode.InvalidArgument when e.Status.Detail.Contains("API key", StringComparison.OrdinalIgnoreCase):
                        sink.Status(Explain(LookupProblem.BadKey));
                        return;
                    case StatusCode.InvalidArgument when token is not null:
                        token = null;
                        await _delay(interval, cancel).ConfigureAwait(false);
                        break;
                    case StatusCode.PermissionDenied or StatusCode.Unauthenticated:
                        sink.Status(new(SourceHealth.Error, "Google не пускает к чату: проверьте ключ и доступ к эфиру"));
                        return;
                    case StatusCode.FailedPrecondition:
                        sink.Status(new(SourceHealth.Error, "эфир закончился или чат отключён"));
                        return;
                    case StatusCode.NotFound or StatusCode.InvalidArgument:
                        sink.Status(new(SourceHealth.Error, "чат этого эфира не найден, проверьте ссылку"));
                        return;
                    default:
                        await RetryAsync().ConfigureAwait(false);
                        break;
                }
            }
            catch (Exception e) when (e is IOException or HttpRequestException)
            {
                await RetryAsync().ConfigureAwait(false);
            }
        }

        void ReportConnected()
        {
            failures = 0;
            exhausted = 0;
            if (reportedConnected) return;
            reportedConnected = true;
            sink.Status(new(SourceHealth.Connected, "подключено"));
        }

        Task RetryAsync()
        {
            reportedConnected = false;
            failures++;
            sink.Status(new(failures >= FailuresUntilError ? SourceHealth.Error : SourceHealth.Waiting,
                "нет связи с YouTube, переподключаюсь"));
            // не чаще interval: неудачное открытие тоже может стоить квоты
            return _delay(Backoff.After(failures, interval, MaxRetry), cancel);
        }
    }

    private static ChatEvent? Translate(LiveChatMessage item, DateTimeOffset since)
    {
        if (item.Snippet is not { } snippet) return null;
        switch (snippet.Type)
        {
            case MessageType.Tombstone:
                return item.Id.Length > 0 ? new Deleted(Platform.YouTube, item.Id) : null;
            case MessageType.UserBannedEvent:
                var banned = snippet.UserBannedDetails?.BannedUserDetails?.ChannelId;
                return string.IsNullOrEmpty(banned) ? null : new UserCleared(Platform.YouTube, banned);
            case MessageType.SponsorOnlyModeStartedEvent or MessageType.SponsorOnlyModeEndedEvent or MessageType.PollEvent:
                return null;
        }

        if (DateTimeOffset.TryParse(snippet.PublishedAt, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out var published) && published < since)
            return null;

        var gift = snippet.Type == MessageType.GiftEvent ? snippet.GiftDetails : null;
        var raw = snippet.Type switch
        {
            MessageType.GiftEvent when gift is not null =>
                (gift.GiftName.Length > 0 ? $"подарок: {gift.GiftName}" : "подарок") + (gift.ComboCount > 1 ? $" ×{gift.ComboCount}" : ""),
            MessageType.SuperChatEvent when snippet.SuperChatDetails is { } paid =>
                $"[{paid.AmountDisplayString}] {paid.UserComment}",
            MessageType.SuperStickerEvent when snippet.SuperStickerDetails is { } sticker =>
                $"[{sticker.AmountDisplayString}] стикер",
            _ => snippet.DisplayMessage,
        };

        var author = item.AuthorDetails?.DisplayName ?? "";
        var text = UntrustedText.Clean(raw, UntrustedText.MaxText);
        if (text.Length == 0) return null;

        var mark = snippet.Type switch
        {
            MessageType.SuperChatEvent or MessageType.SuperStickerEvent or MessageType.GiftEvent => MessageMark.Paid,
            MessageType.NewSponsorEvent or MessageType.MemberMilestoneChatEvent or MessageType.MembershipGiftingEvent
                => MessageMark.Subscription,
            _ => MessageMark.None,
        };
        var paidAmount = snippet.Type switch
        {
            MessageType.SuperChatEvent when snippet.SuperChatDetails is { } details => details.AmountDisplayString,
            MessageType.SuperStickerEvent when snippet.SuperStickerDetails is { } details => details.AmountDisplayString,
            _ => "",
        };
        var amount = UntrustedText.Clean(paidAmount, UntrustedText.MaxAuthor);
        var money = mark is MessageMark.Paid or MessageMark.Subscription;

        var name = UntrustedText.Name(author);
        if (name.Length == 0)
        {
            if (!money) return null;
            name = UntrustedText.Anonymous;
        }

        return new Posted(new ChatMessage(Platform.YouTube, name, text)
        {
            Mark = mark,
            // у подарков и подписок без комментария весь текст и есть сумма
            Amount = amount.Length > 0 ? $"[{amount}]"
                : snippet.Type == MessageType.MemberMilestoneChatEvent ? "[продление участия]"
                : money ? text : "",
            Id = item.Id,
            // одно id на всё комбо подарка, номер комбо отличает обновления
            Revision = gift is { HasComboCount: true } ? gift.ComboCount.ToString(System.Globalization.CultureInfo.InvariantCulture) : "",
            AuthorId = item.AuthorDetails?.ChannelId ?? "",
            Login = author,
        });
    }

    private static SourceStatus Explain(LookupProblem problem) => problem switch
    {
        LookupProblem.BadKey => new(SourceHealth.Error, "неверный API-ключ"),
        LookupProblem.ApiDisabled => new(SourceHealth.Error, "в проекте Google не включён YouTube Data API v3"),
        LookupProblem.KeyRestricted => new(SourceHealth.Error,
            "ограничения ключа мешают: оставьте только ограничение на YouTube Data API v3"),
        LookupProblem.Quota => new(SourceHealth.Error,
            "квота Google кончилась, повторю через 5 минут. Суточная обновляется в полночь по Тихоокеанскому времени"),
        LookupProblem.NotFound => new(SourceHealth.Error, "эфир не найден: проверьте ссылку, приватные эфиры с ключом не видны"),
        LookupProblem.NotLiveVideo => new(SourceHealth.Error, "это обычное видео, нужна ссылка на эфир"),
        LookupProblem.NotStarted => new(SourceHealth.Waiting, "эфир не идёт, проверю через минуту"),
        LookupProblem.Ended => new(SourceHealth.Error, "эфир закончился, вставьте ссылку на новый"),
        LookupProblem.ChatDisabled => new(SourceHealth.Error, "у эфира отключён чат"),
        _ => new(SourceHealth.Waiting, "YouTube недоступен, повторю. Проверьте, открывается ли он у вас"),
    };
}
