using ProcessKeeper.Core;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace ProcessKeeper.App;

/// <summary>Downloads and fully validates the shared avatar away from the UI thread.</summary>
internal static class AuthorAvatarSource
{
    internal static Task<byte[]?> CreateAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => AuthorAvatarLoader.LoadAsync(cancellationToken, ValidateImageAsync), cancellationToken);

    internal static async Task<bool> ValidateImageAsync(byte[] bytes, CancellationToken cancellationToken)
    {
        if (bytes.Length is 0 or > 256 * 1024) return false;
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync().AsTask(cancellationToken).ConfigureAwait(false);
            }
            stream.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(cancellationToken).ConfigureAwait(false);
            if (decoder.DecoderInformation.CodecId != BitmapDecoder.PngDecoderId &&
                decoder.DecoderInformation.CodecId != BitmapDecoder.JpegDecoderId) return false;
            if (decoder.PixelWidth is 0 or > 1024 || decoder.PixelHeight is 0 or > 1024) return false;
            // Reading metadata alone can accept a truncated or otherwise corrupt image.
            var pixels = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage)
                .AsTask(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return pixels.DetachPixelData().Length == checked((int)(decoder.PixelWidth * decoder.PixelHeight * 4));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            // A bad candidate must let the shared loader try its next source.
            return false;
        }
    }
}
