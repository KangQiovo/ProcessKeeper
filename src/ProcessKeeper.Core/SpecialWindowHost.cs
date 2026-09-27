using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace ProcessKeeper.Core;

public sealed class SpecialWindowHost : ISpecialWindowHost
{
    private readonly ProcessCollector _collector = new();
    private static readonly SemaphoreSlim VirtualMachineProvider = new(1, 1);
    public ProcessSnapshot Capture() => _collector.Capture();
    public void ValidateSource(ProcessRecord source, bool hyperV)
    {
        if (!hyperV) { AvdNative.ValidateIdentity(source); return; }
        string expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "vmwp.exe");
        if (!SpecialWindowPolicy.SamePath(source.Path, expected) || source.SessionId != 0 || source.OwnerSid.Length == 0)
            throw new InvalidOperationException(L.T("目标不是可核实的本机 Hyper-V 核心。"));
        using var process = Process.GetProcessById(source.Id);
        _ = process.Handle;
        if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != source.StartTimeUtcTicks ||
            !SpecialWindowPolicy.SamePath(process.MainModule?.FileName ?? "", expected))
            throw new InvalidOperationException(L.T("Hyper-V 核心已退出或身份变化。"));
        var current = Capture().Processes.SingleOrDefault(p => p.Id == source.Id);
        if (current is null || current.StartTimeUtcTicks != source.StartTimeUtcTicks || current.OwnerSid != source.OwnerSid || current.SessionId != 0)
            throw new InvalidOperationException(L.T("无法复核 Hyper-V 核心账户及创建时间。"));
    }
    public IReadOnlyList<string> ReadArguments(ProcessRecord source) => AvdNative.ReadArguments(source);
    public bool FileExists(string path) => File.Exists(path);
    public bool IsGuiExecutable(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            using var reader = new BinaryReader(file);
            if (file.Length < 256 || reader.ReadUInt16() != 0x5a4d) return false;
            file.Position = 0x3c; int offset = reader.ReadInt32();
            if (offset < 64 || offset > file.Length - 100) return false;
            file.Position = offset;
            if (reader.ReadUInt32() != 0x00004550) return false;
            file.Position = offset + 24;
            ushort magic = reader.ReadUInt16();
            if (magic is not (0x10b or 0x20b)) return false;
            file.Position = offset + 24 + 68;
            return reader.ReadUInt16() == 2; // IMAGE_SUBSYSTEM_WINDOWS_GUI
        }
        catch { return false; }
    }
    public string FileHash(string path)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(file));
    }
    public VirtualMachineTarget? ReadVirtualMachine(ProcessRecord source, SpecialWindowKind kind, string targetId)
    {
        return QueryVirtualMachine(() => kind == SpecialWindowKind.HyperV ? ReadHyperV(source.Id) :
            kind == SpecialWindowKind.VirtualBox ? ReadVBox(source.Id, targetId) : ReadVmware(source.Id, targetId));
    }
    internal static VirtualMachineTarget? QueryVirtualMachine(Func<VirtualMachineTarget?> query, TimeSpan? timeout = null)
    {
        // A timeout stops waiting, not an in-progress COM call. Keep the single
        // slot until that call actually returns so retries cannot accumulate workers.
        if (!VirtualMachineProvider.Wait(0))
            throw new TimeoutException(L.T("虚拟机提供程序仍在响应前一次读取，请稍后重试。"));
        var task = Task.Run(() =>
        {
            try { return query(); }
            finally { VirtualMachineProvider.Release(); }
        });
        // The provider may fault after its caller times out; observe that late fault.
        _ = task.ContinueWith(completed => { _ = completed.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return task.WaitAsync(timeout ?? TimeSpan.FromSeconds(8)).GetAwaiter().GetResult();
    }
    private static VirtualMachineTarget? ReadHyperV(int processId)
    {
        object? locator = null, services = null, results = null;
        try
        {
            locator = Activator.CreateInstance(Type.GetTypeFromProgID("WbemScripting.SWbemLocator", true)!);
            services = ((dynamic)locator!).ConnectServer(".", @"root\virtualization\v2");
            results = ((dynamic)services).ExecQuery($"SELECT Name, ElementName, ProcessID, EnabledState FROM Msvm_ComputerSystem WHERE ProcessID = {processId}", "WQL", 48);
            var found = new List<VirtualMachineTarget>();
            foreach (object item in (System.Collections.IEnumerable)results)
            {
                try
                {
                    dynamic vm = item;
                    if ((int)vm.EnabledState == 2 && (int)vm.ProcessID == processId)
                        found.Add(new((string)vm.Name, (string)vm.ElementName, processId));
                }
                finally { Release(item); }
            }
            return found.Count == 1 ? found[0] : null;
        }
        finally { Release(results); Release(services); Release(locator); }
    }
    private static VirtualMachineTarget? ReadVBox(int processId, string id)
    {
        object? vbox = null, machine = null;
        try
        {
            if (!Guid.TryParse(id, out _)) return null;
            vbox = Activator.CreateInstance(Type.GetTypeFromProgID("VirtualBox.VirtualBox", true)!);
            machine = ((dynamic)vbox!).FindMachine(id);
            dynamic vm = machine;
            // MachineState_Running=5. Paused, saved, starting or stopping are not attach plans.
            if ((int)vm.State != 5 || Convert.ToUInt64(vm.SessionPID) != (ulong)processId) return null;
            return new((string)vm.Id, (string)vm.Name, processId);
        }
        finally { Release(machine); Release(vbox); }
    }
    private static VirtualMachineTarget? ReadVmware(int processId, string path)
    {
        if (!Path.IsPathFullyQualified(path)) return null;
        using var stream = File.OpenRead(path);
        if (stream.Length > 1024 * 1024) return null;
        using var reader = new StreamReader(stream);
        var matches = Regex.Matches(reader.ReadToEnd(), "^\\s*displayName\\s*=\\s*\"([^\"\\r\\n]+)\"\\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        if (matches.Count > 1) return null;
        string name = matches.Count == 1 ? matches[0].Groups[1].Value : Path.GetFileNameWithoutExtension(path);
        // Encoded names require a complete VMX parser; do not guess a title.
        if (name.Contains('|') || name.Length == 0) return null;
        return new(path, name, processId);
    }
    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
    }
    public void Launch(SpecialWindowPlan plan)
    {
        var info = new ProcessStartInfo(plan.ExecutablePath)
        {
            UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(plan.ExecutablePath)!
        };
        foreach (string argument in plan.Arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new InvalidOperationException(L.T("Windows 未接受界面启动请求。"));
    }
}
