using System.Globalization;
using System.Text.Json;
using StreamChat.Core.Chat;

namespace StreamChat.Core;

public enum BackgroundStyle { Panel, Bubbles, None }

public enum TextAlign { Left, Center, Right }

public readonly record struct Rgb(byte R, byte G, byte B)
{
    public static Rgb? Parse(string? text)
    {
        var hex = text?.Trim().TrimStart('#');
        if (hex is not { Length: 6 } || !hex.All(char.IsAsciiHexDigit)) return null;
        var value = uint.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return new Rgb((byte)(value >> 16), (byte)(value >> 8), (byte)value);
    }

    public override string ToString() => $"#{R:X2}{G:X2}{B:X2}";
}

/// <summary>Сочетание для RegisterHotKey: флаги MOD_* и виртуальная клавиша.</summary>
public sealed record KeyChord(uint Modifiers, uint Key)
{
    public const uint Alt = 1, Control = 2, Shift = 4;
    private const uint F4 = 0x73;

    public bool IsValid =>
        (Modifiers & (Alt | Control)) != 0
        && (Modifiers & ~(Alt | Control | Shift)) == 0
        // сами Ctrl, Alt, Shift и Win клавишей быть не могут
        && Key is >= 0x08 and <= 0xFE and not (>= 0x10 and <= 0x12) and not (>= 0xA0 and <= 0xA5) and not (0x5B or 0x5C)
        && !(Modifiers == Alt && Key == F4)
        // Ctrl+Alt+0…5 переключают сцены в пресете OBS
        && !(Modifiers == (Control | Alt) && Key is >= '0' and <= '5');
}

public sealed record AppSettings
{
    public const int MaxProfiles = 12;

    private const int MaxProfileName = 40;
    private const int DefaultObsPort = 4455;

    /// <summary>Новый профиль повторяет то, что сейчас на экране.</summary>
    public AppSettings AddProfile(string name)
    {
        var clean = UntrustedText.Clean(name, MaxProfileName);
        if (clean.Length == 0 || Profiles.Count >= MaxProfiles || FindProfile(clean) is not null) return this;

        // текущий экран уже расставлен под новую игру: прошлый профиль им не затираем
        return this with { Profiles = [.. Profiles, LayoutNamed(clean)], ActiveProfile = clean };
    }

    public AppSettings SwitchProfile(string name)
    {
        var stored = WithActiveProfileStored();
        if (stored.FindProfile(name) is not { } profile) return this;
        return stored with
        {
            ActiveProfile = profile.Name,
            Placement = profile.Placement,
            ViewersPlacement = profile.ViewersPlacement,
            ObsPlacement = profile.ObsPlacement,
            FontSize = profile.FontSize,
            ViewersFontSize = profile.ViewersFontSize,
            ObsFontSize = profile.ObsFontSize,
            Opacity = profile.Opacity,
        };
    }

    public AppSettings DeleteProfile(string name) => FindProfile(name) is not { } profile ? this : this with
    {
        Profiles = [.. Profiles.Where(p => p != profile)],
        ActiveProfile = ActiveProfile == profile.Name ? "" : ActiveProfile,
    };

    /// <summary>Профиль помнит последнее, что настроили, пока он был включён.</summary>
    public AppSettings WithActiveProfileStored() => FindProfile(ActiveProfile) is not { } active
        ? this
        : this with { Profiles = [.. Profiles.Select(p => p == active ? LayoutNamed(active.Name) : p)] };

    private LayoutProfile? FindProfile(string name) =>
        Profiles.Find(profile => string.Equals(profile.Name, name, StringComparison.OrdinalIgnoreCase));

    private LayoutProfile LayoutNamed(string name) => new()
    {
        Name = name,
        Placement = Placement,
        ViewersPlacement = ViewersPlacement,
        ObsPlacement = ObsPlacement,
        FontSize = FontSize,
        ViewersFontSize = ViewersFontSize,
        ObsFontSize = ObsFontSize,
        Opacity = Opacity,
    };

