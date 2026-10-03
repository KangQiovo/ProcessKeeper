using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using ProcessKeeper.Core;

namespace ProcessKeeper.App;

public sealed partial class MainWindow
{
    private readonly CancellationTokenSource _riskModeLifetime = new();

    private void InitializeRiskMode()
    {
        RiskModeNotice.Attach(this, RiskNotice);
        // Keep the native Button template while making danger/hover/pressed states explicit.
        foreach (var key in new[] { "ButtonBackground", "ButtonBackgroundPointerOver", "ButtonBackgroundPressed" })
            RiskModeButton.Resources[key] = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 164, 38, 44));
        foreach (var key in new[] { "ButtonForeground", "ButtonForegroundPointerOver", "ButtonForegroundPressed" })
            RiskModeButton.Resources[key] = new SolidColorBrush(Microsoft.UI.Colors.White);
        void Update(object? sender, EventArgs args) => RiskModeButton.Content = L.T(RiskConfirmationMode.IsEnabled ? "恢复风险确认" : "无视风险模式");
        Update(null, EventArgs.Empty);
        RiskConfirmationMode.Changed += Update;
        Closed += (_, _) => { RiskConfirmationMode.Changed -= Update; _riskModeLifetime.Cancel(); _riskModeLifetime.Dispose(); };
    }

    private async void ChangeRiskMode(object sender, RoutedEventArgs args)
    {
        if (_closed || _working || _dialogOpen || _windowOperationRunning || _autorunsView?.IsChanging == true) return;
        if (RiskConfirmationMode.IsEnabled)
        {
            RiskConfirmationMode.Disable();
            Log(L.T("无视风险模式已关闭"));
            return;
        }
        RiskModeButton.IsEnabled = false;
        try
        {
            if (await RiskModeConfirmation.ShowAsync(Root.XamlRoot, Root.ActualTheme, ShowDialog, _riskModeLifetime.Token) && !_closed)
            {
                RiskConfirmationMode.Enable();
                Log(L.T("无视风险模式已启用"));
            }
        }
        catch (Exception exception) { if (!_closed) ShowNotice(L.T("操作中止"), exception.Message, InfoBarSeverity.Error); }
        finally { if (!_closed) RiskModeButton.IsEnabled = true; }
    }
}
