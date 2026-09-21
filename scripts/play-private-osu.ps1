[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$workspaceDirectory = Split-Path -Parent $PSScriptRoot
$environmentPath = Join-Path $workspaceDirectory "server\.env"
$launcherPath = Join-Path $workspaceDirectory "launcher\Start-SharedPrivateOsu.ps1"
$serverScriptPath = Join-Path $PSScriptRoot "easy-server.ps1"

function Read-DotEnvValue {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    foreach ($line in (Get-Content -LiteralPath $environmentPath -Encoding UTF8)) {
        if ($line -notmatch "^$([regex]::Escape($Name))=(.*)$") {
            continue
        }

        $value = $Matches[1].Trim()
        if ($value.Length -ge 2 -and
            (($value.StartsWith('"') -and $value.EndsWith('"')) -or
             ($value.StartsWith("'") -and $value.EndsWith("'")))) {
            $value = $value.Substring(1, $value.Length - 2)
        }
        return $value
    }

    throw "Missing $Name in server\.env. Run START_SERVER.bat first."
}

function Test-PrivateServerHealth {
    param([string]$ServerUrl)

    try {
        $healthUrl = ([Uri]::new([Uri]$ServerUrl, "health")).AbsoluteUri
        $response = Invoke-RestMethod -Uri $healthUrl -TimeoutSec 10
        return [string]$response.status -ceq "ok"
    }
    catch {
        return $false
    }
}

if (-not (Test-Path -LiteralPath $environmentPath -PathType Leaf)) {
    throw "Server configuration is missing. Run START_SERVER.bat first."
}
if (-not (Test-Path -LiteralPath $launcherPath -PathType Leaf)) {
    throw "Private osu! launcher is missing: $launcherPath"
}

$serverUrl = Read-DotEnvValue -Name "SERVER_URL"
if (-not (Test-PrivateServerHealth -ServerUrl $serverUrl)) {
    Write-Host "Private server is offline. Starting it now..." -ForegroundColor Cyan
    & powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File $serverScriptPath -Action Start
    if ($LASTEXITCODE -ne 0 -and $LASTEXITCODE -ne 2) {
        throw "The private server could not be started."
    }
    if (-not (Test-PrivateServerHealth -ServerUrl $serverUrl)) {
        throw "The private server did not pass its HTTPS health check."
    }
}

$clientSecret = Read-DotEnvValue -Name "OSU_CLIENT_SECRET"
if ($clientSecret.Length -lt 16) {
    throw "OSU_CLIENT_SECRET in server\.env is invalid."
}

try {
    [Environment]::SetEnvironmentVariable("PRIVATE_OSU_CLIENT_SECRET", $clientSecret, [EnvironmentVariableTarget]::Process)
    & powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File $launcherPath
    if ($LASTEXITCODE -ne 0) {
        throw "The private osu! client could not be started."
    }
}
finally {
    [Environment]::SetEnvironmentVariable("PRIVATE_OSU_CLIENT_SECRET", $null, [EnvironmentVariableTarget]::Process)
    $clientSecret = $null
}
