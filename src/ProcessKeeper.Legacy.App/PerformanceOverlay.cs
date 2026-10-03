using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using ProcessKeeper.Core;
namespace ProcessKeeper.App;

internal sealed class PerformancePanel : Border
{
    private readonly TextBlock _title=new(){Text="Process Keeper",FontWeight=FontWeights.SemiBold,FontSize=10.5};
    private readonly Grid _metrics=new();
    private readonly Grid _heading=new(){Margin=new Thickness(0,0,0,4)},_compact=new();
    private readonly TextBlock _line=new(){FontSize=11.5,TextWrapping=TextWrapping.NoWrap,TextTrimming=TextTrimming.CharacterEllipsis,VerticalAlignment=VerticalAlignment.Center},_lineMeasure=new(){FontSize=11.5};
    private readonly Grid[] _cells={new(),new(),new(),new()};
    private readonly TextBlock[] _labels={new(),new(),new(),new()},_values={new(),new(),new(),new()};
    private bool? _horizontal,_detailed,_compactLine;
    private PerformanceDisplay? _display;
    private PerformancePreferences _preferences=new();
    private bool? _dark;
    private Brush? _opaque;
    internal PerformancePanel(Action close)
    {
        Padding=new Thickness(8);CornerRadius=new CornerRadius(10);var grid=new Grid();grid.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});grid.RowDefinitions.Add(new RowDefinition());var heading=_heading;heading.ColumnDefinitions.Add(new ColumnDefinition());heading.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});heading.Children.Add(_title);
        var button=new Button{Content="×",Padding=new Thickness(0),MinHeight=18,MinWidth=18,Height=18,Width=18,FontSize=11,ToolTip=L.T("关闭性能显示"),Margin=new Thickness(),Background=Brushes.Transparent,BorderThickness=new Thickness(0)};button.Click+=(_,_)=>close();Grid.SetColumn(button,1);heading.Children.Add(button);grid.Children.Add(heading);
        for(var i=0;i<4;i++)
        {
            _labels[i].FontSize=10;_labels[i].TextWrapping=TextWrapping.Wrap;_labels[i].VerticalAlignment=VerticalAlignment.Center;
            _values[i].FontSize=15;_values[i].FontWeight=FontWeights.SemiBold;_values[i].Text="—";_values[i].VerticalAlignment=VerticalAlignment.Center;
            _cells[i].Children.Add(_labels[i]);_cells[i].Children.Add(_values[i]);_metrics.Children.Add(_cells[i]);
        }
        Grid.SetRow(_metrics,1);grid.Children.Add(_metrics);Child=grid;ToolTip=L.T("UI 回调计数，仅代表本应用；不是游戏帧率。");
        _compact.ColumnDefinitions.Add(new ColumnDefinition());_compact.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});_compact.Children.Add(_line);
        var compactClose=new Button{Content="×",Padding=new Thickness(0),MinHeight=18,MinWidth=18,Height=18,Width=18,FontSize=11,Background=Brushes.Transparent,BorderThickness=new Thickness(0),VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(4,0,0,0),ToolTip=L.T("关闭性能显示")};compactClose.Click+=(_,_)=>close();Grid.SetColumn(compactClose,1);_compact.Children.Add(compactClose);Grid.SetRowSpan(_compact,2);grid.Children.Add(_compact);
        SizeChanged+=(_,_)=>{ArrangeMetrics(_preferences);FitCompactText();};
    }
    internal void SetTheme(bool dark,bool transparent=false){if(_dark!=dark){_dark=dark;_opaque=new SolidColorBrush(dark?Color.FromRgb(28,28,30):Color.FromRgb(248,248,250));_title.Foreground=dark?Brushes.White:Brushes.Black;foreach(var text in _labels.Concat(_values))text.Foreground=_title.Foreground;_line.Foreground=_title.Foreground;}Background=transparent?Brushes.Transparent:_opaque;}
    internal void Configure(PerformancePreferences p,bool dark,bool transparent=false){SetTheme(dark,transparent);ArrangeMetrics(p);}
    private void ArrangeMetrics(PerformancePreferences p)
    {
        _preferences=p;var horizontal=p.Horizontal&&ActualWidth>=220;
        if(_display!=p.Display){foreach(var value in _values)value.Text="—";_line.Text="—";}
        if(_horizontal==horizontal&&_detailed==p.Detailed&&_compactLine==p.CompactLine&&_display==p.Display)return;_horizontal=horizontal;_detailed=p.Detailed;_compactLine=p.CompactLine;_display=p.Display;
        Padding=new Thickness(p.CompactLine||p.Display==PerformanceDisplay.Overlay&&p.Detailed?6:8);_heading.Visibility=_metrics.Visibility=p.CompactLine?Visibility.Collapsed:Visibility.Visible;_compact.Visibility=p.CompactLine?Visibility.Visible:Visibility.Collapsed;
        _metrics.ColumnDefinitions.Clear();_metrics.RowDefinitions.Clear();var count=p.Display==PerformanceDisplay.Overlay?(p.Detailed?4:3):(p.Detailed?3:2);
        for(var i=0;i<count;i++){if(horizontal)_metrics.ColumnDefinitions.Add(new ColumnDefinition());else _metrics.RowDefinitions.Add(new RowDefinition());}
        for(var i=0;i<4;i++)
        {
            var cell=_cells[i];cell.Visibility=i<count?Visibility.Visible:Visibility.Collapsed;cell.RowDefinitions.Clear();cell.ColumnDefinitions.Clear();
            Grid.SetRow(cell,horizontal?0:i);Grid.SetColumn(cell,horizontal?i:0);cell.Margin=new Thickness(horizontal&&i>0?6:0,0,0,0);
            if(horizontal){cell.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});cell.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});cell.VerticalAlignment=VerticalAlignment.Center;}
            else{cell.ColumnDefinitions.Add(new ColumnDefinition());cell.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});cell.VerticalAlignment=VerticalAlignment.Stretch;}
            Grid.SetRow(_values[i],0);Grid.SetColumn(_values[i],horizontal?0:1);Grid.SetRow(_labels[i],horizontal?1:0);Grid.SetColumn(_labels[i],0);
            _values[i].FontSize=p.Display==PerformanceDisplay.Overlay?(horizontal?(i==2?10.5:13):10):i==1&&!p.Detailed?10.5:horizontal?(i==1?12:15):12;_labels[i].FontSize=p.Display==PerformanceDisplay.Overlay&&!horizontal?9.5:10;
            _labels[i].Text=p.Display==PerformanceDisplay.Overlay?
                i==0?L.T("桌面 FPS"):i==1?"CPU":L.T(i==2?"内存":"可用")+" (GiB)":
                i==0?"UI FPS":i==1?L.T(p.Detailed?"本应用":"应用 / 总内存")+(p.Detailed?" (MiB)":""):L.T("总内存")+" (GiB)";
            _values[i].TextTrimming=TextTrimming.CharacterEllipsis;cell.ToolTip=_labels[i].Text;
        }
    }
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
        var explanation=PerformanceMetricText.Explanation(sample,p.Display);ToolTip=explanation;
        _line.ToolTip=explanation+"\n"+_line.Text;foreach(var cell in _cells)cell.ToolTip=explanation;FitCompactText();
    }
    private void FitCompactText(){if(!_preferences.CompactLine||ActualWidth<=0)return;_lineMeasure.Text=_line.Text;_lineMeasure.FontFamily=_line.FontFamily;_lineMeasure.Measure(new Size(double.PositiveInfinity,double.PositiveInfinity));var available=Math.Max(1,ActualWidth-34);_line.FontSize=Math.Max(9,Math.Min(11.5,11.5*available/Math.Max(1,_lineMeasure.DesiredSize.Width)));}
    internal static bool IsButton(DependencyObject? source){while(source is not null){if(source is ButtonBase)return true;source=VisualTreeHelper.GetParent(source);}return false;}
}

