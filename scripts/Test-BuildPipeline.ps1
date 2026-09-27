#requires -Version 7.0
[CmdletBinding()]
param([string]$DotnetPath, [string]$WorkDirectory)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $PSScriptRoot 'BuildTools.ps1')
$dotnet = Resolve-ProcessKeeperDotnet $DotnetPath
$work = if ($WorkDirectory) { [IO.Path]::GetFullPath($WorkDirectory) } else { Join-Path $root ('artifacts/build-tests-' + [Guid]::NewGuid().ToString('N')) }
if (Test-Path -LiteralPath $work) { throw 'Choose a new isolated build test directory.' }
New-Item -ItemType Directory -Path $work | Out-Null
$checks = 0
function Check([bool]$Value,[string]$Name) { if (-not $Value) { throw "FAIL $Name" }; $script:checks++; Write-Host "PASS $Name" }
function Reject([scriptblock]$Action) { try { & $Action | Out-Null; return $false } catch { return $true } }
$parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $root 'package-universal.ps1'),[ref]$null,[ref]$parseErrors)
Check ($parseErrors.Count -eq 0) 'universal package script parses'
foreach ($name in @('Read-BuildMetadata','Assert-CoreBuildStamp')) {
    $node = $ast.Find({ param($value) $value -is [Management.Automation.Language.FunctionDefinitionAst] -and $value.Name -eq $name },$true)
    if (-not $node) { throw "Missing package gate $name" }; . ([scriptblock]::Create($node.Extent.Text))
}
$info = [pscustomobject]@{Version='1.5.0';ReleaseChannel='Preview';BuiltAtUtc='2026-09-27T08:51:03Z';BuiltAtUtc8='2026-09-27T16:51:03+08:00'}
$json = Join-Path $work 'build-info.json'
$info | ConvertTo-Json | Set-Content -LiteralPath $json
Check ((Read-BuildMetadata $json).ReleaseChannel -ceq 'Preview') 'packaging defaults to Preview'
Check (Reject { Read-BuildMetadata $json 'Stable' }) 'Stable packaging refuses a Preview stamp'
$info.ReleaseChannel='Stable'; $info | ConvertTo-Json | Set-Content -LiteralPath $json
Check (Reject { Read-BuildMetadata $json }) 'default packaging refuses a retained Stable stamp'
Check ((Read-BuildMetadata $json 'Stable').ReleaseChannel -ceq 'Stable') 'explicit Stable packaging accepts matching metadata'
$info.ReleaseChannel='Preview'
$originalStamp=$info.BuiltAtUtc8; $info.BuiltAtUtc8='2026-09-27T16:51:04+08:00'; $info|ConvertTo-Json|Set-Content -LiteralPath $json
Check (Reject {Read-BuildMetadata $json}) 'UTC and UTC+8 must describe exactly the same second'
$info.BuiltAtUtc8=$originalStamp; $info|ConvertTo-Json|Set-Content -LiteralPath $json
$file=Join-Path $work 'synthetic.dll'
foreach($alignment in @(0,1)) {
    $bytes=[byte[]]::new(2048)
    [Text.Encoding]::Unicode.GetBytes($info.BuiltAtUtc).CopyTo($bytes,100+$alignment)
    [Text.Encoding]::Unicode.GetBytes($info.BuiltAtUtc8).CopyTo($bytes,400+$alignment)
    [Text.Encoding]::Unicode.GetBytes('ProcessKeeper.Build.Channel:Preview').CopyTo($bytes,700+$alignment)
    [IO.File]::WriteAllBytes($file,$bytes); Assert-CoreBuildStamp $file $info
    Check $true 'package reads date and channel constants at either UTF16 alignment'
}
$info.ReleaseChannel='Stable'; Check (Reject {Assert-CoreBuildStamp $file $info}) 'mixed Preview and Stable payloads cannot be packaged'
$info.ReleaseChannel='Preview'
$bytes=[byte[]]::new(2048);[Text.Encoding]::Unicode.GetBytes($info.BuiltAtUtc).CopyTo($bytes,100);[IO.File]::WriteAllBytes($file,$bytes)
Check (Reject {Assert-CoreBuildStamp $file $info}) 'missing build constants cannot be packaged'
$fixture = Join-Path $work 'channel-fixture'; New-Item -ItemType Directory -Path $fixture | Out-Null
$props=[Security.SecurityElement]::Escape((Join-Path $root 'Directory.Build.props'))
$targets=[Security.SecurityElement]::Escape((Join-Path $root 'Directory.Build.targets'))
$buildInfo=[Security.SecurityElement]::Escape((Join-Path $root 'src/ProcessKeeper.Core/BuildInfo.cs'))
$project=@"
<Project Sdk="Microsoft.NET.Sdk">
 <Import Project="$props"/>
 <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net8.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
 <ItemGroup><Compile Include="$buildInfo" Link="BuildInfo.cs"/></ItemGroup>
 <Import Project="$targets"/>
</Project>
"@
[IO.File]::WriteAllText((Join-Path $fixture 'ChannelFixture.csproj'),$project,[Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText((Join-Path $fixture 'Program.cs'),'Console.WriteLine(ProcessKeeper.Core.BuildInfo.ReleaseChannel + " | " + ProcessKeeper.Core.BuildInfo.IsPreviewBuild);',[Text.UTF8Encoding]::new($false))
# Restore/build only this harmless fixture. Never generate a product package or launch a product app.
Push-Location $root
try {
    foreach ($case in @(@('default','Preview | True'),@('Stable','Stable | False'),@('default-after-stable','Preview | True'))) {
        $arguments=@('run','--project',(Join-Path $fixture 'ChannelFixture.csproj'),'-c','Release','--nologo')
        if ($case[0] -eq 'Stable') {$arguments+='-p:ProcessKeeperReleaseChannel=Stable'}
        $output = & $dotnet @arguments 2>&1
        Check ($LASTEXITCODE -eq 0 -and ($output -join "`n").Contains($case[1])) ('Release build channel ' + $case[0])
    }
    $output = & $dotnet build (Join-Path $fixture 'ChannelFixture.csproj') -c Release '-p:ProcessKeeperReleaseChannel=Unknown' --nologo 2>&1
    Check ($LASTEXITCODE -ne 0) 'unknown channel cannot silently compile'
} finally {Pop-Location}
Write-Host "Build pipeline gates: $checks passed | only isolated fixture builds; no package publication."
