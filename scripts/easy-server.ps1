[CmdletBinding()]
param(
    [ValidateSet("Start", "Setup", "Status", "Logs", "Stop", "Owner")]
    [string]$Action = "Start",

    [string]$Username = "",

    [string]$Domain = "",

    [string]$FetcherClientId = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
if (Get-Variable -Name PSNativeCommandUseErrorActionPreference -ErrorAction SilentlyContinue) {
    $PSNativeCommandUseErrorActionPreference = $false
}
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$WorkspaceDirectory = Split-Path -Parent $PSScriptRoot
$ServerDirectory = Join-Path $WorkspaceDirectory "server"
$EnvironmentTemplatePath = Join-Path $ServerDirectory ".env.example"
$EnvironmentPath = Join-Path $ServerDirectory ".env"
$BaseComposePath = Join-Path $ServerDirectory "docker-compose.yml"
$EasyComposePath = Join-Path $ServerDirectory "docker-compose.easy.yml"
$LauncherTemplatePath = Join-Path $WorkspaceDirectory "launcher\launcher.example.json"
$LauncherConfigurationPath = Join-Path $WorkspaceDirectory "launcher\launcher.json"
$SharedLauncherConfigurationPath = Join-Path $WorkspaceDirectory "launcher\shared-launcher.json"
$ComposeProjectName = "friends-private-osu"
$FetcherSecretEnvironmentVariable = "PRIVATE_OSU_FETCHER_CLIENT_SECRET"
$script:DockerExecutable = $null
$script:CommandExitCode = 0

function Invoke-DockerCaptured {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments,

        [ValidateSet("Stdout", "All", "None")]
        [string]$OutputMode = "Stdout"
    )

    # Windows PowerShell 5.1 converts redirected native stderr into error records.
    # With the script-wide Stop policy, harmless Docker diagnostics would otherwise
    # terminate the wizard before we can inspect Docker's real exit code.
    $previousErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = "Continue"
        switch ($OutputMode) {
            "All" {
                $output = @(& $script:DockerExecutable @Arguments 2>&1)
            }
            "None" {
                & $script:DockerExecutable @Arguments *> $null
                $output = @()
            }
            default {
                $output = @(& $script:DockerExecutable @Arguments 2>$null)
            }
        }
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }

    return [PSCustomObject]@{
        ExitCode = $exitCode
        Output = @($output)
    }
}

function Write-Step {
    param([string]$Message)
    Write-Host "[private-osu] $Message" -ForegroundColor Cyan
}

function Write-WarningMessage {
    param([string]$Message)
    Write-Host "[warning] $Message" -ForegroundColor Yellow
}

function Write-Utf8WithoutBom {
    param(
        [string]$Path,
        [string]$Text
    )

    $encoding = New-Object System.Text.UTF8Encoding($false)
    [IO.File]::WriteAllText($Path, $Text, $encoding)
}

function Install-AtomicFile {
    param(
        [string]$TemporaryPath,
        [string]$DestinationPath
    )

    # Both paths are in the same directory. Move-Item therefore replaces the
    # old file with the fully-written temporary file without relying on
    # File.Replace, whose backup-path overload is broken in Windows PowerShell
    # 5.1 on some Windows installations.
    Move-Item -LiteralPath $TemporaryPath -Destination $DestinationPath -Force
}

function Set-PrivateFileAcl {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Cannot protect missing private file: $Path"
    }

    $currentIdentity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $currentSid = $currentIdentity.User
    if ($null -eq $currentSid) {
        throw "Windows did not report the current user's security identifier."
    }

    # Build the DACL from scratch so permissions inherited from the workspace
    # cannot expose OAuth, database, or session secrets to other local users.
    $security = [IO.File]::GetAccessControl(
        $Path,
        [Security.AccessControl.AccessControlSections]::Access
    )
    $security.SetAccessRuleProtection($true, $false)
    foreach ($existingRule in @($security.GetAccessRules(
        $true,
        $true,
        [Security.Principal.SecurityIdentifier]
    ))) {
        $security.RemoveAccessRuleSpecific($existingRule)
    }
    $fullControl = [Security.AccessControl.FileSystemRights]::FullControl
    $inheritance = [Security.AccessControl.InheritanceFlags]::None
    $propagation = [Security.AccessControl.PropagationFlags]::None
    $allow = [Security.AccessControl.AccessControlType]::Allow
    $sidValues = @(
        $currentSid.Value,
        "S-1-5-18",       # Local System
        "S-1-5-32-544"    # Built-in Administrators
    ) | Select-Object -Unique

    foreach ($sidValue in $sidValues) {
        $sid = New-Object Security.Principal.SecurityIdentifier($sidValue)
        $rule = New-Object Security.AccessControl.FileSystemAccessRule(
            $sid,
            $fullControl,
            $inheritance,
            $propagation,
            $allow
        )
        [void]$security.AddAccessRule($rule)
    }

    [IO.File]::SetAccessControl($Path, $security)
}

function Get-RandomHex {
    param([int]$ByteCount = 32)

    $bytes = New-Object byte[] $ByteCount
    $generator = [Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $generator.GetBytes($bytes)
    }
    finally {
        $generator.Dispose()
    }

    return (($bytes | ForEach-Object { $_.ToString("x2") }) -join "")
}

function Test-DatabaseVolumeExists {
    # docker-compose.yml gives the database volume this stable explicit name so
    # the one-click project can also reuse data from the repository's old
    # default Compose project name ("server").
    $result = Invoke-DockerCaptured -Arguments @("volume", "inspect", "server_mysql_data") -OutputMode None
    return $result.ExitCode -eq 0
}

function ConvertFrom-SecureValue {
    param([Security.SecureString]$SecureValue)

    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($SecureValue)
    try {
        return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
    }
    finally {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer)
    }
}

function Get-EnvironmentValues {
    param([string]$Path = $EnvironmentPath)

    $values = @{}
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return $values
    }

    if ([IO.Path]::GetFullPath($Path) -eq [IO.Path]::GetFullPath($EnvironmentPath)) {
        Set-PrivateFileAcl -Path $Path
    }

    foreach ($line in [IO.File]::ReadAllLines($Path)) {
        if ($line -notmatch '^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.*?)\s*$') {
            continue
        }

        $name = $Matches[1]
        $value = $Matches[2].Trim()
        if ($value.Length -ge 2) {
            if (($value[0] -eq '"' -and $value[$value.Length - 1] -eq '"') -or
                ($value[0] -eq "'" -and $value[$value.Length - 1] -eq "'")) {
                $value = $value.Substring(1, $value.Length - 2)
            }
        }
        $values[$name] = $value
    }

    return $values
}

