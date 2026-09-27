using System.Drawing;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
namespace ProcessKeeper.App;
internal sealed class LegacyIconCache
{
    private const int Capacity = 256;
    private readonly SemaphoreSlim _gate = new(2);
    private readonly Dictionary<string, ImageSource?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _order = new();
    private readonly object _sync = new();
    internal async Task<ImageSource?> Get(string path, CancellationToken token)
    {
        if (string.IsNullOrEmpty(path)) return null;
        lock (_sync) if (_cache.TryGetValue(path, out var known)) return known;
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            lock (_sync) if (_cache.TryGetValue(path, out var known)) return known;
            var image = await Task.Run(() =>
            {
                try
                {
                    if (!File.Exists(path)) return null;
                    using var icon = Icon.ExtractAssociatedIcon(path);
                    if (icon is null) return null;
                    var bitmap = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(32, 32));
                    bitmap.Freeze(); return (ImageSource)bitmap;
                }
                catch (Exception ex) when (ex is IOException or ArgumentException or System.Runtime.InteropServices.ExternalException or UnauthorizedAccessException) { return null; }
            }, token).ConfigureAwait(false);
            lock (_sync)
            {
                if (!_cache.ContainsKey(path)) { _cache[path] = image; _order.Enqueue(path); }
                while (_order.Count > Capacity) _cache.Remove(_order.Dequeue());
            }
            return image;
        }
        finally { _gate.Release(); }
    }
}
