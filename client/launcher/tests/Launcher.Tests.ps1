[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

. (Join-Path -Path (Split-Path -Parent $PSScriptRoot) -ChildPath "Launcher.Common.ps1")

$script:Passed = 0
$script:Failed = 0

function Invoke-TestCase {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,

        [Parameter(Mandatory = $true)]
        [scriptblock]$Body
    )

    try {
        & $Body
        $script:Passed++
        Write-Host "PASS $Name"
    }
    catch {
        $script:Failed++
        Write-Host "FAIL $Name - $($_.Exception.Message)" -ForegroundColor Red
    }
}

function Assert-Equal {
    param($Expected, $Actual)
    if ($Expected -cne $Actual) {
        throw "Expected '$Expected', got '$Actual'."
    }
}

function Assert-Throws {
    param([scriptblock]$Body)
    $didThrow = $false
    try {
        & $Body
    }
    catch {
        $didThrow = $true
    }

    if (-not $didThrow) {
        throw "Expected an exception, but none was thrown."
    }
}

Invoke-TestCase "normalizes a private HTTPS origin" {
    Assert-Equal "https://private.example:8443" (Get-NormalizedPrivateEndpoint -Value "https://private.example:8443/" -Name "test")
}

Invoke-TestCase "rejects HTTP" {
    Assert-Throws { Get-NormalizedPrivateEndpoint -Value "http://private.example" -Name "test" }
}

Invoke-TestCase "rejects official ppy.sh hosts" {
    Assert-Throws { Get-NormalizedPrivateEndpoint -Value "https://osu.ppy.sh" -Name "test" }
    Assert-Throws { Get-NormalizedPrivateEndpoint -Value "https://spectator.ppy.sh" -Name "test" }
}

Invoke-TestCase "rejects paths and credentials in an origin" {
    Assert-Throws { Get-NormalizedPrivateEndpoint -Value "https://private.example/api" -Name "test" }
    Assert-Throws { Get-NormalizedPrivateEndpoint -Value "https://user:pass@private.example" -Name "test" }
}

Invoke-TestCase "accepts only safe private HTTPS service paths" {
    Assert-Equal "https://private.example:8443/signalr/spectator" (Get-NormalizedPrivateEndpoint -Value "https://private.example:8443/signalr/spectator" -Name "test" -RequirePath)
    Assert-Throws { Get-NormalizedPrivateEndpoint -Value "https://private.example" -Name "test" -RequirePath }
    Assert-Throws { Get-NormalizedPrivateEndpoint -Value "https://private.example/signalr/%2e%2e/metadata" -Name "test" -RequirePath }
    Assert-Throws { Get-NormalizedPrivateEndpoint -Value "https://private.example/signalr/metadata?token=no" -Name "test" -RequirePath }
    Assert-Throws { Get-NormalizedPrivateEndpoint -Value "https://spectator.ppy.sh/spectator" -Name "test" -RequirePath }
}

Invoke-TestCase "validates SHA-256 syntax" {
    Assert-Equal ("a" * 64) (Get-NormalizedSha256 -Sha256 ("A" * 64))
    Assert-Throws { Get-NormalizedSha256 -Sha256 "abcd" }
}

Invoke-TestCase "redacts the OAuth client secret" {
    $sanitized = Get-SanitizedPrivateOsuArguments -Arguments @("--api-url=https://private.example", "--client-secret=do-not-print")
    Assert-Equal "--api-url=https://private.example" $sanitized[0]
    Assert-Equal "--client-secret=<redacted>" $sanitized[1]
}

Invoke-TestCase "accepts only the real default osu! data directory" {
    Assert-DefaultOsuDataDirectory -DataDirectory (Join-Path -Path $env:APPDATA -ChildPath "osu")
    Assert-Throws { Assert-DefaultOsuDataDirectory -DataDirectory (Join-Path -Path $env:APPDATA -ChildPath "some-other-osu-profile") }
}

Invoke-TestCase "rejects framework portable storage beside osu executable" {
    $testRoot = Join-Path -Path ([IO.Path]::GetTempPath()) -ChildPath ("private-osu-launcher-portable-tests-{0}" -f [Guid]::NewGuid().ToString("N"))
    try {
        New-Item -ItemType Directory -Path $testRoot | Out-Null
        $executablePath = Join-Path -Path $testRoot -ChildPath "osu!.exe"
        Assert-NoPortableOsuStorage -ExecutablePath $executablePath

        Set-Content -LiteralPath (Join-Path -Path $testRoot -ChildPath "framework.ini") -Value "FrameSync = Limit2x"
        Assert-Throws { Assert-NoPortableOsuStorage -ExecutablePath $executablePath }
    }
    finally {
        $resolvedTestRoot = [IO.Path]::GetFullPath($testRoot)
        $resolvedSystemTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
        if (-not $resolvedTestRoot.StartsWith($resolvedSystemTemp, [StringComparison]::OrdinalIgnoreCase) -or
            -not ([IO.Path]::GetFileName($resolvedTestRoot)).StartsWith("private-osu-launcher-portable-tests-", [StringComparison]::Ordinal)) {
            throw "Refusing unsafe test cleanup path: $resolvedTestRoot"
        }

        if (Test-Path -LiteralPath $resolvedTestRoot -PathType Container) {
            Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force
        }
    }
}