function Test-EnvironmentIsUnconfiguredTemplate {
    if (-not (Test-Path -LiteralPath $EnvironmentPath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $EnvironmentTemplatePath -PathType Leaf)) {
        return $false
    }

    $currentValues = Get-EnvironmentValues $EnvironmentPath
    $templateValues = Get-EnvironmentValues $EnvironmentTemplatePath
    if ($currentValues.Count -ne $templateValues.Count) {
        return $false
    }
    foreach ($name in $templateValues.Keys) {
        if (-not $currentValues.ContainsKey($name) -or
            [string]$currentValues[$name] -cne [string]$templateValues[$name]) {
            return $false
        }
    }
    return $true
}

function Set-EnvironmentValues {
    param([hashtable]$Values)

    foreach ($name in $Values.Keys) {
        $value = [string]$Values[$name]
        if ($value -notmatch '^[A-Za-z0-9._~:/\[\],{}@+-]*$') {
            throw "Value for $name contains characters which are unsafe in this generated .env file."
        }
    }

    $lines = [Collections.Generic.List[string]]::new()
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $sourcePath = if (Test-Path -LiteralPath $EnvironmentPath -PathType Leaf) {
        $EnvironmentPath
    }
    else {
        $EnvironmentTemplatePath
    }
    foreach ($line in [IO.File]::ReadAllLines($sourcePath)) {
        if ($line -match '^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=') {
            $name = $Matches[1]
            if ($Values.ContainsKey($name)) {
                $lines.Add(('{0}="{1}"' -f $name, [string]$Values[$name]))
                [void]$seen.Add($name)
                continue
            }
        }
        $lines.Add($line)
    }

    foreach ($name in ($Values.Keys | Sort-Object)) {
        if ($seen.Contains([string]$name)) {
            continue
        }
        if ($lines.Count -gt 0 -and $lines[$lines.Count - 1] -ne "") {
            $lines.Add("")
        }
        $lines.Add(('{0}="{1}"' -f $name, [string]$Values[$name]))
    }

    $temporaryPath = "$EnvironmentPath.$PID.$([Guid]::NewGuid().ToString('N')).tmp"
    try {
        Write-Utf8WithoutBom -Path $temporaryPath -Text (($lines -join [Environment]::NewLine) + [Environment]::NewLine)
        Set-PrivateFileAcl -Path $temporaryPath
        Install-AtomicFile -TemporaryPath $temporaryPath -DestinationPath $EnvironmentPath
        Set-PrivateFileAcl -Path $EnvironmentPath
    }
    finally {
        if (Test-Path -LiteralPath $temporaryPath -PathType Leaf) {
            Remove-Item -LiteralPath $temporaryPath -Force
        }
    }
}

function Test-Placeholder {
    param([AllowNull()][string]$Value)

    return [string]::IsNullOrWhiteSpace($Value) -or
        $Value -match '(?i)replace_with|private-osu\.example|lazer\.example\.net'
}

function Get-FixedEnvironmentValues {
    return @{
        "MYSQL_DATABASE" = "osu_api"
        "MYSQL_USER" = "osu_api"
        "DEBUG" = "false"
        "JWT_AUDIENCE" = "5"
        "OSU_CLIENT_ID" = "5"
        "OSU_WEB_CLIENT_ID" = "6"
        "ENABLE_ALL_BEATMAP_LEADERBOARD" = "false"
        "ENABLE_ALL_BEATMAP_PP" = "false"
        "ENABLE_AUTO_BEATMAP_SYNC" = "true"
        "ENABLE_RATE_LIMIT" = "true"
        "SCORING_MODE" = "standardised"
    }
}

function Test-PublicDomain {
    param([AllowNull()][string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value) -or $Value.Length -gt 253) {
        return $false
    }
    if ($Value -ne $Value.Trim().TrimEnd('.').ToLowerInvariant()) {
        return $false
    }
    if ([Uri]::CheckHostName($Value) -ne [UriHostNameType]::Dns -or -not $Value.Contains('.')) {
        return $false
    }
    if ($Value -notmatch '^[a-z0-9](?:[a-z0-9.-]*[a-z0-9])$' -or $Value.Contains('..')) {
        return $false
    }
    foreach ($label in $Value.Split('.')) {
        if ($label.Length -lt 1 -or $label.Length -gt 63 -or
            $label -notmatch '^[a-z0-9](?:[a-z0-9-]*[a-z0-9])?$') {
            return $false
        }
    }
    if ($Value -match '(?i)(^|\.)ppy\.sh$' -or $Value -match '(?i)\.(example|invalid|localhost|local|test)$') {
        return $false
    }

    return $true
}

function Read-PublicDomain {
    param(
        [AllowNull()][string]$CurrentValue,
        [switch]$PromptForExisting
    )

    if (-not [string]::IsNullOrWhiteSpace($Domain)) {
        $candidate = $Domain.Trim().TrimEnd('.').ToLowerInvariant()
        if (-not (Test-PublicDomain $candidate)) {
            throw "-Domain must be a public DNS hostname without https://, a port, or a path."
        }
        return $candidate
    }

    if ((Test-PublicDomain $CurrentValue) -and -not $PromptForExisting) {
        return $CurrentValue
    }

    while ($true) {
        $prompt = if (Test-PublicDomain $CurrentValue) {
            "Public domain (press Enter to keep $CurrentValue)"
        }
        else {
            "Public domain (for example lazer.your-domain.com)"
        }
        $candidate = (Read-Host $prompt).Trim().TrimEnd('.').ToLowerInvariant()
        if ([string]::IsNullOrWhiteSpace($candidate) -and (Test-PublicDomain $CurrentValue)) {
            return $CurrentValue
        }
        if (Test-PublicDomain $candidate) {
            return $candidate
        }
        Write-WarningMessage "Enter a real public hostname only, without https://, a port, or a path."
    }
}

