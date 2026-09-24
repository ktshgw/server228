[CmdletBinding()]
param(
    [string]$ConfigPath = "",

    [switch]$Wait,

    [ValidateRange(0, 8)][int]$TestProfile = 0,

    [switch]$PrepareOnly,

    [switch]$PassThru
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($ConfigPath)) {
    $ConfigPath = Join-Path -Path $PSScriptRoot -ChildPath "shared-launcher.json"
}

. (Join-Path -Path $PSScriptRoot -ChildPath "Launcher.Common.ps1")

function Get-StorageRedirectPath {
    param([Parameter(Mandatory = $true)][string]$StorageIniPath)

    if (-not (Test-Path -LiteralPath $StorageIniPath -PathType Leaf)) {
        throw "osu! storage configuration was not found: $StorageIniPath"
    }

    foreach ($line in (Get-Content -LiteralPath $StorageIniPath -Encoding UTF8)) {
        if ($line -match '^\s*FullPath\s*=\s*(.*?)\s*$') {
            $value = $Matches[1].Trim().Trim('"').Trim("'")
            if (-not [string]::IsNullOrWhiteSpace($value)) {
                return [IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($value))
            }
        }
    }

    throw "FullPath is missing from '$StorageIniPath'."
}

function Assert-SafeModule {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$ExpectedSha256,
        [Parameter(Mandatory = $true)][string]$Name
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Name is missing: $Path"
    }

    $actualHash = Get-FileSha256 -LiteralPath $Path
    $expectedHash = Get-NormalizedSha256 -Sha256 $ExpectedSha256 -Name "$Name SHA-256"
    if ($actualHash -cne $expectedHash) {
        throw "$Name failed its integrity check. Expected $expectedHash, got $actualHash."
    }
}

function Wait-TestClientStartup {
    param([Diagnostics.Process]$Process, [string]$ProfileDirectory)

    $started = $Process.StartTime.ToUniversalTime();
    $deadline = [DateTime]::UtcNow.AddSeconds(45)
    $logsDirectory = Join-Path $ProfileDirectory 'logs'
    while ([DateTime]::UtcNow -lt $deadline) {
        if ($Process.WaitForExit(500)) {
            throw "The test client closed during startup (exit $($Process.ExitCode)). Logs: $logsDirectory"
        }
        if (-not (Test-Path -LiteralPath $logsDirectory -PathType Container)) { continue }
        $runtimeLog = Get-ChildItem -LiteralPath $logsDirectory -File -Filter '*.runtime.log' |
            Where-Object { $_.LastWriteTimeUtc -ge $started } | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
        if ($null -eq $runtimeLog) { continue }
        if (Select-String -LiteralPath $runtimeLog.FullName -SimpleMatch 'entered MainMenu#' -Quiet) {
            $Process.Refresh()
            if (-not $Process.HasExited) { return }
        }
    }
    throw "The test client did not reach its menu within 45 seconds (PID $($Process.Id)); it was left open. Logs: $logsDirectory"
}

Assert-LauncherWindowsX64
Assert-LauncherNotElevated

$resolvedConfigPath = Expand-LauncherPath -Path $ConfigPath
$configDirectory = Split-Path -Parent $resolvedConfigPath
if (-not (Test-Path -LiteralPath $resolvedConfigPath -PathType Leaf)) {
    throw "Launcher configuration not found: $resolvedConfigPath"
}

try {
    $config = Get-Content -LiteralPath $resolvedConfigPath -Raw -Encoding UTF8 | ConvertFrom-Json
}
catch {
    throw "Failed to read '$resolvedConfigPath': $($_.Exception.Message)"
}

if ([int]$config.SchemaVersion -ne 1) {
    throw "Unsupported shared launcher configuration version '$($config.SchemaVersion)'."
}

$dataDirectory = Expand-LauncherPath -Path ([string]$config.DataDirectory) -BaseDirectory $configDirectory
$executablePath = Expand-LauncherPath -Path ([string]$config.Osu.ExecutablePath) -BaseDirectory $configDirectory
$enhancedAuthPath = Expand-LauncherPath -Path ([string]$config.Injection.EnhancedAuthPath) -BaseDirectory $configDirectory
$startupHookPath = Expand-LauncherPath -Path ([string]$config.Injection.StartupHookPath) -BaseDirectory $configDirectory
$credentialTarget = [string]$config.Injection.CredentialTarget

