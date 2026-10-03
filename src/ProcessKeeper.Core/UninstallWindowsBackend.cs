using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace ProcessKeeper.Core;

public sealed class UninstallWindowsBackend : IUninstallBackend
{
    private const string Root = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private const int MaximumEntries = 10000;
    private static readonly string[] Names = { "DisplayName", "DisplayVersion", "Publisher", "InstallLocation", "DisplayIcon", "UninstallString", "QuietUninstallString", "WindowsInstaller", "SystemComponent", "NoRemove", "ReleaseType", "ParentKeyName" };
    public UninstallSnapshot Scan(CancellationToken token)
    {
        var entries = new List<UninstallEntry>(); var warnings = new List<string>(); bool truncated = false;
        var applicationIdentity = new UninstallApplicationIdentityResolver();
        foreach (var hive in new[] { "HKLM", "HKCU" })
        foreach (int view in Environment.Is64BitOperatingSystem ? new[] { 64, 32 } : new[] { 32 })
        {
            token.ThrowIfCancellationRequested();
            try
            {
                using var baseKey = OpenBase(hive, view);
                using var root = baseKey.OpenSubKey(Root, false); if (root is null) continue;
                for (uint index = 0; index < MaximumEntries; index++)
                {
                    token.ThrowIfCancellationRequested();
                    if (entries.Count >= MaximumEntries) { truncated = true; break; }
                    var buffer = new StringBuilder(256); uint length = 256;
                    var status = RegEnumKeyEx(root.Handle, index, buffer, ref length, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                    if (status == 259) break;
                    if (status != 0) { if (warnings.Count < 20) warnings.Add(L.T("部分卸载注册项无法读取。")); continue; }
                    try
                    {
                        var entry = Read(new(hive, view, buffer.ToString()), token);
                        if (entry is not null)
                        {
                            var identity = applicationIdentity.Resolve(entry, token);
                            entry = entry with { ApplicationIdentity = identity, ApplicationPaths = identity.IsResolved ? Array.AsReadOnly(new[] { identity.ExecutablePath }) : Array.Empty<string>() };
                            entries.Add(entry);
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch { if (warnings.Count < 20) warnings.Add(hive + " | " + view + " | " + L.T("部分卸载注册项无法读取。")); }
                    if (index == MaximumEntries - 1) truncated = true;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { warnings.Add(hive + " | " + view + " | " + L.T("部分卸载注册项无法读取。")); }
        }
        if (truncated) warnings.Add(L.T("卸载项目已达到扫描上限，请缩小筛选范围或使用 Windows 设置。"));
        return new() { Entries = entries.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToArray(), Warnings = warnings.Distinct().Take(24).ToArray(), Truncated = truncated };
    }
    public UninstallEntry? Read(UninstallLocator locator, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); ValidateLocator(locator);
        using var baseKey = OpenBase(locator.Hive, locator.View);
        using var key = baseKey.OpenSubKey(Root + "\\" + locator.Key, false); if (key is null) return null;
        var values = new Dictionary<string, object?>();
        foreach (var name in Names)
        {
            token.ThrowIfCancellationRequested();
            uint size = 0; var status = RegQueryValueEx(key.Handle, name, IntPtr.Zero, out uint kind, IntPtr.Zero, ref size);
            if (status == 2) { values[name] = null; continue; }
            if (status != 0 || size > 65536 || kind is not (1 or 2 or 4)) throw new InvalidDataException(L.T("卸载注册值格式不受支持。"));
            var raw = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            values[name] = new object?[] { kind, raw };
        }
        return FromValues(locator, values, Environment.GetFolderPath(Environment.SpecialFolder.System), UninstallExecutableGuard.ReadIdentity);
    }
    internal static UninstallEntry FromValues(UninstallLocator locator, IReadOnlyDictionary<string, object?> values, string systemDirectory, Func<string, string> readIdentity)
    {
        ValidateLocator(locator);
        string Text(string name) => values.TryGetValue(name, out var value) && value is object?[] parts ? parts[1] as string ?? "" : "";
        bool Flag(string name) => values.TryGetValue(name, out var value) && value is object?[] parts && parts[1] is int number && number != 0;
        var nameValue = Text("DisplayName");
        bool system = Text("ReleaseType").Length > 0 || Text("ParentKeyName").Length > 0;
        var command = UninstallPolicy.Parse(Text("UninstallString"), Flag("WindowsInstaller"), locator.Key, systemDirectory);
        var quiet = Text("QuietUninstallString").Length > 0 && !Flag("WindowsInstaller")
            ? UninstallPolicy.Parse(Text("QuietUninstallString"), false, locator.Key, systemDirectory) : null;
        var identity = command is null ? "" : readIdentity(command.Executable);
        var quietIdentity = quiet is null ? "" : readIdentity(quiet.Executable);
        var publisher = Text("Publisher");
        bool can = !system && nameValue.Length > 0 && command is not null && identity.Length > 0;
        string icon = Text("DisplayIcon").Trim();
        if (icon.StartsWith("\"", StringComparison.Ordinal)) { int end = icon.IndexOf('"', 1); icon = end > 1 ? icon.Substring(1, end - 1) : ""; }
        else { int comma = icon.LastIndexOf(','); if (comma >= 0) icon = icon.Substring(0, comma); }
        if (!UninstallPolicy.IsLocalExecutable(icon)) icon = command?.Executable ?? "";
        var recommendation = UninstallRecommendations.Match(nameValue.Length > 0 ? nameValue : locator.Key);
        return new()
        {
            Id = AutorunIdentity.Hash(locator.Hive + "|" + locator.View + "|" + locator.Key.ToUpperInvariant()), Locator = locator,
            Name = nameValue.Length > 0 ? nameValue : locator.Key, HasDisplayName = nameValue.Length > 0, Publisher = publisher, Version = Text("DisplayVersion"),
            InstallLocation = Text("InstallLocation"), IconPath = icon, IsHidden = Flag("SystemComponent") || Flag("NoRemove") || nameValue.Length == 0, RegistrationNoRemove = Flag("NoRemove"),
            IsSystem = system, IsRecommended = recommendation is not null, Recommendation = recommendation, Command = command, QuietCommand = quiet,
            CanUninstall = can, CanQuietUninstall = can && quiet is not null && quietIdentity.Length > 0,
            ReadOnlyReason = system ? L.T("系统更新或子组件注册项仅供查看。") : !can ? L.T("没有可验证的注册卸载命令。") : "",
            Fingerprint = AutorunIdentity.Hash(JsonSerializer.Serialize(values)), ExecutableIdentity = identity, QuietExecutableIdentity = quietIdentity
        };
    }
    public async Task<UninstallExecution> ExecuteAsync(UninstallEntry expected, UninstallMode mode, CancellationToken token)
    {
        if (mode != UninstallMode.Normal && mode != UninstallMode.RegisteredQuiet) throw new ArgumentOutOfRangeException(nameof(mode));
        token.ThrowIfCancellationRequested();
        using (var identity = WindowsIdentity.GetCurrent())
            if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) throw new UnauthorizedAccessException(L.T("卸载操作需要管理员权限。"));
        var current = Read(expected.Locator, token);
        if (!UninstallPolicy.Same(expected, current) || current!.IsSystem || !current.CanUninstall || mode == UninstallMode.RegisteredQuiet && !current.CanQuietUninstall)
            throw new InvalidOperationException(L.T("卸载注册信息或程序文件已变化，请重新扫描。"));
        var command = mode == UninstallMode.Normal ? current.Command! : current.QuietCommand!;
        var expectedFile = mode == UninstallMode.Normal ? current.ExecutableIdentity : current.QuietExecutableIdentity;
        // Hold the executable and every ancestor directory against replacement until CreateProcess has opened it.
        using var guard = new UninstallExecutableGuard(command.Executable, expectedFile);
        if (expected.ReviewedMode != mode || expected.ReviewedExecutableSha256.Length != 64 || guard.Hash(token) != expected.ReviewedExecutableSha256)
            throw new InvalidOperationException(L.T("卸载注册信息或程序文件已变化，请重新扫描。"));
        if (command.IsMsi) guard.DescribeSigner(true, token);
        if (command.IsMsi && MsiQueryProductState(command.ProductCode) != 5)
            throw new InvalidOperationException(L.T("Windows Installer 无法确认此产品已安装，未执行卸载。"));
        if (command.IsMsi && (!string.Equals(MsiProperty(command.ProductCode, "ProductName"), current.Name, StringComparison.OrdinalIgnoreCase) ||
            current.Publisher.Length > 0 && !string.Equals(MsiProperty(command.ProductCode, "Publisher"), current.Publisher, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException(L.T("Windows Installer 产品身份与卸载注册项不一致，未执行。"));
        token.ThrowIfCancellationRequested();
        if (!UninstallPolicy.Same(expected, Read(expected.Locator, token))) throw new InvalidOperationException(L.T("卸载注册信息或程序文件已变化，请重新扫描。"));
        var start = new ProcessStartInfo(command.Executable, command.Arguments)
        { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(command.Executable)!, CreateNoWindow = false };
        using var process = Process.Start(start);
        guard.Dispose(); // Release promptly so the vendor can remove its own executable and directory.
        if (process is null) return new(false, false, null);
        return await ObserveAsync(process, token).ConfigureAwait(false);
    }
    // A bootstrapper may exit before its child removes the registration; rescan remains authoritative.
    internal static async Task<UninstallExecution> ObserveAsync(Process process, CancellationToken token)
    {
        var deadline = DateTime.UtcNow.AddMinutes(15);
        try
        {
            while (!process.HasExited)
            {
                if (token.IsCancellationRequested || DateTime.UtcNow >= deadline) return new(true, false, null, true);
                await Task.Delay(250).ConfigureAwait(false);
            }
            return new(true, true, process.ExitCode);
        }
        catch { return new(true, false, null, true); }
    }
    public UninstallReview Review(UninstallEntry expected, UninstallMode mode, CancellationToken token)
    {
        if (mode != UninstallMode.Normal && mode != UninstallMode.RegisteredQuiet) throw new ArgumentOutOfRangeException(nameof(mode));
        var current = Read(expected.Locator, token);
        if (!UninstallPolicy.Same(expected, current) || current!.IsSystem || !current.CanUninstall || mode == UninstallMode.RegisteredQuiet && !current.CanQuietUninstall)
            throw new InvalidOperationException(L.T("卸载注册信息或程序文件已变化，请重新扫描。"));
        var command = mode == UninstallMode.Normal ? current.Command! : current.QuietCommand!;
        using var guard = new UninstallExecutableGuard(command.Executable, mode == UninstallMode.Normal ? current.ExecutableIdentity : current.QuietExecutableIdentity);
        var hash = guard.Hash(token); var signature = guard.DescribeSigner(command.IsMsi, token);
        if (current.RegistrationNoRemove) signature += "\n" + L.T("此软件注册了 NoRemove，通常隐藏移除按钮。本次仅使用已有卸载器，不修改此注册标志。");
        return new(current with { ReviewedExecutableSha256 = hash, ReviewedMode = mode,
            ApplicationIdentity = expected.ApplicationIdentity, ApplicationPaths = expected.ApplicationPaths }, signature);
    }
    private static void ValidateLocator(UninstallLocator locator)
    {
        if (locator is null || locator.Hive is not ("HKLM" or "HKCU") || locator.View is not (32 or 64) || locator.Key.Length is < 1 or > 255 || locator.Key.IndexOfAny(new[] { '\\', '/', '\0' }) >= 0)
            throw new InvalidDataException(L.T("卸载注册位置无效。"));
    }
    private static RegistryKey OpenBase(string hive, int view) => RegistryKey.OpenBaseKey(hive == "HKLM" ? RegistryHive.LocalMachine : RegistryHive.CurrentUser, view == 64 ? RegistryView.Registry64 : RegistryView.Registry32);
    private static string MsiProperty(string code, string property)
    { var buffer = new StringBuilder(4096); uint size = 4096; return MsiGetProductInfo(code, property, buffer, ref size) == 0 ? buffer.ToString() : ""; }
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32), DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegEnumKeyEx(Microsoft.Win32.SafeHandles.SafeRegistryHandle key, uint index, StringBuilder name, ref uint length, IntPtr reserved, IntPtr className, IntPtr classLength, IntPtr written);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32), DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegQueryValueEx(Microsoft.Win32.SafeHandles.SafeRegistryHandle key, string name, IntPtr reserved, out uint kind, IntPtr data, ref uint size);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32), DllImport("msi.dll", EntryPoint = "MsiQueryProductStateW", CharSet = CharSet.Unicode)]
    private static extern int MsiQueryProductState(string product);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32), DllImport("msi.dll", EntryPoint = "MsiGetProductInfoW", CharSet = CharSet.Unicode)]
    private static extern uint MsiGetProductInfo(string product, string property, StringBuilder value, ref uint size);
}
