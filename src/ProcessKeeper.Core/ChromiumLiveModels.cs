namespace ProcessKeeper.Core;

public sealed record BrowserPageTarget(ProcessRecord Source, IReadOnlyList<string> SourceArguments,
    int Port, string Id, string Title, string Url, string WebSocketUrl);

public sealed record ChromiumLiveDiscoveryResult(IReadOnlyList<BrowserPageTarget> Targets, IReadOnlyList<string> Notices);

/// <summary>Read-only process evidence; injectable so tests never inspect a user's browser.</summary>
public interface IChromiumLiveHost
{
    void ValidateIdentity(ProcessRecord process);
    IReadOnlyList<string> ReadArguments(ProcessRecord process);
    IReadOnlyList<AvdPortListener> ReadListeners();
    bool IsPortOwnedBy(int port, int processId);
}

internal sealed class ChromiumLiveHost : IChromiumLiveHost
{
    public void ValidateIdentity(ProcessRecord process) => AvdNative.ValidateIdentity(process);
    public IReadOnlyList<string> ReadArguments(ProcessRecord process) => AvdNative.ReadArguments(process);
    public IReadOnlyList<AvdPortListener> ReadListeners() => AvdNative.ReadLoopbackListeners();
    public bool IsPortOwnedBy(int port, int processId) => AvdNative.IsPortOwnedBy(port, processId);
}

internal sealed class ChromiumLiveFailure(string message) : InvalidOperationException(message);
