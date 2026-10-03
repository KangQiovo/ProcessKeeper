#requires -Version 7.0
[CmdletBinding()]
param(
    [string] $DotnetPath,
    [string] $NugetPackages,
    [string] $PowerShellPath,
    [string] $OutputDirectory = (Join-Path $PSScriptRoot 'App'),
    [ValidateSet('x64','arm64')][string] $Architecture = 'x64',
    [switch] $SkipBuildStamp,
    [switch] $SkipTests,
    [switch] $StableRelease
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'scripts/BuildTools.ps1')

function Assert-ReusedBuildStamp([string]$StampPath, [string]$AppProjectPath, [string]$GeneratedInfoPath, [string]$ExpectedChannel) {
    if (-not (Test-Path -LiteralPath $StampPath -PathType Leaf)) { throw '-SkipBuildStamp requires the fixed metadata from the first architecture build.' }
    $document = [System.Text.Json.JsonDocument]::Parse([IO.File]::ReadAllText($StampPath))
    try {
        if ($document.RootElement.ValueKind -ne [System.Text.Json.JsonValueKind]::Object) { throw 'Invalid reused build metadata object.' }
        $values = @{}
        foreach ($property in $document.RootElement.EnumerateObject()) {
            if ($property.Name -cnotin @('Version','ReleaseChannel','BuiltAtUtc','BuiltAtUtc8') -or $values.ContainsKey($property.Name) -or
                $property.Value.ValueKind -ne [System.Text.Json.JsonValueKind]::String) { throw 'Invalid or duplicate reused build metadata field.' }
            $values[$property.Name] = $property.Value.GetString()
        }
        if ($values.Count -ne 4) { throw 'Incomplete reused build metadata.' }
        if ($ExpectedChannel -cnotin @('Preview','Stable') -or $values.ReleaseChannel -cne $ExpectedChannel) { throw 'The reused build channel does not match this build.' }
        [xml]$projectMetadata = [IO.File]::ReadAllText($AppProjectPath)
        $versions = @($projectMetadata.SelectNodes('/Project/PropertyGroup/Version'))
        if ($versions.Count -ne 1 -or [string]::IsNullOrWhiteSpace($versions[0].InnerText) -or $values.Version -cne $versions[0].InnerText) {
            throw 'The reused build stamp belongs to another product version. Create the first architecture build without -SkipBuildStamp.'
        }
        if ($values.BuiltAtUtc -cnotmatch '\A\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z\z' -or
            $values.BuiltAtUtc8 -cnotmatch '\A\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\+08:00\z') { throw 'Invalid reused build timestamp format.' }
        $utc = [DateTimeOffset]::ParseExact($values.BuiltAtUtc,"yyyy-MM-dd'T'HH:mm:ss'Z'",[Globalization.CultureInfo]::InvariantCulture,[Globalization.DateTimeStyles]::AssumeUniversal)
        $utc8 = [DateTimeOffset]::ParseExact($values.BuiltAtUtc8,"yyyy-MM-dd'T'HH:mm:sszzz",[Globalization.CultureInfo]::InvariantCulture,[Globalization.DateTimeStyles]::None)
        if ($utc.UtcDateTime.Ticks -ne $utc8.UtcDateTime.Ticks) { throw 'The reused UTC and UTC+8 timestamps do not describe the same instant.' }
        $generatedInfo = [IO.File]::ReadAllText($GeneratedInfoPath)
        # BuildInfo owns the dates; the product project owns the version. Match declarations,
        # rather than arbitrary text which could occur in a comment or the wrong constant.
        foreach ($pair in @(@('BuiltAtUtc','BuildDateUtc'),@('BuiltAtUtc8','BuildDateUtc8'))) {
            $pattern = '(?m)^[ \t]*public[ \t]+const[ \t]+string[ \t]+' + $pair[1] + '[ \t]*=[ \t]*"([^"\r\n]*)"[ \t]*;[ \t]*\r?$'
            $declarations = [regex]::Matches($generatedInfo, $pattern)
            if ($declarations.Count -ne 1 -or $declarations[0].Groups[1].Value -cne $values[$pair[0]]) {
                throw ('The generated Core build metadata does not match build-info.json: ' + $pair[1])
            }
        }
    } finally { $document.Dispose() }
}

