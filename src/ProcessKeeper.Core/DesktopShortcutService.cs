using System.Diagnostics;
using System.Text;

namespace ProcessKeeper.Core;

public sealed record DesktopShortcutResult(bool Success, bool AlreadyExists, string Message, string ShortcutPath = "");

/// <summary>Creates a link to the verified original wrapper, never to a cache executable.</summary>
public static class DesktopShortcutService
{
    public static DesktopShortcutResult Ensure(LauncherContext context)
    {
        try
        {
            LauncherContextReader.RequireCurrent(context);
            using var helper = UpdateTrustedFiles.OpenRead(context.HelperPath);
            UpdateTrustedFiles.RequireHash(helper, context.HelperSha256);
            var id = Guid.NewGuid().ToString("N");
            var start = new ProcessStartInfo(context.HelperPath)
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetDirectoryName(context.HelperPath)!, Arguments = "--shortcut " + context.Id + " " + id
            };
            start.EnvironmentVariables["PROCESSKEEPER_HELPER_LANGUAGE"] = L.Language;
            using var process = Process.Start(start) ?? throw new IOException(L.T("无法启动已验证的快捷方式助手。"));
            if (!process.WaitForExit(15000)) throw new IOException(L.T("快捷方式操作尚未完成，请稍后重试。"));
            using var stream = UpdateTrustedFiles.OpenRead(Path.Combine(context.DirectoryPath, "shortcut-" + id + ".txt"));
            if (stream.Length > 140000) throw new InvalidDataException("Invalid shortcut result.");
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var fields = reader.ReadToEnd().TrimEnd('\n').Split('\n');
            if (fields.Length != 3 || fields[0] != "PKLINK1") throw new InvalidDataException("Invalid shortcut response.");
            var path = LauncherContext.Decode(fields[2]);
            return fields[1] switch
            {
                "created" => new(true, false, L.T("已创建指向原始 EXE 的桌面快捷方式。"), path),
                "exists" => new(true, true, L.T("桌面快捷方式已存在并指向原始 EXE。"), path),
                "conflict" => new(false, false, L.T("桌面已有无关的同名快捷方式，未覆盖。"), path),
                _ => new(false, false, L.T("无法创建桌面快捷方式，原有文件未被覆盖。"), path)
            };
        }
        catch (Exception ex) { return new(false, false, ex.Message); }
    }
}