    // почти чёрные: сглаживание букв смешивается с этим цветом и даёт тёмную кайму, а не цветную
    private static readonly Rgb[] TransparentCandidates = [new(1, 2, 3), new(3, 2, 1), new(2, 3, 1), new(2, 1, 3), new(1, 3, 2)];

    public static readonly KeyChord DefaultMoveKey = new(KeyChord.Control | KeyChord.Alt, 0x76);
    public static readonly KeyChord DefaultHideKey = new(KeyChord.Control | KeyChord.Alt, 0x77);

    private static readonly Placement DefaultObsPlacement = new() { Position = Position.TopCenter };
    private static readonly Placement DefaultViewersPlacement = new() { Position = Position.TopRight };

    public Placement Placement { get; init; } = new();

    public string Font { get; init; } = "Segoe UI";
    public int FontSize { get; init; } = 15;
    public bool Bold { get; init; }
    public bool Italic { get; init; }
    public bool Shadow { get; init; }
    public TextAlign Align { get; init; }
    public string TextColor { get; init; } = "#F4F6F8";
    public bool NameColorByPlatform { get; init; } = true;
    public string NameColor { get; init; } = "#4FE3C1";
    public bool ShowPlatform { get; init; } = true;

    public BackgroundStyle Background { get; init; }
    public string BackgroundColor { get; init; } = "#101318";
    public int Opacity { get; init; } = 75;

    public int MaxMessages { get; init; } = 6;
    public int MaxLines { get; init; } = 3;
    public int Spacing { get; init; } = 2;
    public int HideAfterSeconds { get; init; } = 15;
    public bool ShowSettingsOnStart { get; init; } = true;

    public string TwitchChannel { get; init; } = "";
    public string YouTubeLink { get; init; } = "";

    /// <summary>Зашифрован DPAPI: открытого ключа в файле нет.</summary>
    public string YouTubeKeyProtected { get; init; } = "";

    public int YouTubeIntervalSeconds { get; init; } = 15;

    /// <summary>Правила API YouTube требуют согласия до первого подключения.</summary>
    public bool YouTubeTermsAccepted { get; init; }

    /// <summary>Неофициальный путь через TikFinity, поэтому только по согласию.</summary>
    public bool TikTokEnabled { get; init; }

    public string IgnoredNames { get; init; } = "";

    /// <summary>Картинки эмоутов качаются с CDN площадок. Обычные эмодзи встроены.</summary>
    public bool EmoteImages { get; init; } = true;

    public bool Highlight { get; init; } = true;
    public string MentionNames { get; init; } = "";
    public string BlockedWords { get; init; } = "";
    public bool HideLinks { get; init; }
    public bool HideRepeats { get; init; }

    public ProfileList Profiles { get; init; } = [];
    public string ActiveProfile { get; init; } = "";

    public bool CheckUpdates { get; init; } = true;

    public bool ObsEnabled { get; init; }
    public int ObsPort { get; init; } = DefaultObsPort;
    public string ObsPasswordProtected { get; init; } = "";
    public string ObsMicrophone { get; init; } = "";
    public bool ObsInColumn { get; init; }

    public string DonationTokenProtected { get; init; } = "";

    public int ObsFontSize { get; init; } = 16;
    public Placement ObsPlacement { get; init; } = DefaultObsPlacement;

    public bool ShowTwitchViewers { get; init; }
    public bool ShowYouTubeViewers { get; init; }
    public bool ShowTikTokViewers { get; init; }
    public bool ShowViewerTotal { get; init; }
    public bool ViewersInColumn { get; init; }
    public int ViewersFontSize { get; init; } = 18;
    public Placement ViewersPlacement { get; init; } = DefaultViewersPlacement;

    public KeyChord MoveKey { get; init; } = DefaultMoveKey;
    public KeyChord HideKey { get; init; } = DefaultHideKey;

    /// <summary>Цвет, который Windows вырезает, когда фон не сплошной.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Rgb TransparentColor
    {
        get
        {
            string[] used = [TextColor, NameColor, BackgroundColor];
            return TransparentCandidates.First(color => !used.Contains(color.ToString()));
        }
    }

