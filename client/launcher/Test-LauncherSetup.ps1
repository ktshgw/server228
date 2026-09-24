[CmdletBinding()]
param(
    [string]$ConfigPath = "",

    [switch]$ConfigurationOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($ConfigPath)) {
    $ConfigPath = Join-Path -Path $PSScriptRoot -ChildPath "launcher.json"
}

. (Join-Path -Path $PSScriptRoot -ChildPath "Launcher.Common.ps1")

Assert-LauncherWindowsX64
$settings = Read-LauncherConfig -ConfigPath $ConfigPath

Write-Host "Configuration: OK"
Write-Host "Private API:   $($settings.Server.ApiUrl)"
Write-Host "Profile data:  $($settings.Profile.DataDirectory)"
Write-Host "EnhancedAuth:  $($settings.EnhancedAuth.Version) / $($settings.EnhancedAuth.Sha256)"
Write-Host "osu! allow-list: $($settings.Osu.CompatibleVersions -join ', ')"

if ($ConfigurationOnly) {
    Write-Host "Configuration-only check completed. Files and signatures were not checked."
    exit 0
}

Assert-LauncherNotElevated
Assert-PrivateProfileMarker -Settings $settings | Out-Null
Write-Host "Private profile marker: OK"

Assert-EnhancedAuthInstalled -Settings $settings | Out-Null
Write-Host "EnhancedAuth DLL: OK"

$osuVersion = Assert-OsuExecutable -Settings $settings
Write-Host "Official osu! executable: OK (version $osuVersion, valid configured publisher signature)"

$arguments = Get-PrivateOsuArguments -Settings $settings
$sanitizedArguments = Get-SanitizedPrivateOsuArguments -Arguments $arguments
Write-Host "Launch arguments: $($sanitizedArguments -join ' ')"
Write-Host "Full launcher preflight completed successfully. No process was started."
