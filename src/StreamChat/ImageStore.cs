using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using StreamChat.Core.Emotes;

namespace StreamChat;

/// <summary>Картинки эмодзи и эмоутов. Только поток интерфейса, только память.</summary>
internal sealed class ImageStore : IDisposable
{
    private const int MaxCached = 800;
    private const int MaxLoading = 100;
    private const int MaxSourceSide = 1024;
    private const int MaxHeight = 72;
    private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(2);

    private readonly Dispatcher _ui;
    private readonly EmoteImages _downloads = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<string, LinkedListNode<(string Key, ImageSource Image)>> _cache = [];
    private readonly LinkedList<(string Key, ImageSource Image)> _recent = new();
    private readonly HashSet<string> _loading = [];
    private readonly Dictionary<string, DateTime> _failed = [];

    /// <summary>Докачалась картинка, ленту надо перерисовать.</summary>
    public event Action? Loaded;

    public ImageStore(Dispatcher ui) => _ui = ui;

    /// <returns>null, пока картинки нет: тогда показывается слово.</returns>
    public ImageSource? Get(string reference)
    {
        if (_cache.TryGetValue(reference, out var node))
        {
            _recent.Remove(node);
            _recent.AddFirst(node);
            return node.Value.Image;
        }

        if (reference.StartsWith(Emoji.Scheme, StringComparison.Ordinal))
        {
            var image = Emoji.Png(reference[Emoji.Scheme.Length..]) is { } png ? Decode(png) : null;
            if (image is not null) Remember(reference, image);
            return image;
        }

        Download(reference);
        return null;
    }

    public void Dispose()
    {
        _stop.Cancel();
        _downloads.Dispose();
    }

    private void Download(string url)
    {
        if (_stop.IsCancellationRequested || _loading.Count >= MaxLoading) return;
        if (_failed.TryGetValue(url, out var failed) && DateTime.UtcNow - failed < RetryAfter) return;
        if (!_loading.Add(url)) return;

        var cancel = _stop.Token;
        _ = Task.Run(async () =>
        {
            byte[]? bytes = null;
            try
            {
                bytes = await _downloads.DownloadAsync(url, cancel).ConfigureAwait(false);
            }
            finally
            {
                _ = _ui.BeginInvoke(() => Finish(url, bytes));
            }
        });
    }

    private void Finish(string url, byte[]? bytes)
    {
        _loading.Remove(url);
        if (_stop.IsCancellationRequested) return;

        if (bytes is not null && Decode(bytes) is { } image)
        {
            _failed.Remove(url);
            Remember(url, image);
            Loaded?.Invoke();
            return;
        }

        if (_failed.Count >= MaxCached) _failed.Clear();
        _failed[url] = DateTime.UtcNow;
    }

    private void Remember(string key, ImageSource image)
    {
        _cache[key] = _recent.AddFirst((key, image));
        if (_cache.Count <= MaxCached) return;
        _cache.Remove(_recent.Last!.Value.Key);
        _recent.RemoveLast();
    }

    private static BitmapImage? Decode(byte[] bytes)
    {
        try
        {
            int height;
            using (var header = new MemoryStream(bytes))
            {
                // сначала размер: крошечный файл может заявить 65535×65535
                var frame = BitmapDecoder.Create(header,
                    BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None).Frames[0];
                if (frame.PixelWidth is < 1 or > MaxSourceSide || frame.PixelHeight is < 1 or > MaxSourceSide) return null;
                height = frame.PixelHeight;
            }

            using var stream = new MemoryStream(bytes);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            if (height > MaxHeight) image.DecodePixelHeight = MaxHeight;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception)
        {
            // чужой файл: у декодеров Windows не меньше шести видов исключений, итог один, показываем слово
            return null;
        }
    }
}
