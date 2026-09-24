#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")/osu.Game.Rulesets.EnhancedAuth"
# A separate output keeps a running client's loaded DLL intact during a fix.
release_name="${1:-SomsClientRelease}"
if [[ ! "$release_name" =~ ^[A-Za-z0-9][A-Za-z0-9._-]*$ ]]; then
    echo "Invalid release directory name" >&2
    exit 1
fi
build_path="./bin/$release_name"
merged_path="$build_path/merged"

dotnet build -c Release -o "$build_path/source" --nologo
dotnet tool restore

harmony_dll="$HOME/.nuget/packages/lib.harmony/2.4.1/lib/net8.0/0Harmony.dll"
newtonsoft_dll="$(find "$HOME/.nuget/packages/newtonsoft.json" -path '*/lib/net6.0/Newtonsoft.Json.dll' -print | sort -V | tail -n 1)"
framework_dll="$(find "$HOME/.nuget/packages/ppy.osu.framework" -name 'osu.Framework.dll' -print | sort -V | tail -n 1)"
mkdir -p "$merged_path"
dotnet tool run ilrepack \
    -out:"$merged_path/osu.Game.Rulesets.EnhancedAuth.dll" \
    "$build_path/source/osu.Game.Rulesets.EnhancedAuth.dll" \
    "$harmony_dll" \
    -lib:"$(dirname "$harmony_dll")" \
    -lib:"$(dirname "$framework_dll")" \
    -lib:"$build_path/source" \
    -lib:./fakelib \
    -lib:"$(dirname "$newtonsoft_dll")" \
    /internalize

test -s "$merged_path/osu.Game.Rulesets.EnhancedAuth.dll"