    /// <summary>Файл настроек могут прислать или исправить руками, поэтому не доверяем ни одному полю.</summary>
    public AppSettings Normalized()
    {
        var defaults = new AppSettings();
        var placement = Valid(Placement, defaults.Placement);
        var viewers = Valid(ViewersPlacement, defaults.ViewersPlacement);

        var profiles = new ProfileList();
        foreach (var saved in Profiles ?? [])
        {
            if (saved is null || profiles.Count >= MaxProfiles) continue;
            var name = UntrustedText.Clean(saved.Name, MaxProfileName);
            if (name.Length == 0 || profiles.Exists(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))) continue;
            profiles.Add(saved with
            {
                Name = name,
                Placement = Valid(saved.Placement, defaults.Placement),
                ViewersPlacement = Valid(saved.ViewersPlacement, defaults.ViewersPlacement),
                ObsPlacement = Valid(saved.ObsPlacement, defaults.ObsPlacement),
                FontSize = Math.Clamp(saved.FontSize, 10, 40),
                ViewersFontSize = Math.Clamp(saved.ViewersFontSize, 10, 40),
                ObsFontSize = Math.Clamp(saved.ObsFontSize, 10, 40),
                Opacity = Math.Clamp(saved.Opacity, 20, 100),
            });
        }

        var move = MoveKey is { IsValid: true } ? MoveKey : DefaultMoveKey;
        var hide = HideKey is { IsValid: true } ? HideKey : DefaultHideKey;
        if (move == hide) (move, hide) = (DefaultMoveKey, DefaultHideKey);

