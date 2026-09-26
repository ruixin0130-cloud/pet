#!/bin/zsh
set -euo pipefail

repo=$(cd "$(dirname "$0")/.." && pwd)
mkdir -p "$repo/macos/build/ModuleCache"
xcrun swiftc -target x86_64-apple-macos13.0 \
    -module-cache-path "$repo/macos/build/ModuleCache" \
    "$repo/macos/MacAgentCore.swift" "$repo/macos/MacConversation.swift" \
    "$repo/macos/MacAgentUI.swift" "$repo/macos/MacAgentUITests.swift" \
    -o "$repo/macos/build/MacAgentUITests"
"$repo/macos/build/MacAgentUITests" "$repo/macos/build/agent-conversation-ui.png"
