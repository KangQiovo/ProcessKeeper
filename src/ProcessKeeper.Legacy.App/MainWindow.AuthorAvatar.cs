using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace ProcessKeeper.App;

public partial class MainWindow
{
    private BitmapSource? _authorAvatar;
    private Ellipse? _authorAvatarView;
    private Task _authorAvatarCompletion = Task.CompletedTask;

    private void BeginAuthorAvatar(Task<byte[]?>? source)
    {
        if (source is not null) _authorAvatarCompletion = ConsumeAuthorAvatarAsync(source);
    }

    private async Task ConsumeAuthorAvatarAsync(Task<byte[]?> source)
    {
        try
        {
            var bytes = await source.ConfigureAwait(false);
            if (_life.IsCancellationRequested) return;
            var bitmap = await AuthorAvatarSource.DecodeAsync(bytes, _life.Token).ConfigureAwait(false);
            if (bitmap is null || _life.IsCancellationRequested || Dispatcher.HasShutdownStarted) return;
            await Dispatcher.InvokeAsync(() =>
            {
                if (_closed || _life.IsCancellationRequested) return;
                _authorAvatar = bitmap;
                UpdateAuthorAvatarView();
            });
        }
        catch (Exception) { /* An optional image must not interrupt startup or leave a placeholder. */ }
    }

    private void AddAuthorAvatar(StackPanel about)
    {
        _authorAvatarView = new Ellipse
        {
            Width = 64, Height = 64, HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 0, 12), Visibility = Visibility.Collapsed,
            IsHitTestVisible = false
        };
        about.Children.Add(_authorAvatarView);
        UpdateAuthorAvatarView();
    }

    private void UpdateAuthorAvatarView()
    {
        if (_authorAvatarView is null) return;
        _authorAvatarView.Fill = _authorAvatar is null ? null : new ImageBrush(_authorAvatar) { Stretch = Stretch.UniformToFill };
        _authorAvatarView.Visibility = _authorAvatar is null ? Visibility.Collapsed : Visibility.Visible;
    }
}
