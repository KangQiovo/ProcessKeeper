using ProcessKeeper.Core;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace ProcessKeeper.App;

public enum InstalledRowKind { Application, Executable, Process }

public sealed class InstalledRow : INotifyPropertyChanged
{
    private readonly PresentationChanges _changes = new();
    public string RowKey { get; init; } = "";
    public string ApplicationId { get; init; } = "";
    public string ExecutablePath { get; init; } = "";
    public int? ProcessId { get; init; }
    public long ProcessStartTicks { get; init; }
    public InstalledRowKind Kind { get; init; }
    public string PresentationPlatformId { get; set; } = "";
    public bool IsPresentationGroup { get; set; }
    public int PresentationDepth { get; set; }
    public bool IsExpanded { get; set; }
    public string Name { get; set; } = "";
    public string Summary { get; set; } = "";
    public string Status { get; set; } = "";
    public string Details { get; set; } = "";
    public string RoleText { get; set; } = "";
    public string RoleKind { get; set; } = "Unknown";
    public string RoleTooltip { get; set; } = "";
    public Visibility RoleVisibility => Kind == InstalledRowKind.Executable && RoleText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public string RecoveryText { get; set; } = "";
    public string RecoveryTooltip { get; set; } = "";
    public Visibility RecoveryVisibility => Kind == InstalledRowKind.Process && ProcessId.HasValue && RecoveryText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public string IconPath { get; set; } = "";
    public bool IconRequested { get; set; }
    public ImageSource? Icon { get; set; }
    public bool IsKept { get; set; }
    public bool CanKeep { get; set; }
    public string KeepText => IsKept ? L.T("已保留") : L.T("保留");
    public string Chevron => IsExpanded ? "\uE70D" : "\uE76C";
    public Visibility ChevronVisibility => Kind == InstalledRowKind.Application ? Visibility.Visible : Visibility.Collapsed;
    public Visibility KeepVisibility => Kind == InstalledRowKind.Process || IsPresentationGroup ? Visibility.Collapsed : Visibility.Visible;
    public double IconSize => Kind == InstalledRowKind.Application ? 32 : 22;
    public Thickness RowMargin => new(((int)Kind + PresentationDepth) * 24, 0, 0, 0);
    public event PropertyChangedEventHandler? PropertyChanged;
    public void NotifyIcon() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon)));
    public void Notify()
    {
        _changes.Publish(this, PropertyChanged,
            (nameof(RowKey), RowKey), (nameof(ApplicationId), ApplicationId), (nameof(ExecutablePath), ExecutablePath),
            (nameof(ProcessId), ProcessId), (nameof(ProcessStartTicks), ProcessStartTicks), (nameof(Kind), Kind),
            (nameof(PresentationPlatformId), PresentationPlatformId), (nameof(IsPresentationGroup), IsPresentationGroup),
            (nameof(PresentationDepth), PresentationDepth), (nameof(IsExpanded), IsExpanded), (nameof(Name), Name),
            (nameof(Summary), Summary), (nameof(Status), Status), (nameof(Details), Details), (nameof(RecoveryText), RecoveryText),
            (nameof(RoleText), RoleText), (nameof(RoleKind), RoleKind), (nameof(RoleTooltip), RoleTooltip), (nameof(RoleVisibility), RoleVisibility),
            (nameof(RecoveryTooltip), RecoveryTooltip), (nameof(RecoveryVisibility), RecoveryVisibility), (nameof(IconPath), IconPath),
            (nameof(IsKept), IsKept), (nameof(CanKeep), CanKeep), (nameof(KeepText), KeepText), (nameof(Chevron), Chevron),
            (nameof(ChevronVisibility), ChevronVisibility), (nameof(KeepVisibility), KeepVisibility), (nameof(IconSize), IconSize), (nameof(RowMargin), RowMargin));
    }
    public void NotifyKeepState()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsKept)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(KeepText)));
    }
}
