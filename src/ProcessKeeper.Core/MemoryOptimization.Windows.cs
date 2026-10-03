using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ProcessKeeper.Core;

/// <summary>Independent Windows implementation. No launcher code is included.
/// Undocumented NT scopes are optional: errors are reported, never treated as success.
/// This class is invoked only after the UI confirmation through MemoryOptimizationService.</summary>
public sealed class WindowsMemoryOptimizationBackend : IMemoryOptimizationBackend
{
    public IMemoryOptimizationSession Open() => new Session();
    private sealed class Session : IMemoryOptimizationSession
    {
        private readonly TokenScope _token = new();
        public MemoryReading? Capture()
        {
            var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf(typeof(MemoryStatus)) };
            return GlobalMemoryStatusEx(ref memory) ? new(memory.TotalPhysical, memory.AvailablePhysical) : null;
        }
        public MemoryOperation Execute(MemoryOptimizationStage stage)
        {
            var privilege = stage == MemoryOptimizationStage.FileCache ? "SeIncreaseQuotaPrivilege" : "SeProfileSingleProcessPrivilege";
            if (_token.Errors.TryGetValue(privilege, out var detail)) return new(stage, MemoryOperationStatus.Failed, detail);
            try
            {
                if (stage == MemoryOptimizationStage.FileCache)
                    return SetSystemFileCacheSize(SizeTMinusOne, SizeTMinusOne, 0)
                        ? new(stage, MemoryOperationStatus.Succeeded, "") : Win32Failure(stage, Marshal.GetLastWin32Error());
                uint status;
                switch (stage)
                {
                    case MemoryOptimizationStage.WorkingSets:
                    case MemoryOptimizationStage.ModifiedPages:
                    case MemoryOptimizationStage.StandbyPages:
                    case MemoryOptimizationStage.LowPriorityStandby:
                        var command = stage == MemoryOptimizationStage.WorkingSets ? 2 : stage == MemoryOptimizationStage.ModifiedPages ? 3 : stage == MemoryOptimizationStage.StandbyPages ? 4 : 5;
                        status = NtMemoryList(80, ref command, sizeof(int));
                        break;
                    case MemoryOptimizationStage.RegistryCache:
                        status = NtEmptyBuffer(155, IntPtr.Zero, 0);
                        break;
                    case MemoryOptimizationStage.CombinePages:
                        var combined = new CombineInformation();
                        status = NtCombine(130, ref combined, (uint)Marshal.SizeOf(typeof(CombineInformation)));
                        break;
                    default: throw new ArgumentOutOfRangeException(nameof(stage));
                }
                if ((status & 0x80000000) == 0) return new(stage, MemoryOperationStatus.Succeeded, "");
                var unsupported = status is 0xC0000002 or 0xC0000003 or 0xC00000BB;
                return new(stage, unsupported ? MemoryOperationStatus.Unsupported : MemoryOperationStatus.Failed,
                    "NTSTATUS 0x" + status.ToString("X8") + " | " + new Win32Exception((int)RtlNtStatusToDosError(status)).Message);
            }
            catch (EntryPointNotFoundException error) { return new(stage, MemoryOperationStatus.Unsupported, error.Message); }
            catch (DllNotFoundException error) { return new(stage, MemoryOperationStatus.Unsupported, error.Message); }
        }
        public void Dispose() => _token.Dispose();
    }
    public static UIntPtr SizeTMinusOne => UIntPtr.Size == 8 ? new UIntPtr(ulong.MaxValue) : new UIntPtr(uint.MaxValue);
    private static MemoryOperation Win32Failure(MemoryOptimizationStage stage, int code) =>
        new(stage, MemoryOperationStatus.Failed, "Win32 " + code + " | " + new Win32Exception(code).Message);

    // An impersonation token limits temporary privilege changes to the worker thread.
    // Closing it restores its privileges without changing the process primary token.
    private sealed class TokenScope : IDisposable
    {
        private TokenHandle? _original, _temporary;
        private bool _attached;
        public Dictionary<string, string> Errors { get; } = new(StringComparer.Ordinal);
        public TokenScope()
        {
            try
            {
                if (!OpenProcessToken(GetCurrentProcess(), 0x0002 | 0x0008, out var primary)) throw new Win32Exception(Marshal.GetLastWin32Error());
                using (primary)
                    if (!DuplicateTokenEx(primary, 0x0004 | 0x0008 | 0x0020, IntPtr.Zero, 2, 2, out _temporary)) throw new Win32Exception(Marshal.GetLastWin32Error());
                if (!OpenThreadToken(GetCurrentThread(), 0x0004 | 0x0008, true, out _original))
                {
                    var error = Marshal.GetLastWin32Error(); _original.Dispose(); _original = null;
                    if (error != 1008) throw new Win32Exception(error);
                }
                foreach (var privilege in new[] { "SeProfileSingleProcessPrivilege", "SeIncreaseQuotaPrivilege" })
                {
                    if (!LookupPrivilegeValue(null, privilege, out var luid)) { Errors[privilege] = Error(Marshal.GetLastWin32Error()); continue; }
                    var state = new TokenPrivileges { Count = 1, Luid = luid, Attributes = 2 };
                    var adjusted = AdjustTokenPrivileges(_temporary!, false, ref state, 0, IntPtr.Zero, IntPtr.Zero);
                    var code = Marshal.GetLastWin32Error();
                    if (!adjusted || code != 0) Errors[privilege] = Error(code);
                }
                if (!SetThreadToken(IntPtr.Zero, _temporary!.DangerousGetHandle())) throw new Win32Exception(Marshal.GetLastWin32Error());
                _attached = true;
            }
            catch { Dispose(); throw; }
        }
        private static string Error(int code) => "Win32 " + code + " | " + new Win32Exception(code).Message;
        public void Dispose()
        {
            try
            {
                if (_attached)
                {
                    _attached = false;
                    if (!SetThreadToken(IntPtr.Zero, _original?.DangerousGetHandle() ?? IntPtr.Zero))
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "Restoring the worker token failed");
                }
            }
            finally { _temporary?.Dispose(); _original?.Dispose(); _temporary = null; _original = null; }
        }
    }
    private sealed class TokenHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public TokenHandle() : base(true) { }
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }
    [StructLayout(LayoutKind.Sequential)] private struct Luid { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)] private struct TokenPrivileges { public uint Count; public Luid Luid; public uint Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct CombineInformation { public IntPtr Event; public UIntPtr Pages; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct MemoryStatus
    {
        public uint Length, Load; public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetSystemFileCacheSize(UIntPtr minimum, UIntPtr maximum, uint flags);
    [DllImport("ntdll.dll", EntryPoint = "NtSetSystemInformation")] private static extern uint NtMemoryList(int infoClass, ref int command, uint length);
    [DllImport("ntdll.dll", EntryPoint = "NtSetSystemInformation")] private static extern uint NtEmptyBuffer(int infoClass, IntPtr buffer, uint length);
    [DllImport("ntdll.dll", EntryPoint = "NtSetSystemInformation")] private static extern uint NtCombine(int infoClass, ref CombineInformation buffer, uint length);
    [DllImport("ntdll.dll")] private static extern uint RtlNtStatusToDosError(uint status);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentThread();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out TokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenThreadToken(IntPtr thread, uint access, bool openAsSelf, out TokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool DuplicateTokenEx(TokenHandle token, uint access, IntPtr attributes, int impersonation, int type, out TokenHandle duplicate);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool SetThreadToken(IntPtr thread, IntPtr token);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool LookupPrivilegeValue(string? system, string name, out Luid luid);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool AdjustTokenPrivileges(TokenHandle token, bool disableAll, ref TokenPrivileges state, uint length, IntPtr previous, IntPtr returned);
}