function Read-FetcherClientId {
    param(
        [AllowNull()][string]$CurrentValue,
        [switch]$PromptForExisting
    )

    if (-not [string]::IsNullOrWhiteSpace($FetcherClientId)) {
        $candidate = $FetcherClientId.Trim()
    }
    elseif (-not (Test-Placeholder $CurrentValue) -and $CurrentValue -match '^[1-9][0-9]*$' -and
        -not $PromptForExisting) {
        return $CurrentValue
    }
    else {
        $prompt = if ($CurrentValue -match '^[1-9][0-9]*$') {
            "Official osu! OAuth client ID (press Enter to keep $CurrentValue)"
        }
        else {
            "Official osu! OAuth application client ID"
        }
        $candidate = (Read-Host $prompt).Trim()
        if ([string]::IsNullOrWhiteSpace($candidate) -and $CurrentValue -match '^[1-9][0-9]*$') {
            return $CurrentValue
        }
    }

    while ($candidate -notmatch '^[1-9][0-9]*$') {
        Write-WarningMessage "The official osu! OAuth client ID must contain digits only."
        $candidate = (Read-Host "Official osu! OAuth application client ID").Trim()
    }
    return $candidate
}

function Read-FetcherClientSecret {
    param(
        [AllowNull()][string]$CurrentValue,
        [switch]$PromptForExisting
    )

    $environmentSecret = [Environment]::GetEnvironmentVariable(
        $FetcherSecretEnvironmentVariable,
        [EnvironmentVariableTarget]::Process
    )
    [Environment]::SetEnvironmentVariable(
        $FetcherSecretEnvironmentVariable,
        $null,
        [EnvironmentVariableTarget]::Process
    )

    $currentIsValid = -not (Test-Placeholder $CurrentValue) -and
        $CurrentValue -match '^[A-Za-z0-9._~+-]{8,256}$'
    if ($currentIsValid -and -not $PromptForExisting) {
        return $CurrentValue
    }
    if (-not [string]::IsNullOrWhiteSpace($environmentSecret)) {
        if ($environmentSecret -notmatch '^[A-Za-z0-9._~+-]{8,256}$') {
            throw "$FetcherSecretEnvironmentVariable contains an invalid OAuth secret."
        }
        return $environmentSecret
    }
    if ($currentIsValid) {
        if (-not $PromptForExisting) {
            return $CurrentValue
        }
        $replace = (Read-Host "Replace the saved official osu! OAuth secret? [y/N]").Trim()
        if ($replace -notmatch '^(?i:y|yes)$') {
            return $CurrentValue
        }
    }

    while ($true) {
        Write-Step "Copy the complete Client Secret on the osu! website. Do NOT paste it into this window."
        [void](Read-Host "After copying it, press Enter here")
        try {
            $candidate = ([string](Get-Clipboard -Raw)).Trim()
        }
        catch {
            throw "Could not read the Windows clipboard. Copy the OAuth secret, then run START_SERVER.bat again."
        }
        if ($candidate -match '^[A-Za-z0-9._~+-]{8,256}$') {
            try {
                Set-Clipboard -Value ""
            }
            catch {
                # The secret is already in memory; clipboard cleanup is best-effort.
            }
            return $candidate
        }
        Write-WarningMessage "The clipboard does not contain a complete OAuth secret. Copy it again using the website's copy button."
    }
}

function Assert-FetcherCredentials {
    param(
        [string]$ClientId,
        [string]$ClientSecret
    )

    Write-Step "Checking the official osu! OAuth credentials without printing them..."
    $payload = @{
        client_id = [Int64]$ClientId
        client_secret = $ClientSecret
        grant_type = "client_credentials"
        scope = "public"
    }

    try {
        for ($attempt = 1; $attempt -le 3; $attempt++) {
            try {
                $response = Invoke-RestMethod `
                    -Uri "https://osu.ppy.sh/oauth/token" `
                    -Method Post `
                    -Headers @{ Accept = "application/json" } `
                    -ContentType "application/x-www-form-urlencoded" `
                    -Body $payload `
                    -TimeoutSec 20
                if (-not [string]::IsNullOrWhiteSpace([string]$response.access_token)) {
                    Write-Step "Official osu! OAuth credentials accepted."
                    return
                }
            }
            catch {
                $statusCode = $null
                if ($null -ne $_.Exception.Response) {
                    try {
                        $statusCode = [int]$_.Exception.Response.StatusCode
                    }
                    catch {
                        $statusCode = $null
                    }
                }
                if ($statusCode -in @(400, 401, 403)) {
                    throw "The official osu! API rejected the client ID/secret. Check the OAuth application and run Setup again."
                }
            }

            if ($attempt -lt 3) {
                Start-Sleep -Seconds 2
            }
        }
    }
    finally {
        $payload = $null
        $ClientSecret = ""
    }

    throw "Could not validate the official osu! OAuth credentials after three attempts. Check internet access and try again."
}

function Test-LauncherConfigurationShape {
    param([AllowNull()]$Settings)

    if ($null -eq $Settings) {
        return $false
    }
    $serverProperty = $Settings.PSObject.Properties["Server"]
    if ($null -eq $serverProperty -or $null -eq $serverProperty.Value) {
        return $false
    }
    foreach ($propertyName in @(
        "ApiUrl",
        "WebsiteUrl",
        "SpectatorUrl",
        "MultiplayerUrl",
        "MetadataUrl",
        "BeatmapSubmissionServiceUrl",
        "ClientId",
        "ClientSecretEnvironmentVariable"
    )) {
        if ($null -eq $serverProperty.Value.PSObject.Properties[$propertyName]) {
            return $false
        }
    }
    return $true
}

