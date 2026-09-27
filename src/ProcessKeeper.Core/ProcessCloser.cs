using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ProcessKeeper.Core;

/// <summary>Closes a frozen list only. No process-tree termination, image-name kill, or fresh candidate expansion.</summary>
public sealed class ProcessCloser
{
    private readonly SteamShutdownService _steam;
    private readonly Func<ProcessSnapshot> _capture;
    public ProcessCloser(SteamShutdownService? steam = null, Func<ProcessSnapshot>? capture = null)
    { _steam = steam ?? new SteamShutdownService(); _capture = capture ?? new ProcessCollector().Capture; }

    public async Task<IReadOnlyList<CloseResult>> CloseAsync(
        IReadOnlyList<ProcessRecord> frozenTargets,
        bool forceAfterWait,
        Func<ProcessRecord, ProtectionDecision> recheck,
        CancellationToken cancellationToken = default)
    {
        var baseline = _capture();
        return (await CloseDetailedAsync(frozenTargets, forceAfterWait, recheck, baseline, cancellationToken).ConfigureAwait(false)).Results;
    }

    public async Task<CloseBatchReport> CloseDetailedAsync(IReadOnlyList<ProcessRecord> frozenTargets,
        bool forceAfterWait, Func<ProcessRecord, ProtectionDecision> recheck, ProcessSnapshot baselineSnapshot,
        CancellationToken cancellationToken = default, IProgress<string>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(frozenTargets);
        ArgumentNullException.ThrowIfNull(recheck);
        using var whitelistLease = WhitelistProfileOperationGate.EnterClose();
        var seen = new HashSet<int>();
        var tasks = new List<Task<CloseResult>>();
        var steam = new List<ProcessRecord>();
        var ordinary = new List<ProcessRecord>();
        foreach (var process in frozenTargets.ToArray())
        {
            if (!seen.Add(process.Id))
                tasks.Add(Task.FromResult(Result(process, L.T("跳过重复 PID"), false)));
            else if (SteamShutdownService.IsSteam(process)) steam.Add(process);
            else
            {
                ordinary.Add(process);
                tasks.Add(CloseOneAsync(process, forceAfterWait, recheck, cancellationToken));
            }
        }
        var steamTask = _steam.CloseAsync(steam, recheck, cancellationToken, progress);
        var ordinaryResults = await Task.WhenAll(tasks).ConfigureAwait(false);
        var steamResult = await steamTask.ConfigureAwait(false);
        var results = ordinaryResults.Concat(steamResult.Results).ToArray();
        var respawns = new List<ProcessRecord>(steamResult.RespawnedProcesses);
        var observationTargets = ordinary.Where(p => results.Any(r => r.ProcessId == p.Id && r.Success)).ToArray();
        if (observationTargets.Length > 0 && !cancellationToken.IsCancellationRequested)
        {
            try
            {
                // Observation never expands the confirmed action list.
                var first = _capture();
                respawns.AddRange(ProcessRespawnObserver.Find(observationTargets, baselineSnapshot, first));
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
                respawns.AddRange(ProcessRespawnObserver.Find(observationTargets, baselineSnapshot, _capture()));
            }
            catch (OperationCanceledException)
            {
                results = results.Select(result => result.Success && observationTargets.Any(p => p.Id == result.ProcessId)
                    ? result with { Success = false, Outcome = result.Outcome + L.T(" | 已取消稳定检查，请刷新确认。") } : result).ToArray();
            }
            catch (Exception exception)
            {
                results = results.Select(result => ordinary.Any(p => p.Id == result.ProcessId) && result.Success
                    ? result with { Success = false, Outcome = result.Outcome + L.F($" | 无法核实关闭后的进程状态：{exception.Message}") } : result).ToArray();
            }
        }
        if (respawns.Count > 0)
            results = results.Select(result => frozenTargets.Any(old => old.Id == result.ProcessId && respawns.Any(now =>
                old.OwnerSid == now.OwnerSid && old.SessionId == now.SessionId && ProcessRespawnObserver.SamePath(old.Path, now.Path)))
                ? result with { Success = false, Outcome = result.Outcome + L.T(" | 观察到新进程，未自动追加或结束，请刷新确认。") } : result).ToArray();
        return new(results, steamResult.PendingForce, respawns.DistinctBy(p => (p.Id, p.StartTimeUtcTicks)).ToArray());
    }

