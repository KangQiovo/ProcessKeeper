namespace ProcessKeeper.Core;

public static class InstalledExecutableLabels
{
    public static string Kind(InstalledExecutableRoleIdentity identity) => identity.Role switch
    {
        InstalledExecutableRole.PotentialMain => "PossibleMain",
        InstalledExecutableRole.PotentialUninstaller => "PossibleUninstaller",
        _ => identity.Role.ToString()
    };
    public static string Text(InstalledExecutableRoleIdentity identity) => L.T(identity.Role switch
    {
        InstalledExecutableRole.Main => "主程序",
        InstalledExecutableRole.PotentialMain => "可能的主程序",
        InstalledExecutableRole.Uninstaller => "卸载程序",
        InstalledExecutableRole.PotentialUninstaller => "可能的卸载程序",
        InstalledExecutableRole.Diagnostic => "诊断工具",
        InstalledExecutableRole.Helper => "辅助组件",
        _ => "用途未确认"
    });
    public static string Tooltip(InstalledExecutableRoleIdentity identity)
    {
        var evidence = L.T(identity.Evidence switch
        {
            InstalledExecutableEvidence.ExplicitEntry => "来自已安装应用的启动入口。",
            InstalledExecutableEvidence.RegisteredUninstaller => "与注册的卸载命令路径一致。",
            InstalledExecutableEvidence.AssociatedMain => "卸载登记已匹配到此应用文件。",
            InstalledExecutableEvidence.KnownFileName => "与已知产品入口文件名匹配。",
            InstalledExecutableEvidence.ApplicationName => "文件名与应用名称匹配。",
            InstalledExecutableEvidence.ProductDescription => "文件说明与应用名称匹配。",
            InstalledExecutableEvidence.UtilityFileName => "文件名显示工具用途。",
            _ => "现有文件名和登记信息不足以确认用途。"
        });
        return evidence + " " + L.T("文件用途标签不代表它当前正在运行，也不保证它能成功启动。") +
            (identity.IsUncertain ? " " + L.T("可能识别有误，请谨慎甄别。") : "");
    }
}
