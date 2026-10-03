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
foreach ($name in @('Read-BuildMetadata','Assert-CoreBuildStamp','Assert-RelativePath','Get-PublishedFiles')) {
    $node = $ast.Find({ param($value) $value -is [Management.Automation.Language.FunctionDefinitionAst] -and $value.Name -eq $name },$true)
    if (-not $node) { throw "Missing package gate $name" }; . ([scriptblock]::Create($node.Extent.Text))
}
Check (-not (Reject { Assert-RelativePath ('modern/arm64/' + ('a' * 73)) })) 'Win7 package path accepts exactly 86 characters'
Check (Reject { Assert-RelativePath ('modern/arm64/' + ('a' * 74)) }) 'Win7 package path rejects 87 characters without weakening the cache budget'
foreach ($license in Get-ChildItem -LiteralPath (Join-Path $root 'licenses') -File -Recurse) {
    $relative = [IO.Path]::GetRelativePath((Join-Path $root 'licenses'),$license.FullName).Replace('\','/')
    foreach ($prefix in @('modern','modern/arm64','legacy')) {
        Assert-RelativePath "$prefix/licenses/$relative"
    }
}
Check $true 'every retained license fits all three route path budgets'
$privacyFixture = Join-Path $work 'payload-privacy'
New-Item -ItemType Directory -Path $privacyFixture | Out-Null
foreach ($name in @('whitelist-scope.json','whitelist-scope.json.bak','whitelist-scope.json.pending','ProcessKeeper.exe')) {
    [IO.File]::WriteAllText((Join-Path $privacyFixture $name), 'filter fixture only; never executed')
}
$published = @(Get-PublishedFiles $privacyFixture)
Check ($published.Count -eq 1 -and $published[0].Name -ceq 'ProcessKeeper.exe') 'personal whitelist scopes and their backups never enter the package'
$buildAst = [Management.Automation.Language.Parser]::ParseFile((Join-Path $root 'build.ps1'),[ref]$null,[ref]$parseErrors)
Check ($parseErrors.Count -eq 0) 'multi-architecture build script parses'
$reuseFunction = $buildAst.Find({ param($value) $value -is [Management.Automation.Language.FunctionDefinitionAst] -and $value.Name -eq 'Assert-ReusedBuildStamp' },$true)
if (-not $reuseFunction) { throw 'Missing reused build metadata validation.' }
. ([scriptblock]::Create($reuseFunction.Extent.Text))
$info = [pscustomobject]@{Version='1.6.0';ReleaseChannel='Preview';BuiltAtUtc='2026-09-27T08:51:03Z';BuiltAtUtc8='2026-09-27T16:51:03+08:00'}
$json = Join-Path $work 'build-info.json'
$info | ConvertTo-Json | Set-Content -LiteralPath $json
Check ((Read-BuildMetadata $json 'Preview' $info.Version).ReleaseChannel -ceq 'Preview') 'packaging defaults to Preview'
Check (Reject { Read-BuildMetadata $json 'Stable' $info.Version }) 'Stable packaging refuses a Preview stamp'
Check (Reject { Read-BuildMetadata $json 'Preview' '1.7.0' }) 'packaging rejects a stamp from another product version'
Check (Reject { Read-BuildMetadata $json 'Preview' '1.6.0-Preview' }) 'packaging rejects invalid native resource version strings'
$info.ReleaseChannel='Stable'; $info | ConvertTo-Json | Set-Content -LiteralPath $json
Check (Reject { Read-BuildMetadata $json 'Preview' $info.Version }) 'default packaging refuses a retained Stable stamp'
Check ((Read-BuildMetadata $json 'Stable' $info.Version).ReleaseChannel -ceq 'Stable') 'explicit Stable packaging accepts matching metadata'
$info.ReleaseChannel='Preview'
$originalStamp=$info.BuiltAtUtc8; $info.BuiltAtUtc8='2026-09-27T16:51:04+08:00'; $info|ConvertTo-Json|Set-Content -LiteralPath $json
Check (Reject {Read-BuildMetadata $json 'Preview' $info.Version}) 'UTC and UTC+8 must describe exactly the same second'
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
$generatorFixture = Join-Path $work 'timestamp-generator';
New-Item -ItemType Directory -Path (Join-Path $generatorFixture 'src/ProcessKeeper.App'),(Join-Path $generatorFixture 'src/ProcessKeeper.Core') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'write-build-info.ps1') -Destination (Join-Path $generatorFixture 'write-build-info.ps1')
[IO.File]::WriteAllText((Join-Path $generatorFixture 'src/ProcessKeeper.App/ProcessKeeper.App.csproj'), '<Project><PropertyGroup><Version>1.6.0</Version></PropertyGroup></Project>')
foreach ($stable in @($false,$true)) {
    & (Join-Path $generatorFixture 'write-build-info.ps1') -StableRelease:$stable | Out-Null
    $expectedChannel = if ($stable) { 'Stable' } else { 'Preview' }
    $generatedInfo = Read-BuildMetadata (Join-Path $generatorFixture 'build-info.json') $expectedChannel $info.Version
    Check ($generatedInfo.BuiltAtUtc8 -match '\+08:00$') ('generated ' + $expectedChannel + ' metadata preserves ISO UTC+8 machine format')
    $generatedSource = [IO.File]::ReadAllText((Join-Path $generatorFixture 'src/ProcessKeeper.Core/BuildInfo.cs'))
    Check ($generatedSource.Contains('return TimeDisplay.Format(shown);') -and -not $generatedSource.Contains('return shown.ToString("yyyy-MM-dd HH:mm:ss zzz"')) ('generated ' + $expectedChannel + ' source uses shared human-readable timezone formatting')
    $generatedJson = Join-Path $generatorFixture 'build-info.json'
    $generatedProject = Join-Path $generatorFixture 'src/ProcessKeeper.App/ProcessKeeper.App.csproj'
    $generatedCode = Join-Path $generatorFixture 'src/ProcessKeeper.Core/BuildInfo.cs'
    $originalJson = [IO.File]::ReadAllText($generatedJson)
    Assert-ReusedBuildStamp $generatedJson $generatedProject $generatedCode $expectedChannel
    Check (-not $generatedSource.Contains('1.6.0')) ('reuse validates actual generated ' + $expectedChannel + ' metadata without requiring a nonexistent Core version constant')
    $otherChannel = if ($stable) {'Preview'} else {'Stable'}
    Check (Reject { Assert-ReusedBuildStamp $generatedJson $generatedProject $generatedCode $otherChannel }) 'reuse rejects mismatched release channel'
    [IO.File]::WriteAllText($generatedJson,$originalJson.Replace('1.6.0','1.6.1'))
    Check (Reject { Assert-ReusedBuildStamp $generatedJson $generatedProject $generatedCode $expectedChannel }) 'reuse checks product version against the project independently of Core dates'
    [IO.File]::WriteAllText($generatedJson,$originalJson.Replace('"Version": "1.6.0"','"Version": "1.6.0", "Version": "1.6.0"'))
    Check (Reject { Assert-ReusedBuildStamp $generatedJson $generatedProject $generatedCode $expectedChannel }) 'reuse rejects duplicated metadata fields'
    [IO.File]::WriteAllText($generatedJson,$originalJson)
    foreach ($field in @('BuildDateUtc','BuildDateUtc8')) {
        $changedSource=$generatedSource.Replace('public const string '+$field+' =','// public const string '+$field+' =')
        [IO.File]::WriteAllText($generatedCode,$changedSource)
        Check (Reject { Assert-ReusedBuildStamp $generatedJson $generatedProject $generatedCode $expectedChannel }) ('matching timestamp in a comment cannot replace the '+$field+' declaration')
    }
    $mismatchUtc8=[DateTimeOffset]::Parse($generatedInfo.BuiltAtUtc8).AddSeconds(1).ToString("yyyy-MM-dd'T'HH:mm:sszzz",[Globalization.CultureInfo]::InvariantCulture)
    [IO.File]::WriteAllText($generatedCode,$generatedSource.Replace($generatedInfo.BuiltAtUtc8,$mismatchUtc8))
    Check (Reject { Assert-ReusedBuildStamp $generatedJson $generatedProject $generatedCode $expectedChannel }) 'reuse rejects the wrong value in an existing date declaration'
    [IO.File]::WriteAllText($generatedJson,$originalJson.Replace($generatedInfo.BuiltAtUtc8,$mismatchUtc8))
    Check (Reject { Assert-ReusedBuildStamp $generatedJson $generatedProject $generatedCode $expectedChannel }) 'reuse rejects two matching source dates that describe different instants'
    [IO.File]::WriteAllText($generatedJson,$originalJson)
    $duplicate='public const string BuildDateUtc = "'+$generatedInfo.BuiltAtUtc+'";'
    [IO.File]::WriteAllText($generatedCode,$generatedSource+[Environment]::NewLine+$duplicate)
    Check (Reject { Assert-ReusedBuildStamp $generatedJson $generatedProject $generatedCode $expectedChannel }) 'reuse rejects duplicate date declarations'
    [IO.File]::WriteAllText($generatedCode,$generatedSource)
}
$fixture = Join-Path $work 'channel-fixture'; New-Item -ItemType Directory -Path $fixture | Out-Null
$props=[Security.SecurityElement]::Escape((Join-Path $root 'Directory.Build.props'))
$targets=[Security.SecurityElement]::Escape((Join-Path $root 'Directory.Build.targets'))
$buildInfo=[Security.SecurityElement]::Escape((Join-Path $root 'src/ProcessKeeper.Core/BuildInfo.cs'))
$timeDisplay=[Security.SecurityElement]::Escape((Join-Path $root 'src/ProcessKeeper.Core/TimeDisplay.cs'))
$project=@"
<Project Sdk="Microsoft.NET.Sdk">
 <Import Project="$props"/>
 <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net8.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
 <ItemGroup><Compile Include="$buildInfo" Link="BuildInfo.cs"/><Compile Include="$timeDisplay" Link="TimeDisplay.cs"/></ItemGroup>
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
# The final case deliberately rejects a dotnet build; do not report its expected nonzero status as this script's failure.
exit 0