internal sealed class PerformanceOverlay : IDisposable
{
    private readonly Window _window=new(){Title="Process Keeper",Style=null,BorderThickness=new Thickness(0),WindowStyle=WindowStyle.None,ResizeMode=ResizeMode.NoResize,ShowActivated=false,ShowInTaskbar=false,Topmost=true,MinWidth=1,MinHeight=1,Width=232,Height=90};
    private readonly PerformancePanel _panel;
    private readonly Action _closed;
    private readonly Action<PerformanceRectangle> _moved;
    private bool _disposed,_locked,_shown,_themeDark,_clipQueued;
    private bool _materialConfigured,_materialAcrylic;
    private PerformanceRectangle? _lastClip;
    private uint _clipDpi;
    private bool _clipAcrylic;
    internal IntPtr Handle=>new WindowInteropHelper(_window).Handle;
    internal bool AcrylicEnabled {get;private set;}
    internal PerformanceOverlay(Action closed,Action<PerformanceRectangle> moved)
    {
        _closed=closed;_moved=moved;_panel=new PerformancePanel(closed){CornerRadius=new CornerRadius(0)};_panel.SizeChanged+=(_,_)=>{ApplyClip();QueueClip();};_window.SizeChanged+=(_,_)=>QueueClip();_window.Content=_panel;_window.Closed+=(_,_)=>{if(!_disposed){_disposed=true;_closed();}};
        _window.SourceInitialized+=(_,_)=>PerformanceNativeWindow.Configure(Handle,_locked);
        _panel.MouseLeftButtonDown+=(_,args)=>{if(_locked||PerformancePanel.IsButton(args.OriginalSource as DependencyObject))return;PerformanceNativeWindow.Drag(Handle);ClampToWorkArea();_moved(PerformanceNativeWindow.Bounds(Handle));args.Handled=true;};
    }
    internal void Apply(PerformancePreferences p,bool dark)
    {
        var materialChanged=!_materialConfigured||_materialAcrylic!=p.UseAcrylic||_themeDark!=dark;
        _themeDark=dark;_locked=p.Locked;_panel.IsHitTestVisible=!p.Locked;if(!_shown){_panel.SetTheme(dark);_shown=true;_window.Show();}PerformanceNativeWindow.Configure(Handle,p.Locked);
        if(materialChanged)
        {
            _materialConfigured=true;_materialAcrylic=p.UseAcrylic;_lastClip=null;
            var wasAcrylic=AcrylicEnabled;AcrylicEnabled=false;
            try
            {
                // Older WPF-UI backdrop routes can use legacy Mica. Native acrylic
                // requires the documented backdrop and corner policies together.
                if(p.UseAcrylic&&Wpf.Ui.Controls.WindowBackdrop.IsSupported(Wpf.Ui.Controls.WindowBackdropType.Acrylic)
                    &&DwmGetWindowAttribute(Handle,38,out _,sizeof(int))>=0&&DwmGetWindowAttribute(Handle,33,out _,sizeof(int))>=0)
                { Wpf.Ui.Controls.WindowBackdrop.RemoveBackground(_window); var glass=new Margins{Left=-1,Right=-1,Top=-1,Bottom=-1};AcrylicEnabled=DwmExtendFrameIntoClientArea(Handle,ref glass)>=0&&Wpf.Ui.Controls.WindowBackdrop.ApplyBackdrop(Handle,Wpf.Ui.Controls.WindowBackdropType.Acrylic); }
                else if(wasAcrylic){var glass=new Margins();DwmExtendFrameIntoClientArea(Handle,ref glass);Wpf.Ui.Controls.WindowBackdrop.RemoveBackdrop(_window);}
            }
            catch(Exception ex)when(ex is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException or System.ComponentModel.Win32Exception){AcrylicEnabled=false;}
        }
        _panel.Configure(p,dark,AcrylicEnabled);
        _window.Background=AcrylicEnabled?Brushes.Transparent:_panel.Background;
        var scale=PerformanceNativeWindow.Dpi(Handle)/96d;var requested=new PerformanceRectangle((int)p.X,(int)p.Y,(int)(p.Width*scale),(int)(p.Height*scale));PerformanceNativeWindow.Position(Handle,PerformanceNativeWindow.Clamp(requested,PerformanceNativeWindow.WorkArea(requested)));ApplyClip();QueueClip();
    }
    internal void Update(PerformanceSample sample,PerformancePreferences p,bool dark){if(_themeDark!=dark)Apply(p,dark);_panel.SetTheme(dark,AcrylicEnabled);_panel.Update(sample,p);}
    internal void ClampToWorkArea(){if(_disposed||!_shown)return;var bounds=PerformanceNativeWindow.Bounds(Handle);var next=PerformanceNativeWindow.Clamp(bounds,PerformanceNativeWindow.WorkArea(bounds));if(next!=bounds){PerformanceNativeWindow.Position(Handle,next);ApplyClip();}}
    private void QueueClip()
    {
        if(_disposed||_clipQueued)return;_clipQueued=true;
        _window.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle,new Action(()=>
        {
            try{if(!_disposed){PerformanceNativeWindow.Configure(Handle,_locked);_lastClip=null;ApplyClip();}}
            finally{_clipQueued=false;}
        }));
    }
    private void ApplyClip()
    {
        if(_disposed||!_panel.IsLoaded||_panel.ActualWidth<=0||_panel.ActualHeight<=0)return;
        var origin=_panel.PointToScreen(new Point());var end=_panel.PointToScreen(new Point(_panel.ActualWidth,_panel.ActualHeight));
        var bounds=PerformanceNativeWindow.Bounds(Handle);var dpi=PerformanceNativeWindow.Dpi(Handle);
        var content=new PerformanceRectangle((int)Math.Round(origin.X),(int)Math.Round(origin.Y),(int)Math.Round(end.X-origin.X),(int)Math.Round(end.Y-origin.Y));
        var local=content with{X=content.X-bounds.X,Y=content.Y-bounds.Y};
        if(_lastClip==local&&_clipDpi==dpi&&_clipAcrylic==AcrylicEnabled)return;
        // DWM's native acrylic paints outside an explicit GDI region. Let DWM
        // round the complete native backdrop; solid fallback uses the region.
        if(AcrylicEnabled?PerformanceNativeWindow.RoundCorners(Handle,preferSystem:true):PerformanceNativeWindow.RoundContent(Handle,content)){_lastClip=local;_clipDpi=dpi;_clipAcrylic=AcrylicEnabled;}
    }
    [StructLayout(LayoutKind.Sequential)]private struct Margins{public int Left,Right,Top,Bottom;}
    [DllImport("dwmapi.dll")]private static extern int DwmExtendFrameIntoClientArea(IntPtr window,ref Margins margins);
    [DllImport("dwmapi.dll")]private static extern int DwmGetWindowAttribute(IntPtr window,int attribute,out int value,int size);
    public void Dispose(){if(_disposed)return;_disposed=true;_window.Close();}
}
