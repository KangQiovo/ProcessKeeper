using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace ProcessKeeper.Core;

/// <summary>Read-only Windows process, window and service snapshot collector.</summary>
public sealed class ProcessCollector
{
    private readonly object _captureLock = new();
    private readonly Dictionary<string, Metadata> _metadata = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _selfId = Environment.ProcessId;

    public ProcessSnapshot Capture()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException(L.T("进程采集仅支持 Windows。"));
        lock (_captureLock)
        {
            DateTimeOffset startedAt = DateTimeOffset.UtcNow;
            var entries = ReadProcessEntries();
            var sessions = ReadSessionIds();
            var windows = ReadWindows(out string windowWarning);
            var services = ReadServices(out string serviceWarning);
            var processes = new List<ProcessRecord>(entries.Count);
            foreach (var entry in entries)
            {
                processes.Add(ReadProcess(entry, sessions, windows, services, startedAt.UtcTicks, windowWarning, serviceWarning));
            }
            var classified = ProcessIdentity.ClassifyAll(processes);
            if (_metadata.Count > 4096)
            {
                var active = classified.Select(p => p.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (string key in _metadata.Keys.Where(k => !active.Contains(k)).ToArray()) _metadata.Remove(key);
            }
            return new ProcessSnapshot(DateTimeOffset.Now, classified, ProcessIdentity.Group(classified));
        }
    }

