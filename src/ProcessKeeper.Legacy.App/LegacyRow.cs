using System.ComponentModel;
using System.Windows.Media;
using ProcessKeeper.Core;
namespace ProcessKeeper.App;
public sealed class LegacyRow : INotifyPropertyChanged
{
    private readonly PresentationChanges _changes = new();
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Summary { get; set; } = "";
    public string Detail { get; set; } = "";
    public string Path { get; set; } = "";
    public string IconPath { get; set; } = "";
    public string ActionLabel { get; set; } = "";
    public bool HasAction { get; set; }
    public bool CanAct { get; set; } = true;
    public bool IsActionChecked { get; set; }
    private bool? _selectionState = false;
    public bool? SelectionState => _selectionState;
    public void SetSelectionState(bool? value)
    {
        if (_selectionState == value) return;
        _selectionState = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectionState)));
    }
    public void NotifySelectionState() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectionState)));
    public bool HasToggleAction => HasAction && (Model is ApplicationGroup or InstalledApplication or WhitelistRule ||
        Model is AutorunEntry { CanChange: true, Enabled: not null });
    public bool CanToggleAction => CanAct && (!(Model is ApplicationGroup or InstalledApplication) || !IsActionChecked);
    public string ActionCheckboxLabel => Model is WhitelistRule or AutorunEntry ? L.T("启用") : ActionLabel;
    public System.Windows.Visibility ToggleActionVisibility => HasToggleAction ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
    public System.Windows.Visibility SelectionVisibility => HasToggleAction ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
    public System.Windows.Visibility ActionButtonVisibility => HasAction && !HasToggleAction ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
    public bool IsChild { get; set; }
    public string PresentationPlatformId { get; set; } = "";
    public bool IsPresentationGroup { get; set; }
    public int PresentationDepth { get; set; }
    public bool Expanded { get; set; }
    public string Chevron => IsChild ? "" : Expanded ? "⌄" : "›";
    public System.Windows.Thickness Indent => new((IsChild ? 24 : 0) + PresentationDepth * 24, 0, 0, 0);
    public object? Model { get; set; }
    public string ApplicationKey { get; set; } = "";
    public string RoleText { get; set; } = "";
    public string RoleKind { get; set; } = "Unknown";
    public string RoleTooltip { get; set; } = "";
    public System.Windows.Visibility RoleVisibility => RoleText.Length > 0 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
    private ImageSource? _icon;
    public ImageSource? Icon { get => _icon; set { if (ReferenceEquals(_icon, value)) return; _icon = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Apply(LegacyRow next)
    {
        if (!string.Equals(Path, next.Path, StringComparison.OrdinalIgnoreCase) || !string.Equals(IconPath, next.IconPath, StringComparison.OrdinalIgnoreCase)) Icon = null;
        Name = next.Name; Summary = next.Summary; Detail = next.Detail; Path = next.Path; IconPath = next.IconPath;
        ActionLabel = next.ActionLabel; HasAction = next.HasAction; CanAct = next.CanAct; IsChild = next.IsChild;
        IsActionChecked = next.IsActionChecked;
        PresentationPlatformId = next.PresentationPlatformId; IsPresentationGroup = next.IsPresentationGroup;
        PresentationDepth = next.PresentationDepth; Expanded = next.Expanded; Model = next.Model; ApplicationKey = next.ApplicationKey;
        RoleText = next.RoleText; RoleKind = next.RoleKind; RoleTooltip = next.RoleTooltip;
        Notify();
    }
    public void Notify() => _changes.Publish(this, PropertyChanged,
        (nameof(Name), Name), (nameof(Summary), Summary), (nameof(Detail), Detail), (nameof(Path), Path), (nameof(IconPath), IconPath),
        (nameof(ActionLabel), ActionLabel), (nameof(HasAction), HasAction), (nameof(CanAct), CanAct), (nameof(IsChild), IsChild),
        (nameof(IsActionChecked), IsActionChecked), (nameof(HasToggleAction), HasToggleAction), (nameof(CanToggleAction), CanToggleAction),
        (nameof(ActionCheckboxLabel), ActionCheckboxLabel), (nameof(ToggleActionVisibility), ToggleActionVisibility),
        (nameof(SelectionVisibility), SelectionVisibility), (nameof(ActionButtonVisibility), ActionButtonVisibility),
        (nameof(PresentationPlatformId), PresentationPlatformId), (nameof(IsPresentationGroup), IsPresentationGroup),
        (nameof(PresentationDepth), PresentationDepth), (nameof(Expanded), Expanded), (nameof(Chevron), Chevron), (nameof(Indent), Indent),
        (nameof(RoleText), RoleText), (nameof(RoleKind), RoleKind), (nameof(RoleTooltip), RoleTooltip), (nameof(RoleVisibility), RoleVisibility));
    // A native OneWay checkbox changes its target before confirmation. Reset that target even when
    // cancellation leaves the source value unchanged; PresentationChanges deliberately coalesces it.
    public void NotifyActionState() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsActionChecked)));
}
