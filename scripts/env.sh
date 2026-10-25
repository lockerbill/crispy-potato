#!/usr/bin/env bash
# Source this only for development in a restricted cloud workspace.
studio_repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
studio_tools_root="${STUDIO_TOOLS_ROOT:-/workspace/.tools}"
if [ -x "$studio_tools_root/dotnet/dotnet" ]; then export PATH="$studio_tools_root/dotnet:$PATH"; fi
export DOTNET_CLI_HOME="$studio_tools_root/dotnet-home"
export NUGET_PACKAGES="$studio_tools_root/nuget"
export XDG_RUNTIME_DIR="$studio_tools_root/runtime"
export XDG_CONFIG_HOME="$studio_tools_root/config"
export XDG_DATA_HOME="$studio_tools_root/data"
export XDG_CACHE_HOME="$studio_tools_root/cache"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
mkdir -p "$XDG_RUNTIME_DIR" "$XDG_CACHE_HOME"
chmod 700 "$XDG_RUNTIME_DIR"