    /// <summary>Call only after a separate explicit confirmation of these original identities.</summary>
    public async Task<IReadOnlyList<CloseResult>> ForceConfirmedAsync(IReadOnlyList<ProcessRecord> frozenTargets,
        Func<ProcessRecord, ProtectionDecision> recheck, CancellationToken cancellationToken = default)
    {
        using var whitelistLease = WhitelistProfileOperationGate.EnterClose();
        var baseline = _capture();
        var results = new List<CloseResult>();
        foreach (var target in frozenTargets.DistinctBy(p => (p.Id, p.StartTimeUtcTicks)))
        {
            if (!SteamShutdownService.IsClient(target))
                results.Add(Result(target, L.T("目标不属于已确认的 Steam 客户端进程，未强制结束。"), false));
            else results.Add(await CloseOneAsync(target, true, recheck, cancellationToken).ConfigureAwait(false));
        }
        if (results.Any(p => p.Success))
        {
            try
            {
                var observations = new List<ProcessRecord>(ProcessRespawnObserver.Find(frozenTargets, baseline, _capture()));
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
                observations.AddRange(ProcessRespawnObserver.Find(frozenTargets, baseline, _capture()));
                if (observations.Count > 0)
                {
                    var ids = string.Join(", ", observations.Select(p => p.Id).Distinct());
                    for (int i = 0; i < results.Count; i++)
                        results[i] = results[i] with { Success = false, Outcome = results[i].Outcome + L.F($" | 观察到新进程 PID {ids}，未自动结束，请刷新确认。") };
                }
            }
            catch (Exception exception)
            {
                for (int i = 0; i < results.Count; i++)
                    if (results[i].Success) results[i] = results[i] with { Success = false,
                        Outcome = results[i].Outcome + L.F($" | 稳定检查未完成：{exception.Message}") };
            }
        }
        return results;
    }

