using StreamChat.Core;

namespace StreamChat;

/// <summary>Образец для настроек и тестовое сообщение. В настоящий чат сам не попадает.</summary>
internal sealed class SampleChat
{
    public static readonly IReadOnlyList<ChatMessage> Preview =
    [
        new(Platform.Twitch, "образец_1", "привет 👋 так выглядят эмодзи 😂🔥 и эмоуты Kappa")
        {
            Emotes = new Dictionary<string, string> { ["Kappa"] = "https://static-cdn.jtvnw.net/emoticons/v2/25/static/dark/2.0" },
        },
        new(Platform.YouTube, "Образец 2", "окно видишь только ты"),
        new(Platform.TikTok, "образец_3", "длинное сообщение переносится по строкам и обрезается, чтобы один зритель не занял половину экрана своим текстом во время игры"),
        new(Platform.YouTube, "Образец 4", "GG :trophy-yellow-smiling:"),
        new(Platform.Twitch, "образец_5", "@ты, сзади!") { Mark = MessageMark.Mention },
        new(Platform.DonationAlerts, "Образец 6", "[100 RUB] спасибо за стрим") { Mark = MessageMark.Donation },
    ];

    private int _next;

    public ChatMessage Next()
    {
        var sample = Preview[_next++ % Preview.Count];
        return sample with { Author = "Тест" };
    }
}
