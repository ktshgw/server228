[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)]
    [string]$SourceDllPath,

    [string]$ConfigPath = "",

    [switch]$Replace
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
Assert-PrivateProfileMarker -Settings $settings | Out-Null

$resolvedSourcePath = Expand-LauncherPath -Path $SourceDllPath
if (-not (Test-Path -LiteralPath $resolvedSourcePath -PathType Leaf)) {
    throw "EnhancedAuth source DLL not found: $resolvedSourcePath"
}

$sourceHash = Get-FileSha256 -LiteralPath $resolvedSourcePath
if ($sourceHash -cne $settings.EnhancedAuth.Sha256) {
    throw "EnhancedAuth source hash mismatch. Expected $($settings.EnhancedAuth.Sha256), got $sourceHash. Nothing was installed."
}

try {
    $assemblyName = [Reflection.AssemblyName]::GetAssemblyName($resolvedSourcePath).Name
}
catch {
    throw "EnhancedAuth source is not a readable .NET assembly: $($_.Exception.Message)"
}

if ($assemblyName -cne $settings.EnhancedAuth.AssemblyName) {
    throw "Unexpected assembly '$assemblyName'. Expected '$($settings.EnhancedAuth.AssemblyName)'."
}

$destinationPath = $settings.EnhancedAuth.DestinationPath
$rulesetsDirectory = Split-Path -Parent $destinationPath

if (Test-Path -LiteralPath $destinationPath -PathType Leaf) {
    $installedHash = Get-FileSha256 -LiteralPath $destinationPath
    if ($installedHash -ceq $settings.EnhancedAuth.Sha256) {
        Write-Host "EnhancedAuth $($settings.EnhancedAuth.Version) is already installed and verified."
        exit 0
    }

    if (-not $Replace) {
        throw "A different DLL already exists at '$destinationPath'. Rerun with -Replace to preserve it as a timestamped backup before installing the verified DLL."
    }
}

if (-not $PSCmdlet.ShouldProcess($destinationPath, "Install verified EnhancedAuth $($settings.EnhancedAuth.Version)")) {
    exit 0
}

New-Item -ItemType Directory -Path $rulesetsDirectory -Force | Out-Null
$temporaryPath = Join-Path -Path $rulesetsDirectory -ChildPath (".{0}.{1}.tmp" -f $settings.EnhancedAuth.FileName, [Guid]::NewGuid().ToString("N"))
$backupPath = $null

try {
    Copy-Item -LiteralPath $resolvedSourcePath -Destination $temporaryPath
    $temporaryHash = Get-FileSha256 -LiteralPath $temporaryPath
    if ($temporaryHash -cne $settings.EnhancedAuth.Sha256) {
        throw "Hash changed while copying EnhancedAuth into the profile."
    }

    if (Test-Path -LiteralPath $destinationPath -PathType Leaf) {
        $backupDirectory = Join-Path -Path $rulesetsDirectory -ChildPath "private-launcher-backups"
        New-Item -ItemType Directory -Path $backupDirectory -Force | Out-Null
        $timestamp = [DateTime]::UtcNow.ToString("yyyyMMdd-HHmmss")
        $backupPath = Join-Path -Path $backupDirectory -ChildPath ("{0}.{1}.bak" -f $settings.EnhancedAuth.FileName, $timestamp)
        Move-Item -LiteralPath $destinationPath -Destination $backupPath
    }

    Move-Item -LiteralPath $temporaryPath -Destination $destinationPath
    Assert-EnhancedAuthInstalled -Settings $settings | Out-Null
}
catch {
    if (Test-Path -LiteralPath $temporaryPath -PathType Leaf) {
        Remove-Item -LiteralPath $temporaryPath -Force
    }

    if ($null -ne $backupPath -and
        (Test-Path -LiteralPath $backupPath -PathType Leaf) -and
        -not (Test-Path -LiteralPath $destinationPath)) {
        Move-Item -LiteralPath $backupPath -Destination $destinationPath
    }

    throw
}

Write-Host "Installed and verified EnhancedAuth $($settings.EnhancedAuth.Version): $destinationPath"
if ($null -ne $backupPath) {
    Write-Host "Previous DLL preserved at: $backupPath"
}
