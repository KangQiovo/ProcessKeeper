using ProcessKeeper.Core;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.System;
using Microsoft.UI.Windowing;
using System.Runtime.InteropServices;

namespace ProcessKeeper.App;

public sealed partial class MainWindow
{
    private readonly AppearancePreferencesStore _appearanceStore = new();
    private AppearancePreferences _appearance = new();
    private bool _appearanceReady;
    private string _appearanceResult = "";
    private string? _appearancePersistenceWarning;
    private readonly DispatcherTimer _appearanceSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private bool _appearanceSavePending;
    private bool _updatingMaterialControls;
    private bool? _micaSupported, _acrylicSupported;
    private ThemeSettings? _captionThemeSettings;
    private Microsoft.UI.Dispatching.DispatcherQueue? _captionThemeDispatcher;
    private bool _captionThemeClosed;

    private AppearancePreferences CurrentAppearance => _appearance;

    private void ApplyImportedAppearance(AppearancePreferences value)
    {
        CancelPendingAppearanceSave();
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
        InitializeCaptionTheme();
        _appearanceSaveTimer.Tick += (_, _) => FlushAppearanceSave();
        Closed += (_, _) =>
        {
            _captionThemeClosed = true;
            _captionThemeDispatcher = null;
            var captionSettings = _captionThemeSettings;
            _captionThemeSettings = null;
            try { if (captionSettings is not null) captionSettings.Changed -= CaptionThemeSettingsChanged; }
            catch (Exception ex) when (IsCaptionInteropFailure(ex)) { }
            FlushAppearanceSave();
            _appearanceReady = false;
            Root.ActualThemeChanged -= AppearanceActualThemeChanged;
        };
        ApplyAppearance();
        _appearanceReady = true;
    }

    private static bool IsCaptionInteropFailure(Exception exception) =>
        exception is COMException or InvalidOperationException or NotSupportedException or ArgumentException;

    private void InitializeCaptionTheme()
    {
        try
        {
            if (!AppWindowTitleBar.IsCustomizationSupported()) return;
            _captionThemeDispatcher = Root.DispatcherQueue;
            _captionThemeSettings = ThemeSettings.CreateForWindowId(AppWindow.Id);
            _captionThemeSettings.Changed += CaptionThemeSettingsChanged;
        }
        catch (Exception ex) when (IsCaptionInteropFailure(ex))
        {
            _captionThemeSettings = null;
        }
    }

    private void CaptionThemeSettingsChanged(ThemeSettings sender, object args)
    {
        if (_captionThemeClosed) return;
        try
        {
            _captionThemeDispatcher?.TryEnqueue(() =>
            {
                if (!_captionThemeClosed && ReferenceEquals(sender, _captionThemeSettings)) UpdateCaptionTheme();
            });
        }
        catch (Exception ex) when (IsCaptionInteropFailure(ex)) { }
    }

