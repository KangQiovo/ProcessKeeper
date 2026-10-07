using System.Windows;
using System.Windows.Controls;
using ProcessKeeper.Core;

namespace ProcessKeeper.App;

public partial class MainWindow
{
    private static IEnumerable<string> DistributionBadges(UpdateRuntimeIdentity runtime)
    {
        if (runtime.PackageFlavor == UpdatePackageTarget.Universal) yield return L.T("合包");
        if (runtime.DistributionKind == UpdateDistributionKind.Unknown)
        {
            yield return L.T("运行方式未验证");
            if (runtime.CompatibilityUi) yield return L.T("兼容界面");
        }
        else if (runtime.DistributionKind == UpdateDistributionKind.Installer)
            yield return L.T(runtime.CompatibilityUi ? "兼容安装版" : "安装版");
        else yield return L.T(runtime.CompatibilityUi && runtime.PackageFlavor != UpdatePackageTarget.Universal ? "兼容免安装版" : "免安装版");
    }

    private WrapPanel BuildAboutVersion()
    {
        var line = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Left };
        line.Children.Add(Text(CurrentAppVersion + " | .NET Framework 4.6.2 | " + L.T(Environment.Is64BitProcess ? "64 位" : "32 位")));
        foreach (var label in DistributionBadges(_backend.Updates.RuntimeIdentity()))
        {
            var badge = new Border { CornerRadius = new CornerRadius(4), Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(8, 0, 0, 12), VerticalAlignment = VerticalAlignment.Top };
            badge.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
            var text = new TextBlock { Text = label, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            text.SetResourceReference(TextBlock.ForegroundProperty, "InkBrush"); badge.Child = text; line.Children.Add(badge);
        }
        return line;
    }

    private async Task<bool> ConfirmUpdatePackageChangeAsync(UpdateAsset asset, UpdateRuntimeIdentity runtime)
    {
        if (!UpdatePackagePolicy.IsPackageChange(asset, runtime)) return true;
        var detail = L.T("所选更新包与当前运行的包类型不同，可能改变安装方式或兼容界面。确认后继续。") +
            "\n\n" + string.Join(" | ", DistributionBadges(runtime)) + "\n" + asset.Name;
        if (PreservesInstallation(asset, runtime)) detail += "\n\n" + InstalledPayloadNotice();
        return await Confirm(L.T("切换更新包类型？"), detail);
    }
    private static bool PreservesInstallation(UpdateAsset asset, UpdateRuntimeIdentity runtime) =>
        runtime.DistributionKind == UpdateDistributionKind.Installer && asset.DistributionKind == UpdateDistributionKind.Portable;
    private static string InstalledPayloadNotice() => L.T("当前安装目录会继续更新，原有卸载入口与安装状态会保留。") + "\n" +
        L.T("如需独立的免安装副本，请另外手动下载到其他目录。");
}
