namespace ProcessKeeper.Core;

/// <summary>Windows display ownership is independent of service/session close protection.</summary>
public sealed class SystemComponentClassifier
{
    private readonly IMicrosoftPublisherProbe _publisher;
    private readonly string _windowsDirectory;
    private readonly Dictionary<string, (PublisherFileIdentity Identity, bool Microsoft)> _cache = new(StringComparer.OrdinalIgnoreCase);
    public SystemComponentClassifier(IMicrosoftPublisherProbe? publisher = null, string? windowsDirectory = null)
    {
        _publisher = publisher ?? new MicrosoftPublisherProbe();
        _windowsDirectory = windowsDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    }
    public bool IsWindowsComponent(string name, string path)
    {
        if (!ProcessIdentity.IsWindowsComponentName(name) || !ProcessIdentity.IsUnderDirectory(path, _windowsDirectory) ||
            !string.Equals(Path.GetFileName(path), name, StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            var identity = _publisher.ReadIdentity(path);
            if (identity is null) { _cache.Remove(path); return false; }
            if (_cache.TryGetValue(path, out var known) && known.Identity == identity) return known.Microsoft;
            bool microsoft = _publisher.IsMicrosoft(path, CancellationToken.None);
            if (_publisher.ReadIdentity(path) != identity) { _cache.Remove(path); return false; }
            if (_cache.Count >= 512) _cache.Clear();
            _cache[path] = (identity, microsoft);
            return microsoft;
        }
        catch { _cache.Remove(path); return false; }
    }
}
