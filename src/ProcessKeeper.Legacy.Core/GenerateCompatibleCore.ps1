#requires -Version 7.0
param([Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$sourceDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../ProcessKeeper.Core'))
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (-not $output.StartsWith($PSScriptRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Generated source directory must stay inside Legacy.Core.' }
[IO.Directory]::CreateDirectory($output) | Out-Null
$excluded = @('InstalledPackageCatalog.cs', 'ChromiumLiveService.cs', 'ChromiumLiveSession.cs')
function Replace-Required([string]$text, [string]$before, [string]$after, [int]$expectedCount = 1) {
    $actualCount = ([regex]::Matches($text, [regex]::Escape($before))).Count
    if ($actualCount -ne $expectedCount) { throw "Safety adaptation expected $expectedCount matches but found $actualCount for: $before" }
    return $text.Replace($before, $after)
}
$replacements = [ordered]@{
    'Environment.ProcessId' = 'LegacyCompat.ProcessId'
    'OperatingSystem.IsWindows()' = 'LegacyCompat.IsWindows()'
    'ArgumentNullException.ThrowIfNull(' = 'LegacyCompat.ThrowIfNull('
    'SHA256.HashData(' = 'LegacyCompat.HashData('
    'Convert.ToHexString(' = 'LegacyCompat.ToHexString('
    'Math.Clamp(' = 'LegacyCompat.Clamp('
    'Path.IsPathFullyQualified(' = 'LegacyCompat.IsPathFullyQualified('
    'Path.GetRelativePath(' = 'LegacyCompat.GetRelativePath('
    'File.Move(' = 'LegacyCompat.MoveFile('
    'File.ReadAllTextAsync(' = 'LegacyCompat.ReadAllTextAsync('
    'char.IsAsciiLetter(' = 'LegacyCompat.IsAsciiLetter('
    'char.IsAsciiLetterOrDigit(' = 'LegacyCompat.IsAsciiLetterOrDigit('
    'new nint(' = 'new IntPtr('
    'nint.Zero' = 'IntPtr.Zero'
    'BitConverter.ToInt64(value)' = 'BitConverter.ToInt64(value, 0)'
    'info.ArgumentList.Add(' = 'LegacyCompat.AddArgument(info, '
    'start.ArgumentList.Add(' = 'LegacyCompat.AddArgument(start, '
    'System.IO.Path.IsPathFullyQualified(' = 'LegacyCompat.IsPathFullyQualified('
    'string.Join(''|'', values)' = 'string.Join("|", values)'
    '.UtcTicks' = '.UtcDateTime.Ticks'
    'await using var ' = 'using var '
}
foreach ($file in Get-ChildItem -LiteralPath $sourceDirectory -File -Filter '*.cs') {
    if ($file.Name -in $excluded) { continue }
    $source = [IO.File]::ReadAllText($file.FullName)
    foreach ($pair in $replacements.GetEnumerator()) { $source = $source.Replace($pair.Key, $pair.Value) }
    $source = $source.Replace('System.IO.LegacyCompat.', 'LegacyCompat.')
    if ($file.Name -eq 'ActivityLogBuffer.cs') {
        $source = $source.Replace('line.AsSpan(30, 2).SequenceEqual("  ")', 'line.Substring(30, 2) == "  "').Replace('line.AsSpan(14, 2).SequenceEqual("  ")', 'line.Substring(14, 2) == "  "').Replace('line.AsSpan(0, 30)', 'line.Substring(0, 30)')
    }
    if ($file.Name -eq 'AvdRestartService.cs') {
        $source = $source.Replace('new Dictionary<string, string>(_host.ReadEnvironment(launcher), StringComparer.OrdinalIgnoreCase)', '_host.ReadEnvironment(launcher).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase)')
    }
    if ($file.Name -eq 'AvdNative.Tcp.cs') { $source = $source.Replace('new IPAddress(bytes.AsSpan(0, 16),', 'new IPAddress(bytes.AsSpan(0, 16).ToArray(),').Replace('new IPAddress(bytes.AsSpan(4, 4))', 'new IPAddress(bytes.AsSpan(4, 4).ToArray())') }
    if ($file.Name -eq 'WhitelistStore.cs') { $source = $source.Replace('Enum.IsDefined(rule.Kind)', 'Enum.IsDefined(typeof(RuleKind), rule.Kind)') }
    if ($file.Name -eq 'AutorunServices.cs') {
        $source = $source.Replace("string.Join('\0', result)", 'string.Join("\0", result)')
        $source = Replace-Required $source 'ReadConfigScalar(service, 12)' '(LegacyWindowsCapabilities.IsServiceLaunchProtectionSupported ? ReadConfigScalar(service, 12) : 0U)'
    }
    if ($file.Name -eq 'ProcessCollector.cs') {
        $source = Replace-Required $source 'bool critical = false;' 'bool critical = false, criticalUnknown = false;'
        $source = Replace-Required $source 'CollectorNative.OpenProcess(0x1000,' 'CollectorNative.OpenProcess(LegacyWindowsCapabilities.ProcessQueryAccess,'
        $source = Replace-Required $source 'if (CollectorNative.IsProcessCritical(process, out bool isCritical)) { critical = isCritical; nativeCritical = isCritical; }' 'if (LegacyWindowsCapabilities.IsProcessCritical(process, out bool isCritical)) { critical = isCritical; nativeCritical = isCritical; } else { critical = true; criticalUnknown = true; warnings.Add(L.T("无法核实关键进程标志，已保守保护此进程。")); }'
        $source = Replace-Required $source 'SystemReason = critical ? L.T("Windows 标记为关键进程") : ""' 'SystemReason = criticalUnknown ? L.T("无法核实关键进程标志，已保守保护此进程。") : critical ? L.T("Windows 标记为关键进程") : ""'
        $source = Replace-Required $source 'CollectorNative.GetPackageFamilyName(' 'LegacyWindowsCapabilities.GetPackageFamilyName(' 2
        $source = Replace-Required $source 'CollectorNative.DwmGetWindowAttribute(' 'LegacyWindowsCapabilities.DwmGetWindowAttribute('
    }
    if ($file.Name -eq 'ProcessCloser.cs') {
        $source = Replace-Required $source 'CloseNative.IsProcessCritical(' 'LegacyWindowsCapabilities.IsProcessCritical(' 3
        $source = Replace-Required $source 'const uint QueryLimited = 0x1000,' 'uint QueryLimited = LegacyWindowsCapabilities.ProcessQueryAccess,'
        $source = Replace-Required $source 'CloseNative.OpenProcess(0x1000 |' 'CloseNative.OpenProcess(LegacyWindowsCapabilities.ProcessQueryAccess |' 2
    }
    if ($file.Name -eq 'WindowActions.cs') { $source = Replace-Required $source 'WindowNative.DwmGetWindowAttribute(' 'LegacyWindowsCapabilities.DwmGetWindowAttribute(' }
    if ($file.Name -eq 'AvdRestartService.cs') {
        $source = Replace-Required $source 'var plans = new List<AvdRestartPlan>();' 'if (!LegacyRuntimeCapabilities.SupportsAvdRestart && _host is AvdRestartHost) return new([], [L.T("32 位兼容模式不能安全重启 AVD 以恢复页面；仍可显示已经存在的图形窗口。")]); var plans = new List<AvdRestartPlan>();'
    }
    if ($file.Name -eq 'InstalledApplicationCatalog.cs') {
        $source = $source.Replace('new RegistrySeed(name,', 'new RegistrySeed(name!,').Replace('Environment.ExpandEnvironmentVariables(value.Trim())', 'Environment.ExpandEnvironmentVariables(value!.Trim())')
    }
    if ($file.Name -eq 'AutorunAdvancedScanner.cs') {
        $source = $source.Replace('using Windows.ApplicationModel;', '').Replace('using Windows.Management.Deployment;', '')
        $start = $source.IndexOf('    private static WorkerResult ReadPackages(CancellationToken token)', [StringComparison]::Ordinal)
        $end = $source.IndexOf('    public static IReadOnlyList<AutorunEntry> ReadPackageStartupManifest', $start, [StringComparison]::Ordinal)
        if ($start -lt 0 -or $end -lt 0) { throw 'Advanced package scan source boundary changed.' }
        $source = $source.Substring(0, $start) + "    private static WorkerResult ReadPackages(CancellationToken token)`n    { token.ThrowIfCancellationRequested(); return new([], [L.T(`"此兼容版本不读取打包应用启动项。`")]); }`n`n" + $source.Substring($end)
    }
    $target = Join-Path $output $file.Name
    $generated = "// Generated from ../ProcessKeeper.Core/$($file.Name); edit the shared source or this narrow compatibility adapter.`n" + $source
    if (-not [IO.File]::Exists($target) -or [IO.File]::ReadAllText($target) -cne $generated) { [IO.File]::WriteAllText($target, $generated, [Text.UTF8Encoding]::new($false)) }
}