    private static async Task<CloseResult> CloseOneAsync(ProcessRecord target, bool force,
        Func<ProcessRecord, ProtectionDecision> recheck, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return Result(target, L.T("已取消，未发送关闭请求"), false);
        if (target.Id <= 4 || target.Id == Environment.ProcessId)
            return Result(target, L.T("跳过系统进程或当前程序自身"), false);
        if (!HasCompleteIdentity(target)) return Result(target, L.T("跳过：目标进程身份信息不完整"), false);
        try
        {
            var decision = recheck(target);
            if (decision.Protected) return Result(target, L.T("跳过：") + decision.Reason, false);
            const uint QueryLimited = 0x1000, Synchronize = 0x00100000, Terminate = 0x0001;
            using var handle = CloseNative.OpenProcess(QueryLimited | Synchronize | (force ? Terminate : 0), false, target.Id);
            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                return Result(target, error == 87 ? L.T("进程已退出") : L.T("无法打开进程：") + new Win32Exception(error).Message, error == 87);
            }
            if (HasExited(handle)) return Result(target, L.T("进程已退出"), true);
            if (!ValidateIdentity(handle, target, out string identityReason)) return Result(target, identityReason, false);
            if (!CloseNative.IsProcessCritical(handle, out bool critical))
                return Result(target, L.T("跳过：无法检查系统关键进程标志"), false);
            if (critical) return Result(target, L.T("跳过：Windows 将其标记为关键进程"), false);
            // Re-evaluate immediately before the first side effect; rules can change while a dialog is open.
            decision = recheck(target);
            if (decision.Protected) return Result(target, L.T("跳过：") + decision.Reason, false);
            if (cancellationToken.IsCancellationRequested) return Result(target, L.T("已取消，未发送关闭请求"), false);

            int sent = 0;
            int postFailures = 0;
            CloseNative.EnumWindows((window, _) =>
            {
                if (cancellationToken.IsCancellationRequested) return false;
                CloseNative.GetWindowThreadProcessId(window, out uint owner);
                if (owner != (uint)target.Id || HasExited(handle)) return true;
                // PostMessage cannot block on a hung app. A held process handle prevents PID reuse
                // from turning the later force step into termination of a different process.
                if (CloseNative.PostMessageW(window, 0x0010 /* WM_CLOSE */, 0, 0)) sent++;
                else postFailures++;
                return true;
            }, 0);

            if (sent == 0 && !force)
                return HasExited(handle) ? Result(target, L.T("进程已退出"), true) :
                    Result(target, postFailures > 0 ? L.T("未关闭：关闭请求被拒绝（可能需要管理员权限）") :
                        L.T("未关闭：无可关闭窗口，未启用强制结束"), false);

            var grace = Stopwatch.StartNew();
            while (!HasExited(handle) && grace.Elapsed < TimeSpan.FromSeconds(5))
            {
                if (cancellationToken.IsCancellationRequested)
                    return Result(target, sent > 0 ? L.T("已取消后续操作；此前已发送正常关闭请求") : L.T("已取消，进程未结束"), false);
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
            if (HasExited(handle)) return Result(target, sent > 0 ? L.T("已正常退出") : L.T("进程自行退出"), true);
            if (!force) return Result(target, L.T("仍在运行：已发送正常关闭请求，未启用强制结束"), false);
            if (cancellationToken.IsCancellationRequested) return Result(target, L.T("已取消强制结束"), false);

            decision = recheck(target);
            if (decision.Protected) return Result(target, L.T("取消强制结束：") + decision.Reason, false);
            if (!ValidateIdentity(handle, target, out identityReason)) return Result(target, identityReason, false);
            if (!CloseNative.IsProcessCritical(handle, out critical) || critical)
                return Result(target, L.T("取消强制结束：关键进程检查未通过"), false);
            if (cancellationToken.IsCancellationRequested) return Result(target, L.T("已取消强制结束"), false);
            if (!CloseNative.TerminateProcess(handle, 1))
            {
                int error = Marshal.GetLastWin32Error();
                return HasExited(handle) ? Result(target, L.T("进程已退出"), true) :
                    Result(target, L.T("强制结束失败：") + new Win32Exception(error).Message, false);
            }
            for (int i = 0; i < 20 && !HasExited(handle); i++) await Task.Delay(50).ConfigureAwait(false);
            return HasExited(handle) ? Result(target, L.T("已强制结束"), true) : Result(target, L.T("已发送强制结束请求，尚未确认退出"), false);
        }
        catch (OperationCanceledException)
        {
            return Result(target, L.T("已取消后续操作；已发送的正常关闭请求无法撤销"), false);
        }
        catch (Exception ex)
        {
            return Result(target, L.T("跳过或关闭失败：") + ex.Message, false);
        }
    }

    private static bool HasCompleteIdentity(ProcessRecord p) =>
        p.StartTimeUtcTicks > 0 && !string.IsNullOrWhiteSpace(p.OwnerSid) && ProtectionPolicy.TryNormalizePath(p.Path, out _);

    private static bool HasExited(SafeProcessHandle handle) => CloseNative.WaitForSingleObject(handle, 0) == 0;

