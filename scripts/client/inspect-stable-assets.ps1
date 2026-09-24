[CmdletBinding()]
param(
    [string]$InputDirectory = '',
    [string]$OutputDirectory = '',
    [string]$LazerResourcesAssembly = 'C:\Users\Kts\AppData\Local\osulazer\current\osu.Game.Resources.dll'
)

# Local inspection only. Never publishes resources or writes into a client, skin, or server distribution.
# ResourceReader.Value and BinaryFormatter are intentionally not used: Bitmap is an NRBF object, but
# only its embedded PNG/JPEG bytes are extracted. No arbitrary objects are deserialised.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrEmpty($InputDirectory)) { $InputDirectory = Join-Path $PSScriptRoot '..\..\.test-tmp\stable-inspection\input' }
if ([string]::IsNullOrEmpty($OutputDirectory)) { $OutputDirectory = Join-Path $PSScriptRoot '..\..\.test-tmp\stable-inspection\assets' }

$inspectionRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\.test-tmp\stable-inspection\assets'))
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
if (-not $outputRoot.Equals($inspectionRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'OutputDirectory must be the workspace .test-tmp/stable-inspection/assets directory.'
}
$inputRoot = [IO.Path]::GetFullPath($InputDirectory)
[IO.Directory]::CreateDirectory($outputRoot) | Out-Null
$utf8 = [Text.UTF8Encoding]::new($false)
$sha = [Security.Cryptography.SHA256]::Create()
$entries = [Collections.Generic.List[object]]::new()
$sources = [Collections.Generic.List[object]]::new()
$safeNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)

function Get-BytesHash([byte[]]$Bytes) {
    return [BitConverter]::ToString($sha.ComputeHash($Bytes)).Replace('-', '').ToLowerInvariant()
}

function Read-BigEndian32([byte[]]$Bytes, [int]$Offset) {
    if ($Offset -lt 0 -or $Offset + 4 -gt $Bytes.Length) { throw 'Truncated big-endian integer.' }
    return ([uint64]$Bytes[$Offset] * 16777216 + [uint64]$Bytes[$Offset + 1] * 65536 +
        [uint64]$Bytes[$Offset + 2] * 256 + [uint64]$Bytes[$Offset + 3])
}

function Get-EmbeddedPng([byte[]]$Bytes) {
    $signature = [byte[]](137, 80, 78, 71, 13, 10, 26, 10)
    # The PNG normally begins at byte 164 in the NRBF Bitmap envelope; limit the scan to metadata.
    $maximumOffset = [Math]::Min($Bytes.Length - 8, 65536)
    for ($start = 0; $start -le $maximumOffset; $start++) {
        if ($Bytes[$start] -ne 137) { continue }
        $matches = $true
        for ($index = 1; $index -lt 8; $index++) {
            if ($Bytes[$start + $index] -ne $signature[$index]) { $matches = $false; break }
        }
        if (-not $matches) { continue }
        $position = $start + 8
        $width = 0
        $height = 0
        $chunks = 0
        while ($position + 12 -le $Bytes.Length -and $chunks -lt 10000) {
            $length = Read-BigEndian32 $Bytes $position
            if ($length -gt 16777216 -or $position + 12 + $length -gt $Bytes.Length) { break }
            $kind = [Text.Encoding]::ASCII.GetString($Bytes, $position + 4, 4)
            if ($chunks -eq 0) {
                if ($kind -ne 'IHDR' -or $length -ne 13) { break }
                $width = Read-BigEndian32 $Bytes ($position + 8)
                $height = Read-BigEndian32 $Bytes ($position + 12)
                if ($width -lt 1 -or $height -lt 1 -or $width -gt 32768 -or $height -gt 32768) { break }
            }
            $position += [int]$length + 12
            $chunks++
            if ($kind -eq 'IEND' -and $length -eq 0) {
                $payload = [byte[]]::new($position - $start)
                [Array]::Copy($Bytes, $start, $payload, 0, $payload.Length)
                return [pscustomobject]@{ bytes = $payload; extension = 'png'; mime = 'image/png'; width = $width; height = $height; offset = $start }
            }
        }
    }
    return $null
}

