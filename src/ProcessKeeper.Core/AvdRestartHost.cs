using System.Diagnostics;
using System.Security.Cryptography;

namespace ProcessKeeper.Core;

public sealed class AvdRestartHost : IAvdRestartHost
{
    private readonly ProcessCollector _collector = new();
    private static readonly string[] EnvironmentKeys =
    ["ANDROID_AVD_HOME", "ANDROID_USER_HOME", "ANDROID_EMULATOR_HOME", "ANDROID_SDK_HOME", "ANDROID_HOME", "ANDROID_SDK_ROOT", "HOME", "USERPROFILE", "TEMP", "TMP"];

    public ProcessSnapshot Capture() => _collector.Capture();
    public IReadOnlyList<string> ReadArguments(ProcessRecord process) => AvdNative.ReadArguments(process);
    public IReadOnlyDictionary<string, string> ReadEnvironment(ProcessRecord process) => AvdNative.ReadAndroidEnvironment(process);
    public void ValidateIdentity(ProcessRecord process) => AvdNative.ValidateIdentity(process);
    public IReadOnlyList<AvdPortListener> ReadListeners() => AvdNative.ReadLoopbackListeners();
    public Task<string> GetAvdNameAsync(ProcessRecord process, int port, string userProfile, CancellationToken cancellationToken) =>
        AvdNative.GetAvdNameAsync(process, port, userProfile, cancellationToken);
    public Task ShutdownAsync(ProcessRecord process, int port, string avdName, string userProfile, CancellationToken cancellationToken) =>
        AvdNative.ShutdownAsync(process, port, avdName, userProfile, cancellationToken);
    public bool FileExists(string path) => File.Exists(path);
    public string FileHash(string path)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(file));
    }

    public IAvdStartedProcess Launch(AvdRestartPlan plan)
    {
        var info = new ProcessStartInfo(plan.Launcher.Path)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(plan.Launcher.Path)!
        };
        foreach (var argument in plan.LaunchArguments) info.ArgumentList.Add(argument);
        foreach (var key in EnvironmentKeys) info.Environment.Remove(key);
        foreach (var (key, value) in plan.Environment)
            if (EnvironmentKeys.Contains(key, StringComparer.OrdinalIgnoreCase)) info.Environment[key] = value;
        var process = Process.Start(info) ?? throw new InvalidOperationException(L.T("Windows 没有创建模拟器启动进程。"));
        try
        {
            // Keep the handle open even when the short-lived launcher exits.
            _ = process.Handle;
            var identity = new ProcessRecord
            {
                Id = process.Id, StartTimeUtcTicks = process.StartTime.ToUniversalTime().Ticks,
                Path = plan.Launcher.Path, Name = "emulator.exe", SessionId = plan.Launcher.SessionId,
                OwnerSid = plan.Launcher.OwnerSid
            };
            return new StartedProcess(process, identity);
        }
        catch
        {
            process.Dispose();
            throw new InvalidOperationException(L.T("启动请求已发送，但无法核实新进程身份；未将它视为窗口启动成功，请刷新列表。"));
        }
    }

    private sealed class StartedProcess(Process process, ProcessRecord identity) : IAvdStartedProcess
    {
        public ProcessRecord Identity { get; } = identity;
        public bool HasExited => process.HasExited;
        public long? ExitTimeUtcTicks => process.HasExited ? process.ExitTime.ToUniversalTime().Ticks : null;
        public void Dispose() => process.Dispose();
    }
}