        return this with
        {
            Placement = placement,
            ViewersPlacement = viewers,
            ObsPlacement = Valid(ObsPlacement, defaults.ObsPlacement),
            ObsFontSize = Math.Clamp(ObsFontSize, 10, 40),
            Profiles = profiles,
            ActiveProfile = profiles.Find(p => p.Name == ActiveProfile)?.Name ?? "",
            ObsPort = ObsPort is >= 1 and <= 65535 ? ObsPort : DefaultObsPort,
            ObsPasswordProtected = Limit(ObsPasswordProtected, 4096),
            ObsMicrophone = Limit(ObsMicrophone, 256),
            DonationTokenProtected = Limit(DonationTokenProtected, 32 * 1024),
            MentionNames = Limit(MentionNames, 1000),
            BlockedWords = Limit(BlockedWords, 5000),
            ViewersFontSize = Math.Clamp(ViewersFontSize, 10, 40),
            Font = IsFontName(Font) ? Font : defaults.Font,
            FontSize = Math.Clamp(FontSize, 10, 40),
            Align = Enum.IsDefined(Align) ? Align : defaults.Align,
            TextColor = Rgb.Parse(TextColor)?.ToString() ?? defaults.TextColor,
            NameColor = Rgb.Parse(NameColor)?.ToString() ?? defaults.NameColor,
            Background = Enum.IsDefined(Background) ? Background : defaults.Background,
            BackgroundColor = Rgb.Parse(BackgroundColor)?.ToString() ?? defaults.BackgroundColor,
            Opacity = Math.Clamp(Opacity, 20, 100),
            MaxMessages = Math.Clamp(MaxMessages, 1, 15),
            MaxLines = Math.Clamp(MaxLines, 1, 10),
            Spacing = Math.Clamp(Spacing, 0, 20),
            HideAfterSeconds = Math.Clamp(HideAfterSeconds, 0, 120),
            TwitchChannel = Limit(TwitchChannel, 200),
            YouTubeLink = Limit(YouTubeLink, 300),
            YouTubeKeyProtected = Limit(YouTubeKeyProtected, 4096),
            YouTubeIntervalSeconds = Math.Clamp(YouTubeIntervalSeconds, 5, 60),
            IgnoredNames = Limit(IgnoredNames, 5000),
            MoveKey = move,
            HideKey = hide,
        };
    }

    /// <summary>Сброс вида: подключения, секреты, фильтры и профили остаются.</summary>
    public AppSettings ResetLook() => new AppSettings() with
    {
        TwitchChannel = TwitchChannel,
        YouTubeLink = YouTubeLink,
        YouTubeKeyProtected = YouTubeKeyProtected,
        YouTubeIntervalSeconds = YouTubeIntervalSeconds,
        YouTubeTermsAccepted = YouTubeTermsAccepted,
        TikTokEnabled = TikTokEnabled,
        DonationTokenProtected = DonationTokenProtected,
        ObsEnabled = ObsEnabled,
        ObsPort = ObsPort,
        ObsPasswordProtected = ObsPasswordProtected,
        ObsMicrophone = ObsMicrophone,
        IgnoredNames = IgnoredNames,
        Highlight = Highlight,
        MentionNames = MentionNames,
        BlockedWords = BlockedWords,
        HideLinks = HideLinks,
        HideRepeats = HideRepeats,
        EmoteImages = EmoteImages,
        ShowTwitchViewers = ShowTwitchViewers,
        ShowYouTubeViewers = ShowYouTubeViewers,
        ShowTikTokViewers = ShowTikTokViewers,
        ShowViewerTotal = ShowViewerTotal,
        CheckUpdates = CheckUpdates,
        Profiles = Profiles,
        // клавиши могли переназначить из-за игры, это не оформление
        MoveKey = MoveKey,
        HideKey = HideKey,
        ShowSettingsOnStart = ShowSettingsOnStart,

        // активный профиль сброшен: иначе он запомнил бы сброшенный вид
    };

    /// <param name="file">settings.json.</param>
    /// <returns>CanSave false: файл есть, но не читается, и запись затёрла бы ключи и вход.</returns>
    public static (AppSettings Settings, bool CanSave) LoadSafely(string file)
    {
        string text;
        try
        {
            text = ReadWithRetry(file);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return (new AppSettings(), true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (new AppSettings(), false);
        }

        if (Parse(text) is { } settings) return (settings, true);

        // испорченный файл откладываем: в нём могли быть ключ, вход и профили
        try
        {
            File.Copy(file, file + ".broken", overwrite: true);
            return (new AppSettings(), true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (new AppSettings(), false);
        }
    }

    public static AppSettings Load(string file) => LoadSafely(file).Settings;

    private static string ReadWithRetry(string file)
    {
        // антивирус и синхронизация держат файл недолго
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return File.ReadAllText(file);
            }
            catch (IOException e) when (e is not (FileNotFoundException or DirectoryNotFoundException) && attempt < 3)
            {
                Thread.Sleep(200);
            }
        }
    }

    private static AppSettings? Parse(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            // 0.1.1 хранила в файле одно только положение
            var settings = !root.TryGetProperty(nameof(Placement), out _) && root.TryGetProperty(nameof(Placement.Position), out _)
                ? new AppSettings { Placement = root.Deserialize<Placement>()! }
                : root.Deserialize<AppSettings>()!;
            return settings.Normalized();
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    public void Save(string file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temporary = file + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this));
        File.Move(temporary, file, overwrite: true);
    }

    private static Placement Valid(Placement? saved, Placement fallback)
    {
        if (saved is null || !Enum.IsDefined(saved.Position)) return fallback;
        return saved with
        {
            // 1e400 читается как бесконечность, а записать её JSON не может
            X = double.IsFinite(saved.X) ? saved.X : 0,
            Y = double.IsFinite(saved.Y) ? saved.Y : 0,
            Width = double.IsNaN(saved.Width) ? fallback.Width : Math.Clamp(saved.Width, Placement.MinWidth, Placement.MaxWidth),
        };
    }

    private static string Limit(string? text, int max) => text is null ? "" : text.Length > max ? text[..max] : text;

    // WPF принимает в имени шрифта путь и сетевой адрес
    private static bool IsFontName(string? font) =>
        !string.IsNullOrWhiteSpace(font) && font.Length <= 100 && font.IndexOfAny(['/', '\\', '#', ':', ',']) < 0;
}
