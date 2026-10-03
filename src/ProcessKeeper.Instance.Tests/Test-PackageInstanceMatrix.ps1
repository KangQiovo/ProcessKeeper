param(
 [Parameter(Mandatory=$true)][string]$OutputRoot,
 [string]$DotnetExe = 'dotnet',
 [string]$PowerShellExe = 'pwsh'
)
$ErrorActionPreference = 'Stop'
$testSource = $PSScriptRoot
$repository = [IO.Path]::GetFullPath((Join-Path $testSource '..\..'))
$matrixRoot = [IO.Path]::GetFullPath($OutputRoot)
$at = $matrixRoot
while($at) {
 if(Test-Path -LiteralPath $at) {
  if((Get-Item -LiteralPath $at -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Refusing redirected output: $at" }
 }
 $parent = [IO.Directory]::GetParent($at); $at = if($parent){$parent.FullName}else{$null}
}
New-Item -ItemType Directory -Path $matrixRoot -Force | Out-Null
foreach($setting in 'TEMP','TMP','DOTNET_CLI_HOME','MSBuildUserExtensionsPath','PSModuleAnalysisCachePath') {
 $destination = Join-Path $matrixRoot ('environment\'+$setting)
 New-Item -ItemType Directory -Path $destination -Force | Out-Null
 if($setting -eq 'PSModuleAnalysisCachePath'){$destination=Join-Path $destination 'ModuleAnalysisCache'}
 [Environment]::SetEnvironmentVariable($setting,$destination,'Process')
}
foreach($setting in 'NUGET_PACKAGES','NUGET_HTTP_CACHE_PATH','NUGET_PLUGINS_CACHE_PATH') {
 $existing = [Environment]::GetEnvironmentVariable($setting,'Process')
 if(!$existing -or $existing.StartsWith('C:\',[StringComparison]::OrdinalIgnoreCase)) {
  $destination = Join-Path $matrixRoot ('environment\'+$setting); New-Item -ItemType Directory -Path $destination -Force | Out-Null
  [Environment]::SetEnvironmentVariable($setting,$destination,'Process')
 }
}
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'; $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'
function XmlPath([string]$value) { [Security.SecurityElement]::Escape($value) }
function Build([string]$project,[string]$label) {
 & $DotnetExe build $project -c Release -v minimal "-p:PowerShellExe=$PowerShellExe" *> (Join-Path $matrixRoot ($label+'.build.log'))
 if($LASTEXITCODE -ne 0) { Get-Content -LiteralPath (Join-Path $matrixRoot ($label+'.build.log')) -Tail 35; throw "$label build failed" }
}
$core = Join-Path $matrixRoot 'core'; New-Item -ItemType Directory -Path $core -Force | Out-Null
$coreCs = XmlPath (Join-Path $repository 'src\ProcessKeeper.Core\*.cs')
$coreResources = XmlPath (Join-Path $repository 'src\ProcessKeeper.Core\Localization\*.json')
@"
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0-windows10.0.19041.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><AllowUnsafeBlocks>true</AllowUnsafeBlocks><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup><ItemGroup><Compile Include="$coreCs"/><EmbeddedResource Include="$coreResources" Link="Localization/%(Filename)%(Extension)"/></ItemGroup></Project>
"@ | Set-Content -LiteralPath (Join-Path $core 'Core.csproj') -Encoding utf8
Build (Join-Path $core 'Core.csproj') 'core'
$coreDll = Join-Path $core 'bin\Release\net8.0-windows10.0.19041.0\Core.dll'
$legacyBase = Join-Path $matrixRoot 'legacy-core'
$legacyCore = Join-Path $legacyBase 'ProcessKeeper.Legacy.Core'; $sharedCore = Join-Path $legacyBase 'ProcessKeeper.Core'
New-Item -ItemType Directory -Path $legacyCore,$sharedCore -Force | Out-Null
Get-ChildItem -LiteralPath (Join-Path $repository 'src\ProcessKeeper.Core') -File -Filter '*.cs' | Copy-Item -Destination $sharedCore -Force
Copy-Item -LiteralPath (Join-Path $repository 'src\ProcessKeeper.Core\Localization') -Destination $sharedCore -Recurse -Force
Get-ChildItem -LiteralPath (Join-Path $repository 'src\ProcessKeeper.Legacy.Core') -File | Copy-Item -Destination $legacyCore -Force
foreach($folder in 'Localization','Compatibility') { Copy-Item -LiteralPath (Join-Path $repository ('src\ProcessKeeper.Legacy.Core\'+$folder)) -Destination $legacyCore -Recurse -Force }
Build (Join-Path $legacyCore 'ProcessKeeper.Legacy.Core.csproj') 'legacy-core'
$legacyDll = Join-Path $legacyCore 'bin\Release\net462\ProcessKeeper.Legacy.Core.dll'
$program = XmlPath (Join-Path $testSource 'Program.cs')
$coordinator = XmlPath (Join-Path $repository 'src\ProcessKeeper.App\SingleInstanceCoordinator.cs')
$compat = XmlPath (Join-Path $repository 'src\ProcessKeeper.Legacy.App\Compatibility.cs')
$variants = @(
 @{Id='modern17';Version='1.7.0';Ui='trusted-modern';Legacy=$false},
 @{Id='modern16';Version='1.6.0';Ui='trusted-modern';Legacy=$false},
 @{Id='preview17';Version='1.7.0-beta.1';Ui='trusted-modern';Legacy=$false},
 @{Id='compat17';Version='1.7.0';Ui='trusted-compat';Legacy=$true},
 @{Id='compat16';Version='1.6.0';Ui='trusted-compat';Legacy=$true},
 @{Id='compat18';Version='1.8.0';Ui='trusted-compat';Legacy=$true},
 @{Id='ineligible20';Version='2.0.0';Ui='trusted-modern';Legacy=$true},
 @{Id='spoof99';Version='99.0.0';Ui='modern';Legacy=$false}
)
$map = [ordered]@{}
foreach($variant in $variants) {
 $directory = Join-Path $matrixRoot ('variants\'+$variant.Id); New-Item -ItemType Directory -Path $directory -Force | Out-Null
 $framework = if($variant.Legacy){'net462'}else{'net8.0-windows10.0.19041.0'}
 $platform = if($variant.Legacy){'<PlatformTarget>x86</PlatformTarget><Prefer32Bit>true</Prefer32Bit>'}else{'<RuntimeIdentifier>win-x64</RuntimeIdentifier>'}
 $packages = if($variant.Legacy){'<PackageReference Include="Microsoft.NETFramework.ReferenceAssemblies.net462" Version="1.0.3" PrivateAssets="all"/><PackageReference Include="System.Text.Json" Version="8.0.6"/>'}else{''}
 $extra = if($variant.Legacy){'<Compile Include="'+$compat+'" Link="Compatibility.cs"/>'}else{''}
 $dependency = XmlPath $(if($variant.Legacy){$legacyDll}else{$coreDll})
 $project = Join-Path $directory 'Fixture.csproj'
@"
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><AssemblyName>InstanceFixture</AssemblyName><TargetFramework>$framework</TargetFramework><LangVersion>latest</LangVersion><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings>$platform<Product>ProcessKeeper.InstanceFixture</Product><AssemblyTitle>ProcessKeeper singleton fixture | $($variant.Ui)</AssemblyTitle><Version>$($variant.Version)</Version><AutoGenerateBindingRedirects>true</AutoGenerateBindingRedirects><GenerateBindingRedirectsOutputType>true</GenerateBindingRedirectsOutputType></PropertyGroup><ItemGroup><Reference Include="Core"><HintPath>$dependency</HintPath></Reference><Compile Include="$program" Link="Program.cs"/><Compile Include="$coordinator" Link="SingleInstanceCoordinator.cs"/>$extra$packages</ItemGroup></Project>
"@ | Set-Content -LiteralPath $project -Encoding utf8
 Build $project $variant.Id
 $map[$variant.Id] = Join-Path $directory $(if($variant.Legacy){'bin\Release\net462\InstanceFixture.exe'}else{'bin\Release\net8.0-windows10.0.19041.0\win-x64\InstanceFixture.exe'})
}
$copy = Join-Path $matrixRoot 'relocated-package'; New-Item -ItemType Directory -Path $copy -Force | Out-Null
Get-ChildItem -LiteralPath ([IO.Path]::GetDirectoryName($map.modern17)) -File | Copy-Item -Destination $copy -Force
$map.modern17copy = Join-Path $copy 'InstanceFixture.exe'
$mapping = Join-Path $matrixRoot 'fixture-map.json'; $map | ConvertTo-Json | Set-Content -LiteralPath $mapping -Encoding utf8
$env:PROCESSKEEPER_INSTANCE_FIXTURE_MAP=$mapping
$env:PROCESSKEEPER_INSTANCE_TEST_ROOT=$matrixRoot
foreach($runner in @(@{Id='modern';Path=$map.modern17},@{Id='clr32';Path=$map.compat17})) {
 $env:PROCESSKEEPER_INSTANCE_REPORT=Join-Path $matrixRoot ($runner.Id+'.json')
 & $runner.Path *> (Join-Path $matrixRoot ($runner.Id+'.run.log'))
 if($LASTEXITCODE -ne 0) { Get-Content -LiteralPath (Join-Path $matrixRoot ($runner.Id+'.run.log')) -Tail 35; throw ($runner.Id+' fixture failed') }
}
$manifest = Get-Item -LiteralPath (Join-Path $testSource 'Program.cs'),(Join-Path $repository 'src\ProcessKeeper.App\SingleInstanceCoordinator.cs'),(Join-Path $repository 'src\ProcessKeeper.Core\InstanceRedirect.cs'),$coreDll,$legacyDll |
 ForEach-Object { [ordered]@{Path=$_.FullName;Sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash} }
$manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $matrixRoot 'source-hashes.json') -Encoding utf8
Write-Output $mapping
Write-Output (Join-Path $matrixRoot 'modern.json')
Write-Output (Join-Path $matrixRoot 'clr32.json')
