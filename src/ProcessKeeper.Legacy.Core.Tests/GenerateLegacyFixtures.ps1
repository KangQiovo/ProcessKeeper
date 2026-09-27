#requires -Version 7.0
param([Parameter(Mandatory)][string]$OutputFile)
$ErrorActionPreference = 'Stop'
$source = [IO.File]::ReadAllText((Join-Path $PSScriptRoot '../ProcessKeeper.Installed.Tests/InstalledSearchVerification.cs'))
# Framework exposes AppDomain allocation totals, a conservative broader measurement.
# Keep the same upper-bound assertion and disclose the changed measurement in its output.
$source = $source.Replace('GC.GetAllocatedBytesForCurrentThread()', 'LegacyFixtureAllocation.Total').Replace('bytes allocated.', 'bytes allocated across the AppDomain.')
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($OutputFile)) | Out-Null
[IO.File]::WriteAllText($OutputFile, $source, [Text.UTF8Encoding]::new($false))
