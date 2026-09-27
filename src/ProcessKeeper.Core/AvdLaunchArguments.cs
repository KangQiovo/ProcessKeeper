using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace ProcessKeeper.Core;

public sealed record AvdLaunchArguments(string AvdName, IReadOnlyList<string> Arguments,
    IReadOnlyList<string> RemovedOptions, int? ConsolePort);

/// <summary>
/// Converts an already tokenized, verified emulator.exe launch into a standalone native-window
/// cold boot. This is intentionally an allowlist, not a shell parser or a general QEMU replayer.
/// </summary>
public static class AvdArgumentPolicy
{
    private static readonly HashSet<string> Flags = new(StringComparer.Ordinal)
    {
        "-no-snapshot-load", "-no-snapshot-save", "-no-snapshot", "-no-snapstorage",
        "-no-boot-anim", "-no-audio", "-no-skin", "-no-jni", "-no-accel",
        "-verbose", "-show-kernel", "-netfast", "-no-metrics"
    };
    private static readonly HashSet<string> RemovedFlags = new(StringComparer.Ordinal)
    {
        "-no-window", "-qt-hide-window", "-grpc-use-token", "-grpc-use-jwt"
    };
    private static readonly HashSet<string> PathOptions = new(StringComparer.Ordinal)
    {
        "-sysdir", "-datadir", "-data", "-cache", "-sdcard", "-system", "-vendor",
        "-kernel", "-ramdisk", "-snapstorage", "-skindir"
    };
    private static readonly HashSet<string> RemovedPaths = new(StringComparer.Ordinal)
    {
        "-studio-params", "-grpc-tls-key", "-grpc-tls-cer", "-grpc-tls-ca"
    };
    private static readonly HashSet<string> DangerousOptions = new(StringComparer.Ordinal)
    {
        "-wipe-data", "-read-only", "-writable-system", "-qemu", "-shell", "-shell-serial",
        "-script", "-script-file", "-loadvm", "-initdata", "-init-data", "-partition-size",
        "-append", "-prop", "-boot-property", "-netsim-args", "-fuchsia", "-snapshot-list"
    };
    private static readonly HashSet<string> SupportedFeatures = new(StringComparer.Ordinal)
    {
        "Vulkan", "GLDirectMem", "GLAsyncSwap", "HostComposition", "GuestAngle",
        "VulkanNativeSwapchain", "VulkanSnapshots", "VirtioGpuNativeSync", "WindowsHypervisorPlatform"
    };

