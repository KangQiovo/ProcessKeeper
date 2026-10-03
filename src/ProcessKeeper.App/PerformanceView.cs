using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Input;
using ProcessKeeper.Core;
using WinRT.Interop;

namespace ProcessKeeper.App;

public sealed class PerformanceView : UserControl, IDisposable
{
    private readonly Window _owner;
    private readonly Grid _root;
    private readonly PerformancePreferencesStore _store;
    private readonly PerformanceSampler _sampler;
    private readonly SystemPerformanceSampler _systemSampler;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _editDelay = new() { Interval = TimeSpan.FromMilliseconds(35) };
    internal PerformancePreferencesWriter SaveQueue { get; }
    private readonly CheckBox _enabled = new(), _detailed = new(), _acrylic = new(), _locked = new();
    private readonly ComboBox _display = new() { HorizontalAlignment = HorizontalAlignment.Stretch }, _layout=new(){HorizontalAlignment=HorizontalAlignment.Stretch};
    private readonly NumberBox _x = Number(-100000,100000), _y = Number(-100000,100000), _width = Number(180,1200), _height = Number(90,800);
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly PerformancePanel _panel;
    private readonly Canvas _canvas = new() { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
    private PerformanceOverlay? _overlay;
    private bool _setting, _disposed, _rendering, _dragging, _editPending, _initialized;
    private bool _samplePending;
    private int _sampleGeneration;
    private PerformanceDisplay _samplingDisplay;
    private Windows.Foundation.Point _dragStart;
    private double _dragX, _dragY;
    public PerformancePreferences CurrentPreferences { get; private set; } = new();
    internal bool Sampling => _timer.IsEnabled;
    internal bool FramesSubscribed => _rendering;
    internal IntPtr OverlayHandle => _overlay?.Handle ?? IntPtr.Zero;

    public PerformanceView(Window owner, Grid root, string? directory = null, PerformanceSampler? sampler = null,
        PerformancePreferencesWriter? saveQueue = null, PerformancePreferences? initialPreferences = null,
        SystemPerformanceSampler? systemSampler = null)
    {
        _owner = owner; _root = root; _store = new PerformancePreferencesStore(directory); _sampler = sampler ?? new PerformanceSampler();
        _systemSampler = systemSampler ?? new SystemPerformanceSampler();
        SaveQueue = saveQueue ?? new PerformancePreferencesWriter(_store.Save); SaveQueue.SaveFailed += SaveFailed;
        _panel = new PerformancePanel(CloseDisplay);
        var content = new StackPanel { Spacing = 10, HorizontalAlignment = HorizontalAlignment.Left, MaxWidth = 680 };
        SizeChanged += (_, args) => content.Width = Math.Max(0, Math.Min(680, args.NewSize.Width));
        content.Children.Add(new TextBlock { Text = L.T("性能显示"), FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        content.Children.Add(new TextBlock { Text = L.T("应用内显示本应用 UI 帧率与内存；桌面浮窗显示桌面 FPS、电脑 CPU 与总内存。"), TextWrapping = TextWrapping.Wrap, FontSize = 12 });
        _enabled.Content = L.T("启用性能显示"); _detailed.Content = L.T("详细模式"); _acrylic.Content = L.T("亚克力背景");
        _locked.Content = L.T("锁定位置");
        content.Children.Add(_enabled); _display.Header = L.T("显示位置"); _display.ItemsSource = new[] { L.T("应用内"), L.T("桌面浮窗") }; content.Children.Add(_display);
        _layout.Header=L.T("显示布局");_layout.ItemsSource=new[]{L.T("横向卡片"),L.T("纵向排列"),L.T("单行精简")};content.Children.Add(_layout);
        var flags = new Grid { ColumnSpacing = 16, RowSpacing = 6 };
        flags.ColumnDefinitions.Add(new ColumnDefinition()); flags.ColumnDefinitions.Add(new ColumnDefinition());
        flags.RowDefinitions.Add(new RowDefinition()); flags.RowDefinitions.Add(new RowDefinition());
        flags.Children.Add(_detailed); Grid.SetColumn(_acrylic,1); flags.Children.Add(_acrylic);Grid.SetRow(_locked,1);flags.Children.Add(_locked);content.Children.Add(flags);
        var numbers = new Grid { ColumnSpacing = 12, RowSpacing = 8 };
        numbers.ColumnDefinitions.Add(new ColumnDefinition()); numbers.ColumnDefinitions.Add(new ColumnDefinition()); numbers.RowDefinitions.Add(new RowDefinition());numbers.RowDefinitions.Add(new RowDefinition());
        _x.Header=L.T("水平位置");_y.Header=L.T("垂直位置");_width.Header=L.T("宽度");_height.Header=L.T("高度");
        numbers.Children.Add(_x);Grid.SetColumn(_y,1);numbers.Children.Add(_y);Grid.SetRow(_width,1);numbers.Children.Add(_width);Grid.SetColumn(_height,1);Grid.SetRow(_height,1);numbers.Children.Add(_height);content.Children.Add(numbers);
        content.Children.Add(new TextBlock { Text=L.T("应用内位置为 DIP，浮窗位置为屏幕像素；尺寸为 DIP。"),TextWrapping=TextWrapping.Wrap,FontSize=12 });
        content.Children.Add(new TextBlock { Text=L.T("解锁后可拖动浮窗。锁定时可在这里解锁。"),TextWrapping=TextWrapping.Wrap,FontSize=12 });
        var reset = new Button { Content=L.T("恢复默认位置") }; reset.Click+=(_,_)=>Change(CurrentPreferences with {X=16,Y=16,Width=CurrentPreferences.CompactLine?260:232,Height=CurrentPreferences.CompactLine?36:90,Locked=false});content.Children.Add(reset);content.Children.Add(_status);Content=content;
        foreach(var box in new[]{_enabled,_detailed,_acrylic,_locked})box.Click+=(_,_)=>QueueEdit();
        _display.SelectionChanged+=(_,_)=>QueueEdit();_layout.SelectionChanged+=(_,_)=>QueueEdit();foreach(var number in new[]{_x,_y,_width,_height})number.ValueChanged+=(_,_)=>QueueEdit();
        _editDelay.Tick+=(_,_)=>FlushEdits(); _timer.Tick+=(_,_)=>Tick();
        Grid.SetRow(_canvas,root.RowDefinitions.Count>1?1:0);Canvas.SetZIndex(_canvas,100);_canvas.Children.Add(_panel);root.Children.Add(_canvas);_canvas.Visibility=Visibility.Collapsed;
        _panel.PointerPressed+=DragStarted;_panel.PointerMoved+=DragMoved;_panel.PointerReleased+=DragEnded;_panel.PointerCanceled+=DragEnded;
        _panel.PointerCaptureLost+=DragCaptureLost;_canvas.SizeChanged+=CanvasResized;
        root.SizeChanged+=RootResized;root.Loaded+=RootLoaded;root.ActualThemeChanged+=ThemeChanged;owner.Closed+=OwnerClosed;
        try { ApplyPreferences(initialPreferences ?? _store.Load()); }catch(Exception ex){ApplyPreferences(new PerformancePreferences());_status.Text=ex.Message;}
    }
    private static NumberBox Number(double min,double max)=>new(){Minimum=min,Maximum=max,SpinButtonPlacementMode=NumberBoxSpinButtonPlacementMode.Compact,HorizontalAlignment=HorizontalAlignment.Stretch,SmallChange=1,LargeChange=10};
    private void QueueEdit(){if(_setting||_disposed)return;_editPending=true;if(!_editDelay.IsEnabled)_editDelay.Start();}
    private void FlushEdits()
    {
        _editDelay.Stop();if(!_editPending||_disposed)return;_editPending=false;
        try{var next=ReadControls();var sizing=next.CompactLine!=CurrentPreferences.CompactLine||next.Width!=_width.Value||next.Height!=_height.Value;ApplyCore(next,true,sizing);}
        catch(Exception ex){_status.Text=ex.Message;}
    }
    public bool HasPendingSave => _editPending || SaveQueue.HasPending;
    public bool DiscardSaveFailure() => !_editPending && SaveQueue.TryDiscardFailedSave();
    public PerformancePreferences CapturePreferences(){FlushEdits();return CurrentPreferences;}
    public async Task FlushPendingAsync(){do{FlushEdits();await SaveQueue.FlushAsync();}while(_editPending||SaveQueue.HasPending);}
    private void SaveFailed(Exception error)=>DispatcherQueue.TryEnqueue(()=>{if(!_disposed)_status.Text=error.Message;});
    private PerformancePreferences ReadControls()
    {
        var compact=_layout.SelectedIndex==2;var width=_width.Value;var height=_height.Value;
        if(compact!=CurrentPreferences.CompactLine){if(compact&&height==90){height=36;if(width==232)width=260;}else if(!compact&&height<90){height=90;if(width==260)width=232;}}
        return new(_enabled.IsChecked==true,(PerformanceDisplay)Math.Max(0,_display.SelectedIndex),_detailed.IsChecked==true,_acrylic.IsChecked==true,_layout.SelectedIndex!=1,_locked.IsChecked==true,_x.Value,_y.Value,width,height,compact);
    }
    private void Change(PerformancePreferences value){try{ApplyPreferences(value,true);}catch(Exception ex){_status.Text=ex.Message;SetControls();}}
    public void ApplyPreferences(PerformancePreferences value,bool persist=false)
        =>ApplyCore(value,persist,true);
    private void ApplyCore(PerformancePreferences value,bool persist,bool synchronizeControls)
    {
        if(_disposed)return;value=PerformancePreferencesStore.Validate(value);_editDelay.Stop();_editPending=false;
        var changed=value!=CurrentPreferences;CurrentPreferences=value;if(synchronizeControls)SetControls();
        if(persist)SaveQueue.Schedule(value);if(changed||!_initialized){_initialized=true;RefreshDisplay();}
    }
    private void SetControls()
    {
        _setting=true;try{var p=CurrentPreferences;_enabled.IsChecked=p.Enabled;_display.SelectedIndex=(int)p.Display;_detailed.IsChecked=p.Detailed;_acrylic.IsChecked=p.UseAcrylic;_layout.SelectedIndex=p.CompactLine?2:p.Horizontal?0:1;_locked.IsChecked=p.Locked;_x.Value=p.X;_y.Value=p.Y;_width.Value=p.Width;_height.Minimum=p.CompactLine?32:90;_height.Value=p.Height;}finally{_setting=false;}
    }
    private void RefreshDisplay()
    {
        var p=CurrentPreferences;
        EndDrag();
        if(!p.Enabled){_sampleGeneration++;_timer.Stop();SubscribeFrames(false);_sampler.Stop();_systemSampler.Reset();_canvas.Visibility=Visibility.Collapsed;CloseOverlay();_status.Text=L.T("关闭显示后停止采样。");return;}
        if(!_root.IsLoaded)return;
        if(p.Display==PerformanceDisplay.InApp){CloseOverlay();_canvas.Visibility=Visibility.Visible;_panel.Configure(p,_root.ActualTheme);PositionInApp();}
        else{_canvas.Visibility=Visibility.Collapsed;_overlay??=new PerformanceOverlay(_root.ActualTheme,CloseDisplay,OverlayMoved);_overlay.Apply(p);}
        if(!_timer.IsEnabled||_samplingDisplay!=p.Display){_sampleGeneration++;_systemSampler.Reset();}_samplingDisplay=p.Display;
        if(p.Display==PerformanceDisplay.InApp)_sampler.Start();else _sampler.Stop();
        _timer.Start();SubscribeFrames(p.Display==PerformanceDisplay.InApp&&!PerformanceNativeWindow.Minimized(WindowNative.GetWindowHandle(_owner)));
        _status.Text=p.UseAcrylic&&p.Display==PerformanceDisplay.Overlay&&!DesktopAcrylicController.IsSupported()?L.T("此运行环境使用纯色背景。"):L.T("每秒后台采样；桌面 FPS 为实测合成帧率。电脑内存显示已用、总量与可用量。");
    }
    private async void Tick()
    {
        if(_disposed||!CurrentPreferences.Enabled||_samplePending)return;
        var display=CurrentPreferences.Display;var generation=_sampleGeneration;
        SubscribeFrames(display==PerformanceDisplay.InApp&&!PerformanceNativeWindow.Minimized(WindowNative.GetWindowHandle(_owner)));
        _samplePending=true;
        try
        {
            var sample=await Task.Run(()=>
            {
                if(generation!=Volatile.Read(ref _sampleGeneration))return new PerformanceSample(0,null,null,1);
                var app=display==PerformanceDisplay.InApp?_sampler.Sample():null;
                if(display==PerformanceDisplay.InApp&&app is null)return null;
                var system=_systemSampler.Sample();
                return (app??new PerformanceSample(0,null,null,1)) with{System=system};
            });
            if(sample is null||_disposed||!CurrentPreferences.Enabled||generation!=_sampleGeneration||display!=CurrentPreferences.Display)return;
            _panel.Update(sample,CurrentPreferences);_overlay?.Update(sample,CurrentPreferences);_overlay?.ClampToWorkArea();
        }
        catch(Exception error) when(error is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {if(!_disposed)_status.Text=error.Message;}
        finally{_samplePending=false;}
    }
    private void SubscribeFrames(bool on){if(_rendering==on)return;_rendering=on;if(on)CompositionTarget.Rendering+=Frame;else CompositionTarget.Rendering-=Frame;}
    private void Frame(object? sender,object args)=>_sampler.RecordFrame();
    private void PositionInApp()
    {
        var p=CurrentPreferences;var width=Math.Min(p.Width,Math.Max(1,_canvas.ActualWidth));var height=Math.Min(p.Height,Math.Max(1,_canvas.ActualHeight));_panel.Width=width;_panel.Height=height;
        Canvas.SetLeft(_panel,Math.Max(0,Math.Min(p.X,_canvas.ActualWidth-width)));Canvas.SetTop(_panel,Math.Max(0,Math.Min(p.Y,_canvas.ActualHeight-height)));_panel.IsHitTestVisible=!p.Locked;
    }
    private void RootResized(object sender,SizeChangedEventArgs args){if(CurrentPreferences.Enabled&&CurrentPreferences.Display==PerformanceDisplay.InApp)PositionInApp();}
    private void CanvasResized(object sender,SizeChangedEventArgs args)
    {
        _canvas.Clip=new RectangleGeometry{Rect=new Windows.Foundation.Rect(0,0,Math.Max(0,args.NewSize.Width),Math.Max(0,args.NewSize.Height))};
        if(CurrentPreferences.Enabled&&CurrentPreferences.Display==PerformanceDisplay.InApp)PositionInApp();
    }
    private void RootLoaded(object sender,RoutedEventArgs args){if(!_disposed)RefreshDisplay();}
    private void ThemeChanged(FrameworkElement sender,object args){_panel.Configure(CurrentPreferences,_root.ActualTheme);_overlay?.SetTheme(_root.ActualTheme);}
    private void DragStarted(object sender,PointerRoutedEventArgs e)
    {
        if(CurrentPreferences.Locked||!e.GetCurrentPoint(_panel).Properties.IsLeftButtonPressed||PerformancePanel.IsButton(e.OriginalSource as DependencyObject))return;
        if(!_panel.CapturePointer(e.Pointer))return;
        _dragging=true;_dragStart=e.GetCurrentPoint(_canvas).Position;_dragX=Canvas.GetLeft(_panel);_dragY=Canvas.GetTop(_panel);e.Handled=true;
    }
    private void DragMoved(object sender,PointerRoutedEventArgs e)
    {
        if(!_dragging)return;var point=e.GetCurrentPoint(_canvas).Position;CurrentPreferences=CurrentPreferences with{X=Math.Max(0,Math.Min(_dragX+point.X-_dragStart.X,_canvas.ActualWidth-_panel.ActualWidth)),Y=Math.Max(0,Math.Min(_dragY+point.Y-_dragStart.Y,_canvas.ActualHeight-_panel.ActualHeight))};PositionInApp();e.Handled=true;
    }
    private void EndDrag(){_dragging=false;_panel.ReleasePointerCaptures();}
    private void DragEnded(object sender,PointerRoutedEventArgs e){if(!_dragging)return;EndDrag();Change(CurrentPreferences);e.Handled=true;}
    private void DragCaptureLost(object sender,PointerRoutedEventArgs e){if(!_dragging)return;_dragging=false;Change(CurrentPreferences);}
    private void OverlayMoved(PerformanceRectangle bounds){if(_disposed)return;CurrentPreferences=CurrentPreferences with{X=bounds.X,Y=bounds.Y};SaveQueue.Schedule(CurrentPreferences);SetControls();}
    private void CloseDisplay(){if(_disposed)return;ApplyPreferences(CurrentPreferences with{Enabled=false},true);}
    private void CloseOverlay(){var window=_overlay;_overlay=null;window?.Dispose();}
    private void OwnerClosed(object sender,WindowEventArgs args)=>Dispose();
    public void Dispose()
    {
        if(_disposed)return;FlushEdits();EndDrag();_disposed=true;SaveQueue.SaveFailed-=SaveFailed;_ = ObserveFinalSaveAsync();_timer.Stop();_editDelay.Stop();SubscribeFrames(false);_sampler.Dispose();CloseOverlay();_canvas.SizeChanged-=CanvasResized;_panel.PointerCaptureLost-=DragCaptureLost;_root.Children.Remove(_canvas);_root.SizeChanged-=RootResized;_root.Loaded-=RootLoaded;_root.ActualThemeChanged-=ThemeChanged;_owner.Closed-=OwnerClosed;
    }
    private async Task ObserveFinalSaveAsync(){try{await SaveQueue.FlushAsync();}catch{}}
}