function Sync-LauncherConfiguration {
    param(
        [string]$ServerDomain,
        [switch]$AllowOriginChange
    )

    if (-not (Test-Path -LiteralPath $LauncherTemplatePath -PathType Leaf)) {
        throw "launcher\launcher.example.json is missing. Restore the workspace before starting."
    }

    try {
        $templateSettings = Get-Content -LiteralPath $LauncherTemplatePath -Raw -Encoding UTF8 | ConvertFrom-Json
    }
    catch {
        throw "launcher\launcher.example.json is invalid. Restore the workspace before starting."
    }
    if (-not (Test-LauncherConfigurationShape $templateSettings)) {
        throw "launcher\launcher.example.json has an unsupported structure. Restore the workspace before starting."
    }

    $launcherExists = Test-Path -LiteralPath $LauncherConfigurationPath -PathType Leaf
    if ($launcherExists) {
        try {
            $settings = Get-Content -LiteralPath $LauncherConfigurationPath -Raw -Encoding UTF8 | ConvertFrom-Json
        }
        catch {
            if (-not $AllowOriginChange) {
                throw "launcher\launcher.json is invalid. Run START_SERVER.bat Setup to back it up and regenerate it."
            }
            $backupName = "launcher.json.invalid-$((Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss')).bak"
            $backupPath = Join-Path (Split-Path -Parent $LauncherConfigurationPath) $backupName
            Copy-Item -LiteralPath $LauncherConfigurationPath -Destination $backupPath
            Write-WarningMessage "Malformed launcher.json was backed up as $backupName and will be regenerated."
            $settings = $templateSettings
        }
    }
    else {
        $settings = $templateSettings
    }

    if (-not (Test-LauncherConfigurationShape $settings)) {
        if (-not $AllowOriginChange) {
            throw "launcher\launcher.json has an unsupported structure. Run START_SERVER.bat Setup to back it up and regenerate it."
        }
        if ($launcherExists) {
            $backupName = "launcher.json.invalid-$((Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss')).bak"
            $backupPath = Join-Path (Split-Path -Parent $LauncherConfigurationPath) $backupName
            Copy-Item -LiteralPath $LauncherConfigurationPath -Destination $backupPath
            Write-WarningMessage "Unsupported launcher.json was backed up as $backupName and will be regenerated."
        }
        $settings = $templateSettings
    }

    $origin = "https://$ServerDomain"
    $existingOrigin = [string]$settings.Server.ApiUrl
    if (-not [string]::IsNullOrWhiteSpace($existingOrigin) -and
        $existingOrigin -notmatch '(?i)private-osu\.example' -and
        $existingOrigin.TrimEnd('/') -ne $origin) {
        if (-not $AllowOriginChange) {
            throw "launcher\launcher.json targets another server. Run START_SERVER.bat Setup to change it deliberately."
        }
        Write-WarningMessage "Changing the launcher origin can require a new empty private osu! Windows profile."
    }

    $settings.Server.ApiUrl = $origin
    $settings.Server.WebsiteUrl = $origin
    $settings.Server.SpectatorUrl = "$origin/signalr/spectator"
    $settings.Server.MultiplayerUrl = "$origin/signalr/multiplayer"
    $settings.Server.MetadataUrl = "$origin/signalr/metadata"
    $settings.Server.BeatmapSubmissionServiceUrl = "$origin/beatmap-submission"
    $settings.Server.ClientId = "5"

    $json = $settings | ConvertTo-Json -Depth 20
    $temporaryPath = "$LauncherConfigurationPath.$PID.$([Guid]::NewGuid().ToString('N')).tmp"
    try {
        Write-Utf8WithoutBom -Path $temporaryPath -Text ($json + [Environment]::NewLine)
        Install-AtomicFile -TemporaryPath $temporaryPath -DestinationPath $LauncherConfigurationPath
    }
    finally {
        if (Test-Path -LiteralPath $temporaryPath -PathType Leaf) {
            Remove-Item -LiteralPath $temporaryPath -Force
        }
    }
    if (Test-Path -LiteralPath $SharedLauncherConfigurationPath -PathType Leaf) {
        try {
            $sharedSettings = Get-Content -LiteralPath $SharedLauncherConfigurationPath -Raw -Encoding UTF8 | ConvertFrom-Json
        }
        catch {
            throw "launcher\shared-launcher.json is invalid. Restore it before starting the server."
        }

        if ($null -eq $sharedSettings.Server -or $null -eq $sharedSettings.Injection) {
            throw "launcher\shared-launcher.json has an unsupported structure."
        }

        $sharedSettings.Server.ApiUrl = $origin
        $sharedSettings.Server.WebsiteUrl = $origin
        $sharedSettings.Server.SpectatorUrl = "$origin/signalr/spectator"
        $sharedSettings.Server.MultiplayerUrl = "$origin/signalr/multiplayer"
        $sharedSettings.Server.MetadataUrl = "$origin/signalr/metadata"
        $sharedSettings.Server.BeatmapSubmissionServiceUrl = "$origin/beatmap-submission"
        $sharedSettings.Server.ClientId = "5"
        $sharedSettings.Injection.CredentialTarget = "FriendsPrivateOsu/$ServerDomain"

        $sharedJson = $sharedSettings | ConvertTo-Json -Depth 20
        $sharedTemporaryPath = "$SharedLauncherConfigurationPath.$PID.$([Guid]::NewGuid().ToString('N')).tmp"
        try {
            Write-Utf8WithoutBom -Path $sharedTemporaryPath -Text ($sharedJson + [Environment]::NewLine)
            Install-AtomicFile -TemporaryPath $sharedTemporaryPath -DestinationPath $SharedLauncherConfigurationPath
        }
        finally {
            if (Test-Path -LiteralPath $sharedTemporaryPath -PathType Leaf) {
                Remove-Item -LiteralPath $sharedTemporaryPath -Force
            }
        }
    }

    Write-Step "Client endpoint configuration was written to both launchers."
}

