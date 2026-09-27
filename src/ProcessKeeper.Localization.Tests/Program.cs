using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using ProcessKeeper.App;
using ProcessKeeper.Core;

var suite = new Suite();
var root = Path.Combine(Environment.GetEnvironmentVariable("PROCESSKEEPER_LOCALIZATION_TEST_ROOT") ?? Path.GetTempPath(),
    "ProcessKeeper-localization-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var sourceRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../.."));
var languages = new[] { "en", "zh-Hans", "zh-Hant" };
var preferences = new[] { "auto", "en", "zh-Hans", "zh-Hant" };

suite.Case("language default and validation", () =>
{
    suite.Check(L.Language == "en", "the localization engine starts in English");
    suite.Check(new ViewPreferences().Language == "auto", "new preference follows the system through auto");
    foreach (string language in languages) suite.Check(L.IsSupported(language), "supported resolved language " + language);
    foreach (string preference in preferences) suite.Check(LanguageResolver.IsPreference(preference), "supported preference " + preference);
    foreach (string? invalid in new string?[] { null, "", "EN", "zh", "de", "en-US", " auto " })
    {
        suite.Check(!LanguageResolver.IsPreference(invalid), "unknown preference rejected: " + (invalid ?? "null"));
        var previous = L.Language;
        suite.Throws<ArgumentOutOfRangeException>(() => L.Language = invalid!, "resolved language setter rejects invalid values");
        suite.Check(L.Language == previous, "rejected language assignment leaves current language intact");
    }
});

suite.Case("automatic language resolution respects explicit display language", () =>
{
    var matrix = new (string Preference, string Display, string Region, string Zone, string Expected)[]
    {
        ("en", "zh-CN", "CN", "China Standard Time", "en"),
        ("zh-Hans", "en-US", "US", "Pacific Standard Time", "zh-Hans"),
        ("zh-Hant", "en-US", "CN", "Asia/Shanghai", "zh-Hant"),
        ("auto", "en", "CN", "Asia/Shanghai", "en"),
        ("auto", "en-US", "TW", "Taipei Standard Time", "en"),
        ("auto", "en-GB", "HK", "Asia/Hong_Kong", "en"),
        ("auto", "zh-CN", "TW", "Asia/Taipei", "zh-Hans"),
        ("auto", "zh-SG", "US", "UTC", "zh-Hans"),
        ("auto", "zh-Hans-HK", "HK", "Asia/Hong_Kong", "zh-Hans"),
        ("auto", "zh-Hant", "CN", "Asia/Shanghai", "zh-Hant"),
        ("auto", "zh-Hant-CN", "CN", "China Standard Time", "zh-Hant"),
        ("auto", "zh-TW", "US", "UTC", "zh-Hant"),
        ("auto", "zh_HK", "CN", "Asia/Shanghai", "zh-Hant"),
        ("auto", "zh-MO", "CN", "Asia/Shanghai", "zh-Hant"),
        ("auto", "zh", "TW", "UTC", "zh-Hant"),
        ("auto", "zh", "CN", "UTC", "zh-Hans"),
        ("auto", "fr-FR", "CN", "Asia/Shanghai", "en"),
        ("auto", "ja-JP", "TW", "Asia/Taipei", "en"),
        ("auto", "", "CN", "UTC", "zh-Hans"),
        ("auto", "", "TW", "UTC", "zh-Hant"),
        ("auto", "", "HK", "UTC", "zh-Hant"),
        ("auto", "", "US", "Asia/Shanghai", "zh-Hans"),
        ("auto", "", "US", "Asia/Hong_Kong", "zh-Hant"),
        ("auto", "iv", "US", "Asia/Taipei", "zh-Hant"),
        ("auto", "", "US", "UTC", "en")
    };
    foreach (var row in matrix)
        suite.Check(LanguageResolver.Resolve(row.Preference, row.Display, row.Region, row.Zone) == row.Expected,
            $"{row.Preference}/{row.Display}/{row.Region}/{row.Zone} resolves to {row.Expected}");
    suite.Throws<ArgumentOutOfRangeException>(() => LanguageResolver.Resolve("fr", "en", "US", "UTC"), "unsupported saved preference is rejected");
});

suite.Case("all English catalog templates are valid and preserve placeholders", () =>
{
    var catalog = L.EnglishCatalog;
    suite.Check(catalog.Count >= 377, "the embedded catalog contains the full Core dictionary");
    foreach (var entry in catalog)
    {
        suite.Check(!string.IsNullOrWhiteSpace(entry.Value), "nonempty translation: " + entry.Key);
        var source = CompositeFormat.Parse(entry.Key); var translated = CompositeFormat.Parse(entry.Value);
        suite.Check(source.MinimumArgumentCount == translated.MinimumArgumentCount &&
            FormatFields(entry.Key).SequenceEqual(FormatFields(entry.Value)), "format fields preserved: " + entry.Key);
    }
});

