using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using ProcessKeeper.Core;

namespace ProcessKeeper.App;

public sealed class AutorunRow : INotifyPropertyChanged
{
    private readonly PresentationChanges _changes = new();
    public required AutorunEntry Entry { get; set; }
    public ProcessRecord? Process { get; set; }
    public IReadOnlyList<ProcessRecord> Processes { get; set; } = [];
    public IReadOnlyList<AutorunEntry> ApplicationEntries { get; set; } = [];
    public string ApplicationGroupKey { get; set; } = "";
    public string ApplicationIconPath { get; set; } = "";
    public bool IsApplicationGroup { get; set; }
    public int ApplicationEntryCount { get; set; }
    public bool IsProcess => Process is not null;
    public string RowKey => Process is null ? Entry.Id : Entry.Id + "|pid:" + Process.Id + ":" + Process.StartTimeUtcTicks;
    public string Name => Process is null ? Entry.Name : Process.Name + " | PID " + Process.Id;
    public string IconPath => Process?.Path ?? (ApplicationIconPath.Length > 0 ? ApplicationIconPath : Entry.TargetPath);
    public required string Summary { get; set; }
    public required string ProcessSummary { get; set; }
    public required string DetailText { get; set; }
    public bool IsExpanded { get; set; }
    public string PresentationPlatformId { get; set; } = "";
    public bool IsPresentationGroup { get; set; }
    public int PresentationDepth { get; set; }
    public Thickness RowMargin => new((PresentationDepth + (IsProcess ? 1 : 0)) * 24, 0, 0, 0);
    public Visibility ActionVisibility => IsPresentationGroup || IsApplicationGroup || IsProcess ? Visibility.Collapsed : Visibility.Visible;
    public bool IsWhitelistProtected { get; set; }
    public bool Busy { get; set; }
    public ImageSource? Icon { get; set; }
    public string Chevron => IsExpanded ? "\uE70D" : "\uE76C";
    public string ActionText => !Entry.CanChange || Entry.Enabled is null ? L.T("查看原因") : Entry.Enabled == true ? L.T("禁用此项") : L.T("启用此项");
    public bool CanAct => !Busy && !IsPresentationGroup && !IsApplicationGroup && !IsProcess && !(IsWhitelistProtected && Entry.Enabled == true);
    public bool CanChange => CanAct && Entry.CanChange && Entry.Enabled.HasValue;
    public string ActionHint => Entry.CanChange ? L.T("更改下次触发时的启动行为，不结束当前进程。") : Entry.ReadOnlyReason;
    public string ReadOnlyReason => Entry.ReadOnlyReason;
    public Visibility DetailVisibility => IsExpanded && !IsPresentationGroup && !IsApplicationGroup && !IsProcess ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ChevronVisibility => IsProcess ? Visibility.Collapsed : Visibility.Visible;
    public Visibility ProcessVisibility => ProcessSummary.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ReadOnlyVisibility => Entry.ReadOnlyReason.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Notify() => _changes.Publish(this, PropertyChanged,
        (nameof(Name), Name), (nameof(Summary), Summary), (nameof(ProcessSummary), ProcessSummary), (nameof(DetailText), DetailText),
        (nameof(IsExpanded), IsExpanded), (nameof(PresentationPlatformId), PresentationPlatformId),
        (nameof(IsPresentationGroup), IsPresentationGroup), (nameof(IsApplicationGroup), IsApplicationGroup), (nameof(PresentationDepth), PresentationDepth), (nameof(RowMargin), RowMargin),
        (nameof(ActionVisibility), ActionVisibility), (nameof(Busy), Busy), (nameof(Chevron), Chevron), (nameof(ChevronVisibility), ChevronVisibility), (nameof(ActionText), ActionText),
        (nameof(CanAct), CanAct), (nameof(CanChange), CanChange), (nameof(ActionHint), ActionHint), (nameof(ReadOnlyReason), ReadOnlyReason),
        (nameof(DetailVisibility), DetailVisibility), (nameof(ProcessVisibility), ProcessVisibility), (nameof(ReadOnlyVisibility), ReadOnlyVisibility));
    public void NotifyIcon() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon)));
}
