using System.Diagnostics;
using System.Globalization;
using System.Security.Principal;
using System.Text;

namespace ProcessKeeper.Core;

/// <summary>Issued by the native wrapper for one exact managed child, never an arbitrary environment path.</summary>
public sealed class LauncherContext
{
    public string OriginalPath { get; }
    public string OriginalSha256 { get; }
    public string OriginalVersion { get; }
    public UpdatePackageTarget PackageTarget { get; }
    internal string Id { get; }
    internal string DirectoryPath { get; }
    internal string HelperPath { get; }
    internal string HelperSha256 { get; }
    internal LauncherContext(string[] fields, string directory)
    {
        Id = fields[1]; DirectoryPath = directory;
        OriginalPath = Decode(fields[2]); OriginalSha256 = fields[3]; OriginalVersion = fields[4];
        HelperPath = Decode(fields[7]); HelperSha256 = fields[8];
        PackageTarget = fields.Length == 12 ? UpdatePackageTarget.Universal : fields[12] switch
        {
            "Universal" => UpdatePackageTarget.Universal, "Windows7Compat" => UpdatePackageTarget.Windows7Compat,
            "Windows10x64" => UpdatePackageTarget.Windows10x64, "Windows10arm64" => UpdatePackageTarget.Windows10arm64,
            _ => throw new InvalidDataException("Unknown trusted package flavor.")
        };
    }
    internal static string Decode(string text)
    {
        if (text.Length > 131068 || text.Length % 4 != 0) throw new InvalidDataException("Invalid launcher context text.");
        var bytes = new byte[text.Length / 2];
        for (int i = 0; i < bytes.Length; i++) bytes[i] = byte.Parse(text.Substring(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var value = new UnicodeEncoding(false, false, true).GetString(bytes);
        if (value.IndexOf('\0') >= 0) throw new InvalidDataException("Invalid launcher context text.");
        return value;
    }
}

public static class LauncherContextReader
{
    public static bool TryGetCurrent(out LauncherContext? context, out string error)
    {
        context = null;
        try
        {
            var id = Environment.GetEnvironmentVariable("PROCESSKEEPER_LAUNCH_CONTEXT") ?? "";
            if (!UpdateTrustedFiles.ValidId(id)) throw new InvalidDataException(L.T("请从原始单文件 EXE 启动 Process Keeper 后使用此功能。"));
            var directory = Path.Combine(UpdateTrustedFiles.CacheRoot, "sessions", id);
            using var file = UpdateTrustedFiles.OpenRead(Path.Combine(directory, "context.txt"));
            if (file.Length > 300000) throw new InvalidDataException("Invalid launcher context size.");
            string[] fields;
            using (var reader = new StreamReader(file, new UTF8Encoding(false, true), false, 4096, true))
                fields = reader.ReadToEnd().Replace("\r", "").TrimEnd('\n').Split('\n');
            using var process = Process.GetCurrentProcess();
            using var identity = WindowsIdentity.GetCurrent();
            if ((fields.Length != 12 && fields.Length != 13) || fields[0] != "PKLC1" || fields[1] != id ||
                fields[9] != process.Id.ToString(CultureInfo.InvariantCulture) ||
                fields[10] != process.StartTime.ToUniversalTime().ToFileTimeUtc().ToString(CultureInfo.InvariantCulture) ||
                LauncherContext.Decode(fields[11]) != identity.User?.Value)
                throw new InvalidDataException(L.T("可信启动信息不属于当前运行的应用。"));
            var ownPath = process.MainModule?.FileName ?? "";
            if (!UpdateTrustedFiles.SamePath(ownPath, LauncherContext.Decode(fields[5])))
                throw new InvalidDataException(L.T("可信启动信息不属于当前运行的应用。"));
            using var image = UpdateTrustedFiles.OpenRead(ownPath);
            UpdateTrustedFiles.RequireHash(image, fields[6]);
            var result = new LauncherContext(fields, directory);
            if (!UpdateTrustedFiles.ValidHash(result.OriginalSha256) || !UpdateVersion.TryParse(result.OriginalVersion, out _) ||
                !UpdateTrustedFiles.SamePath(result.HelperPath, Path.Combine(Path.GetDirectoryName(ownPath)!, "ProcessKeeper.Updater.exe")))
                throw new InvalidDataException("Invalid launcher update identity.");
            using var helper = UpdateTrustedFiles.OpenRead(result.HelperPath);
            UpdateTrustedFiles.RequireHash(helper, result.HelperSha256);
            context = result; error = ""; return true;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }
    internal static void RequireCurrent(LauncherContext context)
    {
        if (!TryGetCurrent(out var live, out var error) || live!.Id != context.Id || live.OriginalSha256 != context.OriginalSha256 || live.PackageTarget != context.PackageTarget ||
            !UpdateTrustedFiles.SamePath(live.OriginalPath, context.OriginalPath)) throw new IOException(error.Length > 0 ? error : "Launcher context changed.");
    }
}
