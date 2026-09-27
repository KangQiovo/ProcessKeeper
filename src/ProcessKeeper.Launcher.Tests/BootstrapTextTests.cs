using System.Text;
using ProcessKeeper.Launcher;

internal static class BootstrapTextTests
{
    internal static void Run()
    {
        var passed = 0;
        void Check(bool value, string description)
        {
            if (!value) throw new Exception("FAIL: " + description);
            Console.WriteLine("PASS " + description);
            passed++;
        }
        string Read(string json) => LauncherBootstrapText.ReadPreference(Encoding.UTF8.GetBytes(json));
        foreach (var preference in new[] { "auto", "en", "zh-Hans", "zh-Hant" })
            Check(Read("{\"Language\":\"" + preference + "\"}") == preference, "accept language " + preference);
        foreach (var json in new[] { "", "{", "null", "[]", "{}", "{\"Language\":true}", "{\"Language\":\"fr\"}", "{\"Language\":\"EN\"}", "{\"Language\":\"en\",\"Language\":\"zh-Hans\"}" })
            Check(Read(json) == "auto", "invalid settings fall back to automatic: " + json);
        Check(Read("{\"Language\":\"zh-Hant\",\"Executable\":\"bad.exe\"}") == "zh-Hant", "only language read from settings");
        Check(Read(new string(' ', LauncherBootstrapText.MaximumSettingsBytes + 1)) == "auto", "oversized settings rejected");
        Check(Read("{\"Language\":\"en\"}".PadRight(LauncherBootstrapText.MaximumSettingsBytes)) == "en", "exact settings size limit accepted");
        foreach (var row in new[] {
            ("auto", "en-US", "CN", "China Standard Time", "en"),
            ("auto", "zh-CN", "TW", "Taipei Standard Time", "zh-Hans"),
            ("auto", "zh-TW", "CN", "China Standard Time", "zh-Hant"),
            ("auto", "zh-Hant-HK", "CN", "China Standard Time", "zh-Hant"),
            ("auto", "zh_SG", "TW", "Taipei Standard Time", "zh-Hans"),
            ("auto", "ja-JP", "CN", "China Standard Time", "en"),
            ("auto", "", "CN", "", "zh-Hans"),
            ("auto", "", "TW", "", "zh-Hant"),
            ("auto", "", "", "Asia/Taipei", "zh-Hant"),
            ("auto", "iv", "", "Asia/Shanghai", "zh-Hans"),
            ("en", "zh-CN", "CN", "China Standard Time", "en"),
            ("zh-Hant", "en-US", "US", "", "zh-Hant") })
            Check(LauncherBootstrapText.Resolve(row.Item1, row.Item2, row.Item3, row.Item4) == row.Item5, "system language precedence " + row);
        Check(LauncherBootstrapText.Text(LauncherBootstrapText.FailureTitle, "en") == "Process Keeper | Startup failed", "English branding");
        Check(LauncherBootstrapText.Text(LauncherBootstrapText.FailureTitle, "zh-Hans") == "Process Keeper | 启动失败", "simplified title with English branding");
        Check(LauncherBootstrapText.Text(LauncherBootstrapText.FailureTitle, "zh-Hant") == "Process Keeper | 啟動失敗", "traditional conversion and NUL handling");
        const string path = @"E:\测试\資料\file.exe";
        Check(LauncherBootstrapText.Text("Cache file verification failed: " + path, "zh-Hant").EndsWith(path, StringComparison.Ordinal), "dynamic path preserved verbatim");
        const string unknown = "Windows error 123 | untouched";
        Check(LauncherBootstrapText.Text(unknown, "zh-Hant") == unknown, "unknown system errors unchanged");
        Check(!LauncherBootstrapText.Text("The original launcher has changed. Reopen the original single-file application.", "zh-Hans").Contains("Reopen"), "longer known error translated completely");
        Check(LauncherBootstrapText.Text("The original launcher has changed. Unknown extra text", "zh-Hans").StartsWith("The original", StringComparison.Ordinal), "unknown extensions not partially translated");
        Console.WriteLine($"Bootstrap text: {passed} checks passed. Text-only coverage; IPC regression is separate.");
    }
}
