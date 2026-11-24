using System.IO.Compression;
using System.Text;
using StreamChat.Core.Updates;

namespace StreamChat.Tests.Rules;

public class UpdateInstallerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory().FullName;
    private string Exe => Path.Combine(_dir, "StreamChat.exe");
    private string P(string name) => Path.Combine(_dir, name);

    public void Dispose() => Directory.Delete(_dir, true);

    private string MakeZip(params string[] entries)
    {
        var zip = P("z.zip");
        using var fs = File.Create(zip);
        using var a = new ZipArchive(fs, ZipArchiveMode.Create);
        foreach (var name in entries)
        {
            using var w = a.CreateEntry(name).Open();
            w.Write(Encoding.UTF8.GetBytes("zip:" + name));
        }
        return zip;
    }

    [Fact]
    public void Extract_writes_new_exe_only()
    {
        File.WriteAllText(P("LICENSES.txt"), "old");
        var zip = MakeZip("StreamChat/StreamChat.exe", "StreamChat/ИНСТРУКЦИЯ.txt", "StreamChat/LICENSES.txt");

        var path = UpdateInstaller.Extract(zip, Exe);

        Assert.Equal(P("StreamChat.new.exe"), path);
        Assert.Equal("zip:StreamChat/StreamChat.exe", File.ReadAllText(path));
        Assert.False(File.Exists(P("ИНСТРУКЦИЯ.txt")));
        Assert.Equal("zip:StreamChat/LICENSES.txt", File.ReadAllText(P("LICENSES.txt")));
    }

    [Fact]
    public void Extract_rejects_zip_without_exe()
    {
        Assert.Throws<InvalidDataException>(() => UpdateInstaller.Extract(MakeZip("StreamChat/other.exe"), Exe));
        Assert.False(File.Exists(P("StreamChat.new.exe")));
    }

    [Fact]
    public void Extract_rejects_two_exes()
    {
        Assert.Throws<InvalidDataException>(() =>
            UpdateInstaller.Extract(MakeZip("StreamChat/StreamChat.exe", "StreamChat/StreamChat.exe"), Exe));
        Assert.False(File.Exists(P("StreamChat.new.exe")));
    }

    [Fact]
    public void Swap_replaces_and_keeps_old()
    {
        File.WriteAllText(Exe, "old");
        File.WriteAllText(P("StreamChat.new.exe"), "new");
        File.WriteAllText(P("StreamChat.old.exe"), "older");

        UpdateInstaller.Swap(Exe);

        Assert.Equal("new", File.ReadAllText(Exe));
        Assert.Equal("old", File.ReadAllText(P("StreamChat.old.exe")));
        Assert.False(File.Exists(P("StreamChat.new.exe")));
    }

    [Fact]
    public void Swap_rolls_back_when_old_is_locked()
    {
        File.WriteAllText(Exe, "old");
        File.WriteAllText(P("StreamChat.new.exe"), "new");
        Directory.CreateDirectory(P("StreamChat.old.exe")); // имитация блокировки

        Assert.ThrowsAny<IOException>(() => UpdateInstaller.Swap(Exe));

        Assert.Equal("old", File.ReadAllText(Exe));
        Assert.False(File.Exists(P("StreamChat.new.exe")));
    }

    [Fact]
    public void Swap_without_new_exe_keeps_exe()
    {
        File.WriteAllText(Exe, "old");
        Assert.ThrowsAny<IOException>(() => UpdateInstaller.Swap(Exe));
        Assert.Equal("old", File.ReadAllText(Exe));
    }

    [Fact]
    public void Restore_brings_old_back()
    {
        File.WriteAllText(Exe, "new");
        File.WriteAllText(P("StreamChat.old.exe"), "old");
        UpdateInstaller.Restore(Exe);
        Assert.Equal("old", File.ReadAllText(Exe));
        Assert.False(File.Exists(P("StreamChat.old.exe")));
    }

    [Fact]
    public void Restore_without_old_keeps_exe()
    {
        File.WriteAllText(Exe, "new");
        UpdateInstaller.Restore(Exe);
        Assert.Equal("new", File.ReadAllText(Exe));
    }

    [Fact]
    public void Cleanup_ignores_missing_files()
    {
        UpdateInstaller.Cleanup(Exe);
        File.WriteAllText(Exe, "cur");
        File.WriteAllText(P("StreamChat.old.exe"), "x");
        File.WriteAllText(P("StreamChat.download.zip"), "x");
        UpdateInstaller.Cleanup(Exe);
        Assert.False(File.Exists(P("StreamChat.old.exe")));
        Assert.False(File.Exists(P("StreamChat.download.zip")));
    }

    [Fact]
    public void Cleanup_keeps_old_when_exe_is_missing()
    {
        File.WriteAllText(P("StreamChat.old.exe"), "x");
        File.WriteAllText(P("StreamChat.new.exe"), "y");
        UpdateInstaller.Cleanup(Exe);
        Assert.True(File.Exists(P("StreamChat.old.exe")));
        Assert.False(File.Exists(P("StreamChat.new.exe")));
    }

    [Theory]
    [InlineData("StreamChat.new.exe")]
    [InlineData("streamchat.OLD.exe")]
    public void Side_names_are_rejected(string name)
    {
        var exe = P(name);
        File.WriteAllText(exe, "cur");
        Assert.Throws<IOException>(() => UpdateInstaller.Extract(MakeZip("StreamChat/StreamChat.exe"), exe));
        Assert.Throws<IOException>(() => UpdateInstaller.Swap(exe));
        Assert.Equal("cur", File.ReadAllText(exe));
    }

    [Fact]
    public void Rollback_exception_is_an_IOException_with_instruction()
    {
        var e = new UpdateRollbackException("My.exe", new IOException());
        Assert.IsAssignableFrom<IOException>(e);
        Assert.Equal("Программа сохранена как StreamChat.old.exe. Переименуйте её обратно в My.exe.", e.Message);
    }
}
