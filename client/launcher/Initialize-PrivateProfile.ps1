[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$ConfigPath = "",

    [switch]$ConfirmNoOfficialAccountData
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($ConfigPath)) {
    $ConfigPath = Join-Path -Path $PSScriptRoot -ChildPath "launcher.json"
}

. (Join-Path -Path $PSScriptRoot -ChildPath "Launcher.Common.ps1")

Assert-LauncherWindowsX64
Assert-LauncherNotElevated
$settings = Read-LauncherConfig -ConfigPath $ConfigPath

if (-not $ConfirmNoOfficialAccountData) {
    throw "Safety confirmation missing. Use a dedicated Windows account, confirm that its osu! profile contains no official login/data, then rerun with -ConfirmNoOfficialAccountData."
}

if ($PSCmdlet.ShouldProcess($settings.Profile.DataDirectory, "Create a private osu! profile marker bound to this Windows SID and server")) {
    $markerPath = New-PrivateProfileMarker -Settings $settings -ConfirmedNoOfficialAccountData $true
    Write-Host "Private profile marker is ready: $markerPath"
    Write-Host "This marker does not relocate osu! data. It only prevents this launcher from using an unmarked profile."
}