if (-not $IsWindows) { throw '此 WinUI 构建脚本需要 Windows 和 PowerShell 7。' }
Write-Host ('PowerShell {0}  [{1}]' -f $PSVersionTable.PSVersion, (Get-Process -Id $PID).Path)

$projectDirectory = [IO.Path]::GetFullPath($PSScriptRoot)
$appProject = Join-Path $projectDirectory 'src\ProcessKeeper.App\ProcessKeeper.App.csproj'
$testProject = Join-Path $projectDirectory 'src\ProcessKeeper.Tests\ProcessKeeper.Tests.csproj'
$settingsTestProject = Join-Path $projectDirectory 'src\ProcessKeeper.Settings.Tests\ProcessKeeper.Settings.Tests.csproj'
$installedTestProject = Join-Path $projectDirectory 'src\ProcessKeeper.Installed.Tests\ProcessKeeper.Installed.Tests.csproj'
$displayTestProject = Join-Path $projectDirectory 'src\ProcessKeeper.Display.Tests\ProcessKeeper.Display.Tests.csproj'
$iconTestProject = Join-Path $projectDirectory 'src\ProcessKeeper.Icon.Tests\ProcessKeeper.Icon.Tests.csproj'
$avdArgumentsTestProject = Join-Path $projectDirectory 'src\ProcessKeeper.Avd.Arguments.Tests\ProcessKeeper.Avd.Arguments.Tests.csproj'
$avdTestProject = Join-Path $projectDirectory 'src\ProcessKeeper.Avd.Tests\ProcessKeeper.Avd.Tests.csproj'
$specialTestProject = Join-Path $projectDirectory 'src\ProcessKeeper.SpecialWindows.Tests\ProcessKeeper.SpecialWindows.Tests.csproj'
$browserTestProject = Join-Path $projectDirectory 'src\ProcessKeeper.Browser.Tests\ProcessKeeper.Browser.Tests.csproj'
$windowRoutingTestProject = Join-Path $projectDirectory 'src\ProcessKeeper.WindowRouting.Tests\ProcessKeeper.WindowRouting.Tests.csproj'
$startupTestProject = Join-Path $projectDirectory 'src\ProcessKeeper.Startup.Tests\ProcessKeeper.Startup.Tests.csproj'
$instanceTestProject = Join-Path $projectDirectory 'src\ProcessKeeper.Instance.Tests\ProcessKeeper.Instance.Tests.csproj'
$launcherTestProject = Join-Path $projectDirectory 'src\ProcessKeeper.Launcher.Tests\ProcessKeeper.Launcher.Tests.csproj'
$localizationTestProject = Join-Path $projectDirectory 'src\ProcessKeeper.Localization.Tests\ProcessKeeper.Localization.Tests.csproj'
$closeTestProject = Join-Path $projectDirectory 'src\ProcessKeeper.Close.Tests\ProcessKeeper.Close.Tests.csproj'
$activityTestProject = Join-Path $projectDirectory 'src\ProcessKeeper.Activity.Tests\ProcessKeeper.Activity.Tests.csproj'
$searchTestProject = Join-Path $projectDirectory 'src\ProcessKeeper.Search.Tests\ProcessKeeper.Search.Tests.csproj'
$autorunTestProject = Join-Path $projectDirectory 'src\ProcessKeeper.Autorun.Tests\ProcessKeeper.Autorun.Tests.csproj'
$updateTestProject = Join-Path $projectDirectory 'src\ProcessKeeper.Update.Tests\ProcessKeeper.Update.Tests.csproj'
$architectureTestProject = Join-Path $projectDirectory 'src\ProcessKeeper.Architecture.Tests\ProcessKeeper.Architecture.Tests.csproj'
$performanceTestProject = Join-Path $projectDirectory 'src\ProcessKeeper.Performance.Tests\ProcessKeeper.Performance.Tests.csproj'
$uninstallTestProject = Join-Path $projectDirectory 'src\ProcessKeeper.Uninstall.Tests\ProcessKeeper.Uninstall.Tests.csproj'
$catalogUpdateTestProject = Join-Path $projectDirectory 'src\ProcessKeeper.CatalogUpdate.Tests\ProcessKeeper.CatalogUpdate.Tests.csproj'
$avatarTestProject = Join-Path $projectDirectory 'src\ProcessKeeper.Avatar.Tests\ProcessKeeper.Avatar.Tests.csproj'
$toolsTestProject = Join-Path $projectDirectory 'src\ProcessKeeper.Tools.Tests\ProcessKeeper.Tools.Tests.csproj'
$cloudProfilesTestProject = Join-Path $projectDirectory 'src\ProcessKeeper.CloudProfiles.Tests\ProcessKeeper.CloudProfiles.Tests.csproj'
if ($Architecture -eq 'arm64' -and -not $PSBoundParameters.ContainsKey('OutputDirectory')) { $OutputDirectory = Join-Path $PSScriptRoot 'App-arm64' }
$publishDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$publishedExe = Join-Path $publishDirectory 'ProcessKeeper.exe'

