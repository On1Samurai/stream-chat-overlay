using System.IO.Compression;
using System.Text;

namespace StreamChat.Core.Emotes;

/// <param name="Image">Пусто для текста, emoji:ключ для встроенного эмодзи, иначе адрес картинки.</param>
public sealed record MessagePart(string Text, string Image = "");

/// <summary>Цветные эмодзи Twemoji внутри программы: WPF сам рисует их чёрно-белыми.</summary>
public static class Emoji
{
    public const string Scheme = "emoji:";

    private const int EmojiStyle = 0xFE0F;
    private const int TextStyle = 0xFE0E;

    // ниже этой границы без U+FE0F это знаки текста: © ® ™ стрелки
    private const int AlwaysPictureFrom = 0x2300;

    private static readonly Lock ArchiveLock = new();
    private static readonly ZipArchive Archive =
        new(typeof(Emoji).Assembly.GetManifestResourceStream("twemoji.zip")!, ZipArchiveMode.Read);

    private static readonly HashSet<string> Files = [.. Archive.Entries.Select(entry => entry.Name[..^".png".Length])];

    // в тексте U+FE0F то есть, то нет, поэтому ищем без него
    private static readonly Dictionary<string, string> ByCodes = Files
        .GroupBy(file => string.Join('-', file.Split('-').Where(code => code != "fe0f")))
        .ToDictionary(group => group.Key, group => group.First());

    private static readonly HashSet<int> Starts =
        [.. ByCodes.Keys.Select(key => Convert.ToInt32(key.Split('-')[0], 16))];

    private static readonly int Longest = ByCodes.Keys.Max(key => key.Split('-').Length);

    public static IReadOnlyList<MessagePart> Split(string text)
    {
        var runes = text.EnumerateRunes().ToArray();
        var parts = new List<MessagePart>();
        var plain = new StringBuilder();

        for (var start = 0; start < runes.Length;)
        {
            var (file, end) = Match(runes, start);
            if (file.Length == 0)
            {
                plain.Append(runes[start].ToString());
                start++;
                continue;
            }

            if (plain.Length > 0) parts.Add(new MessagePart(plain.ToString()));
            plain.Clear();
            parts.Add(new MessagePart(string.Concat(runes[start..end].Select(rune => rune.ToString())), Scheme + file));
            start = end;
        }

        if (plain.Length > 0) parts.Add(new MessagePart(plain.ToString()));
        return parts;
    }

    public static byte[]? Png(string key)
    {
        if (!Files.Contains(key)) return null;
        // ZipArchive не потокобезопасен
        lock (ArchiveLock)
        {
            using var entry = Archive.GetEntry($"{key}.png")!.Open();
            using var bytes = new MemoryStream();
            entry.CopyTo(bytes);
            return bytes.ToArray();
        }
    }

    private static (string File, int End) Match(Rune[] runes, int start)
    {
        if (!Starts.Contains(runes[start].Value)) return ("", start);

        var key = new StringBuilder();
        var file = "";
        var end = start;
        var length = 0;
        var matchedLength = 0;
        for (var next = start; next < runes.Length && length < Longest; next++)
        {
            if (runes[next].Value == EmojiStyle) continue;
            if (length++ > 0) key.Append('-');
            key.Append(runes[next].Value.ToString("x"));
            if (!ByCodes.TryGetValue(key.ToString(), out var found)) continue;
            (file, end, matchedLength) = (found, next + 1, length);
        }

        if (file.Length == 0) return ("", start);

        var following = end < runes.Length ? runes[end].Value : 0;
        if (matchedLength == 1 && (following == TextStyle || (runes[start].Value < AlwaysPictureFrom && following != EmojiStyle)))
            return ("", start);

        return (file, following == EmojiStyle ? end + 1 : end);
    }
}
