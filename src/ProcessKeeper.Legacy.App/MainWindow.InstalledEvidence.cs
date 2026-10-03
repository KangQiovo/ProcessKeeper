using ProcessKeeper.Core;

namespace ProcessKeeper.App;

public partial class MainWindow
{
    private IReadOnlyList<InstalledApplication>? _mergedInstalledBase, _mergedInstalledResult;
    private IReadOnlyList<UninstallEntry>? _mergedInstalledRegistrations;

    private IReadOnlyList<InstalledApplication> AllInstalledApplications()
    {
        var registrations = _uninstallView?.InventoryEntries ?? Array.Empty<UninstallEntry>();
        if (_mergedInstalledResult is not null && ReferenceEquals(_mergedInstalledBase, _installed) &&
            ReferenceEquals(_mergedInstalledRegistrations, registrations)) return _mergedInstalledResult;
        _mergedInstalledBase = _installed; _mergedInstalledRegistrations = registrations;
        return _mergedInstalledResult = InstalledApplicationCatalog.IncludeVerifiedApplications(_installed, registrations);
    }

    private void OnUninstallInventoryChanged()
    {
        if (_closed) return;
        UpdateDrives(); RefreshWhitelistActionGuards(); RequestPresentationCapture();
        if (_page <= 3) _ = RenderAsync();
    }
}
