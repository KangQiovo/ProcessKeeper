using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace ProcessKeeper.App;

/// <summary>
/// Resolves the executable's real Windows Shell icon. Create and call this service on
/// the owning WinUI thread; slow Shell/file work runs on background threads instead.
/// Successful and missing/inaccessible results have bounded LRU retention. The whole
/// extraction and decoding pipeline is limited, with explicit busy/retry admission.
/// </summary>
public sealed class ProcessIconService : IDisposable
{
    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread()
        ?? throw new InvalidOperationException("ProcessIconService must be created on the WinUI thread.");
    private readonly BoundedAsyncIconCache<ImageSource?> _cache;
    private Task<ImageSource?>? _fallback;

    public ProcessIconService()
    {
        _cache = new BoundedAsyncIconCache<ImageSource?>(capacity: 256, concurrency: 2, maximumQueued: 64,
            LoadAsync, action => _dispatcher.TryEnqueue(() => action()));
    }

    public void Dispose()
    {
        if (!_dispatcher.HasThreadAccess)
            throw new InvalidOperationException("Dispose ProcessIconService on its owning WinUI thread.");
        _cache.Dispose();
        _fallback = null;
    }

    /// <summary>
    /// Returns an executable's original icon, or the Windows generic application icon
    /// for an empty/inaccessible path. Null is only returned if Windows cannot supply
    /// or decode even its generic icon. The returned ImageSource belongs to this UI thread.
    /// A full pending queue throws IconRequestDeferredException: clear the row's request
    /// flag and retry when that row is visible on a later refresh. Disposed work is cancelled.
    /// </summary>
    public Task<ImageSource?> GetAsync(string executablePath)
    {
        if (!_dispatcher.HasThreadAccess)
            throw new InvalidOperationException("Call GetAsync from the owning WinUI thread.");

        var key = NormalizePath(executablePath);
        return _cache.GetAsync(key);
    }

    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        try
        {
            var value = path.Trim();
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
                value = value[1..^1];
            return Path.GetFullPath(value);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return string.Empty;
        }
    }

    private Task<ImageSource?> GetFallbackAsync(CancellationToken token) => _fallback ??= LoadBitmapAsync(null, token);

    private async Task<ImageSource?> LoadAsync(string path, CancellationToken token)
    {
        if (path.Length == 0) return await GetFallbackAsync(token);
        return await LoadBitmapAsync(path, token) ?? await GetFallbackAsync(token);
    }

    private async Task<ImageSource?> LoadBitmapAsync(string? path, CancellationToken token)
    {
        IconPixels? pixels = null;
        token.ThrowIfCancellationRequested();
        try
        {
            pixels = await Task.Run(() => ExtractPixels(path), token);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ExternalException)
        {
            // The process may have exited or the file may be protected/uninstalled.
            // Cache the eventual generic icon just as we cache a successful extraction.
        }
        token.ThrowIfCancellationRequested();

        if (pixels is not null)
        {
            try
            {
                if (!_dispatcher.HasThreadAccess)
                    throw new InvalidOperationException("WinUI icon decoding must resume on its owner thread.");
                using var stream = new InMemoryRandomAccessStream();
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
                token.ThrowIfCancellationRequested();
                encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                    (uint)pixels.Size, (uint)pixels.Size, 96, 96, pixels.Bytes);
                await encoder.FlushAsync();
                token.ThrowIfCancellationRequested();
                stream.Seek(0);
                var bitmap = new BitmapImage();
                await bitmap.SetSourceAsync(stream);
                token.ThrowIfCancellationRequested();
                return bitmap;
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or ExternalException)
            {
                // Fall back without letting a damaged executable's icon break the UI.
            }
        }
        return null;
    }

    private static IconPixels? ExtractPixels(string? path)
    {
        // SHGetFileInfo requires COM initialization. A thread already initialized in
        // another apartment is usable too, but its COM balance must remain untouched.
        var comResult = Native.CoInitializeEx(IntPtr.Zero, 0); // COINIT_MULTITHREADED
        IntPtr icon = IntPtr.Zero;
        try
        {
            if (path is not null)
            {
                if (!File.Exists(path)) return null;
                Native.SHGetFileInfo(path, 0, out var fileInfo,
                    (uint)Marshal.SizeOf<Native.ShellFileInfo>(), Native.ShgfiIcon);
                icon = fileInfo.Icon;
            }
            else
            {
                Native.SHGetFileInfo(".exe", Native.FileAttributeNormal, out var fileInfo,
                    (uint)Marshal.SizeOf<Native.ShellFileInfo>(), Native.ShgfiIcon | Native.ShgfiUseFileAttributes);
                icon = fileInfo.Icon;
                if (icon == IntPtr.Zero)
                {
                    // LoadIcon's system icon is shared: clone it so every nonzero icon
                    // owned by this method has exactly one matching DestroyIcon call.
                    var shared = Native.LoadIcon(IntPtr.Zero, new IntPtr(32512)); // IDI_APPLICATION
                    if (shared != IntPtr.Zero) icon = Native.CopyIcon(shared);
                }
            }
            return icon == IntPtr.Zero ? null : Rasterize(icon);
        }
        finally
        {
            if (icon != IntPtr.Zero) Native.DestroyIcon(icon);
            if (comResult >= 0) Native.CoUninitialize();
        }
    }

    private static IconPixels? Rasterize(IntPtr icon)
    {
        var size = Math.Clamp(Native.GetSystemMetrics(11), 32, 128); // SM_CXICON
        var dc = Native.CreateCompatibleDC(IntPtr.Zero);
        if (dc == IntPtr.Zero) return null;
        IntPtr dib = IntPtr.Zero;
        IntPtr previous = IntPtr.Zero;
        try
        {
            var info = new Native.BitmapInfo
            {
                Header = new Native.BitmapInfoHeader
                {
                    Size = (uint)Marshal.SizeOf<Native.BitmapInfoHeader>(),
                    Width = size,
                    Height = -size, // top-down BGRA
                    Planes = 1,
                    BitCount = 32,
                    SizeImage = (uint)(size * size * 4)
                }
            };
            dib = Native.CreateDIBSection(dc, ref info, 0, out var bits, IntPtr.Zero, 0);
            if (dib == IntPtr.Zero || bits == IntPtr.Zero) return null;
            previous = Native.SelectObject(dc, dib);
            if (previous == IntPtr.Zero || previous == new IntPtr(-1)) return null;

            // Composite onto black and white, then recover premultiplied color and
            // alpha. This preserves transparency for both modern alpha icons and
            // older mask-only icons, whose DIB alpha bytes are otherwise all zero.
            var black = new byte[size * size * 4];
            for (var i = 3; i < black.Length; i += 4) black[i] = 255;
            Marshal.Copy(black, 0, bits, black.Length);
            if (!Native.DrawIconEx(dc, 0, 0, icon, size, size, 0, IntPtr.Zero, 3)) return null;
            Native.GdiFlush();
            Marshal.Copy(bits, black, 0, black.Length);

            var white = new byte[black.Length];
            Array.Fill(white, (byte)255);
            Marshal.Copy(white, 0, bits, white.Length);
            if (!Native.DrawIconEx(dc, 0, 0, icon, size, size, 0, IntPtr.Zero, 3)) return null;
            Native.GdiFlush();
            Marshal.Copy(bits, white, 0, white.Length);

            for (var i = 0; i < black.Length; i += 4)
            {
                var difference = Math.Max(white[i] - black[i],
                    Math.Max(white[i + 1] - black[i + 1], white[i + 2] - black[i + 2]));
                var alpha = (byte)(255 - Math.Clamp(difference, 0, 255));
                black[i + 3] = alpha;
                // Rounding in GDI must not leave RGB greater than premultiplied alpha.
                black[i] = Math.Min(black[i], alpha);
                black[i + 1] = Math.Min(black[i + 1], alpha);
                black[i + 2] = Math.Min(black[i + 2], alpha);
            }
            return new IconPixels(size, black);
        }
        finally
        {
            if (previous != IntPtr.Zero && previous != new IntPtr(-1)) Native.SelectObject(dc, previous);
            if (dib != IntPtr.Zero) Native.DeleteObject(dib);
            Native.DeleteDC(dc);
        }
    }

    private sealed record IconPixels(int Size, byte[] Bytes);

    private static class Native
    {
        internal const uint ShgfiIcon = 0x100;
        internal const uint ShgfiUseFileAttributes = 0x10;
        internal const uint FileAttributeNormal = 0x80;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct ShellFileInfo
        {
            internal IntPtr Icon;
            internal int IconIndex;
            internal uint Attributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] internal string DisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] internal string TypeName;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct BitmapInfoHeader
        {
            internal uint Size;
            internal int Width;
            internal int Height;
            internal ushort Planes;
            internal ushort BitCount;
            internal uint Compression;
            internal uint SizeImage;
            internal int XPelsPerMeter;
            internal int YPelsPerMeter;
            internal uint ClrUsed;
            internal uint ClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct BitmapInfo
        {
            internal BitmapInfoHeader Header;
            internal uint Colors;
        }

        [DllImport("shell32.dll", EntryPoint = "SHGetFileInfoW", CharSet = CharSet.Unicode)]
        internal static extern IntPtr SHGetFileInfo(string path, uint attributes,
            out ShellFileInfo info, uint infoSize, uint flags);
        [DllImport("ole32.dll")] internal static extern int CoInitializeEx(IntPtr reserved, uint apartment);
        [DllImport("ole32.dll")] internal static extern void CoUninitialize();
        [DllImport("user32.dll", EntryPoint = "LoadIconW")] internal static extern IntPtr LoadIcon(IntPtr instance, IntPtr name);
        [DllImport("user32.dll")] internal static extern IntPtr CopyIcon(IntPtr icon);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool DestroyIcon(IntPtr icon);
        [DllImport("user32.dll")] internal static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DrawIconEx(IntPtr dc, int x, int y, IntPtr icon,
            int width, int height, uint step, IntPtr brush, uint flags);
        [DllImport("gdi32.dll")] internal static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] internal static extern IntPtr CreateDIBSection(IntPtr dc,
            ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);
        [DllImport("gdi32.dll")] internal static extern IntPtr SelectObject(IntPtr dc, IntPtr item);
        [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool DeleteObject(IntPtr item);
        [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GdiFlush();
    }
}
