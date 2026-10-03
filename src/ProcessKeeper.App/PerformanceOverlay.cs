using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Composition;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using ProcessKeeper.Core;
using WinRT.Interop;
using WinRT;

namespace ProcessKeeper.App;

internal sealed class PerformancePanel : UserControl
{
    private readonly Border _border=new(){Padding=new Thickness(8),CornerRadius=new CornerRadius(10)};
    private readonly TextBlock _title=new(){Text="Process Keeper",FontWeight=Microsoft.UI.Text.FontWeights.SemiBold,FontSize=10.5};
    private readonly Grid _metrics=new(){ColumnSpacing=6,RowSpacing=1};
    private readonly Grid _heading=new(),_compact=new(){ColumnSpacing=4},_body=new(){RowSpacing=4};
    private readonly TextBlock _line=new(){FontSize=11.5,TextWrapping=TextWrapping.NoWrap,TextTrimming=TextTrimming.CharacterEllipsis,VerticalAlignment=VerticalAlignment.Center},_lineMeasure=new(){FontSize=11.5};
    private readonly Grid[] _cells={new(),new(),new(),new()};
    private readonly TextBlock[] _labels={new(),new(),new(),new()},_values={new(),new(),new(),new()};
    private bool? _horizontal,_detailed,_compactLine;
    private PerformanceDisplay? _display;
    private ElementTheme? _configuredTheme;
    private bool _configuredAcrylic,_configuredBackdrop,_configuredNative;
    private PerformancePreferences _preferences=new();
    internal PerformancePanel(Action close)
    {
        var grid=_body;grid.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});grid.RowDefinitions.Add(new RowDefinition());
        var heading=_heading;heading.ColumnDefinitions.Add(new ColumnDefinition());heading.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});heading.Children.Add(_title);
        var button=new Button{Content="×",Padding=new Thickness(0),MinHeight=18,MinWidth=18,Height=18,Width=18,FontSize=11,Background=new SolidColorBrush(Microsoft.UI.Colors.Transparent),BorderThickness=new Thickness(0)};ToolTipService.SetToolTip(button,L.T("关闭性能显示"));button.Click+=(_,_)=>close();Grid.SetColumn(button,1);heading.Children.Add(button);grid.Children.Add(heading);
        for(var i=0;i<4;i++)
        {
            _labels[i].FontSize=10;_labels[i].TextWrapping=TextWrapping.Wrap;_labels[i].VerticalAlignment=VerticalAlignment.Center;
            _values[i].FontSize=15;_values[i].FontWeight=Microsoft.UI.Text.FontWeights.SemiBold;_values[i].Text="—";_values[i].VerticalAlignment=VerticalAlignment.Center;
            _cells[i].Children.Add(_labels[i]);_cells[i].Children.Add(_values[i]);_metrics.Children.Add(_cells[i]);
        }
        Grid.SetRow(_metrics,1);grid.Children.Add(_metrics);_border.Child=grid;Content=_border;
        _compact.ColumnDefinitions.Add(new ColumnDefinition());_compact.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});_compact.Children.Add(_line);
        var compactClose=new Button{Content="×",Padding=new Thickness(0),MinHeight=18,MinWidth=18,Height=18,Width=18,FontSize=11,Background=new SolidColorBrush(Microsoft.UI.Colors.Transparent),BorderThickness=new Thickness(0),VerticalAlignment=VerticalAlignment.Center};ToolTipService.SetToolTip(compactClose,L.T("关闭性能显示"));compactClose.Click+=(_,_)=>close();Grid.SetColumn(compactClose,1);_compact.Children.Add(compactClose);Grid.SetRowSpan(_compact,2);grid.Children.Add(_compact);
        ToolTipService.SetToolTip(this,L.T("UI 回调计数，仅代表本应用；不是游戏帧率。"));
        SizeChanged+=(_,_)=>{ArrangeMetrics(_preferences);FitCompactText();};
    }
    internal void Configure(PerformancePreferences p,ElementTheme theme,bool nativeBackdrop=false,bool nativeWindow=false)
    {
        if(_configuredTheme!=theme||_configuredAcrylic!=p.UseAcrylic||_configuredBackdrop!=nativeBackdrop||_configuredNative!=nativeWindow)
        {
            _configuredTheme=theme;_configuredAcrylic=p.UseAcrylic;_configuredBackdrop=nativeBackdrop;_configuredNative=nativeWindow;
            _border.CornerRadius=new CornerRadius(nativeWindow?0:10);RequestedTheme=theme;var color=theme==ElementTheme.Dark?Microsoft.UI.ColorHelper.FromArgb(255,28,28,30):Microsoft.UI.ColorHelper.FromArgb(255,248,248,250);
            _border.Background=nativeBackdrop?new SolidColorBrush(Microsoft.UI.Colors.Transparent):p.UseAcrylic?new AcrylicBrush{TintColor=color,TintOpacity=0.22,TintLuminosityOpacity=0.55,FallbackColor=color}:new SolidColorBrush(color);
            var foreground=new SolidColorBrush(theme==ElementTheme.Dark?Microsoft.UI.Colors.White:Microsoft.UI.Colors.Black);_title.Foreground=foreground;
            foreach(var text in _labels.Concat(_values))text.Foreground=foreground;_line.Foreground=foreground;
        }
        ArrangeMetrics(p);
    }
    private void ArrangeMetrics(PerformancePreferences p)
    {
        _preferences=p;var horizontal=p.Horizontal&&ActualWidth>=220;
        if(_display!=p.Display){foreach(var value in _values)value.Text="—";_line.Text="—";}
        if(_horizontal==horizontal&&_detailed==p.Detailed&&_compactLine==p.CompactLine&&_display==p.Display)return;_horizontal=horizontal;_detailed=p.Detailed;_compactLine=p.CompactLine;_display=p.Display;
        _border.Padding=new Thickness(p.CompactLine||p.Display==PerformanceDisplay.Overlay&&p.Detailed?6:8);_heading.Visibility=_metrics.Visibility=p.CompactLine?Visibility.Collapsed:Visibility.Visible;_compact.Visibility=p.CompactLine?Visibility.Visible:Visibility.Collapsed;
        _metrics.Margin=new Thickness(0,0,0,p.Display==PerformanceDisplay.Overlay&&p.Detailed&&!horizontal?2:0);
        _metrics.RowSpacing=p.Display==PerformanceDisplay.Overlay&&p.Detailed&&!horizontal?0:1;
        _metrics.ColumnDefinitions.Clear();_metrics.RowDefinitions.Clear();var count=p.Display==PerformanceDisplay.Overlay?(p.Detailed?4:3):(p.Detailed?3:2);
        for(var i=0;i<count;i++){if(horizontal)_metrics.ColumnDefinitions.Add(new ColumnDefinition());else _metrics.RowDefinitions.Add(new RowDefinition());}
        for(var i=0;i<4;i++)
        {
            var cell=_cells[i];cell.Visibility=i<count?Visibility.Visible:Visibility.Collapsed;cell.RowDefinitions.Clear();cell.ColumnDefinitions.Clear();
            Grid.SetRow(cell,horizontal?0:i);Grid.SetColumn(cell,horizontal?i:0);
            if(horizontal){cell.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});cell.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});cell.VerticalAlignment=VerticalAlignment.Center;}
            else{cell.ColumnDefinitions.Add(new ColumnDefinition());cell.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});cell.VerticalAlignment=VerticalAlignment.Stretch;}
            Grid.SetRow(_values[i],0);Grid.SetColumn(_values[i],horizontal?0:1);Grid.SetRow(_labels[i],horizontal?1:0);Grid.SetColumn(_labels[i],0);
            _values[i].FontSize=p.Display==PerformanceDisplay.Overlay?(horizontal?(i==2?10.5:13):10):i==1&&!p.Detailed?10.5:horizontal?(i==1?12:15):12;_labels[i].FontSize=p.Display==PerformanceDisplay.Overlay&&!horizontal?9.5:10;
            _labels[i].Text=p.Display==PerformanceDisplay.Overlay?
                i==0?L.T("桌面 FPS"):i==1?"CPU":L.T(i==2?"内存":"可用")+" (GiB)":
                i==0?"UI FPS":i==1?L.T(p.Detailed?"本应用":"应用 / 总内存")+(p.Detailed?" (MiB)":""):L.T("总内存")+" (GiB)";
            _values[i].TextTrimming=TextTrimming.CharacterEllipsis;
            ToolTipService.SetToolTip(cell,_labels[i].Text);
        }
    }
    internal static bool IsButton(DependencyObject? source){while(source is not null){if(source is Microsoft.UI.Xaml.Controls.Primitives.ButtonBase)return true;source=VisualTreeHelper.GetParent(source);}return false;}
    internal void Update(PerformanceSample sample,PerformancePreferences p)
    {
        ArrangeMetrics(p);
        if(p.Display==PerformanceDisplay.Overlay)
        {
            _values[0].Text=PerformanceMetricText.Fps(sample.System?.DesktopFramesPerSecond);
            _values[1].Text=PerformanceMetricText.Percent(sample.System?.CpuPercent);
            _values[2].Text=PerformanceMetricText.GiB(sample.System?.UsedMemoryBytes)+" / "+PerformanceMetricText.GiB(sample.System?.TotalMemoryBytes);
            _values[3].Text=PerformanceMetricText.GiB(sample.System?.AvailableMemoryBytes);
        }
        else
        {
            _values[0].Text=sample.FramesPerSecond.ToString("0.0");
            _values[1].Text=PerformanceMetricText.MiB(sample.WorkingSetBytes)+(p.Detailed?"":" MiB / "+PerformanceMetricText.GiB(sample.System?.TotalMemoryBytes)+" GiB");
            _values[2].Text=PerformanceMetricText.GiB(sample.System?.TotalMemoryBytes);
        }
        _line.Text=CompactPerformanceText.Format(sample,p.Detailed,p.Display);
        var explanation=PerformanceMetricText.Explanation(sample,p.Display);ToolTipService.SetToolTip(this,explanation);
        ToolTipService.SetToolTip(_line,explanation+"\n"+_line.Text);foreach(var cell in _cells)ToolTipService.SetToolTip(cell,explanation);FitCompactText();
    }
    private void FitCompactText(){if(!_preferences.CompactLine||ActualWidth<=0)return;_lineMeasure.Text=_line.Text;_lineMeasure.FontFamily=_line.FontFamily;_lineMeasure.Measure(new Windows.Foundation.Size(double.PositiveInfinity,double.PositiveInfinity));var available=Math.Max(1,ActualWidth-34);_line.FontSize=Math.Max(9,Math.Min(11.5,11.5*available/Math.Max(1,_lineMeasure.DesiredSize.Width)));}
}

