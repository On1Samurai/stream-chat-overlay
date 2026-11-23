namespace StreamChat.Core
{
    public enum Platform { Twitch, YouTube, TikTok, DonationAlerts, App }

    public enum MessageMark { None, Mention, FirstMessage, Subscription, Raid, Paid, Donation }

    public sealed record ChatMessage(Platform Platform, string Author, string Text)
    {
        public string Id { get; init; } = "";
        public string AuthorId { get; init; } = "";

        /// <summary>Имя для списка скрытых: у Twitch отличается от показанного.</summary>
        public string Login { get; init; } = "";

        public MessageMark Mark { get; init; }

        /// <summary>Новая версия того же сообщения: счётчик комбо подарка YouTube.</summary>
        public string Revision { get; init; } = "";

        /// <summary>Сумма в начале текста: фильтр слов её не прячет.</summary>
        public string Amount { get; init; } = "";

        /// <summary>Слово в тексте и адрес его картинки, как их прислала площадка.</summary>
        public IReadOnlyDictionary<string, string> Emotes { get; init; } = NoEmotes;

        private static readonly IReadOnlyDictionary<string, string> NoEmotes = new Dictionary<string, string>();
    }
}

namespace StreamChat.Core.Chat
{
    public abstract record ChatEvent;

    public sealed record Posted(ChatMessage Message) : ChatEvent;

    public sealed record Deleted(Platform Platform, string Id) : ChatEvent;

    public sealed record UserCleared(Platform Platform, string AuthorId) : ChatEvent;

    public sealed record Cleared(Platform Platform) : ChatEvent;

    public enum SourceHealth { Off, Connecting, Connected, Waiting, Error }

    /// <summary>Текст видят зрители в уведомлениях: без имён, ссылок, ключей и сообщений.</summary>
    public sealed record SourceStatus(SourceHealth Health, string Text);

    /// <summary>Вызывается из фоновых потоков.</summary>
    public sealed record ChatSink(Action<ChatEvent> Event, Action<SourceStatus> Status, Action<string>? Room = null, Action<long>? Viewers = null);
}
