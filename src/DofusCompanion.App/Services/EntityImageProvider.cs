using System.Windows.Media.Imaging;
using DofusCompanion.Domain;
using DofusCompanion.Infrastructure;

namespace DofusCompanion.App.Services;

/// <summary>
/// Loads entity pictures for display: decoded at the size they are shown (small thumbnails stay cheap),
/// frozen for cross-thread use, and kept in a bounded cache. A missing or unreadable picture simply yields null.
/// </summary>
public sealed class EntityImageProvider(EntityImageStore store)
{
    public const int ThumbnailSize = 40;
    public const int LargeSize = 220;
    private const int CacheCapacity = 400;

    /// <summary>Set at startup so lightweight display records (search results) can ask for thumbnails.</summary>
    public static EntityImageProvider? Current { get; set; }

    private readonly Dictionary<(string Path, int Size, DateTime Stamp), BitmapSource> _cache = [];
    private readonly LinkedList<(string Path, int Size, DateTime Stamp)> _order = new();
    private readonly Lock _lock = new();

    public EntityImageStore Store => store;

    /// <summary>Raised after a picture was added or removed, so open views can refresh.</summary>
    public event EventHandler<string>? Changed;

    public BitmapSource? Get(GameEntity entity, int size)
    {
        var path = store.Resolve(entity);
        if (path is null) return null;
        DateTime stamp;
        try { stamp = File.GetLastWriteTimeUtc(path); }
        catch (IOException) { return null; }
        var key = (path, size, stamp);

        lock (_lock)
        {
            if (_cache.TryGetValue(key, out var hit)) return hit;
        }

        var image = Load(path, size);
        if (image is null) return null;
        lock (_lock)
        {
            _cache[key] = image;
            _order.AddLast(key);
            while (_order.Count > CacheCapacity)
            {
                _cache.Remove(_order.First!.Value);
                _order.RemoveFirst();
            }
        }

        return image;
    }

    public void NotifyChanged(string entityId) => Changed?.Invoke(this, entityId);

    private static BitmapSource? Load(string path, int size)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad; // read now, do not keep the file locked
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bmp.DecodePixelWidth = size * 2; // crisp on 200 % DPI screens
            bmp.UriSource = new Uri(path, UriKind.Absolute);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException or ArgumentException
                                       or System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            return null; // corrupt or unsupported picture: show the placeholder instead
        }
    }
}
