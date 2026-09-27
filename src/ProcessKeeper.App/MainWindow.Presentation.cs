using Microsoft.UI.Xaml;
using ProcessKeeper.Core;

namespace ProcessKeeper.App;

public sealed partial class MainWindow
{
    private const string PlatformRowPrefix = "presentation-platform:";
    private readonly ApplicationDisplayService _displayService = new();
    private readonly CancellationTokenSource _displayLifetime = new();
    private readonly HashSet<string> _collapsedRunningPlatforms = new(StringComparer.Ordinal);
    private readonly HashSet<string> _collapsedInstalledPlatforms = new(StringComparer.Ordinal);
    private ApplicationDisplayCatalog _displayCatalog = ApplicationDisplayCatalog.Empty;
    private bool _groupGamePlatforms = true, _hideMicrosoftApps, _displayCapturing, _displayDirty;
    internal Func<ProcessSnapshot, IReadOnlyList<InstalledApplication>, IReadOnlyList<AutorunEntry>, CancellationToken, ApplicationDisplayCatalog>? CaptureDisplay { get; set; }
    private static bool MatchesPlatformQuery(string query, GameCatalogEntry? game, GamePlatform? platform) => query.Length > 0 &&
        ((game?.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0 || (platform?.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0);

    private void InitializePresentation()
    {
        RunningPresentationOptions.Changed += PresentationChanged;
        InstalledPresentationOptions.Changed += PresentationChanged;
        if (_autorunsView is not null)
        {
            _autorunsView.PresentationChanged += (_, _) => ApplyPresentationChoice(_autorunsView.GroupGamePlatforms, _autorunsView.HideMicrosoftApps);
            _autorunsView.InventoryChanged += (_, _) => RequestPresentationCapture();
        }
        ApplyPresentationPreferences(_groupGamePlatforms, _hideMicrosoftApps);
        Closed += (_, _) => _displayLifetime.Cancel();
    }
    private void PresentationChanged(object? sender, EventArgs args)
    { if (sender is PresentationOptions options) ApplyPresentationChoice(options.GroupGames, options.HideMicrosoft); }
    private void ApplyPresentationChoice(bool group, bool hide)
    {
        if (!_ready || _changingViews || _closed) return;
        ApplyPresentationPreferences(group, hide); SaveViewPreferences();
        RenderApps(); RenderInstalled(); RequestPresentationCapture();
    }
    private void ApplyPresentationPreferences(bool group, bool hide)
    {
        _groupGamePlatforms = group; _hideMicrosoftApps = hide;
        RunningPresentationOptions.Apply(group, hide); InstalledPresentationOptions.Apply(group, hide);
        _autorunsView?.ApplyPresentation(_displayCatalog, group, hide);
    }
    private async void RequestPresentationCapture()
    {
        if (_closed || (!_groupGamePlatforms && !_hideMicrosoftApps)) return;
        _displayDirty = true;
        if (_displayCapturing) return;
        _displayCapturing = true;
        try
        {
            while (_displayDirty && !_closed)
            {
                _displayDirty = false;
                var snapshot = _snapshot; var installed = AllInstalledApplications().ToArray();
                var autoruns = _autorunsView?.InventoryEntries ?? Array.Empty<AutorunEntry>();
                var token = _displayLifetime.Token;
                var result = await Task.Run(() => CaptureDisplay is { } capture ? capture(snapshot, installed, autoruns, token) : _displayService.Capture(snapshot, installed, autoruns, token), token);
                if (_closed || token.IsCancellationRequested) return;
                _displayCatalog = result;
                _autorunsView?.ApplyPresentation(result, _groupGamePlatforms, _hideMicrosoftApps);
                RenderApps(); RenderInstalled();
            }
        }
        catch (OperationCanceledException) when (_displayLifetime.IsCancellationRequested) { }
        catch { /* Display enrichment is optional; unknown items remain visible. */ }
        finally { _displayCapturing = false; }
    }

    private List<(ApplicationGroup App, ProcessRecord? Process, string RowKey)> GroupRunningRows(
        List<(ApplicationGroup App, ProcessRecord? Process, string RowKey)> entries, string query)
    {
        if (!_groupGamePlatforms) return entries;
        var segments = entries.GroupBy(e => e.App.Key).Select(g => (Rows: g.ToArray(), Platform: _displayCatalog.FindGamePlatform(g.First().App))).ToArray();
        var result = new List<(ApplicationGroup App, ProcessRecord? Process, string RowKey)>();
        foreach (var platform in segments.Where(s => s.Platform is not null).GroupBy(s => s.Platform!.Id))
        {
            var first = platform.First().Platform!; var key = PlatformRowPrefix + first.Id;
            result.Add((new ApplicationGroup { Key = key, Name = first.Name, Description = L.T("仅整理显示；关闭与白名单操作仍只针对原应用。") }, null, key));
            if (query.Length > 0 || !_collapsedRunningPlatforms.Contains(first.Id))
                foreach (var segment in platform) result.AddRange(segment.Rows);
        }
        foreach (var segment in segments.Where(s => s.Platform is null)) result.AddRange(segment.Rows);
        return result;
    }

    private static List<InstalledRow> GroupInstalledRows(List<InstalledRow> rows, InstalledApplication[] applications,
        ApplicationDisplayCatalog catalog, bool group, HashSet<string> collapsed, string query)
    {
        if (!group) return rows;
        var lookup = applications.ToDictionary(a => a.Id, StringComparer.OrdinalIgnoreCase);
        var segments = rows.GroupBy(r => r.ApplicationId).Select(g => (Rows: g.ToArray(), Platform: lookup.TryGetValue(g.Key, out var app) ? catalog.FindGamePlatform(app) : null)).ToArray();
        var result = new List<InstalledRow>();
        foreach (var platform in segments.Where(s => s.Platform is not null).GroupBy(s => s.Platform!.Id))
        {
            var first = platform.First().Platform!; var key = PlatformRowPrefix + first.Id;
            var open = query.Length > 0 || !collapsed.Contains(first.Id);
            result.Add(new InstalledRow { RowKey = key, ApplicationId = key, Kind = InstalledRowKind.Application,
                Name = first.Name, IsPresentationGroup = true, PresentationPlatformId = first.Id, IsExpanded = open,
                IconPath = catalog.Clients.FirstOrDefault(c => c.Platform.Id == first.Id)?.ExecutablePath ?? "",
                Summary = L.T("游戏平台"), Details = L.T("仅整理显示；关闭与白名单操作仍只针对原应用。") });
            if (open) foreach (var segment in platform) foreach (var row in segment.Rows)
            { row.PresentationDepth = 1; row.PresentationPlatformId = first.Id; result.Add(row); }
        }
        foreach (var segment in segments.Where(s => s.Platform is null)) result.AddRange(segment.Rows);
        return result;
    }
}
