[CmdletBinding()]
param(
    [string]$ConfigPath = "",

    [switch]$ConfirmNoOfficialLogin,

    [switch]$Wait
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($ConfigPath)) {
    $ConfigPath = Join-Path -Path $PSScriptRoot -ChildPath "launcher.json"
}

. (Join-Path -Path $PSScriptRoot -ChildPath "Launcher.Common.ps1")

Assert-LauncherWindowsX64
Assert-LauncherNotElevated

if (-not $ConfirmNoOfficialLogin) {
    throw "Safety confirmation missing. Rerun with -ConfirmNoOfficialLogin only from the dedicated Windows profile after confirming that no official osu! account is logged in there."
}

$settings = Read-LauncherConfig -ConfigPath $ConfigPath
Assert-PrivateProfileMarker -Settings $settings | Out-Null
Assert-EnhancedAuthInstalled -Settings $settings | Out-Null
$osuVersion = Assert-OsuExecutable -Settings $settings

$runningOsu = @(Get-RunningLazerIds)
if ($runningOsu.Count -gt 0) {
    throw "osu!lazer is already running. Close lazer first; osu!stable can stay open."
}

$arguments = Get-PrivateOsuArguments -Settings $settings
$sanitizedArguments = Get-SanitizedPrivateOsuArguments -Arguments $arguments
Write-Host "Starting verified osu! $osuVersion against $($settings.Server.ApiUrl)"
Write-Host "Arguments: $($sanitizedArguments -join ' ')"
Write-Warning "If the client shows an official osu! session or ppy.sh login instead of the private server, close it immediately. Never enter official credentials in this profile."

$startParameters = @{
    FilePath = $settings.Osu.ExecutablePath
    ArgumentList = $arguments
    WorkingDirectory = (Split-Path -Parent $settings.Osu.ExecutablePath)
    PassThru = $true
}

$process = Start-Process @startParameters
if ($Wait) {
    $process.WaitForExit()
    exit $process.ExitCode
}

Write-Host "osu! process started (PID $($process.Id))."
