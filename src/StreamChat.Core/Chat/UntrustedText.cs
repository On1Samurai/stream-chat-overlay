using System.Globalization;
using System.Text;

namespace StreamChat.Core.Chat;

/// <summary>Текст и имена зрителей: одна строка без управляющих символов.</summary>
public static class UntrustedText
{
    public const int MaxText = 500;
    public const int MaxAuthor = 50;

    public const string Anonymous = "Аноним";

    /// <summary>Первое имя, в котором после очистки что-то видно.</summary>
    public static string Name(params string?[] candidates) =>
        candidates.Select(candidate => Clean(candidate, MaxAuthor)).FirstOrDefault(name => name.Length > 0) ?? "";

    public static string Clean(string? text, int max)
    {
        if (string.IsNullOrEmpty(text)) return "";

        var clean = new StringBuilder(Math.Min(text.Length, max));
        var hasInk = false;
        foreach (var rune in text.EnumerateRunes())
        {
            // переворот строки подделывает чужой ник, U+E0000 добавляет Twitch к повторам
            if (rune.Value is >= 0x202A and <= 0x202E or >= 0x2066 and <= 0x2069 or 0x200E or 0x200F or 0x061C or 0xE0000)
                continue;

            var visible = Rune.IsControl(rune) || rune.Value is 0x2028 or 0x2029 ? new Rune(' ') : rune;
            if (clean.Length + visible.Utf16SequenceLength > max) return hasInk ? clean.ToString().Trim() + "…" : "";
            clean.Append(visible.ToString());
            hasInk |= HasInk(visible);
        }

        // строка из невидимых знаков будила бы окно пустой полосой
        return hasInk ? clean.ToString().Trim() : "";
    }

    private static bool HasInk(Rune rune) =>
        !Rune.IsWhiteSpace(rune)
        && rune.Value is not (0x2800 or 0x3164 or 0x115F or 0x1160 or 0xFFA0)
        && Rune.GetUnicodeCategory(rune) is not (UnicodeCategory.Format or UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark);
}
