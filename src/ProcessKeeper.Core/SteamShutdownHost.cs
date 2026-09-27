using System.Diagnostics;
using System.Security.Principal;

namespace ProcessKeeper.Core;

public sealed class SteamShutdownHost : ISteamShutdownHost
{
    private readonly ProcessCollector _collector = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    public string CurrentUserSid { get; } = WindowsIdentity.GetCurrent().User?.Value ?? "";
    public int CurrentSessionId { get; } = Process.GetCurrentProcess().SessionId;
    public TimeSpan Clock => _clock.Elapsed;
    public ProcessSnapshot Capture() => _collector.Capture();
    public Task Delay(TimeSpan interval, CancellationToken token) => Task.Delay(interval, token);
    public IDisposable HoldVerifiedProcess(ProcessRecord process) => ProcessCloser.OpenVerifiedForShutdown(process);
    public void ValidateProcess(ProcessRecord process) { using var verified = HoldVerifiedProcess(process); }
    public bool HasExited(ProcessRecord process) => ProcessCloser.HasFrozenProcessExited(process);

    public IDisposable HoldExecutable(ProcessRecord launcher)
    {
        if (!launcher.Name.Equals("steam.exe", StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(launcher.Path).Equals("steam.exe", StringComparison.OrdinalIgnoreCase) ||
            (File.GetAttributes(launcher.Path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException(L.T("Steam 主程序路径无法核实，未发送退出请求。"));
        var file = new FileStream(launcher.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            using var reader = new BinaryReader(file, System.Text.Encoding.UTF8, leaveOpen: true);
            if (file.Length < 256 || reader.ReadUInt16() != 0x5A4D) throw new InvalidDataException();
            file.Position = 0x3c; int pe = reader.ReadInt32();
            if (pe < 64 || pe > file.Length - 100) throw new InvalidDataException();
            file.Position = pe;
            if (reader.ReadUInt32() != 0x4550) throw new InvalidDataException();
            file.Position = pe + 24;
            if (reader.ReadUInt16() is not (0x10b or 0x20b)) throw new InvalidDataException();
            file.Position = pe + 24 + 68;
            if (reader.ReadUInt16() != 2) throw new InvalidDataException();
            SteamExecutableTrust.Validate(launcher.Path);
            ValidateProcess(launcher);
            return file;
        }
        catch { file.Dispose(); throw; }
    }

    public ProcessRecord? RequestShutdown(ProcessRecord launcher)
    {
        ValidateProcess(launcher);
        var start = new ProcessStartInfo(launcher.Path)
        {
            UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(launcher.Path)!
        };
        start.ArgumentList.Add("-shutdown");
        using var request = Process.Start(start) ?? throw new IOException(L.T("Windows 未能发送 Steam 正常退出请求。"));
        try
        {
            return new ProcessRecord { Id = request.Id, StartTimeUtcTicks = request.StartTime.ToUniversalTime().Ticks,
                Path = launcher.Path, Name = launcher.Name, OwnerSid = CurrentUserSid, SessionId = CurrentSessionId };
        }
        catch (InvalidOperationException) when (request.HasExited) { return null; }
    }
}
