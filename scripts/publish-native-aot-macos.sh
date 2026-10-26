#!/usr/bin/env bash
set -euo pipefail
project=${1:-src/tests/WebRTC.AotSmoke}
output=${2:-artifacts/aot-macos-arm64}
mkdir -p "$output"
output=$(cd "$output" && pwd)
dotnet restore "$project" -r osx-arm64 -p:PublishAot=true -p:Configuration=Release
original_targets=$(dotnet msbuild "$project" -nologo -p:PublishAot=true -p:RuntimeIdentifier=osx-arm64 -p:Configuration=Release -getProperty:ILCompilerTargetsPath)
patched_targets=$(python3 scripts/patch-apple-native-aot-targets.py "$original_targets" "$output/compiler-targets")
dotnet publish "$project" -c Release -warnaserror -r osx-arm64 -p:PublishAot=true \
  -p:ILCompilerTargetsPath="$patched_targets" -o "$output"
