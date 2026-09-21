[CmdletBinding()]
param(
    [ValidateSet("Admin", "Site")]
    [string]$Target = "Admin"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$workspaceDirectory = Split-Path -Parent $PSScriptRoot
$environmentPath = Join-Path $workspaceDirectory "server\.env"
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
    param([Uri]$ServerUrl)

    try {
        $healthUrl = [Uri]::new($ServerUrl, "health")
        $response = Invoke-RestMethod -Uri $healthUrl.AbsoluteUri -TimeoutSec 10
        return [string]$response.status -ceq "ok"
    }
    catch {
        return $false
    }
}

try {
    if (-not (Test-Path -LiteralPath $environmentPath -PathType Leaf)) {
        throw "Server configuration is missing. Run START_SERVER.bat first."
    }

    $serverUrl = [Uri](Read-DotEnvValue -Name "SERVER_URL")
    if ($serverUrl.Scheme -ne "https") {
        throw "SERVER_URL must use HTTPS before the admin panel can be opened."
    }

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

    $relativePath = if ($Target -eq "Admin") { "admin/" } else { "site/" }
    $pageUrl = [Uri]::new($serverUrl, $relativePath)
    Start-Process -FilePath $pageUrl.AbsoluteUri
}
catch {
    Write-Host "[error] $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}

exit 0
