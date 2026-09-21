[CmdletBinding()]
param(
    [ValidateRange(1, 8)][int]$Count = 2,
    [ValidateRange(1, 8)][int]$StartAt = 1,
    [switch]$Menu,
    [switch]$PrepareOnly
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($Menu) {
    Write-Host 'SOMS! - independent test clients' -ForegroundColor Cyan
    Write-Host '2 clients: 1v1. 4 clients: 2v2. Up to 8 clients for custom matches.'
    $answer = Read-Host "How many clients in total? [$Count]"
    if (-not [string]::IsNullOrWhiteSpace($answer)) {
        $number = 0
        if (-not [int]::TryParse($answer, [ref]$number) -or $number -lt 1 -or $number -gt 8) { throw 'Enter a number from 1 to 8.' }
        $Count = $number
    }
}
if ($StartAt + $Count - 1 -gt 8) { throw 'The last profile must be 8 or lower.' }

$config = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'shared-launcher.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$secretName = [string]$config.Server.ClientSecretEnvironmentVariable
$previousSecret = [Environment]::GetEnvironmentVariable($secretName, [EnvironmentVariableTarget]::Process)
$launchLock = [Threading.Mutex]::new($false, 'Local\SomsTestClientLauncher')
$launchLockOwned = $false
try {
    try { $launchLockOwned = $launchLock.WaitOne(0) }
    catch [Threading.AbandonedMutexException] { $launchLockOwned = $true }
    if (-not $launchLockOwned) { Write-Host 'Another test launcher is already starting clients.'; return }
    if (-not $PrepareOnly -and [string]::IsNullOrWhiteSpace($previousSecret)) {
        $environmentFile = Join-Path (Split-Path -Parent $PSScriptRoot) 'server\.env'
        $privateSecret = $null
        foreach ($line in [IO.File]::ReadLines($environmentFile)) {
            if ($line -match '^OSU_CLIENT_SECRET=(.*)$') { $privateSecret = $Matches[1].Trim().Trim('"').Trim("'"); break }
        }
        if ([string]::IsNullOrWhiteSpace($privateSecret)) { throw 'Local server client configuration is missing. Start the SOMS! server first.' }
        [Environment]::SetEnvironmentVariable($secretName, $privateSecret, [EnvironmentVariableTarget]::Process)
    }
    $results = @()
    $failures = @()
    for ($profileNumber = $StartAt; $profileNumber -lt $StartAt + $Count; $profileNumber++) {
        Write-Host "[$($profileNumber - $StartAt + 1)/$Count] profile-$profileNumber" -ForegroundColor Cyan
        try {
            $results += & (Join-Path $PSScriptRoot 'Start-SharedPrivateOsu.ps1') -TestProfile $profileNumber -PrepareOnly:$PrepareOnly -PassThru
        }
        catch {
            $failures += $profileNumber
            Write-Host "profile-$profileNumber failed: $($_.Exception.Message)" -ForegroundColor Red
        }
    }
    Write-Host "Profiles ready or already open: $($results.Count)/$Count." -ForegroundColor Cyan
    if ($failures.Count -gt 0) { throw "Could not start profiles: $($failures -join ', '). See the errors above; other clients were kept open." }
}
finally {
    [Environment]::SetEnvironmentVariable($secretName, $previousSecret, [EnvironmentVariableTarget]::Process)
    $privateSecret = $null
    if ($launchLockOwned) { $launchLock.ReleaseMutex() }
    $launchLock.Dispose()
}
