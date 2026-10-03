#requires -Version 7.0
# Compatibility command name: all current releases use the Win7-compatible universal launcher.
[CmdletBinding()]
param(
    [string]$InputDirectory = (Join-Path $PSScriptRoot 'App'),
    [Parameter(Mandatory)][string]$Arm64Directory,
    [Parameter(Mandatory)][string]$LegacyDirectory,
    [Parameter(Mandatory)][string]$OutputPath,
    [string]$IntermediateDirectory = (Join-Path $PSScriptRoot 'artifacts/packages'),
    [string]$PowerShellPath,
    [string]$VisualStudioPath,
    [string]$WindowsSdkRoot,
    [string]$WindowsSdkVersion,
    [switch]$StableRelease
)
$ErrorActionPreference = 'Stop'
Write-Host 'package-single-file forwards to package-universal; x64, ARM64 and Legacy payloads are required.'
$arguments = @{ ModernDirectory=$InputDirectory; Arm64Directory=$Arm64Directory; LegacyDirectory=$LegacyDirectory; OutputPath=$OutputPath; IntermediateDirectory=$IntermediateDirectory; StableRelease=$StableRelease }
foreach ($name in @('PowerShellPath','VisualStudioPath','WindowsSdkRoot','WindowsSdkVersion')) {
    $value = Get-Variable -Name $name -ValueOnly
    if ($value) { $arguments[$name] = $value }
}
& (Join-Path $PSScriptRoot 'package-universal.ps1') @arguments
