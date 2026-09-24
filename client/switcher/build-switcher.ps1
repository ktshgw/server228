[CmdletBinding()]
param(
    [string]$OutputDirectory = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $PSScriptRoot "dist"
}

$sourcePath = Join-Path $PSScriptRoot "SomsSwitcher.cs"
$workspace = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$processGuardPath = Join-Path $workspace "client\launcher\LazerProcessGuard.cs"
$logoPath = Join-Path $workspace "static\site\soms-logo-v3.png"
$compiler = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$outputPath = Join-Path $OutputDirectory "SOMS-switcher.exe"

if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) {
    throw "The .NET Framework C# compiler was not found: $compiler"
}
if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
    throw "Switcher source was not found: $sourcePath"
}
if (-not (Test-Path -LiteralPath $logoPath -PathType Leaf)) {
    throw "SOMS! logo was not found: $logoPath"
}

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$iconPath = Join-Path ([System.IO.Path]::GetFullPath($OutputDirectory)) "SOMS-switcher.ico"
& "$PSScriptRoot\build-icon.ps1" -Source $logoPath -Destination $iconPath
if (Test-Path -LiteralPath $outputPath -PathType Leaf) {
    Remove-Item -LiteralPath $outputPath -Force
}

& $compiler `
    /nologo `
    /target:winexe `
    /platform:anycpu `
    /optimize+ `
    /win32manifest:"$PSScriptRoot\app.manifest" `
    /win32icon:"$iconPath" `
    /reference:System.dll `
    /reference:System.Core.dll `
    /reference:System.Drawing.dll `
    /reference:System.Web.Extensions.dll `
    /reference:System.Windows.Forms.dll `
    /resource:"$logoPath,SomsSwitcher.Logo.png" `
    /out:"$outputPath" `
    "$sourcePath" `
    "$processGuardPath"

if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $outputPath -PathType Leaf)) {
    throw "SOMS! switcher compilation failed."
}

$hash = (Get-FileHash -LiteralPath $outputPath -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host "Built: $outputPath" -ForegroundColor Green
Write-Host "SHA-256: $hash"