function Get-PrimitivePayload([byte[]]$Bytes) {
    if ($Bytes.Length -lt 4) { throw 'Truncated resource length prefix.' }
    $length = [BitConverter]::ToInt32($Bytes, 0)
    if ($length -lt 0 -or $length -ne $Bytes.Length - 4) { throw 'Invalid resource length prefix.' }
    $payload = [byte[]]::new($length)
    [Array]::Copy($Bytes, 4, $payload, 0, $length)
    $signature = if ($length -ge 4) { [Text.Encoding]::ASCII.GetString($payload, 0, 4) } else { '' }
    $extension = 'bin'
    $mime = 'application/octet-stream'
    if ($length -ge 4 -and $payload[0] -eq 0 -and $payload[1] -eq 1 -and $payload[2] -eq 0 -and $payload[3] -eq 0) {
        $extension = 'ttf'; $mime = 'font/ttf'
    } elseif ($signature -eq 'OTTO') { $extension = 'otf'; $mime = 'font/otf' }
    elseif ($signature -eq 'wOFF') { $extension = 'woff'; $mime = 'font/woff' }
    elseif ($signature -eq 'wOF2') { $extension = 'woff2'; $mime = 'font/woff2' }
    elseif ($signature -eq 'OggS') { $extension = 'ogg'; $mime = 'audio/ogg' }
    elseif ($signature -eq 'RIFF' -and $length -ge 12 -and [Text.Encoding]::ASCII.GetString($payload, 8, 4) -eq 'WAVE') {
        $extension = 'wav'; $mime = 'audio/wav'
    } elseif (($length -ge 3 -and [Text.Encoding]::ASCII.GetString($payload, 0, 3) -eq 'ID3') -or
              ($length -ge 2 -and $payload[0] -eq 255 -and ($payload[1] -band 224) -eq 224)) {
        $extension = 'mp3'; $mime = 'audio/mpeg'
    }
    return [pscustomobject]@{ bytes = $payload; extension = $extension; mime = $mime; width = $null; height = $null; offset = 4 }
}

function Get-EmbeddedJpeg([byte[]]$Bytes) {
    $maximumOffset = [Math]::Min($Bytes.Length - 12, 65536)
    for ($start = 5; $start -le $maximumOffset; $start++) {
        if ($Bytes[$start] -ne 255 -or $Bytes[$start + 1] -ne 216 -or $Bytes[$start + 2] -ne 255) { continue }
        # Bitmap's NRBF payload is a primitive Byte array: little-endian length, Byte type code, data.
        # Reading this envelope does not instantiate the serialised Bitmap or invoke its callbacks.
        if ($Bytes[$start - 1] -ne 2) { continue }
        $length = [BitConverter]::ToInt32($Bytes, $start - 5)
        if ($length -lt 12 -or $length -gt $Bytes.Length - $start) { continue }
        $end = $start + $length
        if ($Bytes[$end - 2] -ne 255 -or $Bytes[$end - 1] -ne 217) { continue }
        $position = $start + 2
        $width = 0
        $height = 0
        $segments = 0
        while ($position + 4 -lt $end -and $segments -lt 1000) {
            if ($Bytes[$position] -ne 255) { break }
            while ($position -lt $end -and $Bytes[$position] -eq 255) { $position++ }
            if ($position -ge $end) { break }
            $marker = $Bytes[$position++]
            if ($marker -eq 218 -or $marker -eq 217) { break }
            if ($marker -eq 1 -or ($marker -ge 208 -and $marker -le 215)) { continue }
            if ($position + 2 -gt $end) { break }
            $segmentLength = [int]$Bytes[$position] * 256 + [int]$Bytes[$position + 1]
            if ($segmentLength -lt 2 -or $position + $segmentLength -gt $end) { break }
            if (@(192, 193, 194, 195, 197, 198, 199, 201, 202, 203, 205, 206, 207) -contains $marker) {
                if ($segmentLength -lt 8) { break }
                $height = [int]$Bytes[$position + 3] * 256 + [int]$Bytes[$position + 4]
                $width = [int]$Bytes[$position + 5] * 256 + [int]$Bytes[$position + 6]
            }
            $position += $segmentLength
            $segments++
        }
        if ($width -le 0 -or $height -le 0 -or $width -gt 32768 -or $height -gt 32768) { continue }
        $payload = [byte[]]::new($length)
        [Array]::Copy($Bytes, $start, $payload, 0, $length)
        return [pscustomobject]@{ bytes = $payload; extension = 'jpg'; mime = 'image/jpeg'; width = $width; height = $height; offset = $start }
    }
    return $null
}

