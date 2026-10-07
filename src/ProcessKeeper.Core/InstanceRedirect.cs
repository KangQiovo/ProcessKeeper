using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ProcessKeeper.Core;

public sealed record InstancePeerIdentity(string ContextId, int ProcessId, long StartedUtcTicks,
    int SessionId, string ImagePath, string Version, bool IsModern)
{
    public long RequestedUtcTicks { get; init; }
    internal string ContextPath { get; init; } = "";
    internal string HelperPath { get; init; } = "";
    internal long ContextWritten { get; init; }
    internal long ImageWritten { get; init; }
    internal long HelperWritten { get; init; }
    internal long ContextLength { get; init; }
    internal long ImageLength { get; init; }
    internal long HelperLength { get; init; }
    internal long RequestWritten { get; init; }
    internal long RequestLength { get; init; }
}

/// <summary>Opaque protected launcher receipts bind a retained instance to its real live process.</summary>
public static class InstanceRedirect
{
    public static string CurrentOpaqueContextId => LauncherContextReader.TryGetCurrent(out var current, out _) ? current!.Id : "";
    public static long CurrentRequestUtcTicks
    {
        get { try { return LauncherContextReader.TryGetCurrent(out var current, out _) ? RequestTicks(current!.DirectoryPath, Process.GetCurrentProcess().StartTime.ToUniversalTime().ToFileTimeUtc()) : 0; } catch { return 0; } }
    }