function Initialize-Environment {
    param([switch]$Reconfigure)

    if (-not (Test-Path -LiteralPath $EnvironmentTemplatePath -PathType Leaf)) {
        throw "Missing server\.env.example. The workspace is incomplete."
    }
    $isNewEnvironment = -not (Test-Path -LiteralPath $EnvironmentPath -PathType Leaf)
    $values = Get-EnvironmentValues $(if ($isNewEnvironment) { $EnvironmentTemplatePath } else { $EnvironmentPath })
    $currentDomain = if ($values.ContainsKey("SERVER_DOMAIN")) { [string]$values["SERVER_DOMAIN"] } else { "" }
    if (-not (Test-PublicDomain $currentDomain) -and $values.ContainsKey("SERVER_URL")) {
        try {
            $uri = [Uri]([string]$values["SERVER_URL"])
            if ($uri.Scheme -eq "https" -and $uri.IsDefaultPort -and $uri.AbsolutePath -eq "/") {
                $currentDomain = $uri.DnsSafeHost.ToLowerInvariant()
            }
        }
        catch {
            $currentDomain = ""
        }
    }

    $serverDomain = Read-PublicDomain $currentDomain -PromptForExisting:$Reconfigure
    $currentFetcherId = if ($values.ContainsKey("FETCHER_CLIENT_ID")) { [string]$values["FETCHER_CLIENT_ID"] } else { "" }
    $currentFetcherSecret = if ($values.ContainsKey("FETCHER_CLIENT_SECRET")) { [string]$values["FETCHER_CLIENT_SECRET"] } else { "" }
    if ((Test-Placeholder $currentFetcherId) -or (Test-Placeholder $currentFetcherSecret)) {
        Write-Step "Create an official osu! OAuth application at https://osu.ppy.sh/home/account/edit#oauth if you do not have one."
        Write-Step "Its callback URL can be https://$serverDomain/. Never enter your osu! account password here."
    }
    $fetcherId = Read-FetcherClientId $currentFetcherId -PromptForExisting:$Reconfigure
    $fetcherSecret = Read-FetcherClientSecret $currentFetcherSecret -PromptForExisting:$Reconfigure
    Assert-FetcherCredentials -ClientId $fetcherId -ClientSecret $fetcherSecret

    $updates = @{
        "SERVER_DOMAIN" = $serverDomain
        "SERVER_URL" = "https://$serverDomain/"
        "FETCHER_CLIENT_ID" = $fetcherId
        "FETCHER_CLIENT_SECRET" = $fetcherSecret
    }

    $databaseVolumeExists = Test-DatabaseVolumeExists
    $usedSecrets = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $secretSpecifications = @(
        @{ Name = "MYSQL_PASSWORD"; Pattern = '^[A-Za-z0-9._~+-]{32,256}$'; Database = $true },
        @{ Name = "MYSQL_ROOT_PASSWORD"; Pattern = '^[A-Za-z0-9._~+-]{32,256}$'; Database = $true },
        @{ Name = "JWT_SECRET_KEY"; Pattern = '^[A-Za-z0-9._~+-]{32,256}$'; Database = $false },
        @{ Name = "OSU_CLIENT_SECRET"; Pattern = '^[A-Za-z0-9._~-]{32,256}$'; Database = $false },
        @{ Name = "OSU_WEB_CLIENT_SECRET"; Pattern = '^[A-Za-z0-9._~+-]{32,256}$'; Database = $false },
        @{ Name = "SHARED_INTEROP_SECRET"; Pattern = '^[A-Za-z0-9._~+-]{32,256}$'; Database = $false }
    )
    foreach ($specification in $secretSpecifications) {
        $name = [string]$specification.Name
        $existing = if ($values.ContainsKey($name)) { [string]$values[$name] } else { "" }
        $isValid = -not (Test-Placeholder $existing) -and $existing -match [string]$specification.Pattern
        $isUnique = $isValid -and $usedSecrets.Add($existing)
        if ($isValid -and $isUnique) {
            continue
        }
        if ([bool]$specification.Database -and $databaseVolumeExists) {
            throw "The existing MySQL volume needs its original strong $name. Restore the old server\.env; never guess or regenerate a database password."
        }

        do {
            $replacement = Get-RandomHex 32
        } while (-not $usedSecrets.Add($replacement))
        $updates[$name] = $replacement
    }

    $fixedValues = Get-FixedEnvironmentValues
    foreach ($name in $fixedValues.Keys) {
        $updates[$name] = $fixedValues[$name]
    }

    Set-EnvironmentValues $updates
    if ($isNewEnvironment) {
        Write-Step "Created server\.env atomically from the safe template."
    }
    Sync-LauncherConfiguration $serverDomain -AllowOriginChange:$Reconfigure
    Write-Step "First-run configuration is ready. Secrets were saved only in server\.env and were not printed."
}

function Assert-EnvironmentReady {
    if (-not (Test-Path -LiteralPath $EnvironmentPath -PathType Leaf)) {
        throw "server\.env does not exist. Run START_SERVER.bat once to create it."
    }

    $values = Get-EnvironmentValues
    $required = @(
        "SERVER_DOMAIN",
        "SERVER_URL",
        "MYSQL_DATABASE",
        "MYSQL_USER",
        "MYSQL_PASSWORD",
        "MYSQL_ROOT_PASSWORD",
        "JWT_SECRET_KEY",
        "OSU_CLIENT_SECRET",
        "OSU_WEB_CLIENT_SECRET",
        "FETCHER_CLIENT_ID",
        "FETCHER_CLIENT_SECRET",
        "SHARED_INTEROP_SECRET"
    )
    foreach ($name in $required) {
        if (-not $values.ContainsKey($name) -or (Test-Placeholder ([string]$values[$name]))) {
            throw "server\.env is incomplete at $name. Run START_SERVER.bat Setup."
        }
    }

    $domain = [string]$values["SERVER_DOMAIN"]
    if (-not (Test-PublicDomain $domain)) {
        throw "SERVER_DOMAIN is not a valid public DNS hostname. Run START_SERVER.bat Setup."
    }
    if ([string]$values["SERVER_URL"] -ne "https://$domain/") {
        throw "SERVER_URL must exactly match https://SERVER_DOMAIN/. Run START_SERVER.bat Setup."
    }
    if ([string]$values["FETCHER_CLIENT_ID"] -notmatch '^[1-9][0-9]*$') {
        throw "FETCHER_CLIENT_ID must contain digits only. Run START_SERVER.bat Setup."
    }
    if ([string]$values["FETCHER_CLIENT_SECRET"] -notmatch '^[A-Za-z0-9._~+-]{8,256}$') {
        throw "FETCHER_CLIENT_SECRET contains unsupported characters. Run START_SERVER.bat Setup."
    }

    $fixedValues = Get-FixedEnvironmentValues
    foreach ($name in $fixedValues.Keys) {
        $expected = [string]$fixedValues[$name]
        if (-not $values.ContainsKey($name) -or [string]$values[$name] -cne $expected) {
            throw "$name must be '$expected' in one-click mode. Run START_SERVER.bat Setup."
        }
    }

    $localSecretPatterns = @{
        "MYSQL_PASSWORD" = '^[A-Za-z0-9._~+-]{32,256}$'
        "MYSQL_ROOT_PASSWORD" = '^[A-Za-z0-9._~+-]{32,256}$'
        "JWT_SECRET_KEY" = '^[A-Za-z0-9._~+-]{32,256}$'
        "OSU_CLIENT_SECRET" = '^[A-Za-z0-9._~-]{32,256}$'
        "OSU_WEB_CLIENT_SECRET" = '^[A-Za-z0-9._~+-]{32,256}$'
        "SHARED_INTEROP_SECRET" = '^[A-Za-z0-9._~+-]{32,256}$'
    }
    $uniqueSecrets = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($name in $localSecretPatterns.Keys) {
        $value = [string]$values[$name]
        if ($value -notmatch $localSecretPatterns[$name]) {
            throw "$name must be at least 32 safe ASCII characters. Run START_SERVER.bat Setup."
        }
        if (-not $uniqueSecrets.Add($value)) {
            throw "MYSQL/JWT/private-client/interoperability secrets must all be different. Fix server\.env before starting."
        }
    }

    return $values
}

