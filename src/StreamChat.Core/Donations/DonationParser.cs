using System.Globalization;
using System.Text.Json;
using StreamChat.Core.Chat;

namespace StreamChat.Core.Donations;

/// <summary>Публикация Centrifugo: result.data.data это ресурс доната из документации.</summary>
public static class DonationParser
{
    private const decimal LargestSane = 1_000_000_000_000m;

    public static ChatEvent? Parse(string frame)
    {
        try
        {
            using var document = JsonDocument.Parse(frame);
            return ParseResource(Get(Get(Get(document.RootElement, "result"), "data"), "data"));
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Ресурс доната из документации: и в Centrifugo, и в списке /alerts/donations.</summary>
    public static ChatEvent? ParseResource(JsonElement donation)
    {
        try
        {
            if (donation.ValueKind != JsonValueKind.Object) return null;

            var amountField = Get(donation, "amount");
            var amountText = amountField.ValueKind switch
            {
                JsonValueKind.Number => amountField.GetRawText(),
                JsonValueKind.String => amountField.GetString(),
                _ => null,
            };
            if (!decimal.TryParse(amountText, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount)) return null;
            if (amount is <= 0 or > LargestSane) return null;

            var currency = Text(donation, "currency");
            if (currency.Length != 3 || !currency.All(char.IsAsciiLetterUpper)) return null;

            var author = UntrustedText.Clean(Text(donation, "username"), UntrustedText.MaxAuthor);
            var comment = Text(donation, "message_type") == "audio"
                ? "голосовое сообщение"
                : UntrustedText.Clean(Text(donation, "message"), UntrustedText.MaxText);
            var label = $"[{amount.ToString("0.##", CultureInfo.InvariantCulture)} {currency}]";

            var idField = Get(donation, "id");
            return new Posted(new ChatMessage(Platform.DonationAlerts, author.Length > 0 ? author : "Аноним",
                comment.Length > 0 ? $"{label} {comment}" : label)
            {
                Id = idField.ValueKind == JsonValueKind.Number ? idField.GetRawText() : "",
                Mark = MessageMark.Donation,
                Amount = label,
            });
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private static JsonElement Get(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : default;

    private static string Text(JsonElement element, string name) =>
        Get(element, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() ?? "" : "";
}
