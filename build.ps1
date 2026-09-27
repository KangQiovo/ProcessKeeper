#requires -Version 7.0
[CmdletBinding()]
param(
    [string] $DotnetPath,
    [string] $NugetPackages,
    [string] $PowerShellPath,
    [string] $OutputDirectory = (Join-Path $PSScriptRoot 'App'),
    [switch] $StableRelease
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'scripts/BuildTools.ps1')

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
    & (Join-Path $projectDirectory 'write-build-info.ps1') -StableRelease:$StableRelease
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

    Write-Host '正在发布 Windows x64 自包含应用……'
    & $resolvedDotnet publish $appProject --configuration Release @buildProperties --runtime win-x64 --self-contained true '-p:Platform=x64' '-p:WindowsAppSDKSelfContained=true' --output $publishDirectory
    if ($LASTEXITCODE -ne 0) { throw '发布失败。请检查上方构建错误；源文件未被删除或移动。' }
    if (-not (Test-Path -LiteralPath $publishedExe -PathType Leaf)) { throw '发布命令结束，但没有生成 App\ProcessKeeper.exe。' }

    foreach ($readme in Get-ChildItem -LiteralPath $projectDirectory -Filter 'README*.md' -File) { Copy-Item -LiteralPath $readme.FullName -Destination $publishDirectory }
    Copy-Item -LiteralPath (Join-Path $projectDirectory 'LICENSE') -Destination $publishDirectory
    Copy-Item -LiteralPath (Join-Path $projectDirectory 'THIRD-PARTY-NOTICES.md') -Destination $publishDirectory
    $publishedDocs = Join-Path $publishDirectory 'docs'
    New-Item -ItemType Directory -Path $publishedDocs -Force | Out-Null
    foreach ($document in Get-ChildItem -LiteralPath (Join-Path $projectDirectory 'docs') -Filter 'COMPATIBILITY*.md' -File) { Copy-Item -LiteralPath $document.FullName -Destination $publishedDocs }
    foreach ($folder in @('licenses', 'rules')) {
        $target = Join-Path $publishDirectory $folder
        New-Item -ItemType Directory -Path $target -Force | Out-Null
        Get-ChildItem -LiteralPath (Join-Path $projectDirectory $folder) -File | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $target }
    }

    Write-Host "已发布：$publishedExe" -ForegroundColor Green
    Write-Host 'WinUI 中间产物已就绪。构建 Legacy.App 后运行 package-universal.ps1，将两套界面打包为单个可分享的 EXE。'
} finally {
    [Environment]::SetEnvironmentVariable('DOTNET_ROOT', $oldDotnetRoot, 'Process')
    [Environment]::SetEnvironmentVariable('NUGET_PACKAGES', $oldPackages, 'Process')
    Pop-Location
}
