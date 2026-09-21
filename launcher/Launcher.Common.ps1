Set-StrictMode -Version Latest

$script:LauncherSchemaVersion = 1
$script:PrivateProfileMarkerName = ".private-osu-profile.json"
$script:LazerProcessGuardSource = Join-Path $PSScriptRoot 'LazerProcessGuard.cs'

function Get-RunningLazerIds {
    if (-not ('SomsLauncher.LazerProcessGuard' -as [type])) {
        Add-Type -Path $script:LazerProcessGuardSource
    }
    [SomsLauncher.LazerProcessGuard]::FindRunning()
}

function Assert-LauncherWindowsX64 {
    if ($env:OS -ne "Windows_NT") {
        throw "This launcher supports Windows only."
    }

    $nativeArchitecture = $env:PROCESSOR_ARCHITEW6432
    if ([string]::IsNullOrWhiteSpace($nativeArchitecture)) {
        $nativeArchitecture = $env:PROCESSOR_ARCHITECTURE
    }

    if (-not [Environment]::Is64BitOperatingSystem -or $nativeArchitecture -ne "AMD64") {
        throw "This launcher is pinned to Windows x64 (AMD64). Detected architecture: '$nativeArchitecture'."
    }
}

function Assert-LauncherNotElevated {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if ($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "Do not run the private osu! launcher as Administrator. Use a normal, dedicated Windows account."
    }
}

function Assert-OnlyProperties {
    param(
        [Parameter(Mandatory = $true)]
        [psobject]$Object,

        [Parameter(Mandatory = $true)]
        [string[]]$Allowed,

        [Parameter(Mandatory = $true)]
        [string]$Context
    )

    foreach ($property in $Object.PSObject.Properties.Name) {
        if ($Allowed -notcontains $property) {
            throw "Unknown configuration property '$Context.$property'. Refusing to ignore a possible typo."
        }
    }
}

function Assert-RequiredString {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string]$Value,

        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    if ([string]::IsNullOrWhiteSpace($Value)) {
        throw "Configuration property '$Name' is required."
    }
}

function Expand-LauncherPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [string]$BaseDirectory = (Get-Location).Path
    )

    Assert-RequiredString -Value $Path -Name "path"
    $expanded = [Environment]::ExpandEnvironmentVariables($Path)
    if (-not [IO.Path]::IsPathRooted($expanded)) {
        $expanded = Join-Path -Path $BaseDirectory -ChildPath $expanded
    }

    return [IO.Path]::GetFullPath($expanded)
}

function Get-DefaultOsuDataDirectory {
    if ([string]::IsNullOrWhiteSpace($env:APPDATA)) {
        throw "APPDATA is not available for the current Windows account."
    }

    return [IO.Path]::GetFullPath((Join-Path -Path $env:APPDATA -ChildPath "osu"))
}