suite.Case("production static translation keys all have English entries", () =>
{
    var catalog = L.EnglishCatalog;
    int keys = 0;
    var missing = new List<string>();
    foreach (var directory in new[] { "ProcessKeeper.Core", "ProcessKeeper.App" })
        foreach (var file in Directory.EnumerateFiles(Path.Combine(sourceRoot, directory), "*.cs", SearchOption.TopDirectoryOnly))
        {
            if (Path.GetFileName(file) == "Localization.cs") continue;
            var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(file));
            foreach (var invocation in tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var method = invocation.Expression.ToString();
                if (method is not ("L.T" or "L.F") || invocation.ArgumentList.Arguments.Count != 1) continue;
                var expression = invocation.ArgumentList.Arguments[0].Expression;
                string? key = expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression)
                    ? literal.Token.ValueText : expression is InterpolatedStringExpressionSyntax interpolated ? Template(interpolated) : null;
                if (key is null || !Regex.IsMatch(key, "[\\u4e00-\\u9fff]")) continue;
                keys++;
                if (!catalog.ContainsKey(key)) missing.Add($"{Path.GetFileName(file)}:{tree.GetLineSpan(invocation.Span).StartLinePosition.Line + 1}: {key}");
            }
        }
    foreach (var file in Directory.EnumerateFiles(Path.Combine(sourceRoot, "ProcessKeeper.App"), "*.xaml", SearchOption.TopDirectoryOnly))
        foreach (var attribute in XDocument.Load(file).Descendants().Attributes())
        {
            var match = Regex.Match(attribute.Value, "^\\{(?:\\w+:)?Loc Text='(?<key>.*)'\\}$", RegexOptions.Singleline);
            if (!match.Success) continue;
            var key = match.Groups["key"].Value;
            if (!Regex.IsMatch(key, "[\\u4e00-\\u9fff]")) continue;
            keys++;
            if (!catalog.ContainsKey(key)) missing.Add($"{Path.GetFileName(file)} {attribute.Name.LocalName}: {key}");
        }
    suite.Check(keys >= 377, "coverage scan inspected actual production C# and XAML lookup call sites");
    suite.Check(missing.Count == 0, "Missing English static keys:\n" + string.Join("\n", missing));
});

suite.Case("three languages render application text and preserve format arguments", () =>
{
    const string userData = "测试用户{0} | α | 原始设置";
    foreach (string language in languages)
    {
        L.Language = language;
        var text = L.F($"白名单子进程：{userData}（父级 PID {42}）");
        suite.Check(text.Contains(userData, StringComparison.Ordinal), "format argument is unchanged in " + language);
        suite.Check(text.Contains("42") && !text.Contains("{1}"), "numeric argument is substituted in " + language);
        suite.Check(L.T("等待真实窗口") == (language == "en" ? "Wait for actual window" : language == "zh-Hans" ? "等待真实窗口" : "等待真實窗口"),
            "AVD progress label renders exactly in " + language);
    }
    L.Language = "en";
    suite.Check(L.F($"正在等待 {"Fixture_AVD"} 的原生页面，最多 {1.5:0} 秒；控制台与命令行窗口不计入。") ==
        "Waiting up to 2 seconds for the native page of Fixture_AVD; console and command-line windows do not count.",
        "English formatting follows translated placeholder order and numeric format");
});

suite.Case("uncached Traditional conversions have exact contents with no trailing garbage", () =>
{
    L.Language = "zh-Hant";
    for (int index = 0; index < 128; index++)
    {
        var source = "测试应用设置进程 " + index;
        var expected = "測試應用設置進程 " + index;
        var translated = L.T(source);
        suite.Check(translated == expected && !translated.Contains('\0') && !translated.Contains('\uFFFD'), "exact first conversion " + index);
        suite.Check(L.T(source) == expected, "cached conversion is identical " + index);
    }
    suite.Check(L.T("") == "", "empty application text stays empty");
});

suite.Case("default rule descriptions localize without renaming user rules", () =>
{
    var defaults = new[] { new WhitelistRule { Id = "default-codex", Name = "Codex及其工具子进程", Kind = RuleKind.Application, Value = "known:codex", IncludeDescendants = true } };
    var codex = defaults.Single(rule => rule.Id == "default-codex");
    var originals = defaults.ToArray();
    foreach (string language in languages)
    {
        L.Language = language;
        var displayed = WhitelistStore.GetDisplayName(codex);
        suite.Check(displayed == (language == "en" ? "Codex and its tool subprocesses" : language == "zh-Hans" ? "Codex及其工具子进程" : "Codex及其工具子進程"), "owned description in " + language);
        foreach (var custom in new[] { codex with { Id = "custom" }, codex with { Name = "我的Codex" }, codex with { Value = "known:steam" }, codex with { Kind = RuleKind.ProcessName } })
            suite.Check(WhitelistStore.GetDisplayName(custom) == custom.Name, "customized or same-named user rule is unchanged in " + language);
        suite.Check(defaults.SequenceEqual(originals), "stored names and matching identities remain unchanged in " + language);
    }
});

