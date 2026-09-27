using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using ProcessKeeper.Core;

namespace ProcessKeeper.App;

public sealed class AppRow : INotifyPropertyChanged
{
    public string Key { get; set; } = "";
    public string RowKey { get; set; } = "";
    public int? ProcessId { get; set; }
    public bool IsProcess => ProcessId.HasValue;
    public string PresentationPlatformId { get; set; } = "";
    public bool IsPresentationGroup { get; set; }
    public int PresentationDepth { get; set; }
    public bool IsExpanded { get; set; }
    public string Chevron => IsExpanded ? "\uE70D" : "\uE76C";
    public Visibility ChevronVisibility => IsProcess ? Visibility.Collapsed : Visibility.Visible;
    public Visibility WhitelistVisibility => IsProcess || IsPresentationGroup ? Visibility.Collapsed : Visibility.Visible;
    public Thickness RowMargin => new((IsProcess ? 25 : 0) + PresentationDepth * 24, 0, 0, 0);
    public double IconSize => IsProcess ? 22 : 32;
    public string IconPath { get; set; } = "";
    public bool IconRequested { get; set; }
    public ImageSource? Icon { get; set; }
    public string Tooltip { get; set; } = "";
    public string RecoveryText { get; set; } = "";
    public string RecoveryTooltip { get; set; } = "";
    public Visibility RecoveryVisibility => IsProcess && RecoveryText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public string Name { get; set; } = "";
    public string Summary { get; set; } = "";
    public string ProtectionText { get; set; } = "";
    public bool IsWhitelisted { get; set; }
    public bool CanWhitelist { get; set; } = true;
    public event PropertyChangedEventHandler? PropertyChanged;
    public void NotifyIcon() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon)));
    public void Notify() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(""));
}

public sealed class RuleRow : INotifyPropertyChanged
{
    public string Id { get; set; } = "";
    public string RowKey { get; set; } = "";
    public int? ProcessId { get; set; }
    public long ProcessStartTicks { get; set; }
    public bool IsProcess => ProcessId.HasValue;
    public bool IsExpanded { get; set; }
    public string Chevron => IsExpanded ? "\uE70D" : "\uE76C";
    public Visibility ChevronVisibility => IsProcess ? Visibility.Collapsed : Visibility.Visible;
    public Visibility ActionsVisibility => IsProcess ? Visibility.Collapsed : Visibility.Visible;
    public Thickness RowMargin => IsProcess ? new Thickness(25, 0, 0, 0) : new Thickness(0);
    public double IconSize => IsProcess ? 22 : 32;
    public string IconPath { get; set; } = "";
    public bool IconRequested { get; set; }
    public ImageSource? Icon { get; set; }
    public string Name { get; set; } = "";
    public string Detail { get; set; } = "";
    public string MatchText { get; set; } = "";
    public string Tooltip { get; set; } = "";
    public string RecoveryText { get; set; } = "";
    public string RecoveryTooltip { get; set; } = "";
    public Visibility RecoveryVisibility => IsProcess && RecoveryText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public bool Enabled { get; set; }
    public event PropertyChangedEventHandler? PropertyChanged;
    public void NotifyIcon() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon)));
    public void Notify() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(""));
}
