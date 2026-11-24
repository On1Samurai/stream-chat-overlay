using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace StreamChat.Core.Updates;

public enum DownloadError { None, NoWriteAccess, Network, TooLarge, NoSignature, BadSignature, WriteFailed }

public sealed record DownloadResult(DownloadError Error, string ZipPath = "");

/// <summary>Скачивает архив выпуска и подпись, проверяет подпись. Временные файлы при ошибке удаляет.</summary>
public sealed partial class UpdateDownloader : IDisposable
{
    private static readonly string DefaultBase = $"https://github.com/{UpdateRepository.Name}/releases/download/";
    private const int MaxSigBytes = 4 * 1024;
    private const long MaxZipBytes = 200L * 1024 * 1024;

    private readonly HttpClient _http;
    private readonly Uri _base;
    private readonly Func<string, string, byte[], bool> _verify;
    private readonly long _maxZip;

    public UpdateDownloader() : this(new Uri(DefaultBase), (v, h, s) => UpdateSignature.Verify(v, h, s), MaxZipBytes) { }

    public UpdateDownloader(Uri baseAddress, Func<string, string, byte[], bool> verify, long maxZipBytes)
    {
        _base = baseAddress;
        _verify = verify;
        _maxZip = maxZipBytes;
        _http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = true, UseCookies = false })
        {
            Timeout = TimeSpan.FromMinutes(10),
        };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("StreamChat", null));
    }

    [GeneratedRegex(@"\A\d{1,5}\.\d{1,5}\.\d{1,5}\z")]
    private static partial Regex VersionRule();

    public async Task<DownloadResult> DownloadAsync(string version, string exeFolder, IProgress<int> percent, CancellationToken cancel)
    {
        if (!VersionRule().IsMatch(version)) throw new ArgumentException("Недопустимая версия", nameof(version));
        var zipPath = Path.Combine(exeFolder, "StreamChat.download.zip");
        var sigPath = zipPath + ".sig";
        var zipUrl = new Uri(_base, $"v{version}/StreamChat-{version}.zip");
        try
        {
            // Файл закрывается внутри Work, до удаления: на Windows открытый файл не удалить.
            var error = await Work(version, zipPath, zipUrl, percent, cancel);
            return error == DownloadError.None ? new(error, zipPath) : Fail(error, zipPath, sigPath);
        }
        catch (Exception e) when (!cancel.IsCancellationRequested &&
                                  e is HttpRequestException or IOException or OperationCanceledException)
        {
            return Fail(DownloadError.Network, zipPath, sigPath);
        }
        catch
        {
            Delete(zipPath, sigPath);
            throw;
        }
    }

    private async Task<DownloadError> Work(string version, string zipPath, Uri zipUrl, IProgress<int> percent, CancellationToken cancel)
    {
        FileStream file;
        // без буфера: ошибка записи всплывает в WriteAsync, а не при закрытии файла
        try { file = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 0); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return DownloadError.NoWriteAccess; }

        await using (file)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            limit.CancelAfter(TimeSpan.FromMinutes(10));
            // Подпись держим в памяти: 4 КБ, отдельный файл не нужен.
            var sig = await ReadSignature(new Uri(zipUrl + ".sig"), limit.Token);
            if (sig is null) return DownloadError.NoSignature;
            if (sig.Length > MaxSigBytes) return DownloadError.TooLarge;

            var (hash, error) = await ReadZip(zipUrl, file, percent, limit.Token);
            if (hash is null) return error;
            await file.FlushAsync(cancel);
            return _verify(version, hash, sig) ? DownloadError.None : DownloadError.BadSignature;
        }
    }

    /// <summary>null — подписи нет (404); сверх лимита вернёт массив длиннее лимита.</summary>
    private async Task<byte[]?> ReadSignature(Uri url, CancellationToken cancel)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancel);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancel);
        var buffer = new byte[MaxSigBytes + 1];
        var total = 0;
        int read;
        while (total < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(total), cancel)) > 0) total += read;
        return buffer[..total];
    }

    /// <summary>Пишет архив в файл и возвращает SHA-256 строчными; без хэша — причина отказа.</summary>
    private async Task<(string? Hash, DownloadError Error)> ReadZip(Uri url, FileStream file, IProgress<int> percent, CancellationToken cancel)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancel);
        response.EnsureSuccessStatusCode();
        var length = response.Content.Headers.ContentLength;
        if (length > _maxZip) return (null, DownloadError.TooLarge);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var stream = await response.Content.ReadAsStreamAsync(cancel);
        var buffer = new byte[81920];
        long total = 0;
        var shown = -1;
        int read;
        while ((read = await stream.ReadAsync(buffer, cancel)) > 0)
        {
            total += read;
            if (total > _maxZip) return (null, DownloadError.TooLarge);
            sha.AppendData(buffer, 0, read);
            // ошибка записи (нет места) — не сеть: «проверьте интернет» тут не поможет
            try { await file.WriteAsync(buffer.AsMemory(0, read), cancel); }
            catch (IOException) { return (null, DownloadError.WriteFailed); }
            if (length > 0 && (int)(total * 100 / length.Value) is var now && now != shown) percent.Report(shown = now);
        }
        return (Convert.ToHexStringLower(sha.GetHashAndReset()), DownloadError.None);
    }

    private static DownloadResult Fail(DownloadError error, string zipPath, string sigPath)
    {
        Delete(zipPath, sigPath);
        return new(error);
    }

    private static void Delete(params string[] paths)
    {
        foreach (var path in paths)
        {
            try { File.Delete(path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    public void Dispose() => _http.Dispose();
}