if (-not (Test-Path -LiteralPath $appProject -PathType Leaf) -or -not (Test-Path -LiteralPath $testProject -PathType Leaf)) {
    throw '源码目录不完整。请将 build.ps1 与整个 src 文件夹放在一起。'
}
$resolvedDotnet = Resolve-ProcessKeeperDotnet $DotnetPath
$resolvedPowerShell = Resolve-ProcessKeeperPowerShell $PowerShellPath
$channel = if ($StableRelease) { 'Stable' } else { 'Preview' }
$buildProperties = @("-p:ProcessKeeperReleaseChannel=$channel", ('-p:PowerShellExe=' + (ConvertTo-ProcessKeeperMSBuildLiteral $resolvedPowerShell)))
if (-not (Test-Path -LiteralPath $resolvedDotnet -PathType Leaf)) { throw 'DotnetPath 必须指向 dotnet.exe 文件。' }

# Do not stop a running app or attempt to replace its loaded runtime files.
foreach ($running in @(Get-Process -Name ProcessKeeper -ErrorAction SilentlyContinue)) {
    try {
        if ($running.Path -and [string]::Equals([IO.Path]::GetFullPath($running.Path), $publishedExe, [StringComparison]::OrdinalIgnoreCase)) {
            throw "请先退出这一份Process Keeper，再构建：$publishedExe"
        }
    } finally { $running.Dispose() }
}

