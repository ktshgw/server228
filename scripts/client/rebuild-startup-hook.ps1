[CmdletBinding()]
param(
    [string]$OsuVersion = "",
    [switch]$BuildOnly,
    [switch]$SkipSwitcherBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "client-release-tools.ps1")

$version = Get-ClientOsuVersion -RequestedVersion $OsuVersion
Build-StartupHook -Rebuild
Ensure-CompanionArtifacts
$artifacts = Get-ClientArtifacts
Show-ClientArtifactHashes -Artifacts $artifacts

if (-not $BuildOnly) {
    Publish-ClientRelease -OsuVersion $version -Artifacts $artifacts -SkipSwitcherBuild:$SkipSwitcherBuild
} else {
    Write-Host "Build-only mode: SHA/configuration and switcher staging were not changed." -ForegroundColor Yellow
}
