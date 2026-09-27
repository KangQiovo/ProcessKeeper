using System.ComponentModel;
using System.Windows.Media;
namespace ProcessKeeper.App;
public sealed class LegacyRow : INotifyPropertyChanged
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Summary { get; set; } = "";
    public string Detail { get; set; } = "";
    public string Path { get; set; } = "";
    public string ActionLabel { get; set; } = "";
    public bool HasAction { get; set; }
    public bool CanAct { get; set; } = true;
    public bool IsChild { get; set; }
    public string PresentationPlatformId { get; set; } = "";
    public bool IsPresentationGroup { get; set; }
    public int PresentationDepth { get; set; }
    public bool Expanded { get; set; }
    public string Chevron => IsChild ? "" : Expanded ? "⌄" : "›";
    public System.Windows.Thickness Indent => new((IsChild ? 24 : 0) + PresentationDepth * 24, 0, 0, 0);
    public object? Model { get; set; }
    public string ApplicationKey { get; set; } = "";
    private ImageSource? _icon;
    public ImageSource? Icon { get => _icon; set { _icon = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
}
