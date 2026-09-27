using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using ProcessKeeper.Core;
using Windows.Storage.Streams;

namespace ProcessKeeper.App;

/// <summary>Shows decoded frames of one existing headless page; never navigates or restarts it.</summary>
public sealed class BrowserLiveWindow : Window
{
    private readonly Image _image = new() { Stretch = Stretch.Uniform };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly Grid _root = new() { Padding = new Thickness(16), RowSpacing = 10 };
    private readonly CancellationTokenSource _lifetime = new();
    private ChromiumLiveSession? _session;
    private bool _closed;
    private bool _paused;

    public BrowserLiveWindow(string pageTitle, ElementTheme theme)
    {
        Title = L.T("无头页面实时画面 | Process Keeper");
        _root.RequestedTheme = theme;
        _root.Background = (Brush)Application.Current.Resources["ApplicationPageBackgroundThemeBrush"];
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var top = new Grid { ColumnSpacing = 12 };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.Children.Add(new TextBlock { Text = pageTitle, FontSize = 18, TextTrimming = TextTrimming.CharacterEllipsis });
        var pause = new Button { Content = L.T("暂停画面") };
        pause.Click += (_, _) => { _paused = !_paused; pause.Content = _paused ? L.T("继续画面") : L.T("暂停画面");
            _status.Text = _paused ? L.T("画面已暂停 | 原浏览器仍在运行") : L.T("正在更新画面…"); };
        Grid.SetColumn(pause, 1); top.Children.Add(pause);
        _root.RowDefinitions.Insert(0, new RowDefinition { Height = GridLength.Auto });
        var riskNotice = new InfoBar { IsClosable = false, Severity = InfoBarSeverity.Error };
        _root.Children.Add(riskNotice); RiskModeNotice.Attach(this, riskNotice);
        Grid.SetRow(top, 1); _root.Children.Add(top); Grid.SetRow(_image, 2); _root.Children.Add(_image);
        Grid.SetRow(_status, 3); _root.Children.Add(_status);
        _status.Text = L.T("正在读取真实页面图像…");
        Content = _root;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1120, 760));
        WindowSizePolicy.Attach(this);
        Closed += (_, _) => { _closed = true; _lifetime.Cancel(); _image.Source = null; };
    }

    public async Task<bool> StartAsync(ChromiumLiveSession session, CancellationToken cancellationToken)
    {
        _session = session;
        using var first = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
        try
        {
            var frame = await session.CaptureFrameAsync(first.Token);
            await ShowFrameAsync(frame, first.Token);
            if (_closed) return false;
            _root.UpdateLayout();
            if (_image.ActualWidth <= 0 || _image.ActualHeight <= 0) throw new InvalidOperationException(L.T("预览窗口没有可见的画面区域。"));
            _ = ContinueAsync();
            return true;
        }
        catch (Exception exception)
        {
            if (!_closed) _status.Text = L.T("未能显示页面 | ") + exception.Message;
            await session.DisposeAsync(); _session = null;
            return false;
        }
    }
    private async Task ContinueAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                await Task.Delay(1000, _lifetime.Token);
                if (_paused || _session is null) continue;
                await ShowFrameAsync(await _session.CaptureFrameAsync(_lifetime.Token), _lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (!_closed) { _image.Source = null; _status.Text = L.T("实时画面已中断 | ") + exception.Message; }
        }
        finally
        {
            if (_session is not null) await _session.DisposeAsync();
            _session = null;
        }
    }
    private async Task ShowFrameAsync(byte[] frame, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
        { writer.WriteBytes(frame); await writer.StoreAsync(); }
        stream.Seek(0);
        var bitmap = new BitmapImage();
        await bitmap.SetSourceAsync(stream);
        token.ThrowIfCancellationRequested();
        if (bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0) throw new InvalidOperationException(L.T("页面图像未能解码。"));
        if (_closed) return;
        _image.Source = bitmap;
        _status.Text = L.F($"只读实时画面 | {DateTime.Now:HH:mm:ss} | 关闭预览不会关闭原浏览器");
    }
}
