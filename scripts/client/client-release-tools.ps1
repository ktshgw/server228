Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$script:SomsWorkspace = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$script:EnhancedAuthProject = Join-Path $script:SomsWorkspace "client\enhanced-auth\osu.Game.Rulesets.EnhancedAuth\osu.Game.Rulesets.EnhancedAuth.csproj"
$script:StartupHookProject = Join-Path $script:SomsWorkspace "client\startup-hook\PrivateOsu.StartupHook.csproj"
$script:EnhancedAuthOutput = Join-Path $script:SomsWorkspace "client\enhanced-auth\osu.Game.Rulesets.EnhancedAuth\bin\Release\net10.0"
$script:StartupHookOutput = Join-Path $script:SomsWorkspace "client\startup-hook\bin\Release\net8.0"

function Invoke-CheckedCommand {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Executable,

        [Parameter(Mandatory = $true)]
        [string[]]$Arguments,

        [Parameter(Mandatory = $true)]
        [string]$FailureMessage
    )

    & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$FailureMessage (exit code $LASTEXITCODE)."
    }
}

function Assert-DotNetAvailable {
    if ($null -eq (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw "The .NET SDK was not found. Install the SDK required by the client projects and try again."
    }
}

function Get-ClientOsuVersion {
    param([string]$RequestedVersion = "")

    if (-not [string]::IsNullOrWhiteSpace($RequestedVersion)) {
        $version = $RequestedVersion.Trim()
    } else {
        $configPath = Join-Path $script:SomsWorkspace "client\launcher\shared-launcher.json"
        if (-not (Test-Path -LiteralPath $configPath -PathType Leaf)) {
            throw "Launcher configuration was not found: $configPath"
        }

        $config = Get-Content -LiteralPath $configPath -Raw -Encoding UTF8 | ConvertFrom-Json
        $version = [string]$config.Osu.CompatibleVersions[0]
    }

    if ($version -notmatch '^\d{4}\.\d+\.\d+$') {
        throw "Invalid osu! calendar version: $version"
    }

    return $version
}

function Build-EnhancedAuth {
    param([switch]$Rebuild, [switch]$RefreshPackages)

    Assert-DotNetAvailable
    if ($RefreshPackages) {
        Invoke-CheckedCommand -Executable "dotnet" -Arguments @(
            "restore", $script:EnhancedAuthProject, "--force-evaluate"
        ) -FailureMessage "EnhancedAuth package restore failed"
    }

    $arguments = @("build", $script:EnhancedAuthProject, "--configuration", "Release", "--nologo")
    if ($Rebuild) {
        $arguments += @("--no-incremental")
    }
    if ($RefreshPackages) {
        $arguments += "--no-restore"
    }

    Invoke-CheckedCommand -Executable "dotnet" -Arguments $arguments -FailureMessage "EnhancedAuth build failed"
}

function Build-StartupHook {
    param([switch]$Rebuild)

    Assert-DotNetAvailable
    $arguments = @("build", $script:StartupHookProject, "--configuration", "Release", "--nologo")
    if ($Rebuild) {
        $arguments += "--no-incremental"
    }

    Invoke-CheckedCommand -Executable "dotnet" -Arguments $arguments -FailureMessage "PrivateOsu.StartupHook build failed"
}

function Get-ClientArtifacts {
    param([string]$HarmonyPath = "")

    $enhancedAuth = Join-Path $script:EnhancedAuthOutput "osu.Game.Rulesets.EnhancedAuth.dll"
    $startupHook = Join-Path $script:StartupHookOutput "PrivateOsu.StartupHook.dll"
    if ([string]::IsNullOrWhiteSpace($HarmonyPath)) {
        $harmony = Join-Path $script:EnhancedAuthOutput "0Harmony.dll"
    } else {
        $harmony = (Resolve-Path -LiteralPath $HarmonyPath).Path
    }

    foreach ($path in @($enhancedAuth, $startupHook, $harmony)) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Required client build artifact is missing: $path"
        }
    }

    return [pscustomobject]@{
        EnhancedAuth = $enhancedAuth
        StartupHook = $startupHook
        Harmony = $harmony
    }
}

function Ensure-CompanionArtifacts {
    if (-not (Test-Path -LiteralPath (Join-Path $script:EnhancedAuthOutput "osu.Game.Rulesets.EnhancedAuth.dll") -PathType Leaf) -or
        -not (Test-Path -LiteralPath (Join-Path $script:EnhancedAuthOutput "0Harmony.dll") -PathType Leaf)) {
        Write-Host "EnhancedAuth/Harmony output is missing; building it first." -ForegroundColor Yellow
        Build-EnhancedAuth
    }

    if (-not (Test-Path -LiteralPath (Join-Path $script:StartupHookOutput "PrivateOsu.StartupHook.dll") -PathType Leaf)) {
        Write-Host "StartupHook output is missing; building it first." -ForegroundColor Yellow
        Build-StartupHook
    }
}

function Show-ClientArtifactHashes {
    param([Parameter(Mandatory = $true)]$Artifacts)

    foreach ($entry in @(
        @{ Name = "EnhancedAuth"; Path = $Artifacts.EnhancedAuth },
        @{ Name = "StartupHook"; Path = $Artifacts.StartupHook },
        @{ Name = "Harmony"; Path = $Artifacts.Harmony }
    )) {
        $hash = (Get-FileHash -LiteralPath $entry.Path -Algorithm SHA256).Hash.ToLowerInvariant()
        Write-Host "$($entry.Name): $hash"
        Write-Host "  $($entry.Path)"
    }
}

function Publish-ClientRelease {
    param(
        [Parameter(Mandatory = $true)]
        [string]$OsuVersion,

        [Parameter(Mandatory = $true)]
        $Artifacts,

        [switch]$SkipSwitcherBuild
    )

    $buildSwitcher = Join-Path $script:SomsWorkspace "client\switcher\build-switcher.ps1"
    $stageRelease = Join-Path $script:SomsWorkspace "client\switcher\stage-client-release.ps1"

    if (-not $SkipSwitcherBuild) {
        & $buildSwitcher
    }

    & $stageRelease `
        -OsuVersion $OsuVersion `
        -EnhancedAuthPath $Artifacts.EnhancedAuth `
        -StartupHookPath $Artifacts.StartupHook `
        -HarmonyPath $Artifacts.Harmony

    Write-Host "Client release $OsuVersion was staged for the switcher." -ForegroundColor Green
}