if ($TestProfile -gt 0) {
    $testRoot = Join-Path ([IO.Path]::GetTempPath()) 'SOMS\test-clients'
    $dataDirectory = Join-Path $testRoot "profile-$TestProfile"
    # Refuse links even on the first launch, before creating or touching a profile.
    $ancestor = [IO.DirectoryInfo]::new($dataDirectory)
    while ($null -ne $ancestor) {
        if ($ancestor.Exists -and ($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Test profiles cannot use a directory link: $($ancestor.FullName)"
        }
        $ancestor = $ancestor.Parent
    }
    $marker = Join-Path $dataDirectory '.soms-test-profile'
    if (Test-Path -LiteralPath $dataDirectory) {
        if (-not (Test-Path -LiteralPath $marker -PathType Leaf) -or
            [IO.File]::ReadAllText($marker).Trim() -cne 'SOMS-TEST-PROFILE-1') {
            throw "Refusing to use an existing folder without a SOMS! test-profile marker: $dataDirectory"
        }
    }
    else {
        [IO.Directory]::CreateDirectory($dataDirectory) | Out-Null
        [IO.File]::WriteAllText($marker, 'SOMS-TEST-PROFILE-1')
        # Modest defaults make several clients usable on one display. Existing profiles keep their settings.
        [IO.File]::WriteAllText((Join-Path $dataDirectory 'framework.ini'), "WindowMode = Windowed`nWindowedSize = 960x640`nFrameSync = VSync`n")
        [IO.File]::WriteAllText((Join-Path $dataDirectory 'game.ini'), "VolumeUniversal = 0`nShowFirstRunSetup = False`n")
    }
    $hashAlgorithm = [Security.Cryptography.SHA256]::Create()
    try {
        $identity = $dataDirectory.TrimEnd('\').ToUpperInvariant() + '|' + [string]$config.Server.ApiUrl
        $identityHash = [BitConverter]::ToString($hashAlgorithm.ComputeHash([Text.Encoding]::UTF8.GetBytes($identity))).Replace('-', '').Substring(0, 24)
        $pipeHash = [BitConverter]::ToString($hashAlgorithm.ComputeHash([Text.Encoding]::UTF8.GetBytes($dataDirectory.TrimEnd('\').ToUpperInvariant()))).Replace('-', '').Substring(0, 24)
    }
    finally { $hashAlgorithm.Dispose() }
    $credentialTarget = "SomsTest/$identityHash"
}

if (-not (Test-Path -LiteralPath $dataDirectory -PathType Container)) {
    throw "Your existing osu! data directory was not found: $dataDirectory"
}
if ($credentialTarget -notmatch '^[A-Za-z0-9._/-]{3,200}$') {
    throw "Injection.CredentialTarget contains unsupported characters."
}

if ($TestProfile -eq 0) {
$storageIniPath = Join-Path -Path (Get-DefaultOsuDataDirectory) -ChildPath "storage.ini"
$redirectedDirectory = Get-StorageRedirectPath -StorageIniPath $storageIniPath
if (-not $redirectedDirectory.TrimEnd('\').Equals($dataDirectory.TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase)) {
    throw "The normal osu! installation currently uses '$redirectedDirectory', not '$dataDirectory'. Nothing was changed."
}

$clientRealmPath = Join-Path -Path $dataDirectory -ChildPath "client.realm"
$filesDirectory = Join-Path -Path $dataDirectory -ChildPath "files"
if (-not (Test-Path -LiteralPath $clientRealmPath -PathType Leaf) -or
    -not (Test-Path -LiteralPath $filesDirectory -PathType Container)) {
    throw "'$dataDirectory' is not the existing lazer library (client.realm/files are missing)."
}
}

$settings = [pscustomobject]@{
    Profile = [pscustomobject]@{
        StorageMode = "default"
        DataDirectory = Get-DefaultOsuDataDirectory
    }
    Osu = [pscustomobject]@{
        ExecutablePath = $executablePath
        CompatibleVersions = @($config.Osu.CompatibleVersions | ForEach-Object { [string]$_ })
        ExpectedPublisherSubjectContains = [string]$config.Osu.ExpectedPublisherSubjectContains
    }
    Server = [pscustomobject]@{
        ApiUrl = Get-NormalizedPrivateEndpoint -Value ([string]$config.Server.ApiUrl) -Name "Server.ApiUrl"
        WebsiteUrl = Get-NormalizedPrivateEndpoint -Value ([string]$config.Server.WebsiteUrl) -Name "Server.WebsiteUrl"
        SpectatorUrl = Get-NormalizedPrivateEndpoint -Value ([string]$config.Server.SpectatorUrl) -Name "Server.SpectatorUrl" -RequirePath
        MultiplayerUrl = Get-NormalizedPrivateEndpoint -Value ([string]$config.Server.MultiplayerUrl) -Name "Server.MultiplayerUrl" -RequirePath
        MetadataUrl = Get-NormalizedPrivateEndpoint -Value ([string]$config.Server.MetadataUrl) -Name "Server.MetadataUrl" -RequirePath
        BeatmapSubmissionServiceUrl = Get-NormalizedPrivateEndpoint -Value ([string]$config.Server.BeatmapSubmissionServiceUrl) -Name "Server.BeatmapSubmissionServiceUrl" -RequirePath
        ClientId = [string]$config.Server.ClientId
        ClientSecretEnvironmentVariable = [string]$config.Server.ClientSecretEnvironmentVariable
    }
}

$runningOsu = @(Get-RunningLazerIds)
if ($TestProfile -eq 0 -and $runningOsu.Count -gt 0) {
    throw "osu!lazer is already running. Close lazer and run this bat again. osu!stable can stay open."
}

$osuVersion = Assert-OsuExecutable -Settings $settings
Assert-SafeModule -Path $enhancedAuthPath -ExpectedSha256 ([string]$config.Injection.EnhancedAuthSha256) -Name "EnhancedAuth"
Assert-SafeModule -Path $startupHookPath -ExpectedSha256 ([string]$config.Injection.StartupHookSha256) -Name "Private startup hook"
if ($TestProfile -gt 0 -and -not [Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes($enhancedAuthPath)).Contains('SomsTestProfile')) {
    throw 'This EnhancedAuth build does not support isolated test profiles. Update the SOMS! client module first.'
}

if ($PrepareOnly) {
    if ($TestProfile -eq 0) { throw 'PrepareOnly requires a test profile.' }
    Write-Host "Ready: profile-$TestProfile | $dataDirectory | osu! $osuVersion"
    if ($PassThru) { [pscustomobject]@{ Profile = $TestProfile; Status = 'Prepared'; ProcessId = $null } }
    return
}

if ($TestProfile -gt 0) {
    # The native game treats dotted launch arguments as import paths if its IPC
    # is already bound. Skip an open profile before passing any arguments to it.
    $profileMutex = $null
    if ([Threading.Mutex]::TryOpenExisting("Global\osu-framework-soms-test-$pipeHash", [ref]$profileMutex)) {
        $profileMutex.Dispose()
        Write-Host "profile-$TestProfile is already open; skipped." -ForegroundColor Cyan
        if ($PassThru) { [pscustomobject]@{ Profile = $TestProfile; Status = 'AlreadyRunning'; ProcessId = $null } }
        return
    }
    $processRecordPath = Join-Path $dataDirectory 'launcher-process.json'
    if (Test-Path -LiteralPath $processRecordPath -PathType Leaf) {
        $record = [IO.File]::ReadAllText($processRecordPath) | ConvertFrom-Json
        $existingProcess = Get-Process -Id ([int]$record.Id) -ErrorAction SilentlyContinue
        if ($null -ne $existingProcess -and $existingProcess.ProcessName -eq 'osu!' -and $existingProcess.StartTime.Ticks -eq [long]$record.StartTimeTicks) {
            Write-Host "profile-$TestProfile is already starting; skipped." -ForegroundColor Cyan
            if ($PassThru) { [pscustomobject]@{ Profile = $TestProfile; Status = 'AlreadyRunning'; ProcessId = $existingProcess.Id } }
            return
        }
    }
}

$arguments = Get-PrivateOsuArguments -Settings $settings
$sanitizedArguments = Get-SanitizedPrivateOsuArguments -Arguments $arguments

$environmentNames = @(
    "DOTNET_STARTUP_HOOKS",
    "PRIVATE_OSU_ENHANCED_AUTH_PATH",
    "PRIVATE_OSU_CREDENTIAL_TARGET",
    "PRIVATE_OSU_TEST_PROFILE_DIR"
)
$previousEnvironment = @{}
foreach ($name in $environmentNames) {
    $previousEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, [EnvironmentVariableTarget]::Process)
}

try {
    [Environment]::SetEnvironmentVariable("DOTNET_STARTUP_HOOKS", $startupHookPath, [EnvironmentVariableTarget]::Process)
    [Environment]::SetEnvironmentVariable("PRIVATE_OSU_ENHANCED_AUTH_PATH", $enhancedAuthPath, [EnvironmentVariableTarget]::Process)
    [Environment]::SetEnvironmentVariable("PRIVATE_OSU_CREDENTIAL_TARGET", $credentialTarget, [EnvironmentVariableTarget]::Process)
    $testPath = if ($TestProfile -gt 0) { $dataDirectory } else { $null }
    [Environment]::SetEnvironmentVariable("PRIVATE_OSU_TEST_PROFILE_DIR", $testPath, [EnvironmentVariableTarget]::Process)

    Write-Host "Starting signed osu! $osuVersion in private-server mode." -ForegroundColor Cyan
    Write-Host "Data: $dataDirectory"
    Write-Host "Server: $($settings.Server.ApiUrl)"
    Write-Host "Arguments: $($sanitizedArguments -join ' ')"
    if ($TestProfile -gt 0) { Write-Host "SOMS! test profile $TestProfile. Sign in with a separate SOMS! account in this window." -ForegroundColor Cyan }
    else { Write-Warning "Use only your PRIVATE-server username/password in this launch. Your official saved login is kept separate." }

    $process = Start-Process -FilePath $executablePath `
        -ArgumentList $arguments `
        -WorkingDirectory (Split-Path -Parent $executablePath) `
        -WindowStyle Normal `
        -PassThru
    if ($TestProfile -gt 0) {
        $processRecord = @{ Id = $process.Id; StartTimeTicks = $process.StartTime.Ticks } | ConvertTo-Json -Compress
        [IO.File]::WriteAllText($processRecordPath, $processRecord)
        $cleanupScript = Join-Path $PSScriptRoot 'Cleanup-TestProfile.ps1'
        $cleanupArguments = @(
            '-NoLogo', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$cleanupScript`"",
            '-ProcessId', [string]$process.Id, '-StartTimeTicks', [string]$process.StartTime.Ticks,
            '-ProfileDirectory', "`"$dataDirectory`""
        )
        Start-Process -FilePath 'powershell.exe' -ArgumentList $cleanupArguments -WindowStyle Hidden | Out-Null
    }
}
finally {
    foreach ($name in $environmentNames) {
        [Environment]::SetEnvironmentVariable($name, $previousEnvironment[$name], [EnvironmentVariableTarget]::Process)
    }
}

if ($Wait) {
    $process.WaitForExit()
    exit $process.ExitCode
}

if ($TestProfile -gt 0) {
    Write-Host "Waiting for profile-$TestProfile to reach its menu..."
    Wait-TestClientStartup -Process $process -ProfileDirectory $dataDirectory
    Write-Host "profile-$TestProfile is ready (PID $($process.Id))." -ForegroundColor Green
    if ($PassThru) { [pscustomobject]@{ Profile = $TestProfile; Status = 'Started'; ProcessId = $process.Id } }
    return
}
Write-Host "osu! started (PID $($process.Id)). The window may take a few seconds to appear."
