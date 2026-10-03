using ProcessKeeper.Core;

namespace ProcessKeeper.App;

// Pure batch planning shared verbatim with the compatibility UI. It never closes
// processes or changes protection policy. Expansion/virtualization is not scope.
internal static class BulkSelectionPlan
{
    internal sealed record Result(IReadOnlyList<WhitelistRule> Rules, int Changed);

    internal static Result Create(int page, bool enabled, string query, int category, string drive,
        bool showSystem, bool legacySystemFilter, bool hideMicrosoft, ApplicationDisplayCatalog display,
        ProcessSnapshot snapshot, IReadOnlyList<InstalledApplication> installed,
        IReadOnlyList<WhitelistRule> rules, CancellationToken token)
    {
        var requested = new Dictionary<string, WhitelistRule>(StringComparer.OrdinalIgnoreCase);
        var applicationKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ruleIds = new HashSet<string>(StringComparer.Ordinal);
        bool Visible(ProcessRecord p) => showSystem || p.Category != RunCategory.System && (!legacySystemFilter || !p.IsSystem);
        bool MetadataMatch(GameCatalogEntry? game, GamePlatform? platform) => query.Length > 0 &&
            ((game?.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0 ||
             (platform?.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0);
        void Add(WhitelistRule rule)
        {
            if (rule.Value.Length == 0 || rule.Kind == RuleKind.Application && rule.Value.StartsWith("unknown:", StringComparison.OrdinalIgnoreCase)) return;
            requested[Key(rule)] = rule;
        }
        if (page == 0)
        {
            foreach (var original in snapshot.Applications)
            {
                token.ThrowIfCancellationRequested();
                var app = original with { Processes = original.Processes.Where(Visible).ToArray() };
                if (app.Processes.Count == 0 || category > 0 && (int)app.Category != category - 1 ||
                    hideMicrosoft && display.IsMicrosoft(app) ||
                    !ApplicationSearch.MatchesApplication(app, query) && !MetadataMatch(display.FindGame(app), display.FindGamePlatform(app)) ||
                    app.Key.Length == 0 || app.Key.StartsWith("unknown:", StringComparison.OrdinalIgnoreCase)) continue;
                applicationKeys.Add(app.Key);
                Add(new WhitelistRule { Name = app.Name, Kind = RuleKind.Application, Value = app.Key });
            }
        }
        else if (page == 1)
        {
            var search = new InstalledApplicationSearch(snapshot.Processes, showSystem);
            foreach (var app in installed)
            {
                token.ThrowIfCancellationRequested();
                if (hideMicrosoft && display.IsMicrosoft(app) || !search.Matches(app, query, drive) &&
                    !(search.Matches(app, "", drive) && MetadataMatch(display.FindGame(app), display.FindGamePlatform(app)))) continue;
                foreach (var rule in InstalledApplicationCatalog.GetAppRules(app)) { token.ThrowIfCancellationRequested(); Add(rule); }
            }
        }
        else if (page == 2)
        {
            var matcher = new RuleProcessMatcher(snapshot);
            var offline = query.Length == 0 ? new HashSet<string>() : new RuleSearchIndex(installed).MatchingRuleIds(rules, query);
            foreach (var rule in rules)
            {
                token.ThrowIfCancellationRequested();
                var matches = matcher.Match(rule).Where(match => Visible(match.Process)).ToArray();
                if (ApplicationSearch.MatchesRule(rule, WhitelistStore.GetDisplayName(rule), matches, query) || offline.Contains(rule.Id)) ruleIds.Add(rule.Id);
            }
        }
        else return new Result(rules, 0);

        var result = new List<WhitelistRule>(rules.Count + requested.Count);
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var changed = 0;
        foreach (var rule in rules)
        {
            token.ThrowIfCancellationRequested();
            var key = Key(rule); present.Add(key);
            var applies = page == 2 ? ruleIds.Contains(rule.Id) : requested.ContainsKey(key) ||
                !enabled && page == 0 && rule.Kind == RuleKind.Application && applicationKeys.Contains(rule.Value);
            if (applies && rule.Enabled != enabled) { result.Add(rule with { Enabled = enabled }); changed++; }
            else result.Add(rule);
        }
        if (enabled && page != 2)
            foreach (var pair in requested)
            {
                token.ThrowIfCancellationRequested();
                if (present.Add(pair.Key)) { result.Add(pair.Value with { Enabled = true }); changed++; }
            }
        if (changed > 0) RuleFileCodec.Validate(result);
        return new Result(result, changed);
    }

    private static string Key(WhitelistRule rule) => ((int)rule.Kind) + ":" + rule.IncludeDescendants + ":" + rule.Value;
}
