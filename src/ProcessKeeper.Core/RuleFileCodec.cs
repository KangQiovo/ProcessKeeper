using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ProcessKeeper.Core;

/// <summary>The portable, single-profile whitelist format. Parsing never writes active settings.</summary>
public static class RuleFileCodec
{
    public const int MaximumFileBytes = 1024 * 1024;
    public const int MaximumRules = 1000;
    internal static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true, PropertyNameCaseInsensitive = false,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    public static IReadOnlyList<WhitelistRule> Parse(string text)
    {
        if (text is null || text.Length > MaximumFileBytes || Encoding.UTF8.GetByteCount(text) > MaximumFileBytes)
            throw new InvalidDataException(L.T("白名单文件不能超过 1 MiB。"));
        try
        {
            using var document = JsonDocument.Parse(text);
            RequireFields(document.RootElement, "Version", "Rules");
            return ReadRules(document.RootElement);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
        { throw new InvalidDataException(L.T("白名单配置格式无效。"), ex); }
    }

    public static string Serialize(IReadOnlyList<WhitelistRule> rules) => Encoding.UTF8.GetString(SerializeBytes(rules));

    public static void Validate(IReadOnlyList<WhitelistRule> rules)
    {
        if (rules is null) throw new InvalidDataException(L.T("配置缺少 Rules 列表。"));
        if (rules.Count > MaximumRules) throw new InvalidDataException(L.T("白名单规则不能超过 1000 条。"));
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in rules)
        {
            if (rule is null || string.IsNullOrWhiteSpace(rule.Id) || !ids.Add(rule.Id))
                throw new InvalidDataException(L.T("白名单规则缺少唯一 Id，或出现重复 Id。"));
            if (string.IsNullOrWhiteSpace(rule.Name) || string.IsNullOrWhiteSpace(rule.Value) || !Enum.IsDefined(typeof(RuleKind), rule.Kind))
                throw new InvalidDataException(L.T("白名单规则的名称、类型或匹配值无效。"));
            if (rule.Value != rule.Value.Trim() || rule.Value.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0)
                throw new InvalidDataException(L.T("白名单匹配值包含无效的空白或控制字符。"));
            if (rule.Kind is RuleKind.ExecutablePath or RuleKind.Directory && !ProtectionPolicy.TryNormalizePath(rule.Value, out _))
                throw new InvalidDataException(L.T("路径规则必须使用有效的完整路径。"));
            if (rule.Kind == RuleKind.ProcessName && rule.Value.IndexOfAny(new[] { '\\', '/', '*', '?', ':' }) >= 0)
                throw new InvalidDataException(L.T("进程名称规则只接受完整文件名，不接受路径或通配符。"));
        }
        if (JsonSerializer.SerializeToUtf8Bytes(new { Version = 1, Rules = rules }, Options).Length > MaximumFileBytes)
            throw new InvalidDataException(L.T("白名单文件不能超过 1 MiB。"));
    }

    internal static byte[] SerializeBytes(IReadOnlyList<WhitelistRule> rules)
    {
        Validate(rules);
        return JsonSerializer.SerializeToUtf8Bytes(new { Version = 1, Rules = rules }, Options);
    }

    internal static IReadOnlyList<WhitelistRule> ReadRules(JsonElement root)
    {
        if (root.GetProperty("Version").ValueKind != JsonValueKind.Number || !root.GetProperty("Version").TryGetInt32(out var version) || version != 1)
            throw new InvalidDataException(L.T("白名单配置版本无效。"));
        var value = root.GetProperty("Rules");
        if (value.ValueKind != JsonValueKind.Array) throw new InvalidDataException(L.T("配置缺少 Rules 列表。"));
        if (value.GetArrayLength() > MaximumRules) throw new InvalidDataException(L.T("白名单规则不能超过 1000 条。"));
        foreach (var rule in value.EnumerateArray())
        {
            RequireFields(rule, "Id", "Name", "Kind", "Value", "Enabled", "IncludeDescendants");
            var kind = rule.GetProperty("Kind");
            if (kind.ValueKind != JsonValueKind.String || !Enum.GetNames(typeof(RuleKind)).Contains(kind.GetString(), StringComparer.Ordinal))
                throw new InvalidDataException(L.T("白名单规则的名称、类型或匹配值无效。"));
        }
        var rules = value.Deserialize<WhitelistRule[]>(Options) ?? throw new InvalidDataException(L.T("配置缺少 Rules 列表。"));
        Validate(rules);
        return Array.AsReadOnly(rules);
    }

    internal static void RequireFields(JsonElement value, params string[] fields)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException(L.T("白名单配置格式无效。"));
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!fields.Contains(property.Name, StringComparer.Ordinal) || !names.Add(property.Name))
                throw new InvalidDataException(L.T("白名单配置含重复、未知或缺失字段。"));
        if (names.Count != fields.Length) throw new InvalidDataException(L.T("白名单配置含重复、未知或缺失字段。"));
    }
}