    private static List<CollectorNative.ProcessEntry> ReadProcessEntries()
    {
        using var snapshot = CollectorNative.CreateToolhelp32Snapshot(0x00000002, 0);
        if (snapshot.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), L.T("无法创建进程快照。"));
        var entries = new List<CollectorNative.ProcessEntry>();
        var entry = new CollectorNative.ProcessEntry { Size = (uint)Marshal.SizeOf<CollectorNative.ProcessEntry>(), ExeFile = "" };
        if (!CollectorNative.Process32FirstW(snapshot, ref entry))
        {
            int error = Marshal.GetLastWin32Error();
            if (error == 18) return entries; // ERROR_NO_MORE_FILES
            throw new Win32Exception(error, L.T("无法读取进程快照。"));
        }
        do { entries.Add(entry); }
        while (CollectorNative.Process32NextW(snapshot, ref entry));
        int endError = Marshal.GetLastWin32Error();
        if (endError != 18) throw new Win32Exception(endError, L.T("进程快照枚举未完整结束。"));
        return entries;
    }

    private static Dictionary<int, int> ReadSessionIds()
    {
        // ProcessIdToSessionId still requires per-process QUERY_INFORMATION access. The BCL's
        // enumerated Process instances carry the session from its system-wide process snapshot,
        // including inaccessible service processes, without opening each process handle.
        var result = new Dictionary<int, int>();
        Process[] processes;
        try { processes = Process.GetProcesses(); }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException) { return result; }
        foreach (var process in processes)
        {
            using (process)
            {
                try { result[process.Id] = process.SessionId; }
                catch (Exception e) when (e is Win32Exception or InvalidOperationException) { }
            }
        }
        return result;
    }

    private ProcessRecord ReadProcess(CollectorNative.ProcessEntry entry, IReadOnlyDictionary<int, int> sessions,
        IReadOnlyDictionary<int, List<WindowRecord>> windows, IReadOnlyDictionary<int, List<ServiceRecord>> services,
        long enumerationStartedTicks, string windowWarning, string serviceWarning)
    {
        int id = (int)entry.ProcessId;
        int parentId = (int)entry.ParentProcessId;
        string name = entry.ExeFile;
        int session = CollectorNative.ProcessIdToSessionId(entry.ProcessId, out uint sessionId) ? unchecked((int)sessionId) :
            sessions.TryGetValue(id, out int snapshotSession) ? snapshotSession : -1;
        string path = "", owner = "", package = "";
        long start = 0, memory = 0;
        bool critical = false;
        bool? nativeCritical = null;
        var warnings = new List<string>(4);

        using (var process = CollectorNative.OpenProcess(0x1000, false, entry.ProcessId))
        {
            if (process.IsInvalid)
            {
                warnings.Add(L.F($"无法打开进程（Win32 {Marshal.GetLastWin32Error()}）；可能已退出或权限不足"));
            }
            else
            {
                if (CollectorNative.GetProcessTimes(process, out var creation, out _, out _, out _))
                {
                    long fileTime = creation.ToInt64();
                    if (fileTime > 0)
                    {
                        try { start = DateTime.FromFileTimeUtc(fileTime).Ticks; }
                        catch (ArgumentOutOfRangeException) { warnings.Add(L.T("启动时间格式无效")); }
                    }
                }
                else warnings.Add(L.T("无法读取启动时间"));

                var pathBuffer = new StringBuilder(32768);
                uint pathLength = (uint)pathBuffer.Capacity;
                if (CollectorNative.QueryFullProcessImageNameW(process, 0, pathBuffer, ref pathLength))
                {
                    path = pathBuffer.ToString();
                    // An old Toolhelp PID can be reused while the snapshot is read. The handle's image wins.
                    if (path.Length > 0) name = System.IO.Path.GetFileName(path);
                }
                else warnings.Add(L.T("无法读取可执行文件路径"));

                if (CollectorNative.OpenProcessToken(process, 0x0008, out var token))
                {
                    using (token)
                    {
                        owner = ReadOwnerSid(token);
                        nint sessionBuffer = Marshal.AllocHGlobal(sizeof(uint));
                        try
                        {
                            if (CollectorNative.GetTokenInformation(token, 12, sessionBuffer, sizeof(uint), out _))
                                session = Marshal.ReadInt32(sessionBuffer);
                        }
                        finally { Marshal.FreeHGlobal(sessionBuffer); }
                    }
                }
                if (owner.Length == 0) warnings.Add(L.T("无法核实所属用户 SID"));

                uint packageLength = 0;
                int packageResult = CollectorNative.GetPackageFamilyName(process, ref packageLength, null);
                if (packageResult == 122 && packageLength is > 0 and < 32768)
                {
                    var packageBuffer = new StringBuilder((int)packageLength);
                    if (CollectorNative.GetPackageFamilyName(process, ref packageLength, packageBuffer) == 0)
                        package = packageBuffer.ToString();
                }

                var counters = new CollectorNative.ProcessMemoryCounters { Size = (uint)Marshal.SizeOf<CollectorNative.ProcessMemoryCounters>() };
                if (CollectorNative.K32GetProcessMemoryInfo(process, ref counters, counters.Size))
                    memory = (long)Math.Min((ulong)long.MaxValue, counters.WorkingSetSize.ToUInt64());
                if (CollectorNative.IsProcessCritical(process, out bool isCritical)) { critical = isCritical; nativeCritical = isCritical; }

                if (start > enumerationStartedTicks)
                {
                    // All metadata belongs to the opened handle; only the Toolhelp parent is stale/uncertain.
                    parentId = 0;
                    warnings.Add(L.T("进程在本轮枚举期间启动；本轮不关联快照中的父 PID"));
                }
            }
        }

        if (windowWarning.Length > 0) warnings.Add(windowWarning);
        if (serviceWarning.Length > 0) warnings.Add(serviceWarning);
        Metadata metadata = GetMetadata(path);
        return new ProcessRecord
        {
            Id = id, ParentId = parentId, Name = name, SessionId = session, StartTimeUtcTicks = start,
            Path = path, OwnerSid = owner, PackageFamilyName = package, MemoryBytes = memory,
            ProductName = metadata.Product, Company = metadata.Company, Description = metadata.Description,
            IsSelf = id == _selfId, IsSystem = critical, NativeCritical = nativeCritical, SystemReason = critical ? L.T("Windows 标记为关键进程") : "",
            Windows = windows.TryGetValue(id, out var processWindows) ? processWindows.ToArray() : [],
            Services = services.TryGetValue(id, out var processServices) ? processServices.ToArray() : [],
            CollectionWarning = string.Join("；", warnings)
        };
    }

    private static string ReadOwnerSid(CollectorNative.KernelHandle token)
    {
        CollectorNative.GetTokenInformation(token, 1, nint.Zero, 0, out uint needed);
        if (needed is 0 or > 65536) return "";
        nint buffer = Marshal.AllocHGlobal((int)needed);
        try
        {
            if (!CollectorNative.GetTokenInformation(token, 1, buffer, needed, out _)) return "";
            // TOKEN_USER starts with SID_AND_ATTRIBUTES; its first member is the SID pointer.
            nint sid = Marshal.ReadIntPtr(buffer);
            return sid == nint.Zero ? "" : new SecurityIdentifier(sid).Value;
        }
        catch (ArgumentException) { return ""; }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private Metadata GetMetadata(string path)
    {
        if (path.Length == 0) return Metadata.Empty;
        if (_metadata.TryGetValue(path, out var cached) && DateTime.UtcNow - cached.CachedAt < TimeSpan.FromMinutes(5)) return cached;
        Metadata metadata;
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            metadata = new Metadata(info.ProductName?.Trim() ?? "", info.CompanyName?.Trim() ?? "", info.FileDescription?.Trim() ?? "", DateTime.UtcNow);
        }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException or ArgumentException or Win32Exception)
        {
            metadata = Metadata.Empty with { CachedAt = DateTime.UtcNow };
        }
        _metadata[path] = metadata;
        return metadata;
    }

    private static Dictionary<int, List<WindowRecord>> ReadWindows(out string warning)
    {
        var result = new Dictionary<int, List<WindowRecord>>();
        nint foreground = CollectorNative.GetForegroundWindow();
        CollectorNative.EnumWindowsCallback callback = (window, _) =>
        {
            try
            {
                CollectorNative.GetWindowThreadProcessId(window, out uint processId);
                if (processId == 0) return true;
                bool visible = CollectorNative.IsWindowVisible(window);
                bool minimized = CollectorNative.IsIconic(window);
                bool cloaked = CollectorNative.DwmGetWindowAttribute(window, 14, out uint cloakValue, sizeof(uint)) != 0 || cloakValue != 0;
                bool usableBounds = CollectorNative.GetWindowRect(window, out var rect) && rect.Right > rect.Left && rect.Bottom > rect.Top;
                if (visible && !minimized && !usableBounds) visible = false;
                long style = CollectorNative.GetWindowLong(window, -16).ToInt64();
                long extendedStyle = CollectorNative.GetWindowLong(window, -20).ToInt64();
                var className = new StringBuilder(256);
                CollectorNative.GetClassNameW(window, className, className.Capacity);
                int titleLength = Math.Min(CollectorNative.GetWindowTextLengthW(window), 2048);
                var titleBuffer = new StringBuilder(titleLength + 1);
                if (titleLength > 0) CollectorNative.GetWindowTextW(window, titleBuffer, titleBuffer.Capacity);
                var record = new WindowRecord(window, titleBuffer.ToString(), visible, minimized, cloaked, foreground == window)
                {
                    ClassName = className.ToString(),
                    IsTopLevel = CollectorNative.GetAncestor(window, 2) == window && (style & 0x40000000L) == 0 && CollectorNative.GetParent(window) != new nint(-3),
                    IsToolWindow = (extendedStyle & 0x80) != 0,
                    HasUsableBounds = usableBounds,
                    HasCaption = (style & 0x00C00000L) == 0x00C00000L,
                    HasAppWindowStyle = (extendedStyle & 0x00040000L) != 0
                };
                AddWindow(result, (int)processId, record);

                // Legacy UWP apps can be hosted under an ApplicationFrameHost top-level window.
                // Record the actual child HWND owned by the app; never assign the host's HWND to another PID.
                if (className.ToString() == "ApplicationFrameWindow")
                {
                    CollectorNative.EnumWindowsCallback childCallback = (child, childParameter) =>
                    {
                        CollectorNative.GetWindowThreadProcessId(child, out uint childPid);
                        if (childPid != 0 && childPid != processId)
                        {
                            var childClass = new StringBuilder(256);
                            CollectorNative.GetClassNameW(child, childClass, childClass.Capacity);
                            long childStyle = CollectorNative.GetWindowLong(child, -16).ToInt64();
                            long childExtendedStyle = CollectorNative.GetWindowLong(child, -20).ToInt64();
                            AddWindow(result, (int)childPid, record with
                            {
                                Handle = child, IsTopLevel = false, ClassName = childClass.ToString(),
                                IsToolWindow = (childExtendedStyle & 0x80) != 0,
                                HasUsableBounds = CollectorNative.GetWindowRect(child, out var childRect) && childRect.Right > childRect.Left && childRect.Bottom > childRect.Top,
                                HasCaption = (childStyle & 0x00C00000L) == 0x00C00000L,
                                HasAppWindowStyle = (childExtendedStyle & 0x00040000L) != 0
                            });
                        }
                        return true;
                    };
                    CollectorNative.EnumChildWindows(window, childCallback, nint.Zero);
                    GC.KeepAlive(childCallback);
                }
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.IO.IOException)
            {
                // Windows can disappear while enumerating; retain all other records.
            }
            return true;
        };
        bool success = CollectorNative.EnumWindows(callback, nint.Zero);
        GC.KeepAlive(callback);
        warning = success ? "" : L.T("窗口枚举失败；显示状态可能不完整");
        return result;
    }

    private static void AddWindow(Dictionary<int, List<WindowRecord>> result, int processId, WindowRecord record)
    {
        if (!result.TryGetValue(processId, out var list)) result[processId] = list = [];
        if (!list.Any(w => w.Handle == record.Handle)) list.Add(record);
    }

    private static Dictionary<int, List<ServiceRecord>> ReadServices(out string warning)
    {
        warning = "";
        var result = new Dictionary<int, List<ServiceRecord>>();
        using var manager = CollectorNative.OpenSCManagerW(null, null, 0x0004);
        if (manager.IsInvalid)
        {
            warning = L.F($"服务映射不可读（Win32 {Marshal.GetLastWin32Error()}）");
            return result;
        }
        // The API caps pages at 256 KiB. Keep the resume handle and preserve every returned page.
        const uint capacity = 256 * 1024;
        nint buffer = Marshal.AllocHGlobal((int)capacity);
        uint resume = 0;
        int entrySize = Marshal.SizeOf<CollectorNative.EnumServiceStatusProcess>();
        try
        {
            for (int page = 0; page < 64; page++)
            {
                uint previousResume = resume;
                bool success = CollectorNative.EnumServicesStatusExW(manager, 0, 0x30, 3, buffer, capacity,
                    out _, out uint count, ref resume, null);
                int error = success ? 0 : Marshal.GetLastWin32Error();
                if (!success && error != 234)
                {
                    warning = L.F($"服务映射不完整（Win32 {error}）");
                    break;
                }
                for (uint i = 0; i < count; i++)
                {
                    var service = Marshal.PtrToStructure<CollectorNative.EnumServiceStatusProcess>(buffer + checked((int)i * entrySize));
                    int processId = (int)service.Status.ProcessId;
                    if (processId <= 0 || service.Status.CurrentState == 1) continue;
                    string name = Marshal.PtrToStringUni(service.ServiceName) ?? "";
                    string display = Marshal.PtrToStringUni(service.DisplayName) ?? name;
                    if (!result.TryGetValue(processId, out var records)) result[processId] = records = [];
                    if (!records.Any(s => s.Name == name)) records.Add(new ServiceRecord(name, display, processId));
                }
                if (success) break;
                if (resume == previousResume && count == 0)
                {
                    warning = L.T("服务枚举未能继续；映射可能不完整");
                    break;
                }
                if (page == 63) warning = L.T("服务数量超出分页上限；映射可能不完整");
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
        return result;
    }

    private sealed record Metadata(string Product, string Company, string Description, DateTime CachedAt)
    {
        public static Metadata Empty { get; } = new("", "", "", DateTime.MinValue);
    }
}
