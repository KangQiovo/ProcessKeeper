using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace ProcessKeeper.App;

/// <summary>A decoded avatar has a layout footprint; pending and failed requests do not.</summary>
public sealed class AuthorAvatarView : UserControl, IDisposable
{
    private readonly Ellipse _avatar = new() { Width = 64, Height = 64, HorizontalAlignment = HorizontalAlignment.Left };
    private Task<byte[]?>? _source;
    private CancellationTokenSource? _viewLoad;
    private bool _disposed;

    public AuthorAvatarView()
    {
        Width = Height = 64;
        HorizontalAlignment = HorizontalAlignment.Left;
        HorizontalContentAlignment = HorizontalAlignment.Left;
        Visibility = Visibility.Collapsed;
        IsHitTestVisible = false;
        AutomationProperties.SetName(this, "GitHub | KangQi");
        Content = _avatar;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public AuthorAvatarView(Task<byte[]?> source) : this() => Start(source);

    /// <summary>The application owns this shared task; unloading a view never cancels it.</summary>
    public void Start(Task<byte[]?> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (_disposed || _source is not null) return;
        _source = source;
        if (IsLoaded) BeginLoad();
    }

    private void OnLoaded(object sender, RoutedEventArgs args) => BeginLoad();

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        CancelViewLoad();
        HideAvatar();
    }

    private void BeginLoad()
    {
        if (_disposed || _source is null || _viewLoad is not null) return;
        _viewLoad = new CancellationTokenSource();
        _ = ShowAsync(_source, _viewLoad.Token);
    }

    private async Task ShowAsync(Task<byte[]?> source, CancellationToken token)
    {
        try
        {
            var bytes = await source.WaitAsync(token);
            if (!CanDisplay(token) || bytes is null || bytes.Length is 0 or > 256 * 1024) return;
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync().AsTask(token);
            }
            stream.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(token);
            if (decoder.PixelWidth is 0 or > 1024 || decoder.PixelHeight is 0 or > 1024) return;
            // Bound both edges. A single decode dimension can round a 1 x 1024 image's
            // other edge to zero; keep at least one decoded pixel without upscaling.
            var scale = Math.Min(1d, 128d / Math.Max(decoder.PixelWidth, decoder.PixelHeight));
            var bitmap = new BitmapImage
            {
                DecodePixelWidth = Math.Max(1, (int)Math.Round(decoder.PixelWidth * scale)),
                DecodePixelHeight = Math.Max(1, (int)Math.Round(decoder.PixelHeight * scale))
            };
            stream.Seek(0);
            await bitmap.SetSourceAsync(stream).AsTask(token);
            if (!CanDisplay(token) || bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0) return;
            _avatar.Fill = new ImageBrush { ImageSource = bitmap, Stretch = Stretch.UniformToFill };
            Visibility = Visibility.Visible;
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            // Image failure is optional content, never an unhandled UI exception.
            if (CanDisplay(token)) HideAvatar();
        }
    }

    private bool CanDisplay(CancellationToken token) => !_disposed && IsLoaded && !token.IsCancellationRequested;

    private void HideAvatar()
    {
        Visibility = Visibility.Collapsed;
        _avatar.Fill = null;
    }

    private void CancelViewLoad()
    {
        _viewLoad?.Cancel();
        _viewLoad?.Dispose();
        _viewLoad = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
        CancelViewLoad();
        _source = null;
        HideAvatar();
    }
}
