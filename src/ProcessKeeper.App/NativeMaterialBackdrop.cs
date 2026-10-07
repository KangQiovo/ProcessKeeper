using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace ProcessKeeper.App;

/// <summary>Customizes only the native material, retaining XAML's window and system policy configuration.</summary>
internal sealed class NativeMaterialBackdrop : SystemBackdrop
{
    private readonly BackdropMaterial _material;
    private AppearancePreferences _preferences;
    private MicaController? _mica;
    private DesktopAcrylicController? _acrylic;
    private ICompositionSupportsSystemBackdrop? _target;
    private XamlRoot? _xamlRoot;
    private SystemBackdropConfiguration? _configuration;
    private SystemBackdropTheme? _theme;

    internal BackdropMaterial Material => _material;
    internal event Action? AppearanceChanged;

    internal NativeMaterialBackdrop(AppearancePreferences preferences)
    { _material = preferences.Material; _preferences = preferences; }

    internal void Update(AppearancePreferences preferences)
    {
        if (preferences.Material != _material) throw new ArgumentException("Backdrop material cannot change while connected.", nameof(preferences));
        var restoreNativeColor = _preferences.TintColor is not null && preferences.TintColor is null;
        _preferences = preferences;
        ApplyParameters(restoreNativeColor);
    }

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);
        if (_target is not null) throw new InvalidOperationException("A material backdrop belongs to one window.");
        _target = connectedTarget; _xamlRoot = xamlRoot;
        if (_material == BackdropMaterial.Mica) _mica = new MicaController();
        else _acrylic = new DesktopAcrylicController();
        ApplyParameters(true);
        if (_mica is not null) _mica.AddSystemBackdropTarget(connectedTarget);
        else _acrylic!.AddSystemBackdropTarget(connectedTarget);
        AppearanceChanged?.Invoke();
    }

    protected override void OnDefaultSystemBackdropConfigurationChanged(ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot)
    {
        // Native customization disables automatic material theme values. Refresh these explicitly
        // while keeping the original IsInputActive/IsHighContrast/power/transparency policy object.
        if (_target is not null) { ApplyParameters(true); AppearanceChanged?.Invoke(); }
    }

    private void ApplyParameters(bool refreshDefaults)
    {
        if (_target is null || _xamlRoot is null || _mica is null && _acrylic is null) return;
        _configuration = GetDefaultSystemBackdropConfiguration(_target, _xamlRoot);
        if (_mica is not null) _mica.SetSystemBackdropConfiguration(_configuration);
        else _acrylic!.SetSystemBackdropConfiguration(_configuration);
        if (refreshDefaults || _theme != _configuration.Theme || _configuration.IsHighContrast)
        {
            // Reset re-evaluates the native theme/palette. Default color getters can still expose
            // their original Light values, so never copy them back as explicit overrides.
            if (_mica is not null) _mica.ResetProperties();
            else _acrylic!.ResetProperties();
            _theme = _configuration.Theme;
        }
        // Let the controller choose the system high-contrast palette; keep user values for later.
        if (_configuration.IsHighContrast) return;
        if (_mica is not null)
        {
            if (ParseColor(_preferences.TintColor) is { } tint) _mica.TintColor = tint;
            _mica.TintOpacity = (float)_preferences.TintOpacity;
            _mica.LuminosityOpacity = (float)_preferences.LuminosityOpacity;
        }
        else
        {
            if (ParseColor(_preferences.TintColor) is { } tint) _acrylic!.TintColor = tint;
            _acrylic!.TintOpacity = (float)_preferences.TintOpacity;
            _acrylic.LuminosityOpacity = (float)_preferences.LuminosityOpacity;
        }
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        try
        {
            if (_mica is not null) { try { _mica.RemoveSystemBackdropTarget(disconnectedTarget); } finally { _mica.Dispose(); } }
            if (_acrylic is not null) { try { _acrylic.RemoveSystemBackdropTarget(disconnectedTarget); } finally { _acrylic.Dispose(); } }
        }
        finally
        {
            _mica = null; _acrylic = null; _target = null; _xamlRoot = null; _configuration = null; _theme = null;
            base.OnTargetDisconnected(disconnectedTarget);
        }
    }

    private static Color? ParseColor(string? value) => value is null ? null :
        Color.FromArgb(255, Convert.ToByte(value.Substring(1, 2), 16), Convert.ToByte(value.Substring(3, 2), 16), Convert.ToByte(value.Substring(5, 2), 16));
}
