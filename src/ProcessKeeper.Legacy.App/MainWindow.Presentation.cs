using System.Windows;
using ProcessKeeper.Core;

namespace ProcessKeeper.App;

public partial class MainWindow
{
    private ApplicationDisplayCatalog _displayCatalog = ApplicationDisplayCatalog.Empty;
    private bool _displayCapturing, _displayDirty;
    private const string PlatformRowPrefix = "presentation-platform:";
    private readonly HashSet<string> _collapsedPlatforms = new(StringComparer.Ordinal);
    private static bool MatchesPlatformQuery(string query, GameCatalogEntry? game, GamePlatform? platform) => query.Length > 0 &&
        ((game?.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0 || (platform?.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0);

    private void ConfigurePresentationOptions()
    {
        PresentationOptions.Visibility = _page is 0 or 1 or 3 ? Visibility.Visible : Visibility.Collapsed;
        GroupPlatforms.Content = L.T("按游戏平台分组"); GroupPlatforms.IsChecked = _view.GroupGamePlatforms;
        HideMicrosoft.Content = L.T("隐藏 Microsoft 应用"); HideMicrosoft.IsChecked = _view.HideMicrosoftApps;
        GroupPlatforms.ToolTip = L.T("仅整理显示；关闭与白名单操作仍只针对原应用。");
        HideMicrosoft.ToolTip = L.T("仅隐藏已核实的 Microsoft 应用；未知身份仍显示。");
        _uninstallView?.ApplyPresentation(_displayCatalog, _view.GroupGamePlatforms, _view.HideMicrosoftApps);
    }
    private async void PresentationChanged(object sender, RoutedEventArgs args)
    {
        if (!_ready || _closed) return;
        _view = _view with { GroupGamePlatforms = GroupPlatforms.IsChecked == true, HideMicrosoftApps = HideMicrosoft.IsChecked == true };
        _uninstallView?.ApplyPresentation(_displayCatalog, _view.GroupGamePlatforms, _view.HideMicrosoftApps);
        SaveView(); await RenderAsync(); RequestPresentationCapture();
    }
    private async void UninstallPresentationChanged(object? sender, EventArgs args)
    {
        if (!_ready || _closed || _uninstallView is null) return;
        _view = _view with { GroupGamePlatforms = _uninstallView.GroupGamePlatforms, HideMicrosoftApps = _uninstallView.HideMicrosoftApps };
        // Synchronize the shared page choices without firing two intermediate renders.
        bool wasReady = _ready; _ready = false;
        try { GroupPlatforms.IsChecked = _view.GroupGamePlatforms; HideMicrosoft.IsChecked = _view.HideMicrosoftApps; }
        finally { _ready = wasReady; }
        SaveView(); await RenderAsync(); RequestPresentationCapture();
    }
    private async void RequestPresentationCapture()
    {
        if (_closed || (!_view.GroupGamePlatforms && !_view.HideMicrosoftApps && !(_uninstallView?.InventoryEntries.Count > 0))) return;
        _displayDirty = true;
        if (_displayCapturing) return;
        _displayCapturing = true;
        try
        {
            while (_displayDirty && !_closed)
            {
                _displayDirty = false;
                var snapshot = _snapshot; var installed = AllInstalledApplications(); var autoruns = _autoruns.Entries;
                var uninstall = _uninstallView?.InventoryEntries ?? Array.Empty<UninstallEntry>();
                var verifyMicrosoft = _view.HideMicrosoftApps || uninstall.Count > 0;
                var result = await Task.Run(() => _backend.CaptureDisplayForView(snapshot, installed, autoruns, uninstall, verifyMicrosoft, _life.Token), _life.Token);
                if (_closed || _life.IsCancellationRequested) return;
                if (_displayCatalog.HasSamePresentationAs(result)) continue;
                _displayCatalog = result; _uninstallView?.ApplyPresentation(result, _view.GroupGamePlatforms, _view.HideMicrosoftApps); await RenderAsync();
            }
        }
        catch (OperationCanceledException) when (_life.IsCancellationRequested) { }
        catch { /* Failed optional identity enrichment must not hide unknown applications. */ }
        finally { _displayCapturing = false; }
    }
    private static List<LegacyRow> ApplyPresentation(List<LegacyRow> rows, int page, string query,
        ApplicationDisplayCatalog display, bool group, bool hide, HashSet<string> collapsed)
    {
        if (page == 2) return rows;
        var segments = new List<List<LegacyRow>>();
        foreach (var row in rows)
        {
            if (!row.IsChild || segments.Count == 0) segments.Add(new List<LegacyRow>());
            segments[segments.Count - 1].Add(row);
        }
        bool IsMicrosoft(object? model) => model switch
        { ApplicationGroup app => display.IsMicrosoft(app), InstalledApplication app => display.IsMicrosoft(app), AutorunEntry entry => display.IsMicrosoft(entry), _ => false };
        GamePlatform? Platform(object? model) => model switch
        { ApplicationGroup app => display.FindGamePlatform(app), InstalledApplication app => display.FindGamePlatform(app), AutorunEntry entry => display.FindGamePlatform(entry), _ => null };
        var shown = segments.Where(s => !hide || !IsMicrosoft(s[0].Model)).Select(s => (Rows: s, Platform: group ? Platform(s[0].Model) : null)).ToArray();
        foreach (var item in shown)
        {
            var game = item.Rows[0].Model switch { ApplicationGroup app => display.FindGame(app), InstalledApplication app => display.FindGame(app), _ => null };
            if (game?.Name.Length > 0) item.Rows[0].Name = game.Name;
        }
        var result = new List<LegacyRow>();
        foreach (var platform in shown.Where(s => s.Platform is not null).GroupBy(s => s.Platform!.Id))
        {
            var first = platform.First().Platform!; var open = query.Length > 0 || !collapsed.Contains(page + ":" + first.Id);
            result.Add(new LegacyRow { Id = PlatformRowPrefix + first.Id, Name = first.Name, Summary = L.T("游戏平台"),
                Path = display.Clients.FirstOrDefault(c => c.Platform.Id == first.Id)?.ExecutablePath ?? "",
                Detail = L.T("仅整理显示；关闭与白名单操作仍只针对原应用。"),
                IsPresentationGroup = true, PresentationPlatformId = first.Id, Expanded = open });
            if (open) foreach (var segment in platform) foreach (var row in segment.Rows)
            { row.PresentationDepth = 1; row.PresentationPlatformId = first.Id; result.Add(row); }
        }
        foreach (var segment in shown.Where(s => s.Platform is null)) result.AddRange(segment.Rows);
        return result;
    }
}
