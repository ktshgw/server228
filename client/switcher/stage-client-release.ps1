[CmdletBinding()]
param(
    [string]$OsuVersion = "2026.921.0",
    [string]$EnhancedAuthPath = "",
    [string]$HarmonyPath = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ($OsuVersion -notmatch '^\d{4}\.\d+\.\d+$') {
    throw "Invalid osu! calendar version: $OsuVersion"
}

$workspace = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$destination = Join-Path $workspace "static\client\$OsuVersion"
$sharedLauncherConfig = Join-Path $workspace "client\launcher\shared-launcher.json"
if (-not [string]::IsNullOrWhiteSpace($EnhancedAuthPath)) {
    $enhancedAuth = (Resolve-Path -LiteralPath $EnhancedAuthPath).Path
} elseif (Test-Path -LiteralPath $sharedLauncherConfig -PathType Leaf) {
    # Default to the last deliberately staged build, never an old bin folder
    # left by an earlier feature build. New releases pass -EnhancedAuthPath.
    $pinnedConfig = Get-Content -LiteralPath $sharedLauncherConfig -Raw -Encoding UTF8 | ConvertFrom-Json
    $enhancedAuth = $pinnedConfig.Injection.EnhancedAuthPath
    if ([string]::IsNullOrWhiteSpace($enhancedAuth)) {
        throw "No pinned client build. Specify -EnhancedAuthPath explicitly."
    }
} else {
    throw "Specify the tested client DLL with -EnhancedAuthPath."
}

if (-not [string]::IsNullOrWhiteSpace($HarmonyPath)) {
    $harmony = (Resolve-Path -LiteralPath $HarmonyPath).Path
} else {
    $enhancedAuthDirectory = Split-Path -Parent $enhancedAuth
    $harmony = Join-Path $enhancedAuthDirectory "0Harmony.dll"
}

$startupHook = Join-Path $workspace "client\startup-hook\bin\Release\net8.0\PrivateOsu.StartupHook.dll"
$switcher = Join-Path $PSScriptRoot "dist\SOMS-switcher.exe"

foreach ($path in @($enhancedAuth, $startupHook, $harmony, $switcher)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required client module is missing: $path"
    }
}

$enhancedAuthHash = (Get-FileHash -LiteralPath $enhancedAuth -Algorithm SHA256).Hash.ToLowerInvariant()
$startupHookHash = (Get-FileHash -LiteralPath $startupHook -Algorithm SHA256).Hash.ToLowerInvariant()
$harmony = Join-Path (Split-Path -Parent $enhancedAuth) "0Harmony.dll"

if (-not (Test-Path -LiteralPath $harmony -PathType Leaf)) {
    throw "Required Harmony module is missing: $harmony"
}

$harmonyHash = (Get-FileHash -LiteralPath $harmony -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host "0Harmony.dll: $harmonyHash"

# Check the shipped executable, not an independently maintained size limit:
# otherwise a larger DLL can pass publication but fail on every friend's launcher.
$switcherAssembly = [Reflection.Assembly]::LoadFile($switcher)
$switcherType = $switcherAssembly.GetType('SomsSwitcher.MainForm', $true)
$moduleLimitField = $switcherType.GetField('MaxModuleBytes', [Reflection.BindingFlags]'NonPublic,Static')
if ($null -eq $moduleLimitField) { throw 'Cannot verify the published switcher module size limit.' }
$moduleLimit = [long]$moduleLimitField.GetRawConstantValue()
foreach ($modulePath in @($enhancedAuth, $startupHook)) {
    $moduleSize = (Get-Item -LiteralPath $modulePath).Length
    if ($moduleSize -le 0 -or $moduleSize -gt $moduleLimit) {
        throw "Module size $moduleSize exceeds the shipped switcher limit $moduleLimit. Rebuild the switcher before publishing: $modulePath"
    }
}

# The workspace's PLAY_PRIVATE_OSU.bat uses the pinned shared launcher rather
# than the downloadable switcher. Keep both integrity sources in sync whenever
# a client release is staged so a valid newly-built DLL cannot be rejected as
# stale on the next local launch.
$updatedConfigs = @()
foreach ($launcherConfigPath in @($sharedLauncherConfig, (Join-Path $workspace "client\launcher\launcher.json"))) {
    if (-not (Test-Path -LiteralPath $launcherConfigPath -PathType Leaf)) { continue }
    $launcherConfig = Get-Content -LiteralPath $launcherConfigPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($launcherConfig.PSObject.Properties.Name -contains 'Injection') {
        $launcherConfig.Injection.EnhancedAuthSha256 = $enhancedAuthHash
        $launcherConfig.Injection.EnhancedAuthPath = $enhancedAuth
        $launcherConfig.Injection.StartupHookSha256 = $startupHookHash
    } elseif ($launcherConfig.PSObject.Properties.Name -contains 'EnhancedAuth') {
        $launcherConfig.EnhancedAuth.Sha256 = $enhancedAuthHash
        $launcherConfig.EnhancedAuth.Version = $OsuVersion
    } else {
        throw "Unsupported launcher configuration: $launcherConfigPath"
    }
    $updatedConfigs += @{ Path = $launcherConfigPath; Json = ($launcherConfig | ConvertTo-Json -Depth 10) }
}

# Validate both configuration schemas before replacing any published files.
New-Item -ItemType Directory -Path $destination -Force | Out-Null
Copy-Item -LiteralPath $enhancedAuth -Destination (Join-Path $destination "osu.Game.Rulesets.EnhancedAuth.dll") -Force
Copy-Item -LiteralPath $startupHook -Destination (Join-Path $destination "PrivateOsu.StartupHook.dll") -Force
Copy-Item -LiteralPath $harmony -Destination (Join-Path $destination "0Harmony.dll") -Force
Copy-Item -LiteralPath $switcher -Destination (Join-Path $workspace "static\client\SOMS-switcher.exe") -Force
foreach ($updatedConfig in $updatedConfigs) {
    $updatedConfig.Json | Set-Content -LiteralPath $updatedConfig.Path -Encoding UTF8
}

Get-ChildItem -LiteralPath $destination -File | ForEach-Object {
    $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-Host "$($_.Name): $hash"
}

$switcherHash = (Get-FileHash -LiteralPath $switcher -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host "SOMS-switcher.exe: $switcherHash"