suite.Case("legacy version-1 view and bundle without Language resolve to auto", () =>
{
    L.Language = "zh-Hans";
    const string oldView = "{\"Version\":1,\"LiveRefresh\":false,\"ShowSystemProcesses\":true,\"CategoryFilter\":4,\"SortOrder\":3}";
    var directory = Path.Combine(root, "legacy"); Directory.CreateDirectory(directory);
    var store = new ViewPreferencesStore(directory); File.WriteAllText(store.FilePath, oldView);
    var before = File.ReadAllBytes(store.FilePath); var value = store.Load();
    suite.Check(value == new ViewPreferences(false, true, 4, 3, "auto"), "legacy view values are retained with auto language");
    suite.Check(File.ReadAllBytes(store.FilePath).SequenceEqual(before), "legacy loading does not rewrite the original file");
    var path = Path.Combine(directory, "old-bundle.json");
    SettingsBundleStore.Export(path, Bundle("en"));
    var document = JsonNode.Parse(File.ReadAllText(path))!.AsObject(); document["View"]!.AsObject().Remove("Language");
    File.WriteAllText(path, document.ToJsonString()); var exported = File.ReadAllBytes(path);
    suite.Check(SettingsBundleStore.ReadImport(path).View.Language == "auto", "legacy full bundle defaults to auto");
    suite.Check(File.ReadAllBytes(path).SequenceEqual(exported), "import validation preserves the source bundle bytes");
});

foreach (string preference in preferences)
{
    suite.Case("language preference round trip: " + preference, () =>
    {
        L.Language = "zh-Hans";
        var directory = Path.Combine(root, "roundtrip-" + preference); Directory.CreateDirectory(directory);
        var view = new ViewPreferences(false, true, 4, 3, preference); var store = new ViewPreferencesStore(directory);
        store.Save(view); suite.Check(store.Load() == view, "first view save/load keeps every field");
        var bundle = Bundle(preference); var path = Path.Combine(directory, "export.json");
        SettingsBundleStore.Export(path, bundle); var imported = SettingsBundleStore.ReadImport(path);
        suite.Check(imported.View == bundle.View && imported.Appearance == bundle.Appearance && imported.Rules.SequenceEqual(bundle.Rules), "export/import keeps complete settings and rules");
        var destination = Path.Combine(directory, "imported"); SettingsBundleStore.CommitAll(destination, imported);
        suite.Check(new ViewPreferencesStore(destination).Load().Language == preference, "imported language is persisted");
    });
    suite.Case("atomic view replacement: " + preference, () =>
    {
        var directory = Path.Combine(root, "replace-view-" + preference); Directory.CreateDirectory(directory);
        var store = new ViewPreferencesStore(directory); var view = new ViewPreferences(false, true, 4, 3, preference);
        store.Save(view); store.Save(view with { SortOrder = 2 });
        suite.Check(store.Load() == view with { SortOrder = 2 }, "atomic replacement keeps language and changed settings");
    });
    suite.Case("atomic multi-file import replacement: " + preference, () =>
    {
        var directory = Path.Combine(root, "replace-bundle-" + preference); var bundle = Bundle(preference);
        SettingsBundleStore.CommitAll(directory, bundle);
        SettingsBundleStore.CommitAll(directory, bundle with { View = bundle.View with { SortOrder = 1 } });
        suite.Check(new ViewPreferencesStore(directory).Load() == bundle.View with { SortOrder = 1 }, "multi-file replacement keeps language");
    });
}

