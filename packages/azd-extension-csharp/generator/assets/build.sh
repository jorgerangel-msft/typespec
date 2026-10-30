#!/usr/bin/env bash
set -euo pipefail
extension_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
entry_point="$(dotnet msbuild "$extension_dir/Extension.csproj" -getProperty:AssemblyName -nologo)"
output="${OUTPUT_DIR:-"$extension_dir/bin"}"
mkdir -p "$output"
for platform in ${EXTENSION_PLATFORM:-windows/amd64 windows/arm64 linux/amd64 linux/arm64 darwin/amd64 darwin/arm64}; do
  case "${platform%/*}" in
    windows) os=win; suffix=.exe ;;
    darwin) os=osx; suffix= ;;
    linux) os=linux; suffix= ;;
    *) echo "Unsupported platform: $platform" >&2; exit 1 ;;
  esac
  case "${platform#*/}" in
    amd64) arch=x64 ;;
    arm64) arch=arm64 ;;
    *) echo "Unsupported architecture: $platform" >&2; exit 1 ;;
  esac
  publish="$extension_dir/obj/publish/$os-$arch"
  dotnet publish "$extension_dir/Extension.csproj" -c Release -r "$os-$arch" --self-contained true -p:PublishSingleFile=true -o "$publish" --nologo
  cp "$publish/$entry_point$suffix" "$output/$entry_point-${platform%/*}-${platform#*/}$suffix"
done
