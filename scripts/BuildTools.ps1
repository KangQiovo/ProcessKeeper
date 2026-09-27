#requires -Version 7.0
function Resolve-ProcessKeeperPowerShell([string]$Requested) {
    if (-not $Requested) { $Requested = $env:PROCESSKEEPER_PWSH }
    if (-not $Requested -and $PSVersionTable.PSVersion.Major -ge 7) { $Requested = (Get-Process -Id $PID).Path }
    if (-not $Requested) { $Requested = 'pwsh' }
    $command = Get-Command $Requested -CommandType Application -ErrorAction Stop | Select-Object -First 1
    $major = & $command.Source -NoLogo -NoProfile -NonInteractive -Command '$PSVersionTable.PSVersion.Major'
    if ($LASTEXITCODE -ne 0 -or "$major".Trim() -notmatch '^\d+$' -or [int]"$major" -lt 7) { throw 'PowerShell 7 or later is required. Set -PowerShellPath or PROCESSKEEPER_PWSH.' }
    return $command.Source
}
function Resolve-ProcessKeeperDotnet([string]$Requested) {
    if (-not $Requested -and $env:DOTNET_ROOT) {
        $candidate = Join-Path $env:DOTNET_ROOT 'dotnet.exe'
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { $Requested = $candidate }
    }
    if (-not $Requested) { $Requested = 'dotnet' }
    return (Get-Command $Requested -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
}
function ConvertTo-ProcessKeeperMSBuildLiteral([string]$Value) {
    return $Value.Replace('%','%25').Replace(';','%3B').Replace(',','%2C')
}
