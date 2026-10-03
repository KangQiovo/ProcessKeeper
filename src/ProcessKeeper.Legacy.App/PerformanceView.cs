using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ProcessKeeper.Core;
namespace ProcessKeeper.App;

public sealed class PerformanceView : UserControl, IDisposable
{
    private readonly Window _owner;
    private readonly Grid _root;
    private readonly PerformancePreferencesStore _store;
    private readonly PerformanceSampler _sampler;
    private readonly SystemPerformanceSampler _systemSampler;
    private readonly DispatcherTimer _timer=new(){Interval=TimeSpan.FromSeconds(1)},_editDelay=new(){Interval=TimeSpan.FromMilliseconds(35)};
    internal PerformancePreferencesWriter SaveQueue {get;}
    private readonly CheckBox _enabled=new(),_detailed=new(),_acrylic=new(),_locked=new();
    private readonly ComboBox _display=new(){HorizontalAlignment=HorizontalAlignment.Stretch},_layout=new(){HorizontalAlignment=HorizontalAlignment.Stretch};
    private readonly TextBox _x=new(),_y=new(),_width=new(),_height=new();
    private readonly TextBlock _status=new(){TextWrapping=TextWrapping.Wrap,FontSize=12};
    private readonly Canvas _canvas=new(){HorizontalAlignment=HorizontalAlignment.Stretch,VerticalAlignment=VerticalAlignment.Stretch};
    private readonly PerformancePanel _panel;
    private PerformanceOverlay? _overlay;
    private bool _setting,_disposed,_rendering,_dragging,_editPending,_initialized;
    private bool _samplePending;private int _sampleGeneration;private PerformanceDisplay _samplingDisplay;
    private Point _dragStart;private double _dragX,_dragY;
    public PerformancePreferences CurrentPreferences {get;private set;}=new();
    internal bool Sampling=>_timer.IsEnabled;
    internal bool FramesSubscribed=>_rendering;
    internal IntPtr OverlayHandle=>_overlay?.Handle??IntPtr.Zero;
    public PerformanceView(Window owner,Grid root,string? directory=null,PerformanceSampler? sampler=null,
        PerformancePreferencesWriter? saveQueue=null,PerformancePreferences? initialPreferences=null,
        SystemPerformanceSampler? systemSampler=null)
    {
        _owner=owner;_root=root;_store=new PerformancePreferencesStore(directory);_sampler=sampler??new PerformanceSampler();_panel=new PerformancePanel(CloseDisplay);
        _systemSampler=systemSampler??new SystemPerformanceSampler();
        SaveQueue=saveQueue??new PerformancePreferencesWriter(_store.Save);SaveQueue.SaveFailed+=SaveFailed;
        var content=new StackPanel{HorizontalAlignment=HorizontalAlignment.Left,MaxWidth=680};
        SizeChanged+=(_,args)=>content.Width=Math.Max(0,Math.Min(680,args.NewSize.Width));
        void Add(FrameworkElement item){item.Margin=new Thickness(0,0,0,10);content.Children.Add(item);}
        Add(new TextBlock{Text=L.T("性能显示"),FontSize=20,FontWeight=FontWeights.SemiBold});Add(new TextBlock{Text=L.T("应用内显示本应用 UI 帧率与内存；桌面浮窗显示桌面 FPS、电脑 CPU 与总内存。"),TextWrapping=TextWrapping.Wrap,FontSize=12});
        _enabled.Content=L.T("启用性能显示");_detailed.Content=L.T("详细模式");_acrylic.Content=L.T("亚克力背景");_locked.Content=L.T("锁定位置");Add(_enabled);Add(new TextBlock{Text=L.T("显示位置")});_display.ItemsSource=new[]{L.T("应用内"),L.T("桌面浮窗")};Add(_display);
        Add(new TextBlock{Text=L.T("显示布局")});_layout.ItemsSource=new[]{L.T("横向卡片"),L.T("纵向排列"),L.T("单行精简")};Add(_layout);
        var flags=new WrapPanel();foreach(var box in new[]{_detailed,_acrylic,_locked}){box.Margin=new Thickness(0,0,16,8);flags.Children.Add(box);}Add(flags);
        var numbers=new Grid();numbers.ColumnDefinitions.Add(new ColumnDefinition());numbers.ColumnDefinitions.Add(new ColumnDefinition());numbers.RowDefinitions.Add(new RowDefinition());numbers.RowDefinitions.Add(new RowDefinition());
        var fields=new[]{(_x,"水平位置"),(_y,"垂直位置"),(_width,"宽度"),(_height,"高度")};for(var i=0;i<fields.Length;i++){var cell=new StackPanel{Margin=new Thickness(0,0,i%2==0?12:0,8)};cell.Children.Add(new TextBlock{Text=L.T(fields[i].Item2),Margin=new Thickness(0,0,0,4)});cell.Children.Add(fields[i].Item1);Grid.SetRow(cell,i/2);Grid.SetColumn(cell,i%2);numbers.Children.Add(cell);}Add(numbers);
        Add(new TextBlock{Text=L.T("应用内位置为 DIP，浮窗位置为屏幕像素；尺寸为 DIP。"),TextWrapping=TextWrapping.Wrap,FontSize=12});Add(new TextBlock{Text=L.T("解锁后可拖动浮窗。锁定时可在这里解锁。"),TextWrapping=TextWrapping.Wrap,FontSize=12});
        var reset=new Button{Content=L.T("恢复默认位置"),HorizontalAlignment=HorizontalAlignment.Left};reset.Click+=(_,_)=>Change(CurrentPreferences with{X=16,Y=16,Width=CurrentPreferences.CompactLine?260:232,Height=CurrentPreferences.CompactLine?36:90,Locked=false});Add(reset);Add(_status);Content=content;
        foreach(var box in new[]{_enabled,_detailed,_acrylic,_locked}){box.Checked+=(_,_)=>QueueEdit();box.Unchecked+=(_,_)=>QueueEdit();}_display.SelectionChanged+=(_,_)=>QueueEdit();_layout.SelectionChanged+=(_,_)=>QueueEdit();foreach(var box in new[]{_x,_y,_width,_height})box.TextChanged+=(_,_)=>QueueEdit();
        _editDelay.Tick+=(_,_)=>FlushEdits();_timer.Tick+=(_,_)=>Tick();
        Grid.SetRow(_canvas,root.RowDefinitions.Count>1?1:0);Panel.SetZIndex(_canvas,100);_canvas.Children.Add(_panel);root.Children.Add(_canvas);_canvas.Visibility=Visibility.Collapsed;
        _panel.MouseLeftButtonDown+=DragStarted;_panel.MouseMove+=DragMoved;_panel.MouseLeftButtonUp+=DragEnded;_panel.LostMouseCapture+=DragCaptureLost;_canvas.SizeChanged+=CanvasResized;root.SizeChanged+=RootResized;root.Loaded+=RootLoaded;owner.StateChanged+=OwnerStateChanged;owner.Closed+=OwnerClosed;
        try{ApplyPreferences(initialPreferences??_store.Load());}catch(Exception ex){ApplyPreferences(new PerformancePreferences());_status.Text=ex.Message;}
    }
    private void QueueEdit(){if(_setting||_disposed)return;_editPending=true;if(!_editDelay.IsEnabled)_editDelay.Start();}
    private void FlushEdits()
    {
        _editDelay.Stop();if(!_editPending||_disposed)return;_editPending=false;
        try{var next=ReadControls();var sizing=next.CompactLine!=CurrentPreferences.CompactLine;ApplyCore(next,true,sizing);}
        catch(Exception ex){_status.Text=ex.Message;}
    }
    public bool HasPendingSave=>_editPending||SaveQueue.HasPending;
    public bool DiscardSaveFailure()=>!_editPending&&SaveQueue.TryDiscardFailedSave();
    public PerformancePreferences CapturePreferences(){FlushEdits();return CurrentPreferences;}
    public async Task FlushPendingAsync(){do{FlushEdits();await SaveQueue.FlushAsync();}while(_editPending||SaveQueue.HasPending);}
    private void SaveFailed(Exception error)=>Dispatcher.BeginInvoke(new Action(()=>{if(!_disposed)_status.Text=error.Message;}));
    private PerformancePreferences ReadControls()
    {
        double Number(TextBox box)=>double.TryParse(box.Text,NumberStyles.Float,CultureInfo.CurrentCulture,out var value)?value:throw new InvalidDataException(L.T("性能显示设置无效。"));
        var compact=_layout.SelectedIndex==2;var width=Number(_width);var height=Number(_height);
        if(compact!=CurrentPreferences.CompactLine){if(compact&&height==90){height=36;if(width==232)width=260;}else if(!compact&&height<90){height=90;if(width==260)width=232;}}
        return new PerformancePreferences(_enabled.IsChecked==true,(PerformanceDisplay)Math.Max(0,_display.SelectedIndex),_detailed.IsChecked==true,_acrylic.IsChecked==true,_layout.SelectedIndex!=1,_locked.IsChecked==true,Number(_x),Number(_y),width,height,compact);
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
        _setting=true;try{var p=CurrentPreferences;_enabled.IsChecked=p.Enabled;_display.SelectedIndex=(int)p.Display;_detailed.IsChecked=p.Detailed;_acrylic.IsChecked=p.UseAcrylic;_layout.SelectedIndex=p.CompactLine?2:p.Horizontal?0:1;_locked.IsChecked=p.Locked;_x.Text=p.X.ToString(CultureInfo.CurrentCulture);_y.Text=p.Y.ToString(CultureInfo.CurrentCulture);_width.Text=p.Width.ToString(CultureInfo.CurrentCulture);_height.Text=p.Height.ToString(CultureInfo.CurrentCulture);}finally{_setting=false;}
    }
    private bool Dark=>_root.TryFindResource("InkBrush") is SolidColorBrush brush&&brush.Color.R>160;
    private void RefreshDisplay()
    {
        EndDrag();
        var p=CurrentPreferences;if(!p.Enabled){_sampleGeneration++;_timer.Stop();SubscribeFrames(false);_sampler.Stop();_systemSampler.Reset();_canvas.Visibility=Visibility.Collapsed;CloseOverlay();_status.Text=L.T("关闭显示后停止采样。");return;}if(!_root.IsLoaded)return;
        if(p.Display==PerformanceDisplay.InApp){CloseOverlay();_canvas.Visibility=Visibility.Visible;_panel.Configure(p,Dark);PositionInApp();}
        else{_canvas.Visibility=Visibility.Collapsed;_overlay??=new PerformanceOverlay(CloseDisplay,OverlayMoved);_overlay.Apply(p,Dark);}
        if(!_timer.IsEnabled||_samplingDisplay!=p.Display){_sampleGeneration++;_systemSampler.Reset();}_samplingDisplay=p.Display;
        if(p.Display==PerformanceDisplay.InApp)_sampler.Start();else _sampler.Stop();
        _timer.Start();SubscribeFrames(p.Display==PerformanceDisplay.InApp&&_owner.WindowState!=WindowState.Minimized);_status.Text=p.UseAcrylic&&(p.Display==PerformanceDisplay.InApp||_overlay?.AcrylicEnabled!=true)?L.T("此运行环境使用纯色背景。"):L.T("每秒后台采样；桌面 FPS 为实测合成帧率。电脑内存显示已用、总量与可用量。");
    }
    private async void Tick()
    {
        if(_disposed||!CurrentPreferences.Enabled||_samplePending)return;
        var display=CurrentPreferences.Display;var generation=_sampleGeneration;
        SubscribeFrames(display==PerformanceDisplay.InApp&&_owner.WindowState!=WindowState.Minimized);_samplePending=true;
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
            _panel.SetTheme(Dark);_panel.Update(sample,CurrentPreferences);_overlay?.Update(sample,CurrentPreferences,Dark);_overlay?.ClampToWorkArea();
        }
        catch(Exception error) when(error is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {if(!_disposed)_status.Text=error.Message;}
        finally{_samplePending=false;}
    }
    private void SubscribeFrames(bool on){if(on==_rendering)return;_rendering=on;if(on)CompositionTarget.Rendering+=Frame;else CompositionTarget.Rendering-=Frame;}
    private void Frame(object? sender,EventArgs args)=>_sampler.RecordFrame();
    private void PositionInApp(){var p=CurrentPreferences;_panel.Width=Math.Min(p.Width,Math.Max(1,_canvas.ActualWidth));_panel.Height=Math.Min(p.Height,Math.Max(1,_canvas.ActualHeight));Canvas.SetLeft(_panel,Math.Max(0,Math.Min(p.X,_canvas.ActualWidth-_panel.Width)));Canvas.SetTop(_panel,Math.Max(0,Math.Min(p.Y,_canvas.ActualHeight-_panel.Height)));_panel.IsHitTestVisible=!p.Locked;}
    private void RootResized(object sender,SizeChangedEventArgs args){if(CurrentPreferences.Enabled&&CurrentPreferences.Display==PerformanceDisplay.InApp)PositionInApp();}
    private void CanvasResized(object sender,SizeChangedEventArgs args)
    {
        _canvas.Clip=new RectangleGeometry(new Rect(0,0,Math.Max(0,args.NewSize.Width),Math.Max(0,args.NewSize.Height)));
        if(CurrentPreferences.Enabled&&CurrentPreferences.Display==PerformanceDisplay.InApp)PositionInApp();
    }
    private void RootLoaded(object sender,RoutedEventArgs args){if(!_disposed)RefreshDisplay();}
    private void OwnerStateChanged(object? sender,EventArgs args){if(CurrentPreferences.Enabled)SubscribeFrames(CurrentPreferences.Display==PerformanceDisplay.InApp&&_owner.WindowState!=WindowState.Minimized);}
    private void DragStarted(object sender,MouseButtonEventArgs e){if(CurrentPreferences.Locked||PerformancePanel.IsButton(e.OriginalSource as DependencyObject)||!_panel.CaptureMouse())return;_dragging=true;_dragStart=e.GetPosition(_canvas);_dragX=Canvas.GetLeft(_panel);_dragY=Canvas.GetTop(_panel);e.Handled=true;}
    private void DragMoved(object sender,MouseEventArgs e){if(!_dragging)return;var point=e.GetPosition(_canvas);CurrentPreferences=CurrentPreferences with{X=Math.Max(0,Math.Min(_dragX+point.X-_dragStart.X,_canvas.ActualWidth-_panel.ActualWidth)),Y=Math.Max(0,Math.Min(_dragY+point.Y-_dragStart.Y,_canvas.ActualHeight-_panel.ActualHeight))};PositionInApp();e.Handled=true;}
    private void EndDrag(){_dragging=false;_panel.ReleaseMouseCapture();}
    private void DragEnded(object sender,MouseButtonEventArgs e){if(!_dragging)return;EndDrag();Change(CurrentPreferences);e.Handled=true;}
    private void DragCaptureLost(object sender,MouseEventArgs e){if(!_dragging)return;_dragging=false;Change(CurrentPreferences);}
    private void OverlayMoved(PerformanceRectangle bounds){if(_disposed)return;CurrentPreferences=CurrentPreferences with{X=bounds.X,Y=bounds.Y};SaveQueue.Schedule(CurrentPreferences);SetControls();}
    private void CloseDisplay(){if(_disposed)return;ApplyPreferences(CurrentPreferences with{Enabled=false},true);}
    private void CloseOverlay(){var overlay=_overlay;_overlay=null;overlay?.Dispose();}
    private void OwnerClosed(object? sender,EventArgs args)=>Dispose();
    public void Dispose(){if(_disposed)return;FlushEdits();EndDrag();_disposed=true;SaveQueue.SaveFailed-=SaveFailed;_ = ObserveFinalSaveAsync();_timer.Stop();_editDelay.Stop();SubscribeFrames(false);_sampler.Dispose();CloseOverlay();_canvas.SizeChanged-=CanvasResized;_panel.LostMouseCapture-=DragCaptureLost;_root.Children.Remove(_canvas);_root.SizeChanged-=RootResized;_root.Loaded-=RootLoaded;_owner.StateChanged-=OwnerStateChanged;_owner.Closed-=OwnerClosed;}
    private async Task ObserveFinalSaveAsync(){try{await SaveQueue.FlushAsync();}catch{}}
}