    private static bool ValidateIdentity(SafeProcessHandle handle, ProcessRecord target, out string reason)
    {
        reason = L.T("跳过：进程身份已变化或无法重新核验");
        if (CloseNative.GetProcessId(handle) != (uint)target.Id) return false;
        if (!CloseNative.ProcessIdToSessionId((uint)target.Id, out var session) || session != target.SessionId)
        { reason = L.T("跳过：进程所属会话已变化"); return false; }
        if (!CloseNative.GetProcessTimes(handle, out var created, out _, out _, out _)) return false;
        long ticks = DateTime.FromFileTimeUtc(((long)created.High << 32) | created.Low).Ticks;
        if (ticks != target.StartTimeUtcTicks) { reason = L.T("跳过：PID 已复用，创建时间不同"); return false; }
        var path = new StringBuilder(32768);
        int size = path.Capacity;
        if (!CloseNative.QueryFullProcessImageNameW(handle, 0, path, ref size)) return false;
        if (!ProtectionPolicy.TryNormalizePath(path.ToString(), out string actualPath) ||
            !ProtectionPolicy.TryNormalizePath(target.Path, out string expectedPath) ||
            !actualPath.Equals(expectedPath, StringComparison.OrdinalIgnoreCase))
        { reason = L.T("跳过：进程路径已变化"); return false; }
        if (!CloseNative.OpenProcessToken(handle, 0x0008 /* TOKEN_QUERY */, out var token)) return false;
        using (token)
        {
            CloseNative.GetTokenInformation(token, 1 /* TokenUser */, 0, 0, out int length);
            if (length <= 0 || length > 1024 * 1024) return false;
            nint buffer = Marshal.AllocHGlobal(length);
            try
            {
                if (!CloseNative.GetTokenInformation(token, 1, buffer, length, out _)) return false;
                var sidPointer = Marshal.ReadIntPtr(buffer);
                var sid = new SecurityIdentifier(sidPointer).Value;
                if (!sid.Equals(target.OwnerSid, StringComparison.OrdinalIgnoreCase))
                { reason = L.T("跳过：进程所属用户已变化"); return false; }
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        return true;
    }

    private static CloseResult Result(ProcessRecord target, string outcome, bool success) => new(target.Id, target.Name, outcome, success);

    internal static SafeProcessHandle OpenVerifiedForShutdown(ProcessRecord target)
    {
        if (target.Id <= 4 || target.Id == Environment.ProcessId || !HasCompleteIdentity(target))
            throw new InvalidOperationException(L.T("Steam 目标身份不完整或受系统保护。"));
        var handle = CloseNative.OpenProcess(0x1000 | 0x00100000, false, target.Id);
        try
        {
            if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (HasExited(handle) || !ValidateIdentity(handle, target, out _))
                throw new InvalidOperationException(L.T("Steam 目标已退出或身份发生变化。"));
            if (!CloseNative.IsProcessCritical(handle, out var critical) || critical)
                throw new InvalidOperationException(L.T("Steam 目标的关键进程检查未通过。"));
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }

    internal static bool HasFrozenProcessExited(ProcessRecord target)
    {
        using var handle = CloseNative.OpenProcess(0x1000 | 0x00100000, false, target.Id);
        if (handle.IsInvalid)
        {
            if (Marshal.GetLastWin32Error() == 87) return true;
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        if (HasExited(handle)) return true;
        if (!ValidateIdentity(handle, target, out var reason)) throw new InvalidOperationException(reason);
        return false;
    }

    private static class CloseNative
    {
        [StructLayout(LayoutKind.Sequential)] internal struct FileTime { public uint Low; public uint High; }
        internal delegate bool EnumWindowsProc(nint hwnd, nint parameter);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern SafeProcessHandle OpenProcess(uint access, bool inheritHandle, int processId);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint GetProcessId(SafeProcessHandle process);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetProcessTimes(SafeProcessHandle process, out FileTime created, out FileTime exited, out FileTime kernel, out FileTime user);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, StringBuilder path, ref int length);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsProcessCritical(SafeProcessHandle process, [MarshalAs(UnmanagedType.Bool)] out bool critical);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);
        [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool OpenProcessToken(SafeProcessHandle process, uint desiredAccess, out SafeAccessTokenHandle token);
        [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass, nint buffer, int length, out int returnLength);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint window, out uint processId);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool PostMessageW(nint window, uint message, nint wParam, nint lParam);
    }
}