function Get-DockerEngineType {
    $result = Invoke-DockerCaptured -Arguments @("info", "--format", "{{.OSType}}")
    if ($result.ExitCode -ne 0) {
        return ""
    }
    return ([string]($result.Output -join "`n")).Trim().ToLowerInvariant()
}

function Assert-LocalDockerContext {
    $hostOverride = [Environment]::GetEnvironmentVariable("DOCKER_HOST", [EnvironmentVariableTarget]::Process)
    if (-not [string]::IsNullOrWhiteSpace($hostOverride) -and $hostOverride -notmatch '^(?i)npipe://') {
        throw "DOCKER_HOST points to a remote or unsupported Docker engine. One-click mode only sends secrets to local Docker Desktop."
    }

    $contextResult = Invoke-DockerCaptured -Arguments @("context", "show")
    if ($contextResult.ExitCode -ne 0) {
        throw "Could not inspect the active Docker context. Update Docker Desktop and try again."
    }
    $contextName = [string]($contextResult.Output -join "`n").Trim()
    if ([string]::IsNullOrWhiteSpace($contextName)) {
        throw "Docker did not report an active context. Select a local Docker Desktop context and try again."
    }

    $inspectResult = Invoke-DockerCaptured -Arguments @("context", "inspect", $contextName)
    if ($inspectResult.ExitCode -ne 0) {
        throw "Could not inspect Docker context '$contextName'."
    }
    try {
        $contextDetails = ($inspectResult.Output -join "`n") | ConvertFrom-Json
        $endpoint = [string]$contextDetails.Endpoints.docker.Host
    }
    catch {
        throw "Docker context '$contextName' has an unreadable endpoint configuration."
    }
    if ($endpoint -notmatch '^(?i)npipe://') {
        throw "Docker context '$contextName' is not a local Docker Desktop named pipe. Switch to the local desktop-linux/default context before starting."
    }
}

function Initialize-Docker {
    param(
        [switch]$AllowStart,
        [switch]$AllowStopped
    )

    $command = Get-Command docker.exe -ErrorAction SilentlyContinue
    if ($null -eq $command) {
        $command = Get-Command docker -ErrorAction SilentlyContinue
    }
    if ($null -eq $command) {
        throw "Docker Desktop is not installed. Install it from https://www.docker.com/products/docker-desktop/ and run this file again."
    }
    $script:DockerExecutable = $command.Source
    Assert-LocalDockerContext

    $composeVersion = Invoke-DockerCaptured -Arguments @("compose", "version") -OutputMode None
    if ($composeVersion.ExitCode -ne 0) {
        throw "Docker Compose v2 is unavailable. Update Docker Desktop and try again."
    }
    $engineType = Get-DockerEngineType
    if ($engineType -eq "linux") {
        return $true
    }
    if (-not [string]::IsNullOrWhiteSpace($engineType)) {
        throw "Docker is using Windows containers. Switch Docker Desktop to Linux containers and try again."
    }

    if ($AllowStopped) {
        return $false
    }
    if (-not $AllowStart) {
        throw "Docker engine is not running. This command did not start it; start Docker Desktop and try again."
    }

    $dockerDesktopPath = Join-Path ([Environment]::GetFolderPath("ProgramFiles")) "Docker\Docker\Docker Desktop.exe"
    if (Test-Path -LiteralPath $dockerDesktopPath -PathType Leaf) {
        Write-Step "Starting Docker Desktop; this can take up to two minutes..."
        Start-Process -FilePath $dockerDesktopPath -WindowStyle Hidden
        for ($attempt = 0; $attempt -lt 60; $attempt++) {
            Start-Sleep -Seconds 2
            $engineType = Get-DockerEngineType
            if ($engineType -eq "linux") {
                return $true
            }
            if (-not [string]::IsNullOrWhiteSpace($engineType)) {
                throw "Docker started in Windows-containers mode. Switch Docker Desktop to Linux containers and try again."
            }
        }
    }

    throw "Docker engine is not running. Start Docker Desktop, wait until it is ready, and try again."
}

function Get-ProjectContainerIds {
    param([switch]$OnlyRunning)

    $arguments = @("ps")
    if (-not $OnlyRunning) {
        $arguments += "-a"
    }
    $arguments += @(
        "--filter", "label=com.docker.compose.project=$ComposeProjectName",
        "--format", "{{.ID}}"
    )
    $result = Invoke-DockerCaptured -Arguments $arguments
    if ($result.ExitCode -ne 0) {
        throw "Could not list containers for the private osu! project."
    }
    return @($result.Output | ForEach-Object { ([string]$_).Trim() } | Where-Object { $_ })
}

function Assert-NoLegacyComposeContainers {
    $result = Invoke-DockerCaptured -Arguments @(
        "ps", "-a",
        "--filter", "label=com.docker.compose.project=server",
        "--format", "{{.Names}}"
    )
    if ($result.ExitCode -ne 0) {
        throw "Could not check for containers created by the old Compose project."
    }
    $legacyNames = @(
        $result.Output |
            ForEach-Object { ([string]$_).Trim() } |
            Where-Object {
                $_ -in @("osu_api_server", "osu_api_mysql", "osu_api_redis", "performance_server") -or
                $_ -match '^server-(app|mysql|redis|performance-server|spectator|nginx)-[0-9]+$'
            }
    )
    if ($legacyNames.Count -gt 0) {
        throw "Old server Compose containers still exist ($($legacyNames -join ', ')). From the server folder run 'docker compose --project-name server down' WITHOUT -v, then run START_SERVER.bat again. The database volume is reused."
    }
}

function Show-ProjectStatus {
    & $script:DockerExecutable ps -a `
        --filter "label=com.docker.compose.project=$ComposeProjectName" `
        --format "table {{.Names}}`t{{.Status}}`t{{.Ports}}"
    if ($LASTEXITCODE -ne 0) {
        throw "Could not read private osu! container status."
    }
}

