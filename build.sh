#!/usr/bin/env bash
set -e
echo "Building SendProxy..."
rm -rf .build
dotnet build src/YappersHQ.SendProxy/YappersHQ.SendProxy.csproj -c Release
# Copy assets (gamedata) into the build output tree.
if [ -d .asset ]; then cp -r .assets/* .build/ 2>/dev/null || true; fi
echo "Build complete -> .build/"