    public static bool TryGetPeer(string contextId, out InstancePeerIdentity? peer)
    {
        peer = null;
        try
        {
            if (!UpdateTrustedFiles.ValidId(contextId)) return false;
            var fields = ReadLines(Path.Combine(UpdateTrustedFiles.CacheRoot, "sessions", contextId, "context.txt"));
            using var owner = WindowsIdentity.GetCurrent();
            if ((fields.Length < 12 || fields.Length > 14) || fields[0] != "PKLC1" || fields[1] != contextId ||
                LauncherContext.Decode(fields[11]) != owner.User?.Value || !UpdateVersion.TryParse(fields[4], out _) ||
                !UpdateTrustedFiles.ValidHash(fields[3]) || !UpdateTrustedFiles.ValidHash(fields[6]) || !UpdateTrustedFiles.ValidHash(fields[8])) return false;
            var context = new LauncherContext(fields, Path.Combine(UpdateTrustedFiles.CacheRoot, "sessions", contextId));
            var image = Path.GetFullPath(LauncherContext.Decode(fields[5]));
            var helper = Path.GetFullPath(LauncherContext.Decode(fields[7]));
            if (!UpdateTrustedFiles.SamePath(helper, Path.Combine(Path.GetDirectoryName(image)!, "ProcessKeeper.Updater.exe"))) return false;
            var cache = UpdateTrustedFiles.CacheRoot.TrimEnd('\\');
            if (!image.StartsWith(cache + "\\", StringComparison.OrdinalIgnoreCase)) return false;
            var relative = image.Substring(cache.Length).Replace('\\', '/');
            if (relative.Length < 66 || relative[0] != '/' || !UpdateTrustedFiles.ValidHash(relative.Substring(1, 64))) return false;
            var route = relative.Substring(65);
            bool modern = route is "-modern/modern/ProcessKeeper.exe" or "-arm64/modern/arm64/ProcessKeeper.exe";
            if (!modern && route != "-legacy/legacy/ProcessKeeper.exe") return false;
            var machine = route.StartsWith("-arm64/", StringComparison.Ordinal) ? (ushort)0xaa64 : modern ? (ushort)0x8664 : (ushort)0x14c;
            if (!MatchesDeclaredRoute(context.PackageTarget, machine, modern)) return false;
            var host = UpdatePackagePolicy.Current() with { PackageFlavor = UpdatePackageTarget.Unsupported, CompatibilityUi = !modern };
            if (UpdatePackagePolicy.Target(host) == UpdatePackageTarget.Unsupported || modern && host.Machine != machine || !modern && host.Machine is not (0x14c or 0x8664)) return false;
            if (!int.TryParse(fields[9], NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 4 ||
                !long.TryParse(fields[10], NumberStyles.None, CultureInfo.InvariantCulture, out var started) || started <= 0) return false;
            using var process = Process.GetProcessById(id);
            using var self = Process.GetCurrentProcess();
            if (process.HasExited || process.SessionId != self.SessionId || process.StartTime.ToUniversalTime().ToFileTimeUtc() != started ||
                !SameOwner(id, owner.User)) return false;
            var actualImage = ProcessImage(id);
            if (!UpdateTrustedFiles.SamePath(image, actualImage)) return false;
            using (var executable = UpdateTrustedFiles.OpenRead(image))
            {
                UpdateTrustedFiles.RequireHash(executable, fields[6]);
                if (!MatchesExecutableMetadata(image, executable, machine, fields[4])) return false;
            }
            using (var updater = UpdateTrustedFiles.OpenRead(helper)) UpdateTrustedFiles.RequireHash(updater, fields[8]);
            if (process.HasExited || process.StartTime.ToUniversalTime().ToFileTimeUtc() != started) return false;
            var contextInfo = new FileInfo(Path.Combine(context.DirectoryPath, "context.txt")); var imageInfo = new FileInfo(image); var helperInfo = new FileInfo(helper);
            var requestInfo = new FileInfo(Path.Combine(context.DirectoryPath, "instance-request.txt"));
            peer = new(contextId, id, DateTime.FromFileTimeUtc(started).Ticks, process.SessionId, image, fields[4], modern)
            {
                RequestedUtcTicks = RequestTicks(context.DirectoryPath, started), ContextPath = contextInfo.FullName, HelperPath = helper,
                ContextWritten = contextInfo.LastWriteTimeUtc.Ticks, ContextLength = contextInfo.Length,
                ImageWritten = imageInfo.LastWriteTimeUtc.Ticks, ImageLength = imageInfo.Length,
                HelperWritten = helperInfo.LastWriteTimeUtc.Ticks, HelperLength = helperInfo.Length,
                RequestWritten = requestInfo.Exists ? requestInfo.LastWriteTimeUtc.Ticks : 0, RequestLength = requestInfo.Exists ? requestInfo.Length : 0
            };
            return true;
        }
        catch { return false; }
    }

    public static bool TrySignalPeer(string peerContextId)
    {
        try
        {
            if (!LauncherContextReader.TryGetCurrent(out var own, out _) || own!.Id == peerContextId || !TryGetPeer(peerContextId, out _)) return false;
            LauncherContextReader.RequireCurrent(own);
            using var file = UpdateTrustedFiles.Create(Path.Combine(own.DirectoryPath, "instance-redirect.txt"));
            var bytes = Encoding.ASCII.GetBytes("PKINSTANCE1\n" + own.Id + "\n" + peerContextId + "\n");
            file.Write(bytes, 0, bytes.Length); file.Flush(true);
            return true;
        }
        catch { return false; }
    }

    /// <summary>Cheap revalidation for a previously verified immutable peer; no package hashing.</summary>
    public static bool IsPeerAlive(InstancePeerIdentity peer)
    {
        try
        {
            using var process = Process.GetProcessById(peer.ProcessId);
            using var current = WindowsIdentity.GetCurrent();
            return !process.HasExited && process.SessionId == peer.SessionId &&
                process.StartTime.ToUniversalTime().Ticks == peer.StartedUtcTicks &&
                UpdateTrustedFiles.SamePath(ProcessImage(peer.ProcessId), peer.ImagePath) && SameOwner(peer.ProcessId, current.User) &&
                SameFileStamp(peer.ContextPath, peer.ContextWritten, peer.ContextLength) && SameFileStamp(peer.ImagePath, peer.ImageWritten, peer.ImageLength) &&
                SameFileStamp(peer.HelperPath, peer.HelperWritten, peer.HelperLength) &&
                SameFileStamp(Path.Combine(Path.GetDirectoryName(peer.ContextPath)!, "instance-request.txt"), peer.RequestWritten, peer.RequestLength);
        }
        catch { return false; }
    }
    private static bool SameFileStamp(string path, long written, long length)
    { var info = new FileInfo(path); return written == 0 ? !info.Exists : info.Exists && (info.Attributes & FileAttributes.ReparsePoint) == 0 && info.LastWriteTimeUtc.Ticks == written && info.Length == length; }
    private static long RequestTicks(string directory, long started)
    {
        var path = Path.Combine(directory, "instance-request.txt");
        try { _ = File.GetAttributes(path); }
        catch (FileNotFoundException) { return DateTime.FromFileTimeUtc(started).Ticks; }
        catch (DirectoryNotFoundException) { return DateTime.FromFileTimeUtc(started).Ticks; }
        try
        {
            var fields = ReadLines(path);
            if (fields.Length != 3 || fields[0] != "PKREQUEST1" || !uint.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var pid) || pid <= 4 ||
                !long.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var time) || time <= 0 || time > started) throw new InvalidDataException("Invalid instance request.");
            return DateTime.FromFileTimeUtc(time).Ticks;
        }
        catch { throw; }
    }
    internal static bool MatchesDeclaredRoute(UpdatePackageTarget target, ushort machine, bool modern) => target switch
    {
        UpdatePackageTarget.Universal => true, UpdatePackageTarget.Windows7Compat => !modern && machine == 0x14c,
        UpdatePackageTarget.Windows10x64 => machine == 0x8664 && modern || machine == 0x14c && !modern,
        UpdatePackageTarget.Windows10arm64 => modern && machine == 0xaa64, _ => false
    };
    internal static bool MatchesExecutableMetadata(string path, Stream image, ushort machine, string version)
    {
        try
        {
            image.Position = 0; using var reader = new BinaryReader(image, Encoding.UTF8, true);
            if (reader.ReadUInt16() != 0x5a4d || image.Length < 64) return false;
            image.Position = 60; var offset = reader.ReadInt32(); if (offset < 64 || offset > 1048576 || offset > image.Length - 24) return false;
            image.Position = offset; if (reader.ReadUInt32() != 0x4550 || reader.ReadUInt16() != machine) return false;
            image.Position = offset + 22; if ((reader.ReadUInt16() & 0x2000) != 0) return false;
            var info = FileVersionInfo.GetVersionInfo(path);
            return MatchesVersionLabels(info.ProductName ?? "", info.ProductVersion ?? "", version);
        }
        catch { return false; }
        finally { image.Position = 0; }
    }
    internal static bool MatchesVersionLabels(string product, string actualVersion, string expectedVersion) =>
        product is "ProcessKeeper" or "Process Keeper" && UpdateVersion.TryParse(actualVersion, out var actual) &&
        UpdateVersion.TryParse(expectedVersion, out var expected) && actual!.CompareTo(expected) == 0;

    private static string[] ReadLines(string path)
    {
        using var file = UpdateTrustedFiles.OpenRead(path);
        if (file.Length <= 0 || file.Length > 300000) throw new InvalidDataException("Invalid instance receipt size.");
        using var reader = new StreamReader(file, new UTF8Encoding(false, true));
        return reader.ReadToEnd().Replace("\r", "").TrimEnd('\n').Split('\n');
    }
    private static string ProcessImage(int id)
    {
        using var process = OpenProcess(0x1000, false, id);
        var path = new StringBuilder(32768); var length = path.Capacity;
        if (process.IsInvalid || !QueryFullProcessImageName(process, 0, path, ref length)) throw new IOException("Cannot verify instance image.");
        return path.ToString();
    }
    private static bool SameOwner(int id, SecurityIdentifier? expected)
    {
        using var process = OpenProcess(0x1000, false, id);
        if (process.IsInvalid || !OpenProcessToken(process, 8, out var token)) return false;
        using (token) using (var owner = new WindowsIdentity(token.DangerousGetHandle())) return owner.User == expected;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int id);
    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryFullProcessImageName(SafeProcessHandle process, int flags, StringBuilder path, ref int length);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
}
