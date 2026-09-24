[CmdletBinding(DefaultParameterSetName = 'Process')]
param(
    [Parameter(ParameterSetName = 'Process', Mandatory = $true)][int]$ProcessId,
    [Parameter(ParameterSetName = 'Process', Mandatory = $true)][long]$StartTimeTicks,
    [Parameter(ParameterSetName = 'Process', Mandatory = $true)][string]$ProfileDirectory,
    [Parameter(ParameterSetName = 'Sweep', Mandatory = $true)][switch]$Sweep
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$testRoot = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) 'SOMS\test-clients'))

function Remove-SafeTestProfile([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) { return }
    $resolved = [IO.Path]::GetFullPath($Path)
    if (-not $resolved.StartsWith($testRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { return }
    $marker = Join-Path $resolved '.soms-test-profile'
    if (-not (Test-Path -LiteralPath $marker -PathType Leaf)) { return }
    if ([IO.File]::ReadAllText($marker).Trim() -cne 'SOMS-TEST-PROFILE-1') { return }
    Remove-Item -LiteralPath $resolved -Recurse -Force -ErrorAction SilentlyContinue
}

if ($Sweep) {
    if (-not (Test-Path -LiteralPath $testRoot -PathType Container)) { return }
    foreach ($profile in Get-ChildItem -LiteralPath $testRoot -Directory -Force) {
        $recordPath = Join-Path $profile.FullName 'launcher-process.json'
        $active = $false
        if (Test-Path -LiteralPath $recordPath -PathType Leaf) {
            try {
                $record = [IO.File]::ReadAllText($recordPath) | ConvertFrom-Json
                $process = Get-Process -Id ([int]$record.Id) -ErrorAction SilentlyContinue
                $active = $null -ne $process -and $process.ProcessName -eq 'osu!' -and $process.StartTime.Ticks -eq [long]$record.StartTimeTicks
            }
            catch { $active = $false }
        }
        if (-not $active) { Remove-SafeTestProfile $profile.FullName }
    }
    return
}

try {
    $process = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
    if ($null -ne $process -and $process.StartTime.Ticks -eq $StartTimeTicks) { $process.WaitForExit() }
}
catch { }
Remove-SafeTestProfile $ProfileDirectory