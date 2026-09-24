#!/usr/bin/env bash
set -euo pipefail

build_path="./bin/Release"
source_dll="$build_path/osu.Game.Rulesets.EnhancedAuth.source.dll"
output_dll="$build_path/osu.Game.Rulesets.EnhancedAuth.dll"
harmony_dll="$HOME/.nuget/packages/lib.harmony/2.4.1/lib/net8.0/0Harmony.dll"
newtonsoft_dll="$(find "$HOME/.nuget/packages/newtonsoft.json" -path '*/lib/net6.0/Newtonsoft.Json.dll' -print | sort -V | tail -n 1)"
newtonsoft_dir="$(dirname "$newtonsoft_dll")"

dotnet build -c Release -o "$build_path"
mv "$output_dll" "$source_dll"
dotnet tool restore
dotnet tool run ilrepack \
    -out:"$output_dll" \
    "$source_dll" \
    "$harmony_dll" \
    -lib:./fakelib \
    -lib:"$newtonsoft_dir" \
    /internalize

test -s "$output_dll"
