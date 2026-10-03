using System.Windows.Media;
using System.Windows.Media.Imaging;
using ProcessKeeper.Core;

namespace ProcessKeeper.App;

internal static class AuthorAvatarSource
{
    internal static Task<byte[]?> CreateAsync(CancellationToken cancellationToken) =>
        AuthorAvatarLoader.LoadAsync(cancellationToken, validateImage: DecodeValidatedAsync);

    private static async Task<bool> DecodeValidatedAsync(byte[] bytes, CancellationToken cancellationToken) =>
        await DecodeAsync(bytes, cancellationToken).ConfigureAwait(false) is not null;

    internal static Task<BitmapSource?> DecodeAsync(byte[]? bytes, CancellationToken cancellationToken) => Task.Run<BitmapSource?>(() =>
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (bytes is null || bytes.Length == 0 || bytes.Length > 256 * 1024) return null;
            using var dimensionsStream = new MemoryStream(bytes, false);
            var decoder = BitmapDecoder.Create(dimensionsStream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            if (decoder.Frames.Count != 1) return null;
            var frame = decoder.Frames[0];
            if (frame.PixelWidth < 1 || frame.PixelHeight < 1 || frame.PixelWidth > 1024 || frame.PixelHeight > 1024) return null;
            cancellationToken.ThrowIfCancellationRequested();
            using var stream = new MemoryStream(bytes, false);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            if (frame.PixelWidth >= frame.PixelHeight) image.DecodePixelWidth = Math.Min(128, frame.PixelWidth);
            else image.DecodePixelHeight = Math.Min(128, frame.PixelHeight);
            image.StreamSource = stream;
            image.EndInit();
            // Force pixel decoding before accepting a source; a valid header alone is insufficient.
            var stride = checked((image.PixelWidth * image.Format.BitsPerPixel + 7) / 8);
            image.CopyPixels(new byte[checked(stride * image.PixelHeight)], stride, 0);
            image.Freeze();
            cancellationToken.ThrowIfCancellationRequested();
            return image;
        }
        catch (Exception) { return null; }
    }, cancellationToken);
}
