using System.Text;
using System.Text.RegularExpressions;

namespace ProcessKeeper.Core;

public enum UninstallRecommendationBasis { UserWatchlist, PublishedRiskReport }
public sealed record UninstallRecommendationRule(string Id, UninstallRecommendationBasis Basis, IReadOnlyList<string> Names, string Reason, string SourceUrl, string SourcePublishedOn = "", string EnglishReason = "", string TraditionalReason = "");
public sealed record UninstallRecommendation(UninstallRecommendationRule Rule, string MatchedName);

/// <summary>
/// Offline registration-name hints, not file signatures or a malware scanner.
/// Each public report is checked against a named product, never a whole vendor or category.
/// Watchlist entries express a review preference and do not assert that a product is malware.
/// </summary>
public static class UninstallRecommendations
{
    private static CatalogData? _downloaded;
    public static CatalogMetadata Metadata => Volatile.Read(ref _downloaded)?.Metadata ?? CatalogUpdater.BuiltInMetadata();
    public static string Version => Metadata.Version;
    public static string SourcesCheckedOn => Metadata.CheckedOn;
    internal static void Activate(CatalogData data) => Volatile.Write(ref _downloaded, data);
    private const string Malwarebytes = "https://www.malwarebytes.com/blog/detections/";
    private static readonly Regex VersionSuffix = new(@"^\s+(?:(?:v|version|版本)\s*)?[0-9]+(?:[._-][0-9]+)*(?:\s*\((?:x86|x64|32-bit|64-bit|32位|64位)\))?$|^\s*\((?:x86|x64|32-bit|64-bit|32位|64位)\)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
    private static readonly Regex Spaces = new(@"\s+", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
    private static UninstallRecommendationRule Watch(string id, params string[] names) => new(id, UninstallRecommendationBasis.UserWatchlist,
        Array.AsReadOnly(names), "用户关注名单中的具体产品；请核对是否自行安装及仍需使用，不代表病毒判定。", "");
    private static UninstallRecommendationRule Report(string id, string reason, params string[] names) => new(id, UninstallRecommendationBasis.PublishedRiskReport,
        Array.AsReadOnly(names), reason, Malwarebytes + "pup-optional-" + id);
    private static UninstallRecommendationRule Huorong(string id, string article, string date, params string[] names) => new(id, UninstallRecommendationBasis.PublishedRiskReport,
        Array.AsReadOnly(names), "火绒历史报告记录相关安装包的推广、捆绑或恶意模块；渠道、版本可能不同，需核对当前安装来源，不能据名称认定当前文件有毒。",
        "https://www.huorong.cn/document/tech/" + article, date);
    private static UninstallRecommendationRule QihooReport(string id, string reason, params string[] names) => new(id, UninstallRecommendationBasis.PublishedRiskReport,
        Array.AsReadOnly(names), reason, "https://www.360.cn/n/11465.html", "2020-01-03");
    public static IReadOnlyList<UninstallRecommendationRule> Rules => Volatile.Read(ref _downloaded)?.Rules ?? BuiltInRules;
    internal static IReadOnlyList<UninstallRecommendationRule> BuiltInRules { get; } = Array.AsReadOnly(new[]
    {
        Watch("360-security", "360安全卫士", "360 安全卫士", "360安全卫士极速版", "360 Total Security", "360 Internet Security"),
        Watch("360-antivirus", "360杀毒", "360 杀毒"),
        Watch("kingsoft-duba", "金山毒霸", "新毒霸", "Kingsoft Antivirus", "Kingsoft Duba"),
        Watch("2345-security", "2345安全卫士", "2345 安全卫士", "2345安全中心"),
        Huorong("2345-browser", "vir_report/723", "2018-10-25", "2345浏览器", "2345加速浏览器", "2345王牌浏览器"),
        Huorong("2345-compression", "vir_report/723", "2018-10-25", "2345好压", "好压", "好压软件"),
        Huorong("2345-image", "vir_report/723", "2018-10-25", "2345看图", "2345看图王"),
        Huorong("2345-input", "vir_report/723", "2018-10-25", "2345拼音", "2345王牌输入法"),
        Watch("2345-assistant", "2345软件管家"),
        Watch("kingsoft-assistant", "金山软件管家"),
        Watch("drivergenius", "驱动精灵", "驱动精灵万能网卡版"),
        Watch("driverlife", "驱动人生", "驱动人生8", "驱动人生9"),
        Huorong("ludashi", "vir_report/1858", "2025-11-11", "鲁大师"),
        Huorong("fastzip", "vir_report/1669.html", "2018-07-11", "快压", "快压软件", "速压"),
        Watch("xiaoyu-zip", "小鱼压缩"),
        Watch("xiaobai-zip", "小白压缩"),
        Watch("kantu", "万能看图王"),
        Watch("hezip", "嗨格式压缩大师"),
        Report("segurazo", "Malwarebytes 收录 Segurazo 的潜在不受欢迎程序报告。", "Segurazo", "Segurazo Antivirus"),
        Report("wajam", "公开报告描述广告注入及浏览数据收集行为。", "Wajam"),
        Report("relevantknowledge", "公开报告描述捆绑分发及网络使用数据收集。", "RelevantKnowledge", "Relevant Knowledge"),
        Report("webcompanion", "公开报告描述捆绑安装及默认搜索设置变更。", "Web Companion", "WebCompanion", "Adaware Web Companion", "Lavasoft Web Companion"),
        Report("pcacceleratepro", "公开报告描述以误导性系统问题提示推销优化软件。", "PC Accelerate Pro"),
        Report("mypcbackup", "公开报告描述捆绑分发及反复付费注册提醒。", "MyPC Backup", "MyPCBackup"),
        Report("reimage", "Malwarebytes 将相关 Windows 系统优化器列为潜在不受欢迎程序。", "Reimage Repair", "Reimage PC Repair", "Reimage"),
        Report("onestart", "公开报告描述浏览器额外广告及可能的捆绑分发。", "OneStart"),
        Report("onelaunch", "公开报告描述安装程序未经同意更改浏览器设置。", "OneLaunch"),
        Report("bytefence", "公开报告描述反复弹出感染提示以诱导购买。", "ByteFence Anti-Malware", "ByteFence"),
        Report("crossbrowse", "公开报告描述替换浏览器以增加广告展示。", "CrossBrowse"),
        Report("searchprotect", "公开报告描述捆绑分发及浏览器劫持。", "Search Protect", "SearchProtect", "Search Protect by Conduit"),
        Huorong("xiaoniao-wallpaper", "vir_report/1858", "2025-11-11", "小鸟壁纸"),
        Huorong("dxrepair", "vir_report/1858", "2025-11-11", "DXRepair"),
        Huorong("tabxexplorer", "vir_report/1858", "2025-11-11", "TabXExplorer"),
        Huorong("huaban-eyecare", "vir_report/1858", "2025-11-11", "花瓣护眼"),
        Huorong("xiaolange", "vir_report/1858", "2025-11-11", "小蓝鸽"),
        Huorong("dali-shredder", "vir_report/1858", "2025-11-11", "大力文件粉碎"),
        Huorong("xundu-pdf", "vir_report/1858", "2025-11-11", "迅读PDF大师", "迅读 PDF 大师"),
        Huorong("halo-wallpaper", "vir_report/1858", "2025-11-11", "Halo壁纸", "Halo 壁纸"),
        Huorong("mobile-emulator-master", "vir_report/1858", "2025-11-11", "手机模拟大师"),
        Huorong("universal-compression", "vir_report/826", "2020-06-18", "万能压缩"),
        Huorong("qidian-pdf", "vir_report/826", "2020-06-18", "起点PDF阅读器", "起点 PDF 阅读器"),
        Huorong("mini-image-viewer", "vir_report/826", "2020-06-18", "迷你看图王"),
        Huorong("xinsu-compression", "vir_report/826", "2020-06-18", "新速压缩"),
        Huorong("zhigou-assistant", "vir_report/826", "2020-06-18", "直购助手"),
        Huorong("ant-compression", "safety-classroom/1714", "2020-11-19", "Ant压缩", "Ant 压缩"),
        Huorong("koala-wallpaper", "safety-classroom/1714", "2020-11-19", "考拉壁纸"),
        Huorong("xiaoshu-pdf", "safety-classroom/1714", "2020-11-19", "小树PDF", "小树 PDF"),
        Huorong("qiaozip", "vir_report/811", "2020-02-27", "巧压"),
        Huorong("winhome", "vir_report/1669.html", "2018-07-11", "WinHome主页卫士", "WinHome 主页卫士"),
        Huorong("xiaohai-notepad", "vir_report/1669.html", "2018-07-11", "小黑记事本"),
        Huorong("abc-image", "vir_report/1669.html", "2018-07-11", "ABC看图", "ABC 看图"),
        Huorong("bluedove-uninstall", "vir_report/1839", "2025-08-14", "小蓝鸽极速卸载", "BlueDove 小蓝鸽极速卸载"),
        Huorong("dx-strong-repair", "vir_report/1839", "2025-08-14", "DX强力修复", "DX 强力修复"),
        Huorong("echofind", "vir_report/1839", "2025-08-14", "EchoFind", "EchoFind 文件搜索", "EchoFind文件搜索"),
        Huorong("yunjiwan", "vir_report/888.html", "2023-12-04", "云即玩", "云即玩模拟器"),
        Huorong("sky-compression", "vir_report/888.html", "2023-12-04", "天空压缩"),
        Huorong("quzip", "vir_report/1666.html", "2020-03-06", "趣压"),
        Huorong("copyrabbit", "vir_report/1666.html", "2020-03-06", "拷贝兔"),
        Huorong("xiaobai-image", "vir_report/1666.html", "2020-03-06", "小白看图"),
        Huorong("yunai-pe", "vir_report/778.html", "2017-12-01", "云爱PE工具箱", "云爱 PE 工具箱"),
        Huorong("meijie-notes", "vir_report/778.html", "2017-12-01", "美捷便签"),
        Huorong("swf-player-wizard", "vir_report/778.html", "2017-12-01", "swf播放精灵", "swf 播放精灵"),
        Huorong("meijie-alarm", "vir_report/778.html", "2017-12-01", "美捷闹钟"),
        Huorong("jike-pdf", "vir_report/858.html", "2022-08-19", "即刻PDF阅读器", "即刻 PDF 阅读器"),
        Huorong("huoying-wallpaper", "vir_report/846.html", "2021-08-26", "火萤视频桌面"),
        Huorong("wifi-share-master", "vir_report/720.html", "2019-03-14", "WIFI共享大师", "WIFI 共享大师"),
        QihooReport("xuanfeng-pdf", "360 历史报告将此产品列为下载站的额外推广安装对象；这是安装来源核对提示，并非文件感染判定。", "旋风PDF", "旋风 PDF"),
        QihooReport("juzhong-wallpaper", "360 历史报告将相关聚众壁纸样本与祸乱僵尸网络关联；请核对来源和版本，并使用安全软件验证当前文件。", "聚众壁纸")
    });
    public static UninstallRecommendation? Match(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name!.Length > 512 || name.Any(char.IsControl)) return null;
        string normalized;
        try { normalized = Spaces.Replace(name.Normalize(NormalizationForm.FormKC).Trim(), " "); }
        catch (ArgumentException) { return null; }
        foreach (var rule in Rules)
        foreach (var alias in rule.Names)
        {
            if (normalized.Equals(alias, StringComparison.OrdinalIgnoreCase)) return new(rule, alias);
            if (!normalized.StartsWith(alias, StringComparison.OrdinalIgnoreCase)) continue;
            var suffix = normalized.Substring(alias.Length);
            try { if (VersionSuffix.IsMatch(suffix)) return new(rule, alias); }
            catch (RegexMatchTimeoutException) { return null; }
        }
        return null;
    }
    public static string Describe(UninstallEntry entry)
    {
        var match = entry.Recommendation ?? Match(entry.Name);
        if (match is null) return "";
        return L.T("建议卸载") + " | " + L.T(match.Rule.Basis == UninstallRecommendationBasis.UserWatchlist ? "用户关注名单" : "公开风险报告") + "\n" +
            L.F($"注册名匹配：{match.MatchedName}") + "\n" + (L.Language == "en" && match.Rule.EnglishReason.Length > 0 ? match.Rule.EnglishReason : L.Language == "zh-Hant" && match.Rule.TraditionalReason.Length > 0 ? match.Rule.TraditionalReason : L.T(match.Rule.Reason)) + "\n" +
            L.T("此标签仅供核对，由你决定是否卸载；注册名可能被冒用，不是文件病毒检测结果。") + "\n" +
            L.F($"规则版本：{Version} | 来源核对：{SourcesCheckedOn}") + (match.Rule.SourcePublishedOn.Length > 0 ? "\n" + L.F($"报告发布：{match.Rule.SourcePublishedOn}") : "") +
            (match.Rule.SourceUrl.Length > 0 ? "\n" + match.Rule.SourceUrl : "");
    }
    public static string Coverage => L.T("离线提示库仅匹配已知注册名及版本后缀，不是完整病毒库；未标记不代表安全。仅标签提示，不自动卸载。") + "\n" +
        L.F($"规则版本：{Version} | 来源核对：{SourcesCheckedOn}") + "\n" + L.T("PUA 不等同于恶意软件；请使用受信任的安全软件核实文件。") + "\nhttps://learn.microsoft.com/en-us/defender-xdr/criteria";
}