$oldDotnetRoot = [Environment]::GetEnvironmentVariable('DOTNET_ROOT', 'Process')
$oldPackages = [Environment]::GetEnvironmentVariable('NUGET_PACKAGES', 'Process')
Push-Location $projectDirectory
try {
    $env:DOTNET_ROOT = [IO.Path]::GetDirectoryName($resolvedDotnet)
    if (-not [string]::IsNullOrWhiteSpace($NugetPackages)) {
        $packageDirectory = [IO.Path]::GetFullPath($NugetPackages)
        New-Item -ItemType Directory -Path $packageDirectory -Force | Out-Null
        $env:NUGET_PACKAGES = $packageDirectory
    }

    $availableSdks = @(& $resolvedDotnet --list-sdks)
    if ($LASTEXITCODE -ne 0) { throw '无法枚举 .NET SDK。DotnetPath 可能仅包含运行时。' }
    if (-not ($availableSdks | Where-Object { $_ -match '^8\.' })) {
        throw '没有找到 .NET 8 SDK。请安装 SDK，而非仅安装运行时。'
    }
    Write-Host "使用 SDK 入口：$resolvedDotnet"
    if ($SkipBuildStamp) {
        Assert-ReusedBuildStamp (Join-Path $projectDirectory 'build-info.json') $appProject (Join-Path $projectDirectory 'src/ProcessKeeper.Core/BuildInfo.cs') $channel
    } else { & (Join-Path $projectDirectory 'write-build-info.ps1') -StableRelease:$StableRelease }
    if (-not $SkipTests) {
    & $resolvedDotnet run --project $cloudProfilesTestProject --configuration Release @buildProperties
    if ($LASTEXITCODE -ne 0) { throw 'Cloud whitelist profile tests failed.' }
    & $resolvedDotnet run --project $toolsTestProject --configuration Release @buildProperties
    if ($LASTEXITCODE -ne 0) { throw 'Memory optimization and streamed downloader tests failed.' }
    & $resolvedDotnet run --project $architectureTestProject --configuration Release @buildProperties
    if ($LASTEXITCODE -ne 0) { throw 'Architecture boundary tests failed.' }
    foreach ($framework in @('net8.0-windows10.0.19041.0','net462')) {
        & $resolvedDotnet run --project $performanceTestProject --configuration Release --framework $framework @buildProperties
        if ($LASTEXITCODE -ne 0) { throw "Performance settings and sampling tests failed: $framework" }
        & $resolvedDotnet run --project $uninstallTestProject --configuration Release --framework $framework @buildProperties
        if ($LASTEXITCODE -ne 0) { throw "Registered uninstaller safety and lifecycle tests failed: $framework" }
        & $resolvedDotnet run --project $catalogUpdateTestProject --configuration Release --framework $framework @buildProperties
        if ($LASTEXITCODE -ne 0) { throw "Signed recommendation catalog update tests failed: $framework" }
        & $resolvedDotnet run --project $avatarTestProject --configuration Release --framework $framework @buildProperties
        if ($LASTEXITCODE -ne 0) { throw "Author avatar loading and fallback tests failed: $framework" }
    }
    Write-Host '正在运行核心测试……'
    & $resolvedDotnet run --project $testProject --configuration Release @buildProperties
    if ($LASTEXITCODE -ne 0) { throw '核心测试失败，已停止发布。' }

    Write-Host '正在运行全部设置导入导出测试……'
    & $resolvedDotnet run --project $settingsTestProject --configuration Release @buildProperties
    if ($LASTEXITCODE -ne 0) { throw '设置测试失败，已停止发布。' }

    Write-Host '正在运行已安装程序识别测试……'
    & $resolvedDotnet run --project $installedTestProject --configuration Release @buildProperties
    if ($LASTEXITCODE -ne 0) { throw '已安装程序识别测试失败，已停止发布。' }

    Write-Host '正在验证游戏平台分组与微软发布者识别……'
    & $resolvedDotnet run --project $displayTestProject --configuration Release --framework net8.0-windows10.0.19041.0 @buildProperties
    if ($LASTEXITCODE -ne 0) { throw '游戏平台与发布者识别测试失败，已停止发布。' }

    Write-Host '正在运行图标负载与缓存测试……'
    & $resolvedDotnet run --project $iconTestProject --configuration Release @buildProperties
    if ($LASTEXITCODE -ne 0) { throw '图标负载测试失败，已停止发布。' }

    Write-Host '正在运行 AVD 参数与原生窗口重启测试……'
    & $resolvedDotnet run --project $avdArgumentsTestProject --configuration Release @buildProperties
    if ($LASTEXITCODE -ne 0) { throw 'AVD 参数测试失败，已停止发布。' }
    & $resolvedDotnet run --project $avdTestProject --configuration Release @buildProperties
    if ($LASTEXITCODE -ne 0) { throw 'AVD 重启测试失败，已停止发布。' }

    Write-Host '正在运行专用窗口与无头浏览器画面测试……'
    & $resolvedDotnet run --project $specialTestProject --configuration Release @buildProperties
    if ($LASTEXITCODE -ne 0) { throw '专用窗口测试失败，已停止发布。' }
    & $resolvedDotnet run --project $browserTestProject --configuration Release @buildProperties
    if ($LASTEXITCODE -ne 0) { throw '浏览器画面测试失败，已停止发布。' }

    Write-Host '正在验证 AVD 窗口路由、首次引导、实例替换与权限启动入口……'
    & $resolvedDotnet run --project $windowRoutingTestProject --configuration Release @buildProperties
    if ($LASTEXITCODE -ne 0) { throw '窗口路由测试失败，已停止发布。' }
    & $resolvedDotnet run --project $startupTestProject --configuration Release @buildProperties
    if ($LASTEXITCODE -ne 0) { throw '首次引导测试失败，已停止发布。' }
    & $resolvedDotnet run --project $instanceTestProject --configuration Release @buildProperties
    if ($LASTEXITCODE -ne 0) { throw '实例替换测试失败，已停止发布。' }
    $launcherFixtureRoot = if ($env:PROCESSKEEPER_LAUNCHER_TEST_ROOT) { $env:PROCESSKEEPER_LAUNCHER_TEST_ROOT } else { Join-Path ([IO.Path]::GetTempPath()) 'ProcessKeeper.Launcher.Tests' }
    & $resolvedDotnet run --project $launcherTestProject --configuration Release @buildProperties -- $launcherFixtureRoot
    if ($LASTEXITCODE -ne 0) { throw '启动入口测试失败，已停止发布。' }
    & $resolvedDotnet run --project $launcherTestProject --configuration Release @buildProperties --no-build -- --bootstrap-text-only
    if ($LASTEXITCODE -ne 0) { throw '启动器语言测试失败，已停止发布。' }

    Write-Host '正在验证全部语言、设置迁移与 Steam 退出……'
    & $resolvedDotnet run --project $localizationTestProject --configuration Release @buildProperties
    if ($LASTEXITCODE -ne 0) { throw '本地化与设置迁移测试失败，已停止发布。' }
    $closeFixtureRoot = if ($env:PROCESSKEEPER_CLOSE_TEST_ROOT) { $env:PROCESSKEEPER_CLOSE_TEST_ROOT } else { Join-Path ([IO.Path]::GetTempPath()) 'ProcessKeeper.Close.Tests' }
    $closeFixtureRun = Join-Path $closeFixtureRoot ('run-' + [Guid]::NewGuid().ToString('N'))
    & $resolvedDotnet run --project $closeTestProject --configuration Release @buildProperties -- $closeFixtureRun
    if ($LASTEXITCODE -ne 0) { throw '退出流程测试失败，已停止发布。' }
    & $resolvedDotnet run --project $activityTestProject --configuration Release @buildProperties
    if ($LASTEXITCODE -ne 0) { throw '操作记录测试失败，已停止发布。' }
    & $resolvedDotnet run --project $searchTestProject --configuration Release @buildProperties
    if ($LASTEXITCODE -ne 0) { throw '软件与进程搜索测试失败，已停止发布。' }
    & $resolvedDotnet run --project $autorunTestProject --configuration Release @buildProperties
    if ($LASTEXITCODE -ne 0) { throw '自启动扫描与可恢复更改测试失败，已停止发布。' }
    & $resolvedDotnet run --project $updateTestProject --configuration Release @buildProperties
    if ($LASTEXITCODE -ne 0) { throw '更新元数据、下载校验与设置测试失败，已停止发布。' }

    }
    $targetPlatform = if ($Architecture -eq 'arm64') { 'ARM64' } else { 'x64' }
    Write-Host "正在发布 Windows $Architecture 自包含应用……"
    & $resolvedDotnet publish $appProject --configuration Release @buildProperties --runtime "win-$Architecture" --self-contained true "-p:Platform=$targetPlatform" '-p:WindowsAppSDKSelfContained=true' --output $publishDirectory
    if ($LASTEXITCODE -ne 0) { throw '发布失败。请检查上方构建错误；源文件未被删除或移动。' }
    if (-not (Test-Path -LiteralPath $publishedExe -PathType Leaf)) { throw '发布命令结束，但没有生成 App\ProcessKeeper.exe。' }

    foreach ($readme in Get-ChildItem -LiteralPath $projectDirectory -Filter 'README*.md' -File) { Copy-Item -LiteralPath $readme.FullName -Destination $publishDirectory }
    Copy-Item -LiteralPath (Join-Path $projectDirectory 'LICENSE') -Destination $publishDirectory
    foreach ($notice in Get-ChildItem -LiteralPath $projectDirectory -Filter 'THIRD-PARTY-NOTICES*.md' -File) { Copy-Item -LiteralPath $notice.FullName -Destination $publishDirectory }
    $publishedDocs = Join-Path $publishDirectory 'docs'
    New-Item -ItemType Directory -Path $publishedDocs -Force | Out-Null
    foreach ($document in Get-ChildItem -LiteralPath (Join-Path $projectDirectory 'docs') -File | Where-Object { $_.Name -match '^(?:COMPATIBILITY|UTILITIES).*\.md$' }) { Copy-Item -LiteralPath $document.FullName -Destination $publishedDocs }
    foreach ($folder in @('licenses', 'rules')) {
        $target = Join-Path $publishDirectory $folder
        New-Item -ItemType Directory -Path $target -Force | Out-Null
        Get-ChildItem -LiteralPath (Join-Path $projectDirectory $folder) -File | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $target }
    }

    Write-Host "已发布：$publishedExe" -ForegroundColor Green
    Write-Host 'WinUI 中间产物已就绪。复用同一构建时间戳发布 x64/ARM64，并构建 Legacy.App 后运行 package-universal.ps1。'
} finally {
    [Environment]::SetEnvironmentVariable('DOTNET_ROOT', $oldDotnetRoot, 'Process')
    [Environment]::SetEnvironmentVariable('NUGET_PACKAGES', $oldPackages, 'Process')
    Pop-Location
}
