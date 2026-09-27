using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace ProcessKeeper.App;

internal enum InstanceAcquireResult { Acquired, Superseded, Blocked }

/// <summary>One management window per user/session. A newer process asks older peers to close themselves.</summary>
internal sealed class SingleInstanceCoordinator : IDisposable
{
    private sealed record Claim(int ProcessId, long StartedUtcTicks, int SessionId, string ImagePath);
    private readonly string _directory, _claimPath;
    private readonly Claim _self;
    private readonly Action _requestClose;
    private readonly bool _checkLegacy;
    private readonly HashSet<(int Id, long Started)> _legacyNotified = [];
    private readonly CancellationTokenSource _lifetime = new();
    private FileStream? _registration, _exclusive;
    private int _disposed;

    internal SingleInstanceCoordinator(Action requestClose, string? fixtureDirectory = null)
    {
        _requestClose = requestClose;
        _checkLegacy = fixtureDirectory is null;
        _self = Inspect(Environment.ProcessId) ?? throw new IOException("Cannot read the current Process Keeper instance identity.");
        using var identity = WindowsIdentity.GetCurrent();
        _directory = fixtureDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ProcessKeeper", "instances", $"{identity.User?.Value}-{_self.SessionId}");
        _claimPath = Path.Combine(_directory, $"{_self.ProcessId}-{_self.StartedUtcTicks}-{Guid.NewGuid():N}.json");
    }

    internal async Task<InstanceAcquireResult> AcquireAsync(CancellationToken cancellationToken = default,
        TimeSpan? timeout = null)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var token = linked.Token;
        try
        {
            Directory.CreateDirectory(_directory);
            if ((File.GetAttributes(_directory) & FileAttributes.ReparsePoint) != 0)
                return InstanceAcquireResult.Blocked;
            _registration = new FileStream(_claimPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
            JsonSerializer.Serialize(_registration, _self);
            _registration.Flush(true);
            var watch = Stopwatch.StartNew();
            var limit = timeout ?? TimeSpan.FromSeconds(12);
            while (watch.Elapsed < limit)
            {
                token.ThrowIfCancellationRequested();
                if (HasNewerPeer()) { Dispose(); return InstanceAcquireResult.Superseded; }
                try
                {
                    // The file remains locked until the old window's Closed event, including while it cancels work.
                    _exclusive = new FileStream(Path.Combine(_directory, "active.lock"), FileMode.OpenOrCreate,
                        FileAccess.ReadWrite, FileShare.None);
                }
                catch (IOException) { }
                if (_exclusive is not null)
                {
                    if (HasNewerPeer()) { Dispose(); return InstanceAcquireResult.Superseded; }
                    if (!_checkLegacy || !RequestOlderWindowsToClose())
                    {
                        _ = WatchForNewerAsync();
                        return InstanceAcquireResult.Acquired;
                    }
                    _exclusive.Dispose();
                    _exclusive = null;
                }
                await Task.Delay(120, token);
            }
        }
        catch (OperationCanceledException) { Dispose(); return InstanceAcquireResult.Superseded; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        Dispose();
        return InstanceAcquireResult.Blocked;
    }

    private async Task WatchForNewerAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                await Task.Delay(150, _lifetime.Token).ConfigureAwait(false);
                bool newer;
                try { newer = HasNewerPeer(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { continue; }
                if (!newer) continue;
                _requestClose();
                return;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Keep the exclusive lease on an unreadable registry; a second window must not enter management.
        }
    }

    private bool RequestOlderWindowsToClose()
    {
        var waiting = false;
        foreach (var peer in Process.GetProcessesByName("ProcessKeeper"))
        {
            using (peer)
            {
                try
                {
                    var actual = Inspect(peer.Id);
                    if (actual is null || actual.ProcessId == _self.ProcessId || actual.SessionId != _self.SessionId ||
                        IsNewer(actual, _self) || peer.MainWindowHandle == 0 ||
                        !(peer.MainWindowTitle == "Process Keeper" || peer.MainWindowTitle == "留驻" || peer.MainWindowTitle.StartsWith("留驻 |", StringComparison.Ordinal))) continue;
                    var version = FileVersionInfo.GetVersionInfo(actual.ImagePath);
                    if (version.OriginalFilename != "ProcessKeeper.dll" || version.ProductName != "ProcessKeeper") continue;
                    using var handle = OpenProcess(0x1000, false, peer.Id);
                    if (handle.IsInvalid || !OpenProcessToken(handle, 8, out var token)) continue;
                    using (token)
                    using (var owner = new WindowsIdentity(token.DangerousGetHandle()))
                    using (var current = WindowsIdentity.GetCurrent())
                        if (owner.User != current.User) continue;
                    waiting = true;
                    // Legacy releases have no lease watcher. Ask only an identity-checked older UI to close.
                    if (_legacyNotified.Add((actual.ProcessId, actual.StartedUtcTicks)) && Inspect(actual.ProcessId) == actual)
                        peer.CloseMainWindow();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                    System.ComponentModel.Win32Exception or InvalidOperationException or System.Security.SecurityException) { }
            }
        }
        return waiting;
    }

    private bool HasNewerPeer()
    {
        foreach (var path in Directory.EnumerateFiles(_directory, "*.json").Take(1025))
        {
            if (string.Equals(path, _claimPath, StringComparison.OrdinalIgnoreCase)) continue;
            Claim? claim;
            try
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length is <= 0 or > 4096) continue;
                claim = JsonSerializer.Deserialize<Claim>(stream);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { continue; }
            if (claim is null || claim.SessionId != _self.SessionId || !IsNewer(claim, _self)) continue;
            var actual = Inspect(claim.ProcessId);
            if (actual is null || actual.StartedUtcTicks != claim.StartedUtcTicks || actual.SessionId != _self.SessionId ||
                !string.Equals(actual.ImagePath, claim.ImagePath, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetFileName(actual.ImagePath), Path.GetFileName(_self.ImagePath), StringComparison.OrdinalIgnoreCase)) continue;
            return true;
        }
        return false;
    }

    private static bool IsNewer(Claim candidate, Claim current) => candidate.StartedUtcTicks > current.StartedUtcTicks ||
        (candidate.StartedUtcTicks == current.StartedUtcTicks && candidate.ProcessId > current.ProcessId);

    private static Claim? Inspect(int id)
    {
        if (id <= 0) return null;
        using var process = OpenProcess(0x1000 | 0x00100000, false, id);
        if (process.IsInvalid || WaitForSingleObject(process, 0) != 258 ||
            !GetProcessTimes(process, out var created, out _, out _, out _) || !ProcessIdToSessionId((uint)id, out var session)) return null;
        var path = new StringBuilder(32768);
        var length = path.Capacity;
        if (!QueryFullProcessImageNameW(process, 0, path, ref length)) return null;
        try { return new Claim(id, DateTime.FromFileTimeUtc(created).Ticks, (int)session, path.ToString()); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel();
        // Release registration before the exclusive lease, so a successor cannot confuse the just-closed owner.
        _registration?.Dispose();
        try { File.Delete(_claimPath); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        _exclusive?.Dispose();
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int id);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle handle, out long created, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ProcessIdToSessionId(uint id, out uint session);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(SafeProcessHandle handle, int flags, StringBuilder path, ref int length);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
}