Invoke-TestCase "accepts a dedicated portable profile beside its executable" {
    $testRoot = Join-Path -Path ([IO.Path]::GetTempPath()) -ChildPath ("private-osu-launcher-safe-portable-tests-{0}" -f [Guid]::NewGuid().ToString("N"))
    try {
        New-Item -ItemType Directory -Path $testRoot | Out-Null
        $executablePath = Join-Path -Path $testRoot -ChildPath "osu!.exe"
        Set-Content -LiteralPath (Join-Path -Path $testRoot -ChildPath "framework.ini") -Value ""
        Assert-PortableOsuStorage -ExecutablePath $executablePath -DataDirectory $testRoot
        Assert-Throws {
            Assert-PortableOsuStorage -ExecutablePath $executablePath -DataDirectory (Join-Path $testRoot "other")
        }
    }
    finally {
        $resolvedTestRoot = [IO.Path]::GetFullPath($testRoot)
        $resolvedSystemTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
        if (-not $resolvedTestRoot.StartsWith($resolvedSystemTemp, [StringComparison]::OrdinalIgnoreCase) -or
            -not ([IO.Path]::GetFileName($resolvedTestRoot)).StartsWith("private-osu-launcher-safe-portable-tests-", [StringComparison]::Ordinal)) {
            throw "Refusing unsafe test cleanup path: $resolvedTestRoot"
        }

        if (Test-Path -LiteralPath $resolvedTestRoot -PathType Container) {
            Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force
        }
    }
}

Invoke-TestCase "binds an empty private profile to Windows SID and API" {
    $oldAppData = $env:APPDATA
    $testRoot = Join-Path -Path ([IO.Path]::GetTempPath()) -ChildPath ("private-osu-launcher-tests-{0}" -f [Guid]::NewGuid().ToString("N"))
    try {
        $env:APPDATA = $testRoot
        $dataDirectory = Join-Path -Path $testRoot -ChildPath "osu"
        $settings = [pscustomobject]@{
            Profile = [pscustomobject]@{
                Id = "unit-test-profile"
                DataDirectory = $dataDirectory
                StorageMode = "default"
                MarkerPath = Join-Path -Path $dataDirectory -ChildPath ".private-osu-profile.json"
            }
            Server = [pscustomobject]@{
                ApiUrl = "https://private.example"
            }
        }

        New-PrivateProfileMarker -Settings $settings -ConfirmedNoOfficialAccountData $true | Out-Null
        Assert-Equal $true (Assert-PrivateProfileMarker -Settings $settings)

        $settings.Server.ApiUrl = "https://other-private.example"
        Assert-Throws { Assert-PrivateProfileMarker -Settings $settings }
    }
    finally {
        $env:APPDATA = $oldAppData
        $resolvedTestRoot = [IO.Path]::GetFullPath($testRoot)
        $resolvedSystemTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
        if (-not $resolvedTestRoot.StartsWith($resolvedSystemTemp, [StringComparison]::OrdinalIgnoreCase) -or
            -not ([IO.Path]::GetFileName($resolvedTestRoot)).StartsWith("private-osu-launcher-tests-", [StringComparison]::Ordinal)) {
            throw "Refusing unsafe test cleanup path: $resolvedTestRoot"
        }

        if (Test-Path -LiteralPath $resolvedTestRoot -PathType Container) {
            Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force
        }
    }
}

Invoke-TestCase "builds only private redirect arguments" {
    $oldSecret = [Environment]::GetEnvironmentVariable("LAUNCHER_TEST_SECRET", [EnvironmentVariableTarget]::Process)
    try {
        [Environment]::SetEnvironmentVariable("LAUNCHER_TEST_SECRET", "test-secret", [EnvironmentVariableTarget]::Process)
        $settings = [pscustomobject]@{
            Server = [pscustomobject]@{
                ApiUrl = "https://private.example"
                WebsiteUrl = "https://private.example"
                ClientId = "5"
                ClientSecretEnvironmentVariable = "LAUNCHER_TEST_SECRET"
                SpectatorUrl = "https://private.example/signalr/spectator"
                MultiplayerUrl = "https://private.example/signalr/multiplayer"
                MetadataUrl = "https://private.example/signalr/metadata"
                BeatmapSubmissionServiceUrl = "https://private.example/beatmap-submission"
            }
        }

        $arguments = Get-PrivateOsuArguments -Settings $settings
        Assert-Equal 9 $arguments.Count
        Assert-Equal "--spectator-url=https://private.example/signalr/spectator" $arguments[4]
        Assert-Equal "--multiplayer-url=https://private.example/signalr/multiplayer" $arguments[5]
        Assert-Equal "--metadata-url=https://private.example/signalr/metadata" $arguments[6]
        Assert-Equal "--bss-url=https://private.example/beatmap-submission" $arguments[7]
        Assert-Equal "--disable-sentry-logger" $arguments[8]
        if (($arguments -join " ") -match 'ppy\.sh') {
            throw "Official ppy.sh endpoint leaked into launch arguments."
        }
    }
    finally {
        [Environment]::SetEnvironmentVariable("LAUNCHER_TEST_SECRET", $oldSecret, [EnvironmentVariableTarget]::Process)
    }
}

Write-Host "Tests: $script:Passed passed, $script:Failed failed"
if ($script:Failed -ne 0) {
    exit 1
}