internal sealed class PerformanceOverlay : IDisposable
{
    private readonly Window _window=new(){Title="Process Keeper"};
    private readonly PerformancePanel _panel;
    private readonly Action _closed;
    private readonly Action<PerformanceRectangle> _moved;
    private bool _disposed,_locked,_resizeQueued,_chromeQueued;
    private int _resizePasses;
    private int _inputWidth,_inputHeight;
    private PerformanceRectangle? _lastClip;
    private uint _clipDpi;
    private bool _materialConfigured,_materialAcrylic;
    private ElementTheme _materialTheme;
    private ElementTheme _theme;
    private PerformancePreferences _preferences=new();
    private DesktopAcrylicController? _backdrop;
    private SystemBackdropConfiguration? _backdropConfiguration;
    internal bool AcrylicEnabled=>_backdrop is not null;
    internal IntPtr Handle=>WindowNative.GetWindowHandle(_window);
    internal PerformanceOverlay(ElementTheme theme,Action closed,Action<PerformanceRectangle> moved)
    {
        _theme=theme;_closed=closed;_moved=moved;_panel=new PerformancePanel(closed);_window.Content=_panel;
        var presenter=OverlappedPresenter.Create();presenter.IsResizable=false;presenter.IsMaximizable=false;presenter.IsMinimizable=false;presenter.IsAlwaysOnTop=true;_window.AppWindow.SetPresenter(presenter);presenter.SetBorderAndTitleBar(false,false);
        _panel.SizeChanged+=(_,_)=>{QueueContentResize();QueueChromeUpdate();ApplyClip();};
        _window.Closed+=(_,_)=>{if(!_disposed){_disposed=true;ClearBackdrop();_closed();}};
        _panel.PointerPressed+=(_,args)=>{if(_locked||!args.GetCurrentPoint(_panel).Properties.IsLeftButtonPressed||PerformancePanel.IsButton(args.OriginalSource as DependencyObject))return;PerformanceNativeWindow.Drag(Handle);ClampToWorkArea();_moved(PerformanceNativeWindow.Bounds(Handle));args.Handled=true;};
    }
    internal void Apply(PerformancePreferences p)
    {
        _preferences=p;_locked=p.Locked;_panel.IsHitTestVisible=!p.Locked;PerformanceNativeWindow.Configure(Handle,p.Locked);ConfigureBackdrop();
        var scale=PerformanceNativeWindow.Dpi(Handle)/96d;var clientWidth=(int)Math.Round(p.Width*scale);var clientHeight=(int)Math.Round(p.Height*scale);
        if(Math.Abs(_panel.ActualWidth-p.Width)>0.5||Math.Abs(_panel.ActualHeight-p.Height)>0.5){_resizePasses=2;_window.AppWindow.ResizeClient(new Windows.Graphics.SizeInt32(clientWidth,clientHeight));QueueContentResize();}
        if(_inputWidth!=clientWidth||_inputHeight!=clientHeight)
        {
            _inputWidth=clientWidth;_inputHeight=clientHeight;
            _window.AppWindow.TitleBar.SetDragRectangles(Array.Empty<Windows.Graphics.RectInt32>());
            Microsoft.UI.Input.InputNonClientPointerSource.GetForWindowId(_window.AppWindow.Id).SetRegionRects(Microsoft.UI.Input.NonClientRegionKind.Passthrough,new[]{new Windows.Graphics.RectInt32(0,0,clientWidth,clientHeight)});
        }
        PerformanceNativeWindow.Configure(Handle,p.Locked);var current=PerformanceNativeWindow.Bounds(Handle);var requested=new PerformanceRectangle((int)p.X,(int)p.Y,current.Width,current.Height);PerformanceNativeWindow.Position(Handle,PerformanceNativeWindow.Clamp(requested,PerformanceNativeWindow.WorkArea(requested)));ApplyClip();
        QueueChromeUpdate();
    }
    private void QueueChromeUpdate()
    {
        if(_disposed||_chromeQueued)return;_chromeQueued=true;
        _panel.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,()=>
        {
            try{if(!_disposed){((OverlappedPresenter)_window.AppWindow.Presenter).SetBorderAndTitleBar(false,false);PerformanceNativeWindow.Configure(Handle,_locked);_lastClip=null;ApplyClip();}}
            finally{_chromeQueued=false;}
        });
    }
    private void QueueContentResize()
    {
        if(_disposed||_resizePasses==0||_resizeQueued)return;
        _resizeQueued=true;
        _panel.DispatcherQueue.TryEnqueue(()=>
        {
            _resizeQueued=false;if(_disposed||_resizePasses==0||_panel.ActualWidth<=0)return;
            // Extended title bars vary with OS/DPI. Match real XAML content instead of assuming a caption thickness.
            var scale=PerformanceNativeWindow.Dpi(Handle)/96d;
            var dx=(int)Math.Round((_preferences.Width-_panel.ActualWidth)*scale);
            var dy=(int)Math.Round((_preferences.Height-_panel.ActualHeight)*scale);
            if(dx==0&&dy==0){_resizePasses=0;return;}
            _resizePasses--;var original=PerformanceNativeWindow.Bounds(Handle);var size=_window.AppWindow.Size;
            _window.AppWindow.Resize(new Windows.Graphics.SizeInt32(Math.Max(1,size.Width+dx),Math.Max(1,size.Height+dy)));
            ApplyClip();
            ClampToWorkArea();
            QueueChromeUpdate();
        });
    }
    private void ApplyClip()
    {
        if(_disposed||_panel.XamlRoot is null||_panel.ActualWidth<=0||_panel.ActualHeight<=0)return;
        var rectangle=_panel.XamlRoot.CoordinateConverter.ConvertLocalToScreen(new Windows.Foundation.Rect(0,0,_panel.ActualWidth,_panel.ActualHeight));
        var bounds=PerformanceNativeWindow.Bounds(Handle);var dpi=PerformanceNativeWindow.Dpi(Handle);
        var local=new PerformanceRectangle(rectangle.X-bounds.X,rectangle.Y-bounds.Y,rectangle.Width,rectangle.Height);
        if(_lastClip==local&&_clipDpi==dpi)return;
        if(PerformanceNativeWindow.RoundContent(Handle,new PerformanceRectangle(rectangle.X,rectangle.Y,rectangle.Width,rectangle.Height))){_lastClip=local;_clipDpi=dpi;}
    }
    private void ConfigureBackdrop()
    {
        if(_materialConfigured&&_materialAcrylic==_preferences.UseAcrylic&&_materialTheme==_theme){_panel.Configure(_preferences with{UseAcrylic=false},_theme,AcrylicEnabled,nativeWindow:true);return;}
        _materialConfigured=true;_materialAcrylic=_preferences.UseAcrylic;_materialTheme=_theme;_lastClip=null;
        try
        {
            if(_preferences.UseAcrylic&&DesktopAcrylicController.IsSupported())
            {
                if(_backdrop is null)
                {
                    _backdrop=new DesktopAcrylicController();_backdropConfiguration=new SystemBackdropConfiguration{IsInputActive=true};
                    _backdrop.SetSystemBackdropConfiguration(_backdropConfiguration);
                    if(!_backdrop.AddSystemBackdropTarget(_window.As<ICompositionSupportsSystemBackdrop>()))ClearBackdrop();
                }
                if(_backdrop is not null)
                {
                    _backdropConfiguration!.Theme=_theme==ElementTheme.Dark?SystemBackdropTheme.Dark:SystemBackdropTheme.Light;
                    var color=_theme==ElementTheme.Dark?Microsoft.UI.ColorHelper.FromArgb(255,28,28,30):Microsoft.UI.ColorHelper.FromArgb(255,248,248,250);
                    _backdrop.TintColor=color;_backdrop.FallbackColor=color;_backdrop.TintOpacity=0.22f;_backdrop.LuminosityOpacity=0.55f;
                }
            }
            else ClearBackdrop();
        }
        catch{ClearBackdrop();}
        _panel.Configure(_preferences with{UseAcrylic=false},_theme,AcrylicEnabled,nativeWindow:true);
    }
    private void ClearBackdrop(){_backdrop?.Dispose();_backdrop=null;_backdropConfiguration=null;}
    internal void SetTheme(ElementTheme theme){_theme=theme;ConfigureBackdrop();}
    internal void Update(PerformanceSample sample,PerformancePreferences p)=>_panel.Update(sample,p);
    internal void ClampToWorkArea(){if(_disposed)return;var bounds=PerformanceNativeWindow.Bounds(Handle);var next=PerformanceNativeWindow.Clamp(bounds,PerformanceNativeWindow.WorkArea(bounds));if(next!=bounds){PerformanceNativeWindow.Position(Handle,next);ApplyClip();}}
    public void Dispose(){if(_disposed)return;_disposed=true;ClearBackdrop();_window.Close();}
}