function Assert-DefaultOsuDataDirectory {
    param(
        [Parameter(Mandatory = $true)]
        [string]$DataDirectory
    )

    $configured = [IO.Path]::GetFullPath($DataDirectory).TrimEnd('\')
    $expected = (Get-DefaultOsuDataDirectory).TrimEnd('\')
    if (-not $configured.Equals($expected, [StringComparison]::OrdinalIgnoreCase)) {
        throw "This stock-client MVP only supports the current Windows account's default osu! data directory '$expected'. A custom path would not isolate or redirect the release client."
    }
}

function Assert-NoCustomOsuStorageRedirect {
    param(
        [Parameter(Mandatory = $true)]
        [string]$DataDirectory
    )

    $storageConfigPath = Join-Path -Path $DataDirectory -ChildPath "storage.ini"
    if (-not (Test-Path -LiteralPath $storageConfigPath -PathType Leaf)) {
        return
    }

    foreach ($line in (Get-Content -LiteralPath $storageConfigPath -Encoding UTF8)) {
        if ($line -match '^\s*FullPath\s*=\s*(.*?)\s*$') {
            $configuredStoragePath = $Matches[1].Trim()
            if ($configuredStoragePath -ne '""' -and $configuredStoragePath -ne "''" -and -not [string]::IsNullOrWhiteSpace($configuredStoragePath)) {
                throw "osu! storage.ini redirects data to '$configuredStoragePath'. This launcher cannot verify that custom storage and refuses to start. Use the default profile in a dedicated Windows account."
            }
        }
    }
}

function Assert-PortableOsuStorage {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ExecutablePath,

        [Parameter(Mandatory = $true)]
        [string]$DataDirectory
    )

    $executableDirectory = (Split-Path -Parent ([IO.Path]::GetFullPath($ExecutablePath))).TrimEnd('\')
    $configuredDataDirectory = ([IO.Path]::GetFullPath($DataDirectory)).TrimEnd('\')
    if (-not $configuredDataDirectory.Equals($executableDirectory, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Portable osu! data directory must be the executable directory '$executableDirectory'."
    }

    $portableSentinelPath = Join-Path -Path $executableDirectory -ChildPath "framework.ini"
    if (-not (Test-Path -LiteralPath $portableSentinelPath -PathType Leaf)) {
        throw "Portable osu! profile requires '$portableSentinelPath'."
    }
}

function Assert-NoPortableOsuStorage {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ExecutablePath
    )

    $executableDirectory = Split-Path -Parent ([IO.Path]::GetFullPath($ExecutablePath))
    $portableSentinelPath = Join-Path -Path $executableDirectory -ChildPath "framework.ini"
    if (Test-Path -LiteralPath $portableSentinelPath -PathType Leaf) {
        throw "osu! portable storage sentinel found at '$portableSentinelPath'. The stock client will use the executable directory instead of APPDATA, so this launcher refuses to start. Remove the sentinel or use a clean dedicated Windows account/install."
    }
}

function Get-NormalizedSha256 {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Sha256,

        [string]$Name = "SHA-256"
    )

    $normalized = $Sha256.Trim().ToLowerInvariant()
    if ($normalized -notmatch '^[0-9a-f]{64}$') {
        throw "$Name must contain exactly 64 hexadecimal characters."
    }

    return $normalized
}

function Get-FileSha256 {
    param(
        [Parameter(Mandatory = $true)]
        [string]$LiteralPath
    )

    if (-not (Test-Path -LiteralPath $LiteralPath -PathType Leaf)) {
        throw "File not found: $LiteralPath"
    }

    return (Get-FileHash -LiteralPath $LiteralPath -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Test-IsOfficialPpyHost {
    param(
        [Parameter(Mandatory = $true)]
        [string]$HostName
    )

    $normalized = $HostName.TrimEnd('.').ToLowerInvariant()
    return $normalized -eq "ppy.sh" -or $normalized.EndsWith(".ppy.sh", [StringComparison]::Ordinal)
}

function Get-NormalizedPrivateEndpoint {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Value,

        [Parameter(Mandatory = $true)]
        [string]$Name,

        [switch]$RequirePath
    )

    Assert-RequiredString -Value $Value -Name $Name

    $uri = $null
    if (-not [Uri]::TryCreate($Value, [UriKind]::Absolute, [ref]$uri)) {
        throw "'$Name' must be an absolute HTTPS origin."
    }

    if ($uri.Scheme -ne "https") {
        throw "'$Name' must use HTTPS. HTTP endpoints are intentionally rejected."
    }

    if (-not [string]::IsNullOrEmpty($uri.UserInfo)) {
        throw "'$Name' must not contain username or password information."
    }

    if (-not [string]::IsNullOrEmpty($uri.Query) -or -not [string]::IsNullOrEmpty($uri.Fragment)) {
        throw "'$Name' must be an origin without a query string or fragment."
    }

    if ($RequirePath) {
        $path = $uri.AbsolutePath
        if ($path -eq "/" -or $path -notmatch '^/(?:[A-Za-z0-9_~-]+(?:\.[A-Za-z0-9_~-]+)*)(?:/[A-Za-z0-9_~-]+(?:\.[A-Za-z0-9_~-]+)*)*$') {
            throw "'$Name' must contain a safe absolute service path using only ASCII letters, digits, dot, underscore, tilde, dash, and slash."
        }

        foreach ($segment in $path.Split('/', [StringSplitOptions]::RemoveEmptyEntries)) {
            if ($segment -eq "." -or $segment -eq "..") {
                throw "'$Name' must not contain dot path segments."
            }
        }
    }
    elseif ($uri.AbsolutePath -ne "/") {
        throw "'$Name' must be an origin without a path (for example https://lazer.example.test)."
    }

    if (Test-IsOfficialPpyHost -HostName $uri.DnsSafeHost) {
        throw "'$Name' points to an official ppy.sh host. This launcher is only for the private server."
    }

    if ($RequirePath) {
        return $uri.GetLeftPart([UriPartial]::Path).TrimEnd('/')
    }

    return $uri.GetLeftPart([UriPartial]::Authority).TrimEnd('/')
}

function Read-LauncherConfig {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ConfigPath
    )

    $resolvedConfigPath = Expand-LauncherPath -Path $ConfigPath
    if (-not (Test-Path -LiteralPath $resolvedConfigPath -PathType Leaf)) {
        throw "Launcher configuration not found: $resolvedConfigPath"
    }

    try {
        $rawConfig = Get-Content -LiteralPath $resolvedConfigPath -Raw -Encoding UTF8
        $config = $rawConfig | ConvertFrom-Json
    }
    catch {
        throw "Failed to read launcher configuration '$resolvedConfigPath': $($_.Exception.Message)"
    }

    Assert-OnlyProperties -Object $config -Allowed @("SchemaVersion", "Profile", "Osu", "EnhancedAuth", "Server") -Context "config"

    if ([int]$config.SchemaVersion -ne $script:LauncherSchemaVersion) {
        throw "Unsupported launcher configuration schema '$($config.SchemaVersion)'. Expected '$script:LauncherSchemaVersion'."
    }

    foreach ($sectionName in @("Profile", "Osu", "EnhancedAuth", "Server")) {
        if ($null -eq $config.$sectionName) {
            throw "Configuration section '$sectionName' is required."
        }
    }

    Assert-OnlyProperties -Object $config.Profile -Allowed @("Id", "DataDirectory", "StorageMode") -Context "Profile"
    Assert-OnlyProperties -Object $config.Osu -Allowed @("ExecutablePath", "CompatibleVersions", "ExpectedPublisherSubjectContains") -Context "Osu"
    Assert-OnlyProperties -Object $config.EnhancedAuth -Allowed @("Version", "FileName", "Sha256", "AssemblyName") -Context "EnhancedAuth"
    Assert-OnlyProperties -Object $config.Server -Allowed @(
        "ApiUrl",
        "WebsiteUrl",
        "SpectatorUrl",
        "MultiplayerUrl",
        "MetadataUrl",
        "BeatmapSubmissionServiceUrl",
        "ClientId",
        "ClientSecretEnvironmentVariable"
    ) -Context "Server"

    Assert-RequiredString -Value ([string]$config.Profile.Id) -Name "Profile.Id"
    if ([string]$config.Profile.Id -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]{2,63}$') {
        throw "'Profile.Id' must be 3-64 characters and contain only ASCII letters, digits, dot, underscore, or dash."
    }

    $configDirectory = Split-Path -Parent $resolvedConfigPath
    $dataDirectory = Expand-LauncherPath -Path ([string]$config.Profile.DataDirectory) -BaseDirectory $configDirectory
    $executablePath = Expand-LauncherPath -Path ([string]$config.Osu.ExecutablePath) -BaseDirectory $configDirectory

    $storageMode = [string]$config.Profile.StorageMode
    if ([string]::IsNullOrWhiteSpace($storageMode)) {
        $storageMode = "default"
    }
    $storageMode = $storageMode.Trim().ToLowerInvariant()
    if ($storageMode -notin @("default", "portable")) {
        throw "'Profile.StorageMode' must be either 'default' or 'portable'."
    }

    if ($storageMode -eq "portable") {
        Assert-PortableOsuStorage -ExecutablePath $executablePath -DataDirectory $dataDirectory
    }
    else {
        Assert-DefaultOsuDataDirectory -DataDirectory $dataDirectory
        Assert-NoPortableOsuStorage -ExecutablePath $executablePath
    }
    Assert-NoCustomOsuStorageRedirect -DataDirectory $dataDirectory

    $dataRoot = [IO.Path]::GetPathRoot($dataDirectory)
    if ($dataDirectory.TrimEnd('\') -eq $dataRoot.TrimEnd('\')) {
        throw "'Profile.DataDirectory' must not be a drive root."
    }

    Assert-RequiredString -Value ([string]$config.Osu.ExpectedPublisherSubjectContains) -Name "Osu.ExpectedPublisherSubjectContains"

    $compatibleVersions = @($config.Osu.CompatibleVersions)
    if ($compatibleVersions.Count -eq 0) {
        throw "At least one explicitly tested 'Osu.CompatibleVersions' entry is required."
    }

    foreach ($version in $compatibleVersions) {
        if ([string]$version -notmatch '^\d{4}\.\d+\.\d+$') {
            throw "Invalid osu! version '$version'. Expected a value like 2026.702.1."
        }
    }

    Assert-RequiredString -Value ([string]$config.EnhancedAuth.Version) -Name "EnhancedAuth.Version"
    if ([string]$config.EnhancedAuth.Version -notmatch '^\d{4}\.\d+\.\d+$') {
        throw "Invalid 'EnhancedAuth.Version'. Expected a value like 2026.709.0."
    }
    Assert-RequiredString -Value ([string]$config.EnhancedAuth.FileName) -Name "EnhancedAuth.FileName"
    Assert-RequiredString -Value ([string]$config.EnhancedAuth.AssemblyName) -Name "EnhancedAuth.AssemblyName"
    if ([IO.Path]::GetFileName([string]$config.EnhancedAuth.FileName) -ne [string]$config.EnhancedAuth.FileName -or
        -not ([string]$config.EnhancedAuth.FileName).EndsWith(".dll", [StringComparison]::OrdinalIgnoreCase)) {
        throw "'EnhancedAuth.FileName' must be a plain DLL filename, not a path."
    }

    $enhancedAuthSha256 = Get-NormalizedSha256 -Sha256 ([string]$config.EnhancedAuth.Sha256) -Name "EnhancedAuth.Sha256"

    $server = [pscustomobject]@{
        ApiUrl = Get-NormalizedPrivateEndpoint -Value ([string]$config.Server.ApiUrl) -Name "Server.ApiUrl"
        WebsiteUrl = Get-NormalizedPrivateEndpoint -Value ([string]$config.Server.WebsiteUrl) -Name "Server.WebsiteUrl"
        SpectatorUrl = Get-NormalizedPrivateEndpoint -Value ([string]$config.Server.SpectatorUrl) -Name "Server.SpectatorUrl" -RequirePath
        MultiplayerUrl = Get-NormalizedPrivateEndpoint -Value ([string]$config.Server.MultiplayerUrl) -Name "Server.MultiplayerUrl" -RequirePath
        MetadataUrl = Get-NormalizedPrivateEndpoint -Value ([string]$config.Server.MetadataUrl) -Name "Server.MetadataUrl" -RequirePath
        BeatmapSubmissionServiceUrl = Get-NormalizedPrivateEndpoint -Value ([string]$config.Server.BeatmapSubmissionServiceUrl) -Name "Server.BeatmapSubmissionServiceUrl" -RequirePath
        ClientId = [string]$config.Server.ClientId
        ClientSecretEnvironmentVariable = [string]$config.Server.ClientSecretEnvironmentVariable
    }

    if ($server.ClientId -notmatch '^[1-9]\d*$') {
        throw "'Server.ClientId' must be a positive integer encoded as a string."
    }

    if ($server.ClientSecretEnvironmentVariable -notmatch '^[A-Za-z_][A-Za-z0-9_]*$') {
        throw "'Server.ClientSecretEnvironmentVariable' must be a valid environment variable name."
    }

    return [pscustomobject]@{
        ConfigPath = $resolvedConfigPath
        Profile = [pscustomobject]@{
            Id = [string]$config.Profile.Id
            DataDirectory = $dataDirectory
            StorageMode = $storageMode
            MarkerPath = Join-Path -Path $dataDirectory -ChildPath $script:PrivateProfileMarkerName
        }
        Osu = [pscustomobject]@{
            ExecutablePath = $executablePath
            CompatibleVersions = @($compatibleVersions | ForEach-Object { [string]$_ })
            ExpectedPublisherSubjectContains = [string]$config.Osu.ExpectedPublisherSubjectContains
        }
        EnhancedAuth = [pscustomobject]@{
            Version = [string]$config.EnhancedAuth.Version
            FileName = [string]$config.EnhancedAuth.FileName
            Sha256 = $enhancedAuthSha256
            AssemblyName = [string]$config.EnhancedAuth.AssemblyName
            DestinationPath = Join-Path -Path (Join-Path -Path $dataDirectory -ChildPath "rulesets") -ChildPath ([string]$config.EnhancedAuth.FileName)
        }
        Server = $server
    }
}

function Get-CurrentWindowsSid {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    if ($null -eq $identity.User) {
        throw "Could not determine the current Windows SID."
    }

    return $identity.User.Value
}

function New-PrivateProfileMarker {
    param(
        [Parameter(Mandatory = $true)]
        [psobject]$Settings,

        [Parameter(Mandatory = $true)]
        [bool]$ConfirmedNoOfficialAccountData
    )

    if (-not $ConfirmedNoOfficialAccountData) {
        throw "Refusing to initialise a profile without explicit confirmation that it contains no official osu! account data."
    }

    $dataDirectory = $Settings.Profile.DataDirectory
    Assert-NoCustomOsuStorageRedirect -DataDirectory $dataDirectory
    if (-not (Test-Path -LiteralPath $dataDirectory)) {
        New-Item -ItemType Directory -Path $dataDirectory -Force | Out-Null
    }

    if (-not (Test-Path -LiteralPath $dataDirectory -PathType Container)) {
        throw "Profile data path is not a directory: $dataDirectory"
    }

    if (Test-Path -LiteralPath $Settings.Profile.MarkerPath -PathType Leaf) {
        Assert-PrivateProfileMarker -Settings $Settings
        return $Settings.Profile.MarkerPath
    }

    if ($Settings.Profile.StorageMode -eq "portable") {
        $officialAccountDatabase = Join-Path -Path $dataDirectory -ChildPath "client.realm"
        if (Test-Path -LiteralPath $officialAccountDatabase) {
            throw "Portable directory already contains client.realm and has no private marker. Nothing was changed."
        }
    }
    else {
        $existingEntries = @(Get-ChildItem -LiteralPath $dataDirectory -Force)
        if ($existingEntries.Count -ne 0) {
            throw "Profile directory is not empty and has no private marker: $dataDirectory. Nothing was changed. Use a new dedicated Windows account/profile."
        }
    }

    $marker = [ordered]@{
        SchemaVersion = $script:LauncherSchemaVersion
        ProfileId = $Settings.Profile.Id
        WindowsSid = Get-CurrentWindowsSid
        ApiUrl = $Settings.Server.ApiUrl
        CreatedUtc = [DateTime]::UtcNow.ToString("o")
    }

    $marker | ConvertTo-Json | Set-Content -LiteralPath $Settings.Profile.MarkerPath -Encoding UTF8
    return $Settings.Profile.MarkerPath
}

function Assert-PrivateProfileMarker {
    param(
        [Parameter(Mandatory = $true)]
        [psobject]$Settings
    )

    Assert-NoCustomOsuStorageRedirect -DataDirectory $Settings.Profile.DataDirectory
    $markerPath = $Settings.Profile.MarkerPath
    if (-not (Test-Path -LiteralPath $markerPath -PathType Leaf)) {
        throw "Private profile marker not found: $markerPath. Run Initialize-PrivateProfile.ps1 first."
    }

    $markerItem = Get-Item -LiteralPath $markerPath -Force
    if (($markerItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Private profile marker must not be a symbolic link or reparse point."
    }

    try {
        $marker = Get-Content -LiteralPath $markerPath -Raw -Encoding UTF8 | ConvertFrom-Json
    }
    catch {
        throw "Private profile marker is invalid: $($_.Exception.Message)"
    }

    if ([int]$marker.SchemaVersion -ne $script:LauncherSchemaVersion -or
        [string]$marker.ProfileId -cne $Settings.Profile.Id -or
        [string]$marker.WindowsSid -cne (Get-CurrentWindowsSid) -or
        [string]$marker.ApiUrl -cne $Settings.Server.ApiUrl) {
        throw "Private profile marker does not match this config, server, or Windows account. Refusing to touch the profile."
    }

    return $true
}

function Assert-EnhancedAuthInstalled {
    param(
        [Parameter(Mandatory = $true)]
        [psobject]$Settings
    )

    $destinationPath = $Settings.EnhancedAuth.DestinationPath
    if (-not (Test-Path -LiteralPath $destinationPath -PathType Leaf)) {
        throw "EnhancedAuth is not installed at '$destinationPath'. Run Install-EnhancedAuth.ps1 with a locally downloaded DLL."
    }

    $actualHash = Get-FileSha256 -LiteralPath $destinationPath
    if ($actualHash -cne $Settings.EnhancedAuth.Sha256) {
        throw "EnhancedAuth hash mismatch at '$destinationPath'. Expected $($Settings.EnhancedAuth.Sha256), got $actualHash."
    }

    return $true
}

function Get-OsuExecutableVersion {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ExecutablePath
    )

    $versionInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($ExecutablePath)
    foreach ($candidate in @($versionInfo.ProductVersion, $versionInfo.FileVersion)) {
        if (-not [string]::IsNullOrWhiteSpace($candidate)) {
            $match = [regex]::Match($candidate, '(?<!\d)(\d{4}\.\d+\.\d+)(?!\d)')
            if ($match.Success) {
                return $match.Groups[1].Value
            }
        }
    }

    throw "Could not determine an osu! calendar version from '$ExecutablePath'."
}

function Assert-OsuExecutable {
    param(
        [Parameter(Mandatory = $true)]
        [psobject]$Settings
    )

    $executablePath = $Settings.Osu.ExecutablePath
    if (-not (Test-Path -LiteralPath $executablePath -PathType Leaf)) {
        throw "osu! executable not found: $executablePath"
    }

    if (-not $executablePath.EndsWith(".exe", [StringComparison]::OrdinalIgnoreCase)) {
        throw "Configured osu! executable must be a Windows .exe file."
    }

    if ($Settings.Profile.StorageMode -eq "portable") {
        Assert-PortableOsuStorage -ExecutablePath $executablePath -DataDirectory $Settings.Profile.DataDirectory
    }
    else {
        Assert-NoPortableOsuStorage -ExecutablePath $executablePath
    }

    $signature = Get-AuthenticodeSignature -LiteralPath $executablePath
    if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid) {
        throw "osu! executable does not have a valid Authenticode signature: $($signature.StatusMessage)"
    }

    if ($null -eq $signature.SignerCertificate -or
        $signature.SignerCertificate.Subject.IndexOf($Settings.Osu.ExpectedPublisherSubjectContains, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw "osu! executable signer is not the configured publisher '$($Settings.Osu.ExpectedPublisherSubjectContains)'."
    }

    $version = Get-OsuExecutableVersion -ExecutablePath $executablePath
    if ($Settings.Osu.CompatibleVersions -cnotcontains $version) {
        throw "osu! version '$version' is not in the tested allow-list: $($Settings.Osu.CompatibleVersions -join ', '). Do not update until EnhancedAuth compatibility is verified."
    }

    return $version
}

function Get-PrivateOsuArguments {
    param(
        [Parameter(Mandatory = $true)]
        [psobject]$Settings
    )

    $secretName = $Settings.Server.ClientSecretEnvironmentVariable
    $clientSecret = [Environment]::GetEnvironmentVariable($secretName, [EnvironmentVariableTarget]::Process)
    if ([string]::IsNullOrWhiteSpace($clientSecret)) {
        throw "Required private-server OAuth client secret environment variable '$secretName' is not set in this process."
    }

    # EnhancedAuth splits each option on '=' and only consumes the second part.
    if ($clientSecret -notmatch '^[A-Za-z0-9._~-]+$') {
        throw "'$secretName' contains unsupported characters. Use an ASCII client secret without whitespace or '='."
    }

    return @(
        "--api-url=$($Settings.Server.ApiUrl)",
        "--website-url=$($Settings.Server.WebsiteUrl)",
        "--client-id=$($Settings.Server.ClientId)",
        "--client-secret=$clientSecret",
        "--spectator-url=$($Settings.Server.SpectatorUrl)",
        "--multiplayer-url=$($Settings.Server.MultiplayerUrl)",
        "--metadata-url=$($Settings.Server.MetadataUrl)",
        "--bss-url=$($Settings.Server.BeatmapSubmissionServiceUrl)",
        "--disable-sentry-logger"
    )
}

function Get-SanitizedPrivateOsuArguments {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    return @($Arguments | ForEach-Object {
        if ($_.StartsWith("--client-secret=", [StringComparison]::Ordinal)) {
            "--client-secret=<redacted>"
        }
        else {
            $_
        }
    })
}
