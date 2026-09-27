using ProcessKeeper.Core;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ProcessKeeper.App;

public sealed partial class MainWindow
{
    private readonly AppearancePreferencesStore _appearanceStore = new();
    private AppearancePreferences _appearance = new();
    private bool _appearanceReady;
    private string _appearanceResult = "";
    private string? _appearancePersistenceWarning;

    private AppearancePreferences CurrentAppearance => _appearance;

    private void ApplyImportedAppearance(AppearancePreferences value)
    {
        _appearance = value;
        _appearancePersistenceWarning = null;
        SetAppearanceControls();
        ApplyAppearance();
    }

    private void InitializeAppearance()
    {
        _appearance = _appearanceStore.Load(out _appearancePersistenceWarning);
        SetAppearanceControls();
        Root.ActualThemeChanged += AppearanceActualThemeChanged;
        Closed += (_, _) =>
        {
            _appearanceReady = false;
            Root.ActualThemeChanged -= AppearanceActualThemeChanged;
        };
        ApplyAppearance();
        _appearanceReady = true;
    }

    private void SetAppearanceControls()
    {
        var ready = _appearanceReady;
        _appearanceReady = false;
        try
        {
            BackdropEnabled.IsChecked = _appearance.BackdropEnabled;
            BackdropChoice.SelectedIndex = (int)_appearance.Material;
            ThemeChoice.SelectedIndex = (int)_appearance.Theme;
        }
        finally { _appearanceReady = ready; }
    }

    private void AppearanceSelectionChanged(object sender, SelectionChangedEventArgs args) => ChangeAppearance();
    private void AppearanceToggleClicked(object sender, RoutedEventArgs args) => ChangeAppearance();

    private void ChangeAppearance()
    {
        if (!_appearanceReady || _closed || BackdropChoice.SelectedIndex is < 0 or > 1 || ThemeChoice.SelectedIndex is < 0 or > 2) return;
        _appearance = new AppearancePreferences(BackdropEnabled.IsChecked == true,
            (BackdropMaterial)BackdropChoice.SelectedIndex, (AppearanceTheme)ThemeChoice.SelectedIndex);
        ApplyAppearance();
        SaveAppearance();
    }

    private void ResetAppearance(object sender, RoutedEventArgs args)
    {
        if (!_appearanceReady || _closed) return;
        _appearance = new AppearancePreferences();
        SetAppearanceControls();
        ApplyAppearance();
        SaveAppearance();
    }

    private void ApplyAppearance()
    {
        Root.RequestedTheme = _appearance.Theme switch
        {
            AppearanceTheme.Light => ElementTheme.Light,
            AppearanceTheme.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
        BackdropChoice.IsEnabled = _appearance.BackdropEnabled;
        try
        {
            if (!_appearance.BackdropEnabled)
            {
                UseSolidAppearance();
                _appearanceResult = L.T("已关闭背景材质，使用随主题切换的纯色背景。");
            }
            else
            {
                var mica = _appearance.Material == BackdropMaterial.Mica;
                var supported = mica ? MicaController.IsSupported() : DesktopAcrylicController.IsSupported();
                if (!supported)
                {
                    UseSolidAppearance();
                    _appearanceResult = L.F($"当前 Windows 环境不支持 {(mica ? L.T("Mica 云母") : L.T("Acrylic 亚克力"))}，已回退为纯色背景。");
                }
                else
                {
                    if (mica && SystemBackdrop is not MicaBackdrop) SystemBackdrop = new MicaBackdrop();
                    else if (!mica && SystemBackdrop is not DesktopAcrylicBackdrop) SystemBackdrop = new DesktopAcrylicBackdrop();
                    // A local transparent brush reveals the native material beneath the XAML content.
                    Root.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                    _appearanceResult = L.F($"已启用 {(mica ? L.T("Mica 云母") : L.T("Acrylic 亚克力"))}。Windows 会根据透明效果、对比度和电源等系统设置自动调整材质。");
                }
            }
        }
        catch (Exception)
        {
            UseSolidAppearance();
            _appearanceResult = L.T("当前环境无法启用背景材质，已回退为纯色背景。");
        }
        UpdateAppearanceStatus();
    }

    private void UseSolidAppearance()
    {
        // Root.Style owns a ThemeResource setter. Removing only the local value keeps that live
        // resource expression, including explicit Light/Dark themes and later system theme changes.
        Root.ClearValue(Panel.BackgroundProperty);
        try { SystemBackdrop = null; }
        catch (Exception) { /* The opaque theme brush still hides a backdrop if native teardown fails. */ }
    }

    private void AppearanceActualThemeChanged(FrameworkElement sender, object args)
    {
        Root.DispatcherQueue.TryEnqueue(() => { if (!_closed) TextFlyoutTheme.Refresh(Root); });
        if (!_appearanceReady || _closed) return;
        UpdateAppearanceStatus();
    }

    private void SaveAppearance()
    {
        try
        {
            _appearanceStore.Save(_appearance);
            _appearancePersistenceWarning = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _appearancePersistenceWarning = L.T("当前外观已应用，但未能保存；下次启动可能恢复原设置。");
            ShowNotice(L.T("外观设置未保存"), ex.Message, InfoBarSeverity.Warning);
        }
        UpdateAppearanceStatus();
    }

    private void UpdateAppearanceStatus()
    {
        var theme = Root.ActualTheme == ElementTheme.Dark ? L.T("深色") : L.T("浅色");
        AppearanceStatus.Text = L.F($"{_appearanceResult}\n当前主题：{theme}") +
            (_appearance.Theme == AppearanceTheme.System ? L.T("（跟随系统）") : "") +
            (string.IsNullOrEmpty(_appearancePersistenceWarning) ? "" : $"\n{_appearancePersistenceWarning}");
    }
}
