#!/usr/bin/env bash
# Build a Windows x64 standalone player (Mono backend) headlessly.
# Uses Assets/Editor/HeadlessWinBuild.cs (HeadlessWinBuild.BuildMono).
# Requires the "Windows Build Support (Mono)" module for this editor (install via
# Unity Hub). IL2CPP for Windows cannot be produced on macOS, so this builds Mono;
# for an IL2CPP release use the GitHub Actions workflow (.github/workflows/build.yml).
set -euo pipefail
REPO="$(cd "$(dirname "$0")/.." && pwd)"
VER="2022.3.62f3"

UNITY=""
for c in \
  "/Applications/Unity/Hub/Editor/$VER/Unity.app/Contents/MacOS/Unity" \
  "/Applications/Unity/Unity.app/Contents/MacOS/Unity"; do
  [ -x "$c" ] && UNITY="$c" && break
done
[ -n "$UNITY" ] || { echo "Unity $VER not found (install via Unity Hub)"; exit 1; }

if [ -e "$REPO/Temp/UnityLockfile" ]; then
  echo "Project is open in the Unity Editor. Quit it first (the build needs the lock)."; exit 1
fi

mkdir -p "$REPO/logs"
LOG="$REPO/logs/player-build-win.log"
echo "Building Build/Windows/UmaViewer.exe  (log: $LOG)"
"$UNITY" -quit -batchmode -nographics -projectPath "$REPO" -buildTarget Win64 \
  -executeMethod HeadlessWinBuild.BuildMono -logFile "$LOG" || true

if grep -q "BUILD_OK" "$LOG"; then
  echo "OK -> $REPO/Build/Windows/UmaViewer.exe"
else
  echo "BUILD FAILED. Tail of log:"; tail -30 "$LOG"
  if grep -qiE "no.*module|StandaloneWindows64|windows.*support|BuildTarget is not supported" "$LOG"; then
    echo "-> Install 'Windows Build Support (Mono)' for $VER in Unity Hub, then retry."
  fi
  exit 1
fi
