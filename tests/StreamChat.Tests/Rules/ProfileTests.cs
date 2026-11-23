using StreamChat.Core;

namespace StreamChat.Tests.Rules;

public sealed class ProfileTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("streamchat-test-").FullName;
    private string SettingsFile => Path.Combine(_folder, "settings.json");

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static AppSettings At(Position position, int size = 15) =>
        new() { Placement = new Placement { Position = position }, FontSize = size };

    [Fact]
    public void Each_game_keeps_its_own_layout()
    {
        var settings = At(Position.TopLeft, 14).AddProfile("CS2");
        settings = settings with { Placement = new Placement { Position = Position.BottomRight }, FontSize = 22 };
        settings = settings.AddProfile("Dota");
        settings = settings with { Placement = new Placement { Position = Position.TopCenter }, FontSize = 30 };

        // «Создать» берёт экран для новой игры и не трогает прошлую
        var first = settings.SwitchProfile("CS2");
        Assert.Equal((Position.TopLeft, 14), (first.Placement.Position, first.FontSize));

        var second = first.SwitchProfile("Dota");
        Assert.Equal((Position.TopCenter, 30), (second.Placement.Position, second.FontSize));
    }

    [Fact]
    public void Creating_a_profile_does_not_overwrite_the_active_one()
    {
        var settings = At(Position.TopLeft, 14).AddProfile("CS2");
        settings = (settings with { Placement = new Placement { Position = Position.BottomRight } }).AddProfile("Dota");
        Assert.Equal(Position.TopLeft, settings.Profiles.Single(p => p.Name == "CS2").Placement.Position);
    }

    [Fact]
    public void New_profile_starts_from_the_current_layout()
    {
        var settings = At(Position.MiddleRight).AddProfile("Rust");
        Assert.Equal("Rust", settings.ActiveProfile);
        Assert.Equal(Position.MiddleRight, Assert.Single(settings.Profiles).Placement.Position);
    }

    [Fact]
    public void Switch_does_not_touch_channels() =>
        Assert.Equal("me", (new AppSettings { TwitchChannel = "me" }.AddProfile("A").AddProfile("B").SwitchProfile("A")).TwitchChannel);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("cs2")]
    public void Empty_or_taken_name_changes_nothing(string name)
    {
        var settings = At(Position.TopLeft).AddProfile("CS2");
        Assert.Equal(settings, settings.AddProfile(name));
    }

    [Fact]
    public void Unknown_profile_changes_nothing()
    {
        var settings = At(Position.TopLeft).AddProfile("A");
        Assert.Equal(settings, settings.SwitchProfile("no"));
    }

    [Fact]
    public void Deleting_the_active_profile_keeps_the_screen_as_is()
    {
        var settings = At(Position.BottomLeft).AddProfile("A").DeleteProfile("A");
        Assert.Empty(settings.Profiles);
        Assert.Equal(("", Position.BottomLeft), (settings.ActiveProfile, settings.Placement.Position));
    }

    [Fact]
    public void Profiles_survive_restart()
    {
        var saved = At(Position.TopRight).AddProfile("A").AddProfile("B");
        saved.Save(SettingsFile);
        Assert.Equal(saved, AppSettings.Load(SettingsFile));
    }

    [Fact]
    public void Broken_profiles_are_dropped()
    {
        File.WriteAllText(SettingsFile, """{"Placement":{},"Profiles":[null,{"Name":""},{"Name":"A","FontSize":999},{"Name":"a"}],"ActiveProfile":"ghost"}""");
        var loaded = AppSettings.Load(SettingsFile);
        Assert.Equal(("A", 40, ""), (Assert.Single(loaded.Profiles).Name, loaded.Profiles[0].FontSize, loaded.ActiveProfile));
    }

    [Fact]
    public void Number_of_profiles_is_limited()
    {
        var settings = new AppSettings();
        for (var i = 0; i < 40; i++) settings = settings.AddProfile($"game{i}");
        Assert.Equal(AppSettings.MaxProfiles, settings.Profiles.Count);
    }
}