    private void UpdateCaptionTheme()
    {
        if (_captionThemeClosed || _closed) return;
        try
        {
            if (!AppWindowTitleBar.IsCustomizationSupported()) return;
            // Keep the system palette in high contrast or when theme information is unavailable.
            // Native button colors remain unset so Windows owns hover, pressed and inactive states.
            var theme = TitleBarTheme.Legacy;
            try
            {
                if (_captionThemeSettings is { HighContrast: false })
                    theme = Root.ActualTheme == ElementTheme.Light ? TitleBarTheme.Light : TitleBarTheme.Dark;
            }
            catch (Exception ex) when (IsCaptionInteropFailure(ex)) { }
            AppWindow.TitleBar.PreferredTheme = theme;
        }
        catch (Exception ex) when (IsCaptionInteropFailure(ex)) { /* Retain native caption behavior. */ }
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
            CustomBackdropEnabled.IsChecked = _appearance.CustomBackdropEnabled;
            TintOpacitySlider.Value = _appearance.TintOpacity * 100;
            LuminosityOpacitySlider.Value = _appearance.LuminosityOpacity * 100;
            UpdateMaterialControlValues();
        }
        finally { _appearanceReady = ready; }
    }

    private void AppearanceSelectionChanged(object sender, SelectionChangedEventArgs args) => ChangeAppearance();
    private void AppearanceToggleClicked(object sender, RoutedEventArgs args) => ChangeAppearance();

    private void ChangeAppearance()
    {
        if (!_appearanceReady || _closed || BackdropChoice.SelectedIndex is < 0 or > 1 || ThemeChoice.SelectedIndex is < 0 or > 2) return;
        _appearance = _appearance with { BackdropEnabled = BackdropEnabled.IsChecked == true,
            Material = (BackdropMaterial)BackdropChoice.SelectedIndex, Theme = (AppearanceTheme)ThemeChoice.SelectedIndex,
            CustomBackdropEnabled = CustomBackdropEnabled.IsChecked == true };
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

    private void MaterialOpacityChanged(object sender, RangeBaseValueChangedEventArgs args)
    {
        if (!_appearanceReady || _closed || _updatingMaterialControls) return;
        _appearance = _appearance with { TintOpacity = TintOpacitySlider.Value / 100, LuminosityOpacity = LuminosityOpacitySlider.Value / 100 };
        ApplyAppearance();
        _appearanceSavePending = true;
        _appearanceSaveTimer.Stop(); _appearanceSaveTimer.Start();
    }

    private void MaterialColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (!_appearanceReady || _closed || _updatingMaterialControls) return;
        _appearance = _appearance with { TintColor = $"#{args.NewColor.R:X2}{args.NewColor.G:X2}{args.NewColor.B:X2}" };
        ApplyAppearance();
        _appearanceSavePending = true;
        _appearanceSaveTimer.Stop(); _appearanceSaveTimer.Start();
    }

    private void ApplyMaterialColor(object sender, RoutedEventArgs args)
    {
        if (!_appearanceReady || _closed || !TintColorPicker.IsEnabled) return;
        var color = TintColorPicker.Color;
        _appearance = _appearance with { TintColor = $"#{color.R:X2}{color.G:X2}{color.B:X2}" };
        ApplyAppearance(); SaveAppearance(); TintColorButton.Flyout.Hide();
    }
    private void FollowMaterialTheme(object sender, RoutedEventArgs args)
    {
        if (!_appearanceReady || _closed) return;
        _appearance = _appearance with { TintColor = null };
        ApplyAppearance(); SaveAppearance();
    }

    private void RestoreNativeMaterial(object sender, RoutedEventArgs args)
    {
        if (!_appearanceReady || _closed) return;
        _appearance = _appearance with { CustomBackdropEnabled = false, TintOpacity = .8, LuminosityOpacity = .85, TintColor = null };
        SetAppearanceControls(); ApplyAppearance(); SaveAppearance();
    }

    private void CancelPendingAppearanceSave() { _appearanceSaveTimer.Stop(); _appearanceSavePending = false; }
    private void FlushAppearanceSave() { if (_appearanceSavePending) SaveAppearance(); else _appearanceSaveTimer.Stop(); }

    private void UpdateMaterialControlValues()
    {
        _updatingMaterialControls = true;
        try
        {
            TintOpacityValue.Text = (_appearance.TintOpacity * 100).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "%";
            LuminosityOpacityValue.Text = (_appearance.LuminosityOpacity * 100).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "%";
            if (_appearance.TintColor is { } value)
            {
                var color = Windows.UI.Color.FromArgb(255, Convert.ToByte(value.Substring(1, 2), 16), Convert.ToByte(value.Substring(3, 2), 16), Convert.ToByte(value.Substring(5, 2), 16));
                TintColorPicker.Color = color; TintColorPreview.Background = new SolidColorBrush(color);
            }
            else
            {
                TintColorPreview.ClearValue(Border.BackgroundProperty);
                // This native theme swatch is a picker starting point, not the material's effective RGB.
                // Clearing an explicit color must also clear the picker's stale selection, so selecting
                // a different former color raises ColorChanged; Apply also commits the current initial RGB.
                if (TintColorPreview.Background is SolidColorBrush themeSwatch)
                    TintColorPicker.Color = Windows.UI.Color.FromArgb(255, themeSwatch.Color.R, themeSwatch.Color.G, themeSwatch.Color.B);
            }
            TintColorValue.Text = _appearance.TintColor ?? L.T("跟随主题");
        }
        finally { _updatingMaterialControls = false; }
    }

    private void ApplyAppearance()
    {
        Root.RequestedTheme = _appearance.Theme switch
        {
            AppearanceTheme.Light => ElementTheme.Light,
            AppearanceTheme.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
        UpdateCaptionTheme();
        BackdropChoice.IsEnabled = _appearance.BackdropEnabled;
        var materialReady = false;
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
                var supported = IsBackdropSupported(_appearance.Material);
                if (!supported)
                {
                    UseSolidAppearance();
                    _appearanceResult = L.F($"当前 Windows 环境不支持 {(mica ? L.T("Mica 云母") : L.T("Acrylic 亚克力"))}，已回退为纯色背景。");
                }
                else
                {
                    if (_appearance.CustomBackdropEnabled)
                    {
                        if (SystemBackdrop is NativeMaterialBackdrop custom && custom.Material == _appearance.Material) custom.Update(_appearance);
                        else
                        {
                            var material = _appearance.Material;
                            var backdrop = new NativeMaterialBackdrop(_appearance, () => MaterialThemeColors(material));
                            backdrop.AppearanceChanged += () => Root.DispatcherQueue.TryEnqueue(() =>
                            { if (!_closed && ReferenceEquals(SystemBackdrop, backdrop)) { UpdateMaterialReadabilitySurface(); UpdateMaterialControlValues(); } });
                            SystemBackdrop = backdrop;
                        }
                    }
                    else if (mica && SystemBackdrop is not MicaBackdrop) SystemBackdrop = new MicaBackdrop();
                    else if (!mica && SystemBackdrop is not DesktopAcrylicBackdrop) SystemBackdrop = new DesktopAcrylicBackdrop();
                    // A local transparent brush reveals the native material beneath the XAML content.
                    if (Root.Background is not SolidColorBrush { Color.A: 0 }) Root.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                    materialReady = true;
                    _appearanceResult = L.F($"已启用 {(mica ? L.T("Mica 云母") : L.T("Acrylic 亚克力"))}。Windows 会根据透明效果、对比度和电源等系统设置自动调整材质。");
                }
            }
        }
        catch (Exception)
        {
            UseSolidAppearance();
            _appearanceResult = L.T("当前环境无法启用背景材质，已回退为纯色背景。");
        }
        CustomBackdropEnabled.IsEnabled = _appearance.BackdropEnabled && materialReady;
        // A uniform WinUI theme layer protects every content area from arbitrary user tint or
        // a fully transparent native material without changing the user's theme or parameters.
        UpdateMaterialReadabilitySurface();
        var parametersEnabled = CustomBackdropEnabled.IsEnabled && _appearance.CustomBackdropEnabled;
        TintOpacitySlider.IsEnabled = LuminosityOpacitySlider.IsEnabled = TintColorButton.IsEnabled = TintColorPicker.IsEnabled = TintApplyButton.IsEnabled = TintThemeButton.IsEnabled = parametersEnabled;
        NativeDefaultsButton.IsEnabled = CustomBackdropEnabled.IsEnabled;
        UpdateMaterialControlValues();
        UpdateAppearanceStatus();
    }

    private bool IsBackdropSupported(BackdropMaterial material) => material == BackdropMaterial.Mica
        ? (_micaSupported ??= MicaController.IsSupported())
        : (_acrylicSupported ??= DesktopAcrylicController.IsSupported());

    private (Windows.UI.Color Tint, Windows.UI.Color Fallback) MaterialThemeColors(BackdropMaterial material)
    {
        // Resolve live ThemeResources in this window rather than copying controller getters:
        // native customization disables automatic Light/Dark values for all four properties.
        if (material == BackdropMaterial.Acrylic && MaterialAcrylicPalette.Background is AcrylicBrush acrylic)
            return (acrylic.TintColor, acrylic.FallbackColor);
        var color = ((SolidColorBrush)MaterialReadabilitySurface.Background).Color;
        return (color, color);
    }

    private void UpdateMaterialReadabilitySurface() => MaterialReadabilitySurface.Visibility =
        _appearance.BackdropEnabled && _appearance.CustomBackdropEnabled && SystemBackdrop is NativeMaterialBackdrop { IsHighContrast: false }
        ? Visibility.Visible : Visibility.Collapsed;

    private void UseSolidAppearance()
    {
        // Root.Style owns a ThemeResource setter. Removing only the local value keeps that live
        // resource expression, including explicit Light/Dark themes and later system theme changes.
        Root.ClearValue(Panel.BackgroundProperty);
        MaterialReadabilitySurface.Visibility = Visibility.Collapsed;
        try { SystemBackdrop = null; }
        catch (Exception) { /* The opaque theme brush still hides a backdrop if native teardown fails. */ }
    }

    private void AppearanceActualThemeChanged(FrameworkElement sender, object args)
    {
        UpdateCaptionTheme();
        Root.DispatcherQueue.TryEnqueue(() => { if (!_closed) TextFlyoutTheme.Refresh(Root); });
        if (!_appearanceReady || _closed) return;
        if (_appearance.BackdropEnabled && _appearance.CustomBackdropEnabled && SystemBackdrop is NativeMaterialBackdrop custom && custom.Material == _appearance.Material)
        {
            try { custom.Update(_appearance); }
            catch (Exception)
            {
                UseSolidAppearance();
                CustomBackdropEnabled.IsEnabled = TintOpacitySlider.IsEnabled = LuminosityOpacitySlider.IsEnabled = TintColorButton.IsEnabled = TintColorPicker.IsEnabled = TintApplyButton.IsEnabled = TintThemeButton.IsEnabled = NativeDefaultsButton.IsEnabled = false;
                _appearanceResult = L.T("当前环境无法启用背景材质，已回退为纯色背景。");
            }
        }
        UpdateMaterialControlValues();
        UpdateAppearanceStatus();
    }

    private void SaveAppearance()
    {
        CancelPendingAppearanceSave();
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