$lazerNames = @()
if (Test-Path -LiteralPath $LazerResourcesAssembly -PathType Leaf) {
    $lazerAssembly = [Reflection.Assembly]::ReflectionOnlyLoadFrom([IO.Path]::GetFullPath($LazerResourcesAssembly))
    $lazerNames = @($lazerAssembly.GetManifestResourceNames())
}
$lazerIndex = @{}
foreach ($name in $lazerNames) {
    # Store exact filename stems, not inferred counterparts with different names or widget-generated art.
    if ($name -match '\.(?<stem>[^.]+?)(?:@2x)?\.(?:png|jpg|jpeg|ttf|otf|wav|mp3|ogg)$') {
        $stem = $Matches.stem.ToLowerInvariant()
        if (-not $lazerIndex.ContainsKey($stem)) { $lazerIndex[$stem] = [Collections.Generic.List[string]]::new() }
        $lazerIndex[$stem].Add($name)
    }
}

$selection = '^(?:menu|selection|ranking|pause|fail|back|star|score|default-[0-9]|hit(?:0|50|100|200|300)|(?:mania|taiko)-hit|button|mode|lobby|room|multiplayer|match|options|overlay|user|welcome|play[-_]|inputoverlay|topmenu|volume|seekbar|Aller|Exo2|FontAwesome|songselect|select[-_]|rank[-_]|check[-_]|circle[-_]|editor[-_]|fps|loading|searching|notification|notify|sliderbar|levelbar|commentbox|featured|key[-_]|outbound|opaque-white|masking-border|triangle|worldmap|lazer-stable-shortcuts|test-build-overlay|medal|beatmapimport|epilepsy|unpause|applause)'
try {
    foreach ($source in @(
        @{ file = 'osu!ui.dll'; store = 'osu_ui.ResourcesStore.resources'; directory = 'ui' },
        @{ file = 'osu!gameplay.dll'; store = 'osu_gameplay.ResourcesStore.resources'; directory = 'gameplay-ui' }
    )) {
        $sourcePath = Join-Path $inputRoot $source.file
        $sourceBytes = [IO.File]::ReadAllBytes($sourcePath)
        $sources.Add([ordered]@{ file = $source.file; size_bytes = $sourceBytes.Length; sha256 = Get-BytesHash $sourceBytes; resource_store = $source.store })
        $assembly = [Reflection.Assembly]::ReflectionOnlyLoadFrom($sourcePath)
        $resourceStream = $assembly.GetManifestResourceStream($source.store)
        if ($null -eq $resourceStream) { throw ('Resource store not found: ' + $source.store) }
        $reader = [Resources.ResourceReader]::new($resourceStream)
        try {
            $enumerator = $reader.GetEnumerator()
            $keys = [Collections.Generic.List[string]]::new()
            while ($enumerator.MoveNext()) { $keys.Add([string]$enumerator.Key) }
            foreach ($key in ($keys | Sort-Object)) {
                $type = ''
                $raw = [byte[]]@()
                $reader.GetResourceData($key, [ref]$type, [ref]$raw)
                $normalName = ($key -replace '@2x$', '').ToLowerInvariant()
                $counterparts = if ($lazerIndex.ContainsKey($normalName)) { @($lazerIndex[$normalName].ToArray()) } else { @() }
                $entry = [ordered]@{
                    source = $source.file; resource_store = $source.store; key = $key; resource_type = $type
                    raw_size_bytes = $raw.Length; raw_sha256 = Get-BytesHash $raw
                    selected = [bool]($key -match $selection); extracted = $false
                    path = $null; media_type = $null; size_bytes = $null; sha256 = $null
                    pixel_width = $null; pixel_height = $null; logical_width = $null; logical_height = $null
                    embedded_payload_offset = $null; skip_reason = $null
                    lazer_same_stem_resources = @($counterparts)
                    lazer_legacy_same_stem_resources = @($counterparts | Where-Object { $_ -like '*.Skins.Legacy.*' })
                }
                if ($entry.selected) {
                    $decoded = $null
                    if ($type -like 'System.Drawing.Bitmap,*') {
                        $decoded = Get-EmbeddedPng $raw
                        if ($null -eq $decoded) { $decoded = Get-EmbeddedJpeg $raw }
                    }
                    elseif ($type -eq 'ResourceTypeCode.ByteArray' -or $type -eq 'ResourceTypeCode.Stream') { $decoded = Get-PrimitivePayload $raw }
                    if ($null -eq $decoded) { $entry.skip_reason = 'No supported primitive or embedded PNG/JPEG; arbitrary-object deserialisation is prohibited.' }
                    else {
                        $safeStem = ($key -replace '[^a-zA-Z0-9@._-]', '_').Trim('.')
                        if ([string]::IsNullOrWhiteSpace($safeStem)) { $safeStem = 'resource' }
                        if ($safeStem -match '^(?:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)') { $safeStem = '_' + $safeStem }
                        if ($safeStem.Length -gt 120) { $safeStem = $safeStem.Substring(0, 108) + '-' + $entry.raw_sha256.Substring(0, 10) }
                        $relative = $source.directory + '/' + $safeStem + '.' + $decoded.extension
                        if (-not $safeNames.Add($relative)) { throw ('Filename collision: ' + $relative) }
                        $target = [IO.Path]::GetFullPath((Join-Path $outputRoot $relative))
                        if (-not $target.StartsWith($outputRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe output path.' }
                        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
                        [IO.File]::WriteAllBytes($target, $decoded.bytes)
                        $entry.extracted = $true
                        $entry.path = $relative
                        $entry.media_type = $decoded.mime
                        $entry.size_bytes = $decoded.bytes.Length
                        $entry.sha256 = Get-BytesHash $decoded.bytes
                        $entry.pixel_width = $decoded.width
                        $entry.pixel_height = $decoded.height
                        if ($null -ne $decoded.width) {
                            $density = if ($key -match '@2x$') { 2 } else { 1 }
                            $entry.logical_width = $decoded.width / $density
                            $entry.logical_height = $decoded.height / $density
                        }
                        $entry.embedded_payload_offset = $decoded.offset
                    }
                } else { $entry.skip_reason = 'Outside the interface/font/HUD selection.' }
                $entries.Add($entry)
            }
        } finally { $reader.Dispose() }
    }

    $catalogue = [ordered]@{
        schema_version = 1
        purpose = 'Local, read-only-source inspection. This export does not publish files; scripts/client/stage-stable-interface-assets.ps1 selects the explicit optional interface subset.'
        extraction_method = 'ResourceReader.GetResourceData; safe primitive length prefix, embedded PNG signature/chunk boundaries, or embedded JPEG primitive byte-array length/SOF/EOI. No BinaryFormatter or arbitrary object deserialisation.'
        selection_pattern = $selection
        lazer_comparison = 'Exact case-insensitive filename stem in the installed osu.Game.Resources.dll, with @2x normalised; absence does not imply an equivalent lazer widget does not exist.'
        sources = @($sources.ToArray())
        resource_count = $entries.Count
        selected_count = @($entries | Where-Object { $_.selected }).Count
        extracted_count = @($entries | Where-Object { $_.extracted }).Count
        entries = @($entries.ToArray())
    }
    [IO.File]::WriteAllText((Join-Path $outputRoot 'catalogue.json'), ($catalogue | ConvertTo-Json -Depth 12), $utf8)
    [pscustomobject]@{ catalogue = (Join-Path $outputRoot 'catalogue.json'); resources = $catalogue.resource_count; selected = $catalogue.selected_count; extracted = $catalogue.extracted_count } | ConvertTo-Json -Compress
} finally { $sha.Dispose() }