function Get-RedactionValues {
    if (-not (Test-Path -LiteralPath $EnvironmentPath -PathType Leaf)) {
        return @()
    }
    $values = Get-EnvironmentValues
    return @(
        $values.GetEnumerator() |
            Where-Object { $_.Key -match '(?i)(SECRET|PASSWORD|TOKEN)' -and ([string]$_.Value).Length -ge 8 } |
            ForEach-Object { [string]$_.Value } |
            Sort-Object Length -Descending -Unique
    )
}

function Show-ProjectLogs {
    $containerIds = @(Get-ProjectContainerIds)
    if ($containerIds.Count -eq 0) {
        Write-Step "No containers exist for this private osu! project."
        return
    }
    $redactions = @(Get-RedactionValues)
    foreach ($containerId in $containerIds) {
        $inspectResult = Invoke-DockerCaptured -Arguments @("inspect", "--format", "{{.Name}}", $containerId)
        $containerName = [string]($inspectResult.Output -join "`n")
        if ($inspectResult.ExitCode -ne 0) {
            $containerName = $containerId
        }
        Write-Host "`n===== $(([string]$containerName).TrimStart('/')) =====" -ForegroundColor Cyan
        $logResult = Invoke-DockerCaptured -Arguments @("logs", "--tail", "200", $containerId) -OutputMode All
        $logResult.Output | ForEach-Object {
            $line = [string]$_
            foreach ($secret in $redactions) {
                $line = $line.Replace($secret, "<redacted>")
            }
            Write-Host $line
        }
        if ($logResult.ExitCode -ne 0) {
            Write-WarningMessage "Docker could not read all logs for $containerId (exit code $($logResult.ExitCode))."
        }
    }
}

function Stop-ProjectContainers {
    $containerIds = @(Get-ProjectContainerIds -OnlyRunning)
    if ($containerIds.Count -eq 0) {
        Write-Step "The private osu! containers are already stopped."
        return
    }
    & $script:DockerExecutable stop @containerIds
    if ($LASTEXITCODE -ne 0) {
        throw "Docker could not stop every private osu! container."
    }
    Write-Step "Server stopped. Database, replays, and certificates were preserved."
}

function Invoke-Compose {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments,
        [switch]$IgnoreExitCode
    )

    $composeArguments = @(
        "compose",
        "--project-name", $ComposeProjectName,
        "--project-directory", $ServerDirectory,
        "--env-file", $EnvironmentPath,
        "-f", $BaseComposePath,
        "-f", $EasyComposePath
    ) + $Arguments

    & $script:DockerExecutable @composeArguments
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0 -and -not $IgnoreExitCode) {
        throw "docker compose failed with exit code $exitCode."
    }
}

function Assert-RequiredServicesRunning {
    $composeArguments = @(
        "compose",
        "--project-name", $ComposeProjectName,
        "--project-directory", $ServerDirectory,
        "--env-file", $EnvironmentPath,
        "-f", $BaseComposePath,
        "-f", $EasyComposePath,
        "ps", "--services", "--filter", "status=running"
    )
    $result = Invoke-DockerCaptured -Arguments $composeArguments
    if ($result.ExitCode -ne 0) {
        throw "Could not verify the state of the Docker Compose services."
    }
    $running = @($result.Output | ForEach-Object { ([string]$_).Trim() } | Where-Object { $_ })
    $required = @("app", "mysql", "redis", "performance-server", "spectator", "nginx", "caddy")
    $missing = @($required | Where-Object { $_ -notin $running })
    if ($missing.Count -gt 0) {
        throw "Required containers are not running: $($missing -join ', '). Run START_SERVER.bat Logs."
    }

    foreach ($serviceName in @("mysql", "redis")) {
        $serviceArguments = @(
            "compose",
            "--project-name", $ComposeProjectName,
            "--project-directory", $ServerDirectory,
            "--env-file", $EnvironmentPath,
            "-f", $BaseComposePath,
            "-f", $EasyComposePath,
            "ps", "-q", $serviceName
        )
        $serviceResult = Invoke-DockerCaptured -Arguments $serviceArguments
        $containerId = [string]($serviceResult.Output -join "").Trim()
        if ($serviceResult.ExitCode -ne 0 -or [string]::IsNullOrWhiteSpace($containerId)) {
            throw "Could not find the $serviceName container for its health check."
        }
        $healthResult = Invoke-DockerCaptured -Arguments @(
            "inspect", "--format", "{{if .State.Health}}{{.State.Health.Status}}{{else}}missing{{end}}", $containerId
        )
        $health = [string]($healthResult.Output -join "`n").Trim().ToLowerInvariant()
        if ($healthResult.ExitCode -ne 0 -or $health -ne "healthy") {
            throw "$serviceName is not healthy (reported '$health'). Run START_SERVER.bat Logs."
        }
    }
}

function Test-PerformanceService {
    $composeArguments = @(
        "compose",
        "--project-name", $ComposeProjectName,
        "--project-directory", $ServerDirectory,
        "--env-file", $EnvironmentPath,
        "-f", $BaseComposePath,
        "-f", $EasyComposePath,
        "exec", "-T", "app", "curl", "--fail", "--silent", "--show-error", "--max-time", "10",
        "http://performance-server:8080/available_rulesets"
    )
    $result = Invoke-DockerCaptured -Arguments $composeArguments
    if ($result.ExitCode -ne 0) {
        return $false
    }
    try {
        $payload = ($result.Output -join "`n") | ConvertFrom-Json
        $available = @($payload.has_performance_calculator)
        return @("osu", "taiko", "fruits", "mania" | Where-Object { $_ -notin $available }).Count -eq 0
    }
    catch {
        return $false
    }
}