suite.Case("invalid Language fields are rejected without changing existing files", () =>
{
    L.Language = "zh-Hans";
    var directory = Path.Combine(root, "invalid"); Directory.CreateDirectory(directory);
    var store = new ViewPreferencesStore(directory); store.Save(new(Language: "en"));
    var valid = File.ReadAllBytes(store.FilePath);
    foreach (var value in new[] { "\"unknown\"", "null", "42", "true", "\"\"", "[]", "{}" })
    {
        var invalid = "{\"Version\":1,\"LiveRefresh\":true,\"ShowSystemProcesses\":false,\"CategoryFilter\":0,\"SortOrder\":0,\"Language\":" + value + "}";
        File.WriteAllText(store.FilePath, invalid); var bytes = File.ReadAllBytes(store.FilePath);
        suite.Throws<InvalidDataException>(() => store.Load(), "invalid Language value rejected: " + value);
        suite.Check(File.ReadAllBytes(store.FilePath).SequenceEqual(bytes), "invalid view file is preserved: " + value);
    }
    foreach (var suffix in new[] { "\"Language\":\"en\",\"Language\":\"zh-Hans\"", "\"Language\":\"en\",\"language\":\"zh-Hans\"" })
    {
        File.WriteAllText(store.FilePath, "{\"Version\":1,\"LiveRefresh\":true,\"ShowSystemProcesses\":false,\"CategoryFilter\":0,\"SortOrder\":0," + suffix + "}");
        var bytes = File.ReadAllBytes(store.FilePath);
        suite.Throws<InvalidDataException>(() => store.Load(), "duplicate or unknown Language field rejected");
        suite.Check(File.ReadAllBytes(store.FilePath).SequenceEqual(bytes), "duplicate or unknown fields do not rewrite the view file");
    }
    File.WriteAllBytes(store.FilePath, valid);
    foreach (string? invalid in new string?[] { null, "unknown", "en-US", "", "AUTO" })
    {
        suite.Throws<InvalidDataException>(() => store.Save(new(Language: invalid!)), "invalid typed preference rejected before save");
        suite.Check(File.ReadAllBytes(store.FilePath).SequenceEqual(valid), "failed save keeps the valid original file");
    }
    var target = Path.Combine(directory, "current"); SettingsBundleStore.CommitAll(target, Bundle("en"));
    var current = ConfigurationHashes(target);
    foreach (string? invalid in new string?[] { null, "unknown", "en-US" })
    {
        suite.Throws<InvalidDataException>(() => SettingsBundleStore.CommitAll(target, Bundle(invalid!)), "invalid bundle rejected before commit");
        suite.Check(ConfigurationHashes(target).SequenceEqual(current), "invalid bundle commit leaves all current files intact");
    }
    var import = Path.Combine(directory, "invalid-bundle.json"); SettingsBundleStore.Export(import, Bundle("en"));
    var validBundle = File.ReadAllText(import);
    foreach (var replacement in new[] { "\"Language\": null", "\"Language\": \"unknown\"", "\"Language\": \"en\", \"Language\": \"zh-Hant\"" })
    {
        File.WriteAllText(import, Regex.Replace(validBundle, "\"Language\"\\s*:\\s*\"en\"", replacement));
        var input = File.ReadAllBytes(import);
        suite.Throws<InvalidDataException>(() => SettingsBundleStore.ReadImport(import), "invalid imported Language field rejected");
        suite.Check(File.ReadAllBytes(import).SequenceEqual(input) && ConfigurationHashes(target).SequenceEqual(current), "invalid import keeps the source and current settings intact");
    }
});

L.Language = "en";
Console.WriteLine($"Localization fixtures | {suite.Passed} cases passed | {suite.Failed} failed | {suite.Assertions} assertions | root={root}");
return suite.Failed == 0 ? 0 : 1;

static string[] FormatFields(string template) => Regex.Matches(template, @"(?<!\{)\{\d+(?:,[^}:]+)?(?::[^}]+)?\}(?!\})")
    .Select(match => match.Value).Order(StringComparer.Ordinal).ToArray();
static string Template(InterpolatedStringExpressionSyntax expression)
{
    int index = 0;
    return string.Concat(expression.Contents.Select(content => content is InterpolatedStringTextSyntax text ? text.TextToken.ValueText :
        "{" + index++ + (((InterpolationSyntax)content).AlignmentClause?.ToString() ?? "") + (((InterpolationSyntax)content).FormatClause?.ToString() ?? "") + "}"));
}
static SettingsBundle Bundle(string language) => new(WhitelistStore.CreateDefaults(), new(false, BackdropMaterial.Acrylic, AppearanceTheme.Dark), new(false, true, 4, 3, language));
static string[] ConfigurationHashes(string directory) => new[] { "whitelist.json", "appearance.json", "view.json" }
    .Select(name => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(directory, name))))).ToArray();

sealed class Suite
{
    public int Passed { get; private set; }
    public int Failed { get; private set; }
    public int Assertions { get; private set; }
    public void Check(bool condition, string label)
    {
        Assertions++;
        if (!condition) throw new InvalidOperationException(label);
    }
    public void Throws<T>(Action action, string label) where T : Exception
    {
        try { action(); } catch (T) { Check(true, label); return; }
        Check(false, label);
    }
    public void Case(string label, Action action)
    {
        try { action(); Passed++; Console.WriteLine("PASS | " + label); }
        catch (Exception ex) { Failed++; Console.WriteLine("FAIL | " + label + " | " + ex); }
    }
}
