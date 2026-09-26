#!/bin/zsh
set -euo pipefail

repo=$(cd "$(dirname "$0")/.." && pwd)
app="$repo/macos/build/TamagoMac.app"
contents="$app/Contents"
mkdir -p "$contents/MacOS" "$contents/Resources/tamago"
mkdir -p "$repo/macos/build/ModuleCache"

CLANG_MODULE_CACHE_PATH="$repo/macos/build/ModuleCache" \
xcrun swiftc -O -target x86_64-apple-macos13.0 \
    -module-cache-path "$repo/macos/build/ModuleCache" \
    "$repo/macos/TamagoMac.swift" "$repo/macos/MacAgentCore.swift" "$repo/macos/MacConversation.swift" \
    "$repo/macos/MacQwenAdapter.swift" "$repo/macos/MacAgentUI.swift" \
    -o "$contents/MacOS/TamagoMac"
cp "$repo/macos/Info.plist" "$contents/Info.plist"
cp "$repo/assets/tamago-app-icon.png" "$contents/Resources/AppIcon.png"
cp "$repo/content/pets/tamago/manifest.json" "$contents/Resources/tamago/manifest.json"
cp "$repo/content/pets/tamago/profile.json" "$contents/Resources/tamago/profile.json"
cp "$repo/content/pets/tamago/"*.png "$contents/Resources/tamago/"
echo "Built: $app"
