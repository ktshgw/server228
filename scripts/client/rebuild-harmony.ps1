[CmdletBinding()]
param(
    [string]$OsuVersion = "",
    [string]$HarmonyProject = "",
    [switch]$BuildOnly,
    [switch]$SkipSwitcherBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "client-release-tools.ps1")

$version = Get-ClientOsuVersion -RequestedVersion $OsuVersion
$harmonyPath = ""

if ([string]::IsNullOrWhiteSpace($HarmonyProject)) {
    # Harmony is currently a pinned Lib.Harmony NuGet dependency of EnhancedAuth.
    # A forced restore plus a clean rebuild refreshes the exact 0Harmony.dll that
    # the client loads without requiring an untracked upstream source checkout.
    Build-EnhancedAuth -Rebuild -RefreshPackages
} else {
    $resolvedProject = (Resolve-Path -LiteralPath $HarmonyProject).Path
    Invoke-CheckedCommand -Executable "dotnet" -Arguments @(
        "build", $resolvedProject, "--configuration", "Release", "--nologo", "--no-incremental"
    ) -FailureMessage "Harmony source build failed"

    $projectDirectory = Split-Path -Parent $resolvedProject
    $harmonyCandidate = Get-ChildItem -LiteralPath (Join-Path $projectDirectory "bin\Release") `
        -Recurse -File -Filter "0Harmony.dll" -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1
    if ($null -eq $harmonyCandidate) {
        throw "Harmony build succeeded but 0Harmony.dll was not found under $projectDirectory\bin\Release."
    }
    $harmonyPath = $harmonyCandidate.FullName
}

Ensure-CompanionArtifacts
$artifacts = Get-ClientArtifacts -HarmonyPath $harmonyPath
Show-ClientArtifactHashes -Artifacts $artifacts

if (-not $BuildOnly) {
    Publish-ClientRelease -OsuVersion $version -Artifacts $artifacts -SkipSwitcherBuild:$SkipSwitcherBuild
} else {
    Write-Host "Build-only mode: SHA/configuration and switcher staging were not changed." -ForegroundColor Yellow
}