    public static AvdLaunchArguments Prepare(IReadOnlyList<string> fullArguments, string expectedLauncherPath)
    {
        ArgumentNullException.ThrowIfNull(fullArguments);
        if (fullArguments.Count is < 2 or > 512) throw Invalid(L.T("模拟器启动参数数量不受支持。"));
        var input = fullArguments.ToArray();
        if (input.Any(value => value is null || value.Length > 32767 || value.Any(char.IsControl)) ||
            input.Sum(value => (long)value.Length + 1) > 32767)
            throw Invalid(L.T("模拟器启动参数包含空值、控制字符或超长内容。"));
        var expected = AbsolutePath(expectedLauncherPath, "emulator.exe");
        var actual = AbsolutePath(input[0], "argv[0]");
        if (!Path.GetFileName(expected).Equals("emulator.exe", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            throw Invalid(L.T("启动参数的程序路径与已核实的 emulator.exe 不一致。"));

        var output = new List<string>();
        var changes = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var features = new HashSet<string>(StringComparer.Ordinal);
        string? avd = null;
        int? consolePort = null;
        bool portSeen = false;
        for (var index = 1; index < input.Length; index++)
        {
            var option = input[index];
            if (option.StartsWith('@'))
            {
                SetAvd(option[1..]);
                changes.Add(L.T("将 @AVD 简写转换为 -avd 参数。"));
                continue;
            }
            if (!option.StartsWith('-') || option.Length < 2)
                throw Invalid(L.T("存在未归属的参数值；不会猜测或拼接执行。"));
            if (DangerousOptions.Contains(option)) throw Invalid(L.F($"不支持自动重放 {option}：它可能改变数据、磁盘写入模式或执行额外命令。"));
            if (option != "-feature" && !seen.Add(option)) throw Invalid(L.F($"参数 {SafeOption(option)} 重复，无法确定原启动意图。"));
            if (option == "-avd") { SetAvd(Value(ref index, option)); continue; }
            if (RemovedFlags.Contains(option))
            {
                changes.Add(L.F($"移除 {option}（取消无窗口或 IDE 嵌入参数）。"));
                continue;
            }
            if (option is "-idle-grpc-timeout" or "-grpc")
            {
                var value = Value(ref index, option);
                Integer(value, 0, option == "-grpc" ? 65535 : int.MaxValue, option);
                changes.Add(L.F($"移除 {option} 及其值（取消 IDE 通信与退出生命周期参数）。"));
                continue;
            }
            if (RemovedPaths.Contains(option))
            {
                AbsolutePath(Value(ref index, option), option);
                changes.Add(L.F($"移除 {option} 及其路径（取消 IDE 通信配置，不记录原值）。"));
                continue;
            }
            if (option == "-snapshot")
            {
                Value(ref index, option);
                changes.Add(L.T("移除 -snapshot 及其名称，改为冷启动。"));
                continue;
            }
            if (option is "-port" or "-ports")
            {
                if (portSeen) throw Invalid(L.T("-port 与 -ports 不能重复或同时出现。"));
                portSeen = true;
                var value = Value(ref index, option);
                var parts = value.Split(',');
                if ((option == "-port" && parts.Length != 1) || (option == "-ports" && parts.Length != 2))
                    throw Invalid(L.F($"{option} 的端口格式不受支持。"));
                var port = Integer(parts[0], 5554, 5682, option);
                if ((port & 1) != 0) throw Invalid(L.T("AVD 控制台端口必须为 5554 至 5682 内的偶数。"));
                if (parts.Length == 2)
                {
                    var adb = Integer(parts[1], 5555, 5683, option);
                    if ((adb & 1) == 0) throw Invalid(L.T("AVD 的 adb 端口必须为 5555 至 5683 内的奇数。"));
                }
                consolePort = port;
                output.Add(option); output.Add(value);
                continue;
            }
            if (Flags.Contains(option)) { output.Add(option); continue; }
            if (PathOptions.Contains(option))
            {
                var value = Value(ref index, option);
                var normalized = AbsolutePath(value, option);
                output.Add(option); output.Add(normalized);
                if (value != normalized) changes.Add(L.F($"规范化 {option} 的绝对路径（目标不变）。"));
                continue;
            }
            if (option == "-feature")
            {
                var value = Value(ref index, option, allowLeadingDash: true);
                foreach (var feature in value.Split(','))
                {
                    var name = feature.StartsWith('-') ? feature[1..] : feature;
                    if (!SupportedFeatures.Contains(name)) throw Invalid(L.T("-feature 含不受支持的功能名称，不会将其他选项误当作功能值。"));
                    if (!features.Add(name)) throw Invalid(L.T("-feature 重复或互相冲突。"));
                }
                output.Add(option); output.Add(value);
                continue;
            }
            if (!IsValueOption(option)) throw Invalid(L.F($"尚不支持自动重放参数 {SafeOption(option)}；请从 Android Studio 或模拟器自身入口启动。"));
            var original = Value(ref index, option);
            var validated = ValidateValue(option, original);
            output.Add(option); output.Add(validated);
            if (original != validated) changes.Add(L.F($"规范化 {option} 的绝对路径（目标不变）。"));
        }
        if (avd is null) throw Invalid(L.T("启动参数中没有唯一的 -avd 名称或 @名称，不能安全确定目标模拟器。"));
        if (seen.Contains("-no-accel") && seen.Contains("-accel")) throw Invalid(L.T("-no-accel 与 -accel 同时出现，无法确定硬件加速设置。"));
        if (!output.Contains("-no-snapshot-load", StringComparer.Ordinal))
        {
            output.Add("-no-snapshot-load");
            changes.Add(L.T("添加 -no-snapshot-load（冷启动，避免恢复无窗口快照）。"));
        }
        return new AvdLaunchArguments(avd, output.AsReadOnly(), changes.AsReadOnly(), consolePort);

        void SetAvd(string name)
        {
            if (avd is not null) throw Invalid(L.T("只能包含一个 -avd 名称或 @名称，重复目标不会自动处理。"));
            if (!Regex.IsMatch(name, @"\A[A-Za-z0-9_][A-Za-z0-9_.-]{0,254}\z", RegexOptions.CultureInvariant))
                throw Invalid(L.T("AVD 名称包含不支持的字符或路径内容。"));
            avd = name;
            output.Add("-avd"); output.Add(name);
        }
        string Value(ref int position, string name, bool allowLeadingDash = false)
        {
            if (position + 1 >= input.Length || string.IsNullOrWhiteSpace(input[position + 1]) ||
                (!allowLeadingDash && input[position + 1].StartsWith('-')) || input[position + 1].StartsWith('@'))
                throw Invalid(L.F($"参数 {name} 缺少有效值，不会吞掉后续选项。"));
            return input[++position];
        }
    }

    private static bool IsValueOption(string option) => option is "-gpu" or "-accel" or "-engine" or
        "-memory" or "-cores" or "-dpi-device" or "-scale" or "-netdelay" or "-netspeed" or
        "-camera-front" or "-camera-back" or "-skin" or "-timezone" or "-dns-server" or "-http-proxy";

    private static string ValidateValue(string option, string value)
    {
        bool valid;
        switch (option)
        {
            case "-gpu": valid = value is "auto" or "host" or "software" or "lavapipe" or "swiftshader" or "swangle" or "mesa" or "angle" or "angle_indirect" or "guest" or "swiftshader_indirect" or "swangle_indirect" or "off"; break;
            case "-accel": valid = value is "auto" or "on" or "off"; break;
            case "-engine": valid = value is "auto" or "classic" or "qemu2"; break;
            case "-memory": Integer(value, 128, 131072, option); return value;
            case "-cores": Integer(value, 1, 128, option); return value;
            case "-dpi-device": Integer(value, 72, 1000, option); return value;
            case "-scale": valid = value == "auto" || (decimal.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var scale) && scale is >= 0.1m and <= 3m); break;
            case "-netdelay": case "-netspeed":
                valid = value is "gsm" or "hscsd" or "gprs" or "edge" or "umts" or "hsdpa" or "lte" or "evdo" ||
                    (option == "-netdelay" ? value == "none" : value == "full") || NumericPair(value, option == "-netdelay"); break;
            case "-camera-front": case "-camera-back":
                foreach (var prefix in new[] { "imagefile:", "videofile:", "image360:" })
                    if (value.StartsWith(prefix, StringComparison.Ordinal)) return prefix + AbsolutePath(value[prefix.Length..], option);
                valid = value is "none" or "emulated" or "environment" or "virtualscene" || Regex.IsMatch(value, @"\Awebcam[0-9]{1,3}\z"); break;
            case "-skin": valid = Regex.IsMatch(value, @"\A[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}\z"); break;
            case "-timezone": valid = Regex.IsMatch(value, @"\A[A-Za-z][A-Za-z0-9_+/.-]{0,127}\z") && !value.Contains(".."); break;
            case "-dns-server":
                var hosts = value.Split(',');
                valid = hosts.Length is >= 1 and <= 4 && hosts.All(host => host.Length is > 0 and < 254 &&
                    (IPAddress.TryParse(host, out _) || Uri.CheckHostName(host) == UriHostNameType.Dns)); break;
            case "-http-proxy":
                valid = Uri.TryCreate(value.Contains("://", StringComparison.Ordinal) ? value : "http://" + value, UriKind.Absolute, out var proxy) &&
                    proxy.Scheme == "http" && proxy.Host.Length > 0 && proxy.Port is >= 1 and <= 65535 &&
                    proxy.AbsolutePath == "/" && proxy.Query.Length == 0 && proxy.Fragment.Length == 0; break;
            default: valid = false; break;
        }
        if (!valid) throw Invalid(L.F($"参数 {option} 的值不受支持或格式无效（未记录原值）。"));
        return value;
    }

    private static bool NumericPair(string value, bool requireAscending)
    {
        var parts = value.Split(':');
        if (parts.Length is < 1 or > 2 || parts.Any(part => !int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < 0)) return false;
        return parts.Length == 1 || !requireAscending || int.Parse(parts[0], CultureInfo.InvariantCulture) <= int.Parse(parts[1], CultureInfo.InvariantCulture);
    }

    private static int Integer(string value, int minimum, int maximum, string option)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed < minimum || parsed > maximum)
            throw Invalid(L.F($"参数 {option} 需要 {minimum} 至 {maximum} 范围内的整数。"));
        return parsed;
    }

    private static string AbsolutePath(string value, string option)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl) || value.IndexOfAny(['"', '*', '?', '|', '<', '>']) >= 0 ||
            !Path.IsPathFullyQualified(value) || value.StartsWith(@"\\.\", StringComparison.Ordinal) || value.StartsWith(@"\\?\", StringComparison.Ordinal))
            throw Invalid(L.F($"{option} 必须使用普通绝对路径，不能依赖原进程的工作目录。"));
        try
        {
            var normalized = Path.GetFullPath(value).Replace('/', '\\');
            if (normalized.Length > 32767 || normalized.AsSpan(2).Contains(':')) throw Invalid(L.F($"{option} 的路径格式不受支持。"));
            return normalized;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or System.Security.SecurityException)
        { throw Invalid(L.F($"{option} 的绝对路径无法规范化。")); }
    }

    // Unknown arguments can carry secrets after '='; diagnostics must not echo arbitrary values.
    private static string SafeOption(string value) => Regex.IsMatch(value, @"\A-[A-Za-z][A-Za-z0-9-]{0,63}\z") ? value : L.T("（未知选项，原值已隐藏）");
    private static InvalidDataException Invalid(string message) => new(message);
}