function Test-SpectatorService {
    $url = "http://127.0.0.1:8000/signalr/metadata/negotiate?negotiateVersion=1"
    try {
        $response = Invoke-WebRequest `
            -Uri $url `
            -Method Post `
            -ContentType "application/json" `
            -Body "{}" `
            -UseBasicParsing `
            -MaximumRedirection 0 `
            -TimeoutSec 5
        return $response.StatusCode -eq 200
    }
    catch {
        if ($null -ne $_.Exception.Response) {
            try {
                $statusCode = [int]$_.Exception.Response.StatusCode
                return $statusCode -in @(400, 401, 403, 405)
            }
            catch {
                return $false
            }
        }
        return $false
    }
}

function Assert-ComponentReadiness {
    Write-Step "Checking standard PP rulesets and the spectator gateway..."
    $deadline = [DateTime]::UtcNow.AddSeconds(45)
    while ([DateTime]::UtcNow -lt $deadline) {
        if ((Test-PerformanceService) -and (Test-SpectatorService)) {
            return
        }
        Start-Sleep -Seconds 3
    }
    throw "PP rulesets or spectator are not ready. Run START_SERVER.bat Logs and inspect performance-server/spectator."
}

function Test-HealthEndpoint {
    param(
        [string]$Url,
        [int]$TimeoutSeconds = 4
    )

    try {
        $response = Invoke-WebRequest `
            -Uri $Url `
            -UseBasicParsing `
            -MaximumRedirection 0 `
            -TimeoutSec $TimeoutSeconds
        if ($response.StatusCode -ne 200) {
            return $false
        }
        $body = $response.Content | ConvertFrom-Json
        return [string]$body.status -ceq "ok"
    }
    catch {
        return $false
    }
}

function Wait-ForLocalHealth {
    Write-Step "Waiting for MySQL, migrations, API, spectator, and proxy..."
    $deadline = [DateTime]::UtcNow.AddMinutes(3)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (Test-HealthEndpoint "http://127.0.0.1:8000/health" 3) {
            return
        }
        Start-Sleep -Seconds 2
    }

    Invoke-Compose -Arguments @("ps") -IgnoreExitCode
    throw "The local health check did not pass within three minutes. Run START_SERVER.bat Logs to inspect the containers."
}

function Test-PublicHealth {
    param([string]$ServerDomain)

    $url = "https://$ServerDomain/health"
    Write-Step "Checking public HTTPS at $url ..."
    $deadline = [DateTime]::UtcNow.AddSeconds(90)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (Test-HealthEndpoint $url 5) {
            Write-Host "Server is online for friends: $url" -ForegroundColor Green
            return $true
        }
        Start-Sleep -Seconds 5
    }

    Write-WarningMessage "The containers are healthy locally, but $url is not reachable from this PC yet."
    Write-WarningMessage "Check the DNS A/AAAA records, router forwarding for TCP 80/443, Windows Firewall, and CGNAT."
    Write-WarningMessage "Some routers lack NAT loopback; in that case verify the URL from a phone on mobile data."
    Write-WarningMessage "Do not expose ports 8000, 3306, or 6379 to the internet."
    return $false
}

function Start-PrivateServer {
    [void](Initialize-Docker -AllowStart)
    Assert-NoLegacyComposeContainers
    if (-not (Test-Path -LiteralPath $EnvironmentPath -PathType Leaf) -or
        (Test-EnvironmentIsUnconfiguredTemplate)) {
        Initialize-Environment
    }
    else {
        Assert-EnvironmentReady | Out-Null
    }

    $values = Assert-EnvironmentReady
    Sync-LauncherConfiguration ([string]$values["SERVER_DOMAIN"])
    Write-Step "Validating Docker Compose configuration..."
    Invoke-Compose -Arguments @("config", "--quiet")
    Write-Step "Starting the private osu! server. The first build can take several minutes..."
    Invoke-Compose -Arguments @("up", "-d", "--build")
    # nginx resolves Docker service names when it starts. If an application
    # container was rebuilt, restart nginx so it cannot keep a stale IP.
    Invoke-Compose -Arguments @("restart", "nginx")
    Wait-ForLocalHealth
    Assert-RequiredServicesRunning
    Assert-ComponentReadiness
    Invoke-Compose -Arguments @("ps")
    if (-not (Test-PublicHealth ([string]$values["SERVER_DOMAIN"]))) {
        $script:CommandExitCode = 2
    }
}

function Grant-Owner {
    [void](Initialize-Docker -AllowStart)
    Assert-NoLegacyComposeContainers
    Assert-EnvironmentReady | Out-Null
    if ([string]::IsNullOrWhiteSpace($Username)) {
        $script:Username = (Read-Host "Local username to make owner").Trim()
    }
    if ($Username -notmatch '^[\w \[\]-]{2,15}$') {
        throw "Username must match the server registration rules (2-15 letters/digits, spaces, _, -, [ or ])."
    }
    $confirmation = (Read-Host "Type the exact local username again to confirm owner access").Trim()
    if ($confirmation -cne $Username) {
        throw "Owner confirmation did not exactly match the username. Nothing was changed."
    }

    Invoke-Compose -Arguments @(
        "exec", "-T", "app", "uv", "run", "--no-sync", "python", "tools/grant_owner.py",
        "--username=$Username",
        "--confirm=$confirmation"
    )
    Write-Host "$Username is now the server owner." -ForegroundColor Green
}

function Assert-ServerFilesPresent {
    if (-not (Test-Path -LiteralPath $ServerDirectory -PathType Container) -or
        -not (Test-Path -LiteralPath $BaseComposePath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $EasyComposePath -PathType Leaf)) {
        throw "Server files are incomplete. Keep START_SERVER.bat beside the server and scripts folders."
    }
}

try {
    switch ($Action) {
        "Start" {
            Assert-ServerFilesPresent
            Start-PrivateServer
        }
        "Setup" {
            Assert-ServerFilesPresent
            [void](Initialize-Docker -AllowStart)
            Initialize-Environment -Reconfigure
            Assert-EnvironmentReady | Out-Null
            Write-Step "Configuration is valid. Run START_SERVER.bat to start the server."
        }
        "Status" {
            $dockerRunning = Initialize-Docker -AllowStopped
            if ($dockerRunning) {
                Assert-NoLegacyComposeContainers
                Show-ProjectStatus
            }
            else {
                Write-Step "Docker engine is stopped, so the private osu! server is offline."
            }
        }
        "Logs" {
            $dockerRunning = Initialize-Docker -AllowStopped
            if ($dockerRunning) {
                Assert-NoLegacyComposeContainers
                Show-ProjectLogs
            }
            else {
                Write-Step "Docker engine is stopped; there are no live logs to show."
            }
        }
        "Stop" {
            [void](Initialize-Docker -AllowStart)
            Assert-NoLegacyComposeContainers
            Stop-ProjectContainers
        }
        "Owner" {
            Assert-ServerFilesPresent
            Grant-Owner
        }
    }
}
catch {
    Write-Host "[error] $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}

exit $script:CommandExitCode
