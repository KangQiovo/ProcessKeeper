using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using ProcessKeeper.Core;

namespace ProcessKeeper.App;

internal enum InstanceAcquireResult { Acquired, Superseded, Blocked }

/// <summary>All flavors share a user/session lease. A successor waits for the prior process, not just its window.</summary>
internal sealed class SingleInstanceCoordinator : IDisposable
{
    // ContextId is only a locator: JSON cannot assert version, route, or eligibility.
    private sealed record Claim(int ProcessId, long StartedUtcTicks, int SessionId, string ImagePath, string ContextId = "");
    private sealed record Candidate(Claim Identity, UpdateVersion Version, bool IsModern, bool Trusted, long RequestedUtcTicks, InstancePeerIdentity? ProtectedPeer = null);
    private readonly string _directory, _claimPath, _sid;
    private readonly Claim _self;
    private readonly Candidate? _candidate;
    private readonly Action _requestClose;
    private readonly bool _fixture;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<(int Id, long Started), SafeProcessHandle> _priorProcesses = new();
    private readonly Dictionary<(int Id, long Started, string Path, string Context), Candidate> _validated = new();
    private readonly HashSet<(int Id, long Started)> _legacyNotified = new();
    private FileStream? _registration, _exclusive;
    private Candidate? _winner;
    private volatile bool _revoked, _owner;
    private int _disposed;
    internal bool IsOwner => _owner && !_revoked && Volatile.Read(ref _disposed) == 0;
    internal string WinningContextId => _winner?.Identity.ContextId ?? "";

    internal SingleInstanceCoordinator(Action requestClose, string? fixtureDirectory = null)
    {
        _requestClose = requestClose; _fixture = fixtureDirectory is not null;
        using var identity = WindowsIdentity.GetCurrent();
        _sid = identity.User?.Value ?? throw new IOException("Cannot read the current user identity.");
        using var current = Process.GetCurrentProcess();
        _self = Inspect(current.Id) ?? throw new IOException("Cannot read the current Process Keeper instance identity.");
        if (!_fixture) _self = _self with { ContextId = InstanceRedirect.CurrentOpaqueContextId };
        else if (FileVersionInfo.GetVersionInfo(_self.ImagePath).FileDescription?.Contains("| trusted-", StringComparison.Ordinal) == true)
            _self = _self with { ContextId = "fixture-authenticated" };
        _candidate = Resolve(_self);
        _directory = fixtureDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ProcessKeeper", "instances", $"{_sid}-{_self.SessionId}");
        _claimPath = Path.Combine(_directory, $"{_self.ProcessId}-{_self.StartedUtcTicks}-{Guid.NewGuid():N}.json");
    }

