using ProcessKeeper.Core;

// Historical personal choices are test data only; none are installed as product defaults.
internal static class HistoricalWhitelistFixture
{
    internal static IReadOnlyList<WhitelistRule> Create() => new[]
    { "clash", "codex", "qq", "steam", "huorong", "uu", "translucenttb" }.Select(key => new WhitelistRule
    { Id = "default-" + key, Name = key == "codex" ? "Codex及其工具子进程" : key,
      Kind = RuleKind.Application, Value = "known:" + key, IncludeDescendants = key == "codex" }).ToArray();
}
