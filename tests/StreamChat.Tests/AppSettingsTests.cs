using StreamChat.Core;

namespace StreamChat.Tests;

public sealed class AppSettingsTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("streamchat-test-").FullName;
    private string SettingsFile => Path.Combine(_folder, "StreamChat", "settings.json");

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private AppSettings LoadText(string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
        File.WriteAllText(SettingsFile, content);
        return AppSettings.Load(SettingsFile);
    }

    [Fact]
    public void Settings_survive_restart()
    {
        var saved = new AppSettings
        {
            Font = "Comic Sans MS", FontSize = 24, Bold = true, Background = BackgroundStyle.None,
            TextColor = "#FFEE00", Placement = new Placement { Position = Position.BottomRight, Width = 420 },
            HideKey = new KeyChord(KeyChord.Control | KeyChord.Shift, 0x48), ShowSettingsOnStart = false,
        };
        saved.Save(SettingsFile);
        Assert.Equal(saved, AppSettings.Load(SettingsFile));
    }

    [Fact]
    public void Missing_file_gives_defaults() => Assert.Equal(new AppSettings(), AppSettings.Load(SettingsFile));

    [Theory]
    [InlineData("")]
    [InlineData("{broken")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"FontSize\":\"большой\"}")]
    [InlineData("{\"\\uD800\\uD800\":1}")]
    public void Broken_file_gives_defaults(string content) => Assert.Equal(new AppSettings(), LoadText(content));

    [Fact]
    public void Position_saved_by_version_0_1_1_is_kept()
    {
        var loaded = LoadText("{\"Position\":7,\"X\":0,\"Y\":0,\"GrowsUp\":false,\"Width\":500}");
        Assert.Equal(new Placement { Position = Position.BottomRight, Width = 500 }, loaded.Placement);
    }

    [Fact]
    public void Nulls_are_replaced_with_defaults()
    {
        var loaded = LoadText("""{"Placement":null,"Font":null,"TextColor":null,"HideKey":null,"MoveKey":null}""");
        Assert.Equal(new AppSettings(), loaded);
    }

    [Fact]
    public void Numbers_are_clamped()
    {
        var low = new AppSettings { FontSize = -1, Opacity = 0, MaxMessages = 0, MaxLines = 0, HideAfterSeconds = -5, Spacing = -1 }.Normalized();
        Assert.Equal((10, 20, 1, 1, 0, 0), (low.FontSize, low.Opacity, low.MaxMessages, low.MaxLines, low.HideAfterSeconds, low.Spacing));

        var high = new AppSettings { FontSize = 999, Opacity = 999, MaxMessages = 999, MaxLines = 999, HideAfterSeconds = 999, Spacing = 999 }.Normalized();
        Assert.Equal((40, 100, 15, 10, 120, 20), (high.FontSize, high.Opacity, high.MaxMessages, high.MaxLines, high.HideAfterSeconds, high.Spacing));
    }

    [Theory]
    [InlineData(5, 220)]
    [InlineData(5000, 800)]
    public void Width_is_clamped(double saved, double expected) =>
        Assert.Equal(expected, new AppSettings { Placement = new Placement { Width = saved } }.Normalized().Placement.Width);

    [Fact]
    public void Unknown_choices_become_defaults()
    {
        var settings = new AppSettings
        {
            Background = (BackgroundStyle)9, Align = (TextAlign)9, Placement = new Placement { Position = (Position)99 },
        }.Normalized();
        Assert.Equal(new AppSettings(), settings);
    }

    // файл настроек могут прислать: имя шрифта не должно открывать пути и сеть
    [Theory]
    [InlineData(@"\\server\share\#Evil")]
    [InlineData("file:///c:/font.ttf#X")]
    [InlineData("./#Local")]
    [InlineData("  ")]
    public void Font_cannot_be_a_path(string font) =>
        Assert.Equal(new AppSettings().Font, new AppSettings { Font = font }.Normalized().Font);

    [Theory]
    [InlineData("#ffee00", "#FFEE00")]
    [InlineData("ffee00", "#FFEE00")]
    [InlineData(" #12abEF ", "#12ABEF")]
    [InlineData("#fff", null)]
    [InlineData("red", null)]
    [InlineData("#GG0000", null)]
    [InlineData("+12345", null)]
    [InlineData(null, null)]
    public void Colors_are_parsed_strictly(string? input, string? expected) =>
        Assert.Equal(expected, Rgb.Parse(input)?.ToString());

    [Theory]
    [InlineData(KeyChord.Control, 0x48u, true)]
    [InlineData(KeyChord.Alt | KeyChord.Shift, 0x70u, true)]
    [InlineData(0u, 0x48u, false)]
    [InlineData(KeyChord.Shift, 0x48u, false)]
    [InlineData(KeyChord.Control, 0x11u, false)]
    [InlineData(KeyChord.Control, 0xA2u, false)]
    [InlineData(KeyChord.Alt, 0x73u, false)]
    [InlineData(KeyChord.Control | 8u, 0x48u, false)]
    // заняты пресетом OBS
    [InlineData(KeyChord.Control | KeyChord.Alt, 0x30u, false)]
    [InlineData(KeyChord.Control | KeyChord.Alt, 0x35u, false)]
    [InlineData(KeyChord.Control | KeyChord.Alt, 0x36u, true)]
    public void Hotkey_needs_ctrl_or_alt_and_a_real_key(uint modifiers, uint key, bool valid) =>
        Assert.Equal(valid, new KeyChord(modifiers, key).IsValid);

    [Fact]
    public void Infinite_position_can_still_be_saved()
    {
        var loaded = LoadText("""{"Placement":{"Position":8,"X":1e400,"Y":-1e400}}""");
        Assert.Equal((0d, 0d), (loaded.Placement.X, loaded.Placement.Y));
        loaded.Save(SettingsFile);
    }

    [Fact]
    public void Viewer_settings_survive_restart()
    {
        var saved = new AppSettings
        {
            ShowTwitchViewers = true, ShowYouTubeViewers = true, ShowTikTokViewers = true, ShowViewerTotal = true,
            ViewersInColumn = true, ViewersFontSize = 26, ViewersPlacement = new Placement { Position = Position.BottomCenter },
        };
        saved.Save(SettingsFile);
        Assert.Equal(saved, AppSettings.Load(SettingsFile));
    }

    [Fact]
    public void Older_settings_put_counters_top_right_and_keep_them_off()
    {
        var loaded = LoadText("""{"Placement":{"Position":0},"ViewersPlacement":null,"ViewersFontSize":999}""");
        Assert.Equal((Position.TopRight, 40, false), (loaded.ViewersPlacement.Position, loaded.ViewersFontSize, loaded.ShowTwitchViewers));
    }

    [Fact]
    public void Obs_and_donation_settings_survive_restart()
    {
        var saved = new AppSettings
        {
            ObsEnabled = true, ObsPort = 4460, ObsPasswordProtected = "AQID", ObsMicrophone = "Микрофон", ObsInColumn = true,
            DonationTokenProtected = "BAUG", CheckUpdates = false,
        };
        saved.Save(SettingsFile);
        Assert.Equal(saved, AppSettings.Load(SettingsFile));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(70000)]
    public void Impossible_port_becomes_the_default(int port) =>
        Assert.Equal(4455, new AppSettings { ObsPort = port }.Normalized().ObsPort);

    [Fact]
    public void Reset_of_the_look_keeps_channels_secrets_filters_and_profiles()
    {
        var mine = new AppSettings
        {
            TwitchChannel = "me", YouTubeKeyProtected = "AQID", DonationTokenProtected = "BAUG", ObsPasswordProtected = "CQ",
            ObsEnabled = true, BlockedWords = "спам", IgnoredNames = "bot", FontSize = 30, Opacity = 40,
            MoveKey = new(KeyChord.Control | KeyChord.Shift, 0x79), ShowSettingsOnStart = false,
        }.AddProfile("CS2");
        var reset = mine.ResetLook();
        Assert.Equal(("me", "AQID", "BAUG", "CQ", true, "спам", "bot"),
            (reset.TwitchChannel, reset.YouTubeKeyProtected, reset.DonationTokenProtected, reset.ObsPasswordProtected,
             reset.ObsEnabled, reset.BlockedWords, reset.IgnoredNames));
        Assert.Equal(mine.Profiles, reset.Profiles);
        Assert.Equal((15, 75), (reset.FontSize, reset.Opacity));
        Assert.Equal((mine.MoveKey, false), (reset.MoveKey, reset.ShowSettingsOnStart));
    }

    [Fact]
    public void Broken_file_is_kept_aside_before_it_can_be_overwritten()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
        File.WriteAllText(SettingsFile, "{broken but had a token");
        var (settings, canSave) = AppSettings.LoadSafely(SettingsFile);
        Assert.Equal((new AppSettings(), true), (settings, canSave));
        Assert.Equal("{broken but had a token", File.ReadAllText(SettingsFile + ".broken"));
    }

    [Fact]
    public void Unreadable_file_turns_saving_off()
    {
        Directory.CreateDirectory(SettingsFile);
        Assert.False(AppSettings.LoadSafely(SettingsFile).CanSave);
    }

    [Fact]
    public void Missing_file_allows_saving() => Assert.True(AppSettings.LoadSafely(SettingsFile).CanSave);

    [Fact]
    public void Two_actions_cannot_share_a_hotkey()
    {
        var chord = new KeyChord(KeyChord.Control, 0x48);
        var settings = new AppSettings { MoveKey = chord, HideKey = chord }.Normalized();
        Assert.NotEqual(settings.MoveKey, settings.HideKey);
    }

    [Fact]
    public void Transparent_color_never_matches_a_used_color()
    {
        var first = new AppSettings().TransparentColor;
        var settings = new AppSettings { TextColor = first.ToString() };
        Assert.NotEqual(first, settings.TransparentColor);

        var both = settings with { BackgroundColor = settings.TransparentColor.ToString() };
        Assert.DoesNotContain(both.TransparentColor.ToString(), new[] { both.TextColor, both.BackgroundColor });
    }

    [Fact]
    public void Chat_connection_settings_survive_restart()
    {
        var saved = new AppSettings
        {
            TwitchChannel = "channel", YouTubeLink = "dQw4w9WgXcQ", YouTubeKeyProtected = "AQID", IgnoredNames = "nightbot",
            YouTubeIntervalSeconds = 30, YouTubeTermsAccepted = true, TikTokEnabled = true, EmoteImages = false,
        };
        saved.Save(SettingsFile);
        Assert.Equal(saved, AppSettings.Load(SettingsFile));
    }

    [Theory]
    [InlineData(1, 5)]
    [InlineData(600, 60)]
    public void YouTube_interval_cannot_burn_quota(int saved, int expected) =>
        Assert.Equal(expected, new AppSettings { YouTubeIntervalSeconds = saved }.Normalized().YouTubeIntervalSeconds);

    [Fact]
    public void Settings_of_0_2_0_get_empty_connections()
    {
        var loaded = LoadText("""{"Placement":{"Position":2},"FontSize":22,"TwitchChannel":null}""");
        Assert.Equal((22, "", ""), (loaded.FontSize, loaded.TwitchChannel, loaded.YouTubeKeyProtected));
    }
}
