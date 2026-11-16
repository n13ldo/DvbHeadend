#!/usr/bin/env bash
# Builds, runs the tests and produces ./publish (portable: runs on arm64 and x64 alike).
set -euo pipefail
cd "$(dirname "$0")"
dotnet run -c Release --project tests/DvbHeadend.Tests
dotnet publish src/DvbHeadend -c Release -o publish -p:UseAppHost=false
echo "Done. Copy this folder to the Pi and run: sudo deploy/install.sh"
