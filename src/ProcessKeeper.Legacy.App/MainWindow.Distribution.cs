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
        line.Children.Add(Text(DisplayAboutVersion(CurrentAppVersion) + " | .NET Framework 4.6.2 | " + L.T(Environment.Is64BitProcess ? "64 位" : "32 位")));
        foreach (var label in DistributionBadges(_backend.Updates.RuntimeIdentity()))
        {
            var badge = new Border { CornerRadius = new CornerRadius(4), Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(8, 0, 0, 12), VerticalAlignment = VerticalAlignment.Top };
            badge.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
            var text = new TextBlock { Text = label, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            text.SetResourceReference(TextBlock.ForegroundProperty, "InkBrush"); badge.Child = text; line.Children.Add(badge);
        }
        return line;
    }
    private static string DisplayAboutVersion(string version) => ProcessKeeper.Core.ReleaseIdentity.DisplayVersion(version.Split('+')[0]);
    internal static string DisplayUpdateReleaseTag(UpdateRelease release) => release.Tag == "v" + release.Version
        ? "v" + ProcessKeeper.Core.ReleaseIdentity.DisplayVersion(release.Version) : release.Tag;

    private void AddAboutBuildHash(StackPanel about)
    {
        var information = System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(typeof(MainWindow).Assembly)?.InformationalVersion;
        var hash = ReadAboutBuildHash(information);
        if (hash is null) return;
        var line = new TextBox { Tag = "about-build-hash", Text = L.F($"构建哈希：{hash}"), IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap, FontSize = 11, BorderThickness = new Thickness(0), Padding = new Thickness(0),
            MinHeight = 0, Background = System.Windows.Media.Brushes.Transparent, Margin = new Thickness(0, 0, 0, 12), HorizontalAlignment = HorizontalAlignment.Stretch };
        line.SetResourceReference(TextBox.ForegroundProperty, "MutedBrush"); about.Children.Add(line);
    }
    private static string? ReadAboutBuildHash(string? information)
    {
        if (!UpdateVersion.TryParse(information, out _) || information is null) return null;
        var separator = information.IndexOf('+');
        if (separator < 0) return null;
        var hash = information.Substring(separator + 1);
        return (hash.Length == 40 || hash.Length == 64) && hash.All(Uri.IsHexDigit) ? hash : null;
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
