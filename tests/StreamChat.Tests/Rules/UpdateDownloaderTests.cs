using System.Net;
using System.Security.Cryptography;
using StreamChat.Core.Updates;

namespace StreamChat.Tests.Rules;

public class UpdateDownloaderTests : IDisposable
{
    private const string Version = "1.2.3";
    private static readonly byte[] Zip = [.. Enumerable.Range(0, 5000).Select(i => (byte)i)];
    private static readonly byte[] Sig = [1, 2, 3];

    private readonly LocalServer _server = new();
    private readonly string _dir = Directory.CreateTempSubdirectory("dl").FullName;

    public void Dispose()
    {
        _server.Dispose();
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(_dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Directory.Delete(_dir, true);
    }

    private Uri Base => new($"http://localhost:{_server.Port}/");
    private string ZipUrl => $"v{Version}/StreamChat-{Version}.zip";

    /// <summary>Отвечает на запросы по таблице путей, пока не закроют сервер.</summary>
    private void Serve(Func<string, HttpListenerContext, Task<bool>> handler) => _ = Task.Run(async () =>
    {
        try
        {
            while (true)
            {
                var context = await _server.Next();
                var path = context.Request.Url!.AbsolutePath.TrimStart('/');
                if (!await handler(path, context))
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                }
            }
        }
        catch { }
    });

    private static async Task<bool> Body(HttpListenerContext c, byte[] data)
    {
        c.Response.ContentLength64 = data.Length;
        await c.Response.OutputStream.WriteAsync(data);
        c.Response.Close();
        return true;
    }

    private UpdateDownloader Make(Func<string, string, byte[], bool> verify, long max = 1024 * 1024) =>
        new(Base, verify, max);

    private Task<DownloadResult> Run(UpdateDownloader d) =>
        d.DownloadAsync(Version, _dir, new Progress<int>(), CancellationToken.None);

    private bool NoTemp() => !File.Exists(Path.Combine(_dir, "StreamChat.download.zip"))
        && !File.Exists(Path.Combine(_dir, "StreamChat.download.zip.sig"));