    internal async Task<InstanceAcquireResult> AcquireAsync(CancellationToken cancellationToken = default, TimeSpan? timeout = null)
    {
        if (_candidate is null) return InstanceAcquireResult.Superseded;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var token = linked.Token;
        try
        {
            token.ThrowIfCancellationRequested(); Directory.CreateDirectory(_directory);
            if ((File.GetAttributes(_directory) & FileAttributes.ReparsePoint) != 0) return InstanceAcquireResult.Blocked;
            _registration = new FileStream(_claimPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
            JsonSerializer.Serialize(_registration, _self); _registration.Flush(true);
            var watch = Stopwatch.StartNew(); var limit = timeout ?? TimeSpan.FromSeconds(20);
            while (watch.Elapsed < limit)
            {
                token.ThrowIfCancellationRequested(); var peers = CollectPeers();
                if (FindSuperior(peers) is { } winner) { _winner = winner; Dispose(); return InstanceAcquireResult.Superseded; }
                foreach (var peer in peers) Remember(peer);
                if (_exclusive is null)
                    try { _exclusive = new FileStream(Path.Combine(_directory, "active.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                    catch (IOException) { }
                if (_exclusive is not null)
                {
                    peers = CollectPeers();
                    if (FindSuperior(peers) is { } lateWinner) { _winner = lateWinner; Dispose(); return InstanceAcquireResult.Superseded; }
                    foreach (var peer in peers) { Remember(peer); RequestUncoordinatedClose(peer); }
                    // Retained handles remain valid after claims disappear and the old window releases its lease.
                    if (_priorProcesses.Values.All(handle => WaitForSingleObject(handle, 0) == 0))
                    { _owner = true; _ = WatchForSuperiorAsync(); return InstanceAcquireResult.Acquired; }
                }
                await Task.Delay(120, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { Dispose(); return InstanceAcquireResult.Superseded; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        Dispose(); return InstanceAcquireResult.Blocked;
    }

    private async Task WatchForSuperiorAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                await Task.Delay(150, _lifetime.Token).ConfigureAwait(false); Candidate? winner;
                try { winner = FindSuperior(CollectPeers()); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { continue; }
                if (winner is null) continue;
                _winner = winner; _revoked = true; _requestClose(); return;
            }
        }
        catch (OperationCanceledException) { }
    }
    private Candidate? FindSuperior(IEnumerable<Candidate> peers) => peers.Where(peer => Compare(peer, _candidate!) > 0)
        .OrderByDescending(peer => peer, Comparer<Candidate>.Create(Compare)).FirstOrDefault();
    private static int Compare(Candidate left, Candidate right)
    {
        // Mutable VERSIONINFO on an unwrapped developer executable cannot displace a protected packaged instance.
        var result = left.Trusted.CompareTo(right.Trusted); if (result != 0) return result;
        result = left.Version.CompareTo(right.Version); if (result != 0) return result;
        result = left.IsModern.CompareTo(right.IsModern); if (result != 0) return result;
        result = left.RequestedUtcTicks.CompareTo(right.RequestedUtcTicks); if (result != 0) return result;
        result = left.Identity.StartedUtcTicks.CompareTo(right.Identity.StartedUtcTicks); if (result != 0) return result;
        return left.Identity.ProcessId.CompareTo(right.Identity.ProcessId);
    }
    private IReadOnlyList<Candidate> CollectPeers()
    {
        var peers = new Dictionary<(int, long), Candidate>();
        foreach (var path in Directory.EnumerateFiles(_directory, "*.json").Take(4096))
        {
            if (string.Equals(path, _claimPath, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length is <= 0 or > 4096) continue;
                var claim = JsonSerializer.Deserialize<Claim>(stream);
                if (claim is null || claim.ProcessId == _self.ProcessId || claim.SessionId != _self.SessionId) continue;
                var live = Inspect(claim.ProcessId);
                if (live is null || live.StartedUtcTicks != claim.StartedUtcTicks || live.SessionId != claim.SessionId || !string.Equals(live.ImagePath, claim.ImagePath, StringComparison.OrdinalIgnoreCase)) continue;
                var candidate = Resolve(live with { ContextId = claim.ContextId ?? "" });
                if (candidate is not null)
                {
                    var key = (live.ProcessId, live.StartedUtcTicks);
                    if (!peers.TryGetValue(key, out var prior) || Compare(candidate, prior) > 0) peers[key] = candidate;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or System.Security.SecurityException) { }
        }
        if (!_fixture)
            foreach (var process in Process.GetProcessesByName("ProcessKeeper"))
                using (process)
                    try
                    {
                        var live = Inspect(process.Id);
                        if (live is null || live.ProcessId == _self.ProcessId || live.SessionId != _self.SessionId || peers.ContainsKey((live.ProcessId, live.StartedUtcTicks))) continue;
                        var candidate = Resolve(live); if (candidate is not null) peers[(live.ProcessId, live.StartedUtcTicks)] = candidate;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException or System.Security.SecurityException) { }
        return peers.Values.ToArray();
    }
    private Candidate? Resolve(Claim claim)
    {
        try
        {
            if (claim.SessionId != _self.SessionId || !IsCurrentOwner(claim.ProcessId)) return null;
            var key = (claim.ProcessId, claim.StartedUtcTicks, claim.ImagePath, claim.ContextId);
            if (_validated.TryGetValue(key, out var cached))
            {
                if (cached.ProtectedPeer is null || InstanceRedirect.IsPeerAlive(cached.ProtectedPeer)) return cached;
                _validated.Remove(key);
            }
            Candidate? result = null;
            if (!_fixture && claim.ContextId.Length > 0 && InstanceRedirect.TryGetPeer(claim.ContextId, out var trusted) && trusted is not null &&
                trusted.ProcessId == claim.ProcessId && trusted.StartedUtcTicks == claim.StartedUtcTicks && trusted.SessionId == claim.SessionId &&
                string.Equals(trusted.ImagePath, claim.ImagePath, StringComparison.OrdinalIgnoreCase) && UpdateVersion.TryParse(trusted.Version, out var trustedVersion))
                result = new Candidate(claim, trustedVersion!, trusted.IsModern, true, trusted.RequestedUtcTicks > 0 ? trusted.RequestedUtcTicks : claim.StartedUtcTicks, trusted);
            else
            {
                // Direct developer launches: only known product metadata from the exact live executable is eligible.
                var info = FileVersionInfo.GetVersionInfo(claim.ImagePath); bool modern; var trustedFixture = false;
                if (_fixture)
                {
                    if (info.ProductName != "ProcessKeeper.InstanceFixture" || !(info.FileDescription is "ProcessKeeper singleton fixture | modern" or "ProcessKeeper singleton fixture | compat" or "ProcessKeeper singleton fixture | trusted-modern" or "ProcessKeeper singleton fixture | trusted-compat")) return null;
                    modern = info.FileDescription.EndsWith("modern", StringComparison.Ordinal);
                    trustedFixture = info.FileDescription.Contains("| trusted-", StringComparison.Ordinal) && claim.ContextId == "fixture-authenticated";
                }
                else
                {
                    modern = info.OriginalFilename == "ProcessKeeper.dll" && info.ProductName == "ProcessKeeper";
                    if (!modern && !(info.OriginalFilename == "ProcessKeeper.exe" && info.ProductName == "Process Keeper")) return null;
                }
                if (modern && !IsEligibleModernImage(claim.ImagePath) || !UpdateVersion.TryParse(info.ProductVersion, out var version)) return null;
                result = new Candidate(claim with { ContextId = trustedFixture ? claim.ContextId : "" }, version!, modern, trustedFixture, claim.StartedUtcTicks);
            }
            if (_validated.Count < 4096) _validated[key] = result;
            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or System.Security.SecurityException or ArgumentException) { return null; }
    }
    private void Remember(Candidate peer)
    {
        var key = (peer.Identity.ProcessId, peer.Identity.StartedUtcTicks); if (_priorProcesses.ContainsKey(key)) return;
        var handle = OpenProcess(0x1000 | 0x00100000, false, key.ProcessId);
        if (handle.IsInvalid || !GetProcessTimes(handle, out var created, out _, out _, out _) || DateTime.FromFileTimeUtc(created).Ticks != key.StartedUtcTicks)
        { handle.Dispose(); return; }
        _priorProcesses.Add(key, handle);
    }
    private void RequestUncoordinatedClose(Candidate peer)
    {
        if (_fixture || peer.Identity.ContextId.Length > 0 || Compare(peer, _candidate!) >= 0) return;
        var key = (peer.Identity.ProcessId, peer.Identity.StartedUtcTicks); if (!_legacyNotified.Add(key)) return;
        try
        {
            using var process = Process.GetProcessById(key.ProcessId); var live = Inspect(key.ProcessId);
            if (live is not null && live.StartedUtcTicks == key.StartedUtcTicks && process.MainWindowHandle != IntPtr.Zero &&
                (process.MainWindowTitle == "Process Keeper" || process.MainWindowTitle == "留驻" || process.MainWindowTitle.StartsWith("留驻 |", StringComparison.Ordinal))) process.CloseMainWindow();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException) { }
    }
    internal void NotifyWinner()
    {
        Environment.ExitCode = 3;
        var winner = _winner; if (winner is null) return;
        if (!_fixture && winner.Identity.ContextId.Length > 0 && InstanceRedirect.TrySignalPeer(winner.Identity.ContextId)) Environment.ExitCode = 3;
        var live = Inspect(winner.Identity.ProcessId);
        if (live is null || live.StartedUtcTicks != winner.Identity.StartedUtcTicks || !IsCurrentOwner(live.ProcessId)) return;
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var pid); if (pid != live.ProcessId || !IsWindowVisible(window)) return true;
            var name = new StringBuilder(256); GetClassNameW(window, name, name.Capacity);
            if (name.ToString() is "ConsoleWindowClass" or "PseudoConsoleWindow") return true;
            if (IsIconic(window)) ShowWindow(window, 9); SetForegroundWindow(window); return false;
        }, IntPtr.Zero);
    }
    private bool IsCurrentOwner(int id)
    {
        using var process = OpenProcess(0x1000, false, id);
        if (process.IsInvalid || !OpenProcessToken(process, 8, out var token)) return false;
        using (token) using (var owner = new WindowsIdentity(token.DangerousGetHandle())) return owner.User?.Value == _sid;
    }
    private static Claim? Inspect(int id)
    {
        if (id <= 0) return null;
        using var process = OpenProcess(0x1000 | 0x00100000, false, id);
        if (process.IsInvalid || WaitForSingleObject(process, 0) != 258 || !GetProcessTimes(process, out var created, out _, out _, out _) || !ProcessIdToSessionId((uint)id, out var session)) return null;
        var path = new StringBuilder(32768); var length = path.Capacity;
        if (!QueryFullProcessImageNameW(process, 0, path, ref length)) return null;
        try { return new Claim(id, DateTime.FromFileTimeUtc(created).Ticks, (int)session, path.ToString()); } catch (ArgumentOutOfRangeException) { return null; }
    }
    private static bool IsEligibleModernImage(string path)
    {
        var version = new OsVersion { Size = (uint)Marshal.SizeOf(typeof(OsVersion)) };
        if (RtlGetVersion(ref version) != 0 || version.Major < 10 || version.Build < 19041) return false;
        GetNativeSystemInfo(out var system);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); using var reader = new BinaryReader(stream);
        if (stream.Length < 64 || reader.ReadUInt16() != 0x5A4D) return false;
        stream.Position = 0x3C; var pe = reader.ReadUInt32(); if (pe > stream.Length - 6) return false;
        stream.Position = pe; if (reader.ReadUInt32() != 0x4550) return false;
        var machine = reader.ReadUInt16(); return system.Architecture == 9 && machine == 0x8664 || system.Architecture == 12 && machine == 0xAA64;
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _owner = false; _lifetime.Cancel(); _registration?.Dispose();
        try { File.Delete(_claimPath); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        _exclusive?.Dispose(); foreach (var handle in _priorProcesses.Values) handle.Dispose();
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct OsVersion { public uint Size, Major, Minor, Build, Platform; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string ServicePack; }
    [StructLayout(LayoutKind.Sequential)] private struct SystemInfo { public ushort Architecture, Reserved; public uint PageSize; public IntPtr Minimum, Maximum, Mask; public uint Processors, Type, Granularity; public ushort Level, Revision; }
    [DllImport("ntdll.dll")] private static extern int RtlGetVersion(ref OsVersion version);
    [DllImport("kernel32.dll")] private static extern void GetNativeSystemInfo(out SystemInfo info);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int id);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetProcessTimes(SafeProcessHandle handle, out long created, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ProcessIdToSessionId(uint id, out uint session);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryFullProcessImageNameW(SafeProcessHandle handle, int flags, StringBuilder path, ref int length);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    private delegate bool EnumWindowCallback(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out int id);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(IntPtr window, StringBuilder name, int maximum);
}
