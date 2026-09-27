using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using ProcessKeeper.Core;

namespace ProcessKeeper.App;

public sealed class AutorunRow : INotifyPropertyChanged
{
    public required AutorunEntry Entry { get; init; }
    public string Name => Entry.Name;
    public required string Summary { get; set; }
    public required string ProcessSummary { get; set; }
    public required string DetailText { get; set; }
    public bool IsExpanded { get; set; }
    public string PresentationPlatformId { get; set; } = "";
    public bool IsPresentationGroup { get; set; }
    public int PresentationDepth { get; set; }
    public Thickness RowMargin => new(PresentationDepth * 24, 0, 0, 0);
    public Visibility ActionVisibility => IsPresentationGroup ? Visibility.Collapsed : Visibility.Visible;
    public bool Busy { get; set; }
    public ImageSource? Icon { get; set; }
    public string Chevron => IsExpanded ? "\uE70D" : "\uE76C";
    public string ActionText => !Entry.CanChange || Entry.Enabled is null ? L.T("查看原因") : Entry.Enabled == true ? L.T("禁用此项") : L.T("启用此项");
    public bool CanAct => !Busy && !IsPresentationGroup;
    public bool CanChange => !Busy && Entry.CanChange && Entry.Enabled.HasValue;
    public string ActionHint => Entry.CanChange ? L.T("更改下次触发时的启动行为，不结束当前进程。") : Entry.ReadOnlyReason;
    public string ReadOnlyReason => Entry.ReadOnlyReason;
    public Visibility DetailVisibility => IsExpanded && !IsPresentationGroup ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ProcessVisibility => ProcessSummary.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ReadOnlyVisibility => Entry.ReadOnlyReason.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Notify() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(""));
    public void NotifyIcon() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon)));
}