    [Fact]
    public async Task Good_release_is_accepted()
    {
        Serve((p, c) => p == ZipUrl ? Body(c, Zip) : p == ZipUrl + ".sig" ? Body(c, Sig) : Task.FromResult(false));
        string? seenHash = null;
        using var d = Make((v, h, s) => { seenHash = h; return v == Version && s.SequenceEqual(Sig); });

        var result = await Run(d);

        Assert.Equal(DownloadError.None, result.Error);
        Assert.Equal(Zip, await File.ReadAllBytesAsync(result.ZipPath));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Zip)), seenHash);
    }

    [Fact]
    public async Task Missing_signature_is_reported()
    {
        Serve((p, c) => p == ZipUrl ? Body(c, Zip) : Task.FromResult(false));
        using var d = Make((_, _, _) => true);

        Assert.Equal(DownloadError.NoSignature, (await Run(d)).Error);
        Assert.True(NoTemp());
    }

    [Fact]
    public async Task Wrong_signature_is_rejected_and_deleted()
    {
        Serve((p, c) => p == ZipUrl ? Body(c, Zip) : p == ZipUrl + ".sig" ? Body(c, Sig) : Task.FromResult(false));
        using var d = Make((_, _, _) => false);

        Assert.Equal(DownloadError.BadSignature, (await Run(d)).Error);
        Assert.True(NoTemp());
    }

    [Fact]
    public async Task Oversized_zip_is_cut()
    {
        Serve(async (p, c) =>
        {
            if (p == ZipUrl + ".sig") return await Body(c, Sig);
            if (p != ZipUrl) return false;
            c.Response.SendChunked = true; // без Content-Length
            try
            {
                for (var i = 0; i < 100; i++) await c.Response.OutputStream.WriteAsync(new byte[1000]);
                c.Response.Close();
            }
            catch { }
            return true;
        });
        using var d = Make((_, _, _) => true, max: 10_000);

        Assert.Equal(DownloadError.TooLarge, (await Run(d)).Error);
        Assert.True(NoTemp());
    }

    [Fact]
    public async Task Oversized_signature_is_too_large()
    {
        Serve((p, c) => p == ZipUrl + ".sig" ? Body(c, new byte[5000]) : Task.FromResult(false));
        using var d = Make((_, _, _) => true);

        Assert.Equal(DownloadError.TooLarge, (await Run(d)).Error);
        Assert.True(NoTemp());
    }

    [Fact]
    public async Task Read_only_folder_is_no_write_access()
    {
        if (!OperatingSystem.IsLinux() || Environment.UserName == "root") return;
        File.SetUnixFileMode(_dir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        using var d = Make((_, _, _) => true);

        Assert.Equal(DownloadError.NoWriteAccess, (await Run(d)).Error);
    }

    [Fact]
    public async Task Redirect_is_followed()
    {
        Serve((p, c) =>
        {
            if (p == ZipUrl)
            {
                c.Response.StatusCode = 302;
                c.Response.RedirectLocation = $"http://localhost:{_server.Port}/cdn/file";
                c.Response.Close();
                return Task.FromResult(true);
            }
            return p == "cdn/file" ? Body(c, Zip) : p == ZipUrl + ".sig" ? Body(c, Sig) : Task.FromResult(false);
        });
        using var d = Make((_, _, _) => true);

        var result = await Run(d);

        Assert.Equal(DownloadError.None, result.Error);
        Assert.Equal(Zip, await File.ReadAllBytesAsync(result.ZipPath));
    }

    [Fact]
    public async Task Bad_version_is_rejected_before_network()
    {
        using var d = Make((_, _, _) => true);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            d.DownloadAsync("1.2.3/../x", _dir, new Progress<int>(), CancellationToken.None));
    }

    [Theory]
    [InlineData("1.2.3\n")]
    [InlineData("1.3.0-rc1")]
    public async Task Unusual_version_is_rejected(string version)
    {
        Serve((_, _) => Task.FromResult(false));
        using var d = Make((_, _, _) => true);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            d.DownloadAsync(version, _dir, new Progress<int>(), CancellationToken.None));
    }

    [Fact]
    public async Task Full_disk_is_write_failed()
    {
        // /dev/full на любую запись отвечает «нет места»: честный полный диск без заполнения настоящего
        if (!OperatingSystem.IsLinux() || !File.Exists("/dev/full")) return;
        File.CreateSymbolicLink(Path.Combine(_dir, "StreamChat.download.zip"), "/dev/full");
        Serve((p, c) => p == ZipUrl ? Body(c, Zip) : p == ZipUrl + ".sig" ? Body(c, Sig) : Task.FromResult(false));
        using var d = Make((_, _, _) => true);

        Assert.Equal(DownloadError.WriteFailed, (await Run(d)).Error);
        Assert.True(NoTemp());
    }

    [Fact]
    public async Task Cancel_propagates_and_cleans()
    {
        Serve(async (p, c) => { await Task.Delay(5000); return false; });
        using var d = Make((_, _, _) => true);
        using var cts = new CancellationTokenSource(200);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            d.DownloadAsync(Version, _dir, new Progress<int>(), cts.Token));
        Assert.True(NoTemp());
    }

    [Fact]
    public async Task Server_error_on_zip_is_network()
    {
        Serve((p, c) =>
        {
            if (p == ZipUrl) { c.Response.StatusCode = 500; c.Response.Close(); return Task.FromResult(true); }
            return p == ZipUrl + ".sig" ? Body(c, Sig) : Task.FromResult(false);
        });
        using var d = Make((_, _, _) => true);

        Assert.Equal(DownloadError.Network, (await Run(d)).Error);
        Assert.True(NoTemp());
    }

    [Fact]
    public async Task Server_error_on_signature_is_network()
    {
        Serve((p, c) => { c.Response.StatusCode = 500; c.Response.Close(); return Task.FromResult(true); });
        using var d = Make((_, _, _) => true);

        Assert.Equal(DownloadError.Network, (await Run(d)).Error);
    }

    [Fact]
    public async Task Declared_length_over_limit_is_refused_early()
    {
        Serve((p, c) => p == ZipUrl ? Body(c, Zip) : p == ZipUrl + ".sig" ? Body(c, Sig) : Task.FromResult(false));
        using var d = Make((_, _, _) => true, max: Zip.Length - 1);

        Assert.Equal(DownloadError.TooLarge, (await Run(d)).Error);
        Assert.True(NoTemp());
    }

    [Fact]
    public async Task Progress_is_monotonic_and_ends_at_100()
    {
        var big = new byte[500_000];
        Serve((p, c) => p == ZipUrl ? Body(c, big) : p == ZipUrl + ".sig" ? Body(c, Sig) : Task.FromResult(false));
        var seen = new List<int>();
        using var d = Make((_, _, _) => true);

        await d.DownloadAsync(Version, _dir, new SyncProgress(seen.Add), CancellationToken.None);

        Assert.Equal(100, seen[^1]);
        Assert.Equal(seen.Order().Distinct(), seen);
    }

    private sealed class SyncProgress(Action<int> add) : IProgress<int>
    {
        public void Report(int value) => add(value);
    }
}
