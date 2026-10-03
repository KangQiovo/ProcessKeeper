#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PrivateKeyPath,
    [string]$CatalogPath = (Join-Path $PSScriptRoot '../catalog/v1/catalog.json'),
    [string]$CoreAssemblyPath = (Join-Path $PSScriptRoot '../src/ProcessKeeper.Core/bin/Release/net8.0-windows10.0.19041.0/ProcessKeeper.Core.dll'),
    [string]$PreviousCatalogPath
)
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$keyFile = [IO.Path]::GetFullPath($PrivateKeyPath)
if ($keyFile.StartsWith($repositoryRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Keep the private signing key outside this repository.' }
$catalogFile = [IO.Path]::GetFullPath($CatalogPath)
if ((Get-Item -LiteralPath $catalogFile).Length -gt 524288) { throw 'Catalog exceeds 512 KiB.' }
$utf8 = [Text.UTF8Encoding]::new($false, $true)
$text = $utf8.GetString([IO.File]::ReadAllBytes($catalogFile)).TrimStart([char]0xfeff).Replace("`r`n", "`n")
if ($text.Contains("`r")) { throw 'Use LF line endings.' }
$document = $text | ConvertFrom-Json
if ($PreviousCatalogPath) {
    $previous = [IO.File]::ReadAllText([IO.Path]::GetFullPath($PreviousCatalogPath)) | ConvertFrom-Json
    if ([long]$document.revision -le [long]$previous.revision) { throw 'A published catalog correction requires a strictly higher revision.' }
}
$bytes = $utf8.GetBytes($text)
$rsa = [Security.Cryptography.RSA]::Create()
try {
    $rsa.FromXmlString([IO.File]::ReadAllText($keyFile))
    if ($rsa.KeySize -ne 3072) { throw 'The catalog requires the approved 3072-bit RSA key.' }
    $signature = $rsa.SignData($bytes, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
    # Validate using the same fixed public key and strict schema as the shipped client.
    $assembly = [Reflection.Assembly]::LoadFrom([IO.Path]::GetFullPath($CoreAssemblyPath))
    $type = $assembly.GetType('ProcessKeeper.Core.CatalogUpdater', $true)
    $flags = [Reflection.BindingFlags]'NonPublic,Static'
    $publicKey = $type.GetMethod('PublicKey', $flags).Invoke($null, @())
    $validated = $type.GetMethod('Verify', $flags).Invoke($null, @($bytes, $signature, $publicKey))
    [IO.File]::WriteAllBytes($catalogFile, $bytes)
    [IO.File]::WriteAllBytes([IO.Path]::ChangeExtension($catalogFile, '.sig'), $signature)
    Write-Output ('Signed catalog revision {0} | {1} rules | SHA-256 {2}' -f $document.revision, $document.rules.Count, $validated.Metadata.Sha256)
} finally { $rsa.Dispose() }
