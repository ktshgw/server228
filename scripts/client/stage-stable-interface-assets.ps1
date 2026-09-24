[CmdletBinding()]
param()

# Explicit, reproducible subset for the user-requested stable interface reconstruction.
# The inspection tool remains read-only with respect to the installed client.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$inspection = Join-Path $workspace '.test-tmp\stable-inspection\assets'
$destination = Join-Path $workspace 'client\enhanced-auth\osu.Game.Rulesets.EnhancedAuth\Resources\StableInterface'
$catalogue = Get-Content -LiteralPath (Join-Path $inspection 'catalogue.json') -Raw -Encoding UTF8 | ConvertFrom-Json

$selection = '^(?:menu-osu(?:-shockwave)?|menu-button-(?:play|edit|options|exit|freeplay|multiplayer|back)(?:-over)?|menu-button-background|menu-background|menu-np|selection-.+|songselect-(?:top|bottom)|mode-(?:osu|taiko|fruits|mania)(?:-med|-small)?|ranking-.+|star[23]?|back-button-layer|user-(?:bg|border)|levelbar(?:-bg)?|button-(?:left|middle|right))$'
$selected = @($catalogue.entries | Where-Object { $_.extracted -and $_.media_type -match '^image/' -and ($_.key -replace '@2x$', '') -cmatch $selection })
$assets = [Collections.Generic.List[object]]::new()
[IO.Directory]::CreateDirectory($destination) | Out-Null

foreach ($group in ($selected | Group-Object { $_.key -replace '@2x$', '' } | Sort-Object Name)) {
    $entry = $group.Group | Sort-Object { if ($_.key -like '*@2x') { 0 } else { 1 } } | Select-Object -First 1
    $sourcePath = [IO.Path]::GetFullPath((Join-Path $inspection $entry.path))
    if (-not $sourcePath.StartsWith($inspection + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid source path.' }
    $sourceHash = (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($sourceHash -ne $entry.sha256) { throw ('Extracted resource hash differs: ' + $entry.key) }
    $fileName = [IO.Path]::GetFileName($sourcePath)
    Copy-Item -LiteralPath $sourcePath -Destination (Join-Path $destination $fileName)
    $assets.Add([pscustomobject][ordered]@{
        file = $fileName; key = $entry.key; source = $entry.source; sha256 = $entry.sha256
        size_bytes = $entry.size_bytes; logical_width = $entry.logical_width; logical_height = $entry.logical_height
    })
}

$manifest = [ordered]@{
    schema_version = 1
    purpose = 'Original resources selected for the optional SOMS Legacy interface. User skin overrides take priority.'
    source_assemblies = $catalogue.sources
    assets = @($assets.ToArray())
}
[IO.File]::WriteAllText((Join-Path $destination 'manifest.json'), ($manifest | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
[pscustomobject]@{ assets = $assets.Count; size_bytes = ($assets | Measure-Object -Property size_bytes -Sum).Sum; destination = $destination } | ConvertTo-Json -Compress
