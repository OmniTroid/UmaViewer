#!/usr/bin/env bash
# Build a macOS standalone player (Mono backend) headlessly.
# Uses Assets/Editor/HeadlessMacBuild.cs (HeadlessMacBuild.BuildMono).
# The project's default scripting backend is IL2CPP (not installed on most macOS
# setups); the build method temporarily switches to Mono2x and restores it.
set -euo pipefail
REPO="$(cd "$(dirname "$0")/.." && pwd)"
VER="2022.3.62f3"

# --release: plain release player. Default is a Development build for local debugging.
UMA_ARGS=""
for arg in "$@"; do
  case "$arg" in
    --release) UMA_ARGS="--release" ;;
    *) echo "Unknown option: $arg" >&2; exit 2 ;;
  esac
done

UNITY=""
for c in \
  "/Applications/Unity/Hub/Editor/$VER/Unity.app/Contents/MacOS/Unity" \
  "/Applications/Unity/Unity.app/Contents/MacOS/Unity"; do
  [ -x "$c" ] && UNITY="$c" && break
done
[ -n "$UNITY" ] || { echo "Unity $VER not found (install via Unity Hub)"; exit 1; }

# A build that dies (compile error, kill, crash) leaves Temp/UnityLockfile behind, and every
# later build then refuses to start while blaming an editor that is not running. Only treat the
# lock as real if a Unity process actually exists.
if [ -e "$REPO/Temp/UnityLockfile" ]; then
  if command -v pgrep >/dev/null 2>&1 && pgrep -x Unity >/dev/null 2>&1; then
    echo "Project is open in the Unity Editor. Quit it first (the build needs the lock)."; exit 1
  fi
  echo "Removing stale Temp/UnityLockfile (no Unity process running)."
  rm -f "$REPO/Temp/UnityLockfile"
fi

mkdir -p "$REPO/logs"
LOG="$REPO/logs/player-build.log"
echo "Building Build/UmaViewer.app  (log: $LOG)"
"$UNITY" -quit -batchmode -nographics -projectPath "$REPO" \
  -executeMethod HeadlessMacBuild.BuildMono $UMA_ARGS -logFile "$LOG" || true

if grep -q "BUILD_OK" "$LOG"; then
  echo "OK -> $REPO/Build/UmaViewer.app"
else
  echo "BUILD FAILED. Tail of log:"; tail -20 "$LOG"; exit 1
fi
