#!/bin/zsh
set -euo pipefail

repo=$(cd "$(dirname "$0")/.." && pwd)
mkdir -p "$repo/macos/build/ModuleCache"
CLANG_MODULE_CACHE_PATH="$repo/macos/build/ModuleCache" \
xcrun swiftc -target x86_64-apple-macos13.0 \
    -module-cache-path "$repo/macos/build/ModuleCache" \
    "$repo/macos/MacAgentCore.swift" "$repo/macos/MacQwenAdapter.swift" \
    "$repo/macos/MacAgentTests.swift" -o "$repo/macos/build/MacAgentTests"
"$repo/macos/build/MacAgentTests"
