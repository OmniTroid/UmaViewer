#!/usr/bin/env bash
# Build a UmaViewer player headlessly. Usage: tools/build.sh <win|mac|web> [--release]
# --release: plain release player (web: lean prod with data caching). Default is a debug-friendly
# build (web: full stack traces; standalone: a Development build). WebGL is IL2CPP-only; standalone
# builds temporarily switch to Mono2x (the build methods restore it).
set -euo pipefail
REPO="$(cd "$(dirname "$0")/.." && pwd)"
VER="2022.3.62f3"

PLATFORM=""
UMA_ARGS=""
for arg in "$@"; do
  case "$arg" in
    web) PLATFORM="web" ;;
    mac) PLATFORM="mac" ;;
    win) PLATFORM="win" ;;
    --release) UMA_ARGS="--release" ;;
    *) echo "Unknown argument: $arg" >&2; exit 2 ;;
  esac
done
[ -n "$PLATFORM" ] || { echo "Usage: tools/build.sh <win|mac|web> [--release]"; exit 2; }

case "$PLATFORM" in
  web) TARGET_OPT="-buildTarget WebGL"; METHOD=HeadlessWebBuild.Build;     LOG=player-build-web.log; OUT="Build/Web";                  MODULE="WebGL Build Support";          OK_HINT="  (serve it: python3 tools/serve-web.py)" ;;
  win) TARGET_OPT="-buildTarget Win64"; METHOD=HeadlessWinBuild.BuildMono; LOG=player-build-win.log; OUT="Build/Windows/UmaViewer.exe"; MODULE="Windows Build Support (Mono)"; OK_HINT="" ;;
  mac) TARGET_OPT="";                   METHOD=HeadlessMacBuild.BuildMono; LOG=player-build.log;     OUT="Build/UmaViewer.app";         MODULE="Mac Build Support (Mono)";     OK_HINT="" ;;
esac

# Unity is a native binary on Windows: hand it Windows paths, not MSYS ones.
winpath() { if command -v cygpath >/dev/null 2>&1; then cygpath -w "$1"; else printf %s "$1"; fi; }

# Find the editor (mac and windows candidates; missing paths just skip).
UNITY=""
for c in \
  "/Applications/Unity/Hub/Editor/$VER/Unity.app/Contents/MacOS/Unity" \
  "/Applications/Unity/Unity.app/Contents/MacOS/Unity" \
  "/c/Program Files/Unity/Hub/Editor/$VER/Editor/Unity.exe" \
  "/c/Program Files/Unity/Editor/Unity.exe" \
  "${PROGRAMFILES:-/c/Program Files}/Unity/Hub/Editor/$VER/Editor/Unity.exe"; do
  [ -x "$c" ] && UNITY="$c" && break
done
[ -n "$UNITY" ] || { echo "Unity $VER not found (install via Unity Hub)"; exit 1; }

# A build that dies (compile error, kill, crash) leaves Temp/UnityLockfile behind, and every later
# build then refuses while blaming an editor that is not running. Only treat the lock as real if a
# Unity process actually exists.
if [ -e "$REPO/Temp/UnityLockfile" ]; then
  unity_running=""
  if command -v tasklist >/dev/null 2>&1; then
    tasklist //FI "IMAGENAME eq Unity.exe" 2>/dev/null | grep -qi "Unity.exe" && unity_running=1
  elif command -v pgrep >/dev/null 2>&1; then
    pgrep -x Unity >/dev/null 2>&1 && unity_running=1
  else
    unity_running=1   # cannot tell; assume the lock is real
  fi
  if [ -n "$unity_running" ]; then
    echo "Project is open in the Unity Editor. Quit it first (the build needs the lock)."; exit 1
  fi
  echo "Removing stale Temp/UnityLockfile (no Unity process running)."
  rm -f "$REPO/Temp/UnityLockfile"
fi

mkdir -p "$REPO/logs"
LOGPATH="$REPO/logs/$LOG"
echo "Building $OUT${UMA_ARGS:+ (release)}  (log: $LOGPATH)"
"$UNITY" -quit -batchmode -nographics -projectPath "$(winpath "$REPO")" $TARGET_OPT \
  -executeMethod "$METHOD" $UMA_ARGS -logFile "$(winpath "$LOGPATH")" || true

if grep -q "BUILD_OK" "$LOGPATH"; then
  echo "OK -> $REPO/$OUT$OK_HINT"
else
  echo "BUILD FAILED."
  if grep -q "error CS" "$LOGPATH"; then
    echo "-> Compile errors:"; grep -m 20 "error CS" "$LOGPATH" | sed 's/^/   /'
  else
    echo "Tail of log:"; tail -30 "$LOGPATH"
    if grep -qiE "module is not installed|BuildTarget is not supported|no .* module (is )?(installed|found)" "$LOGPATH"; then
      echo "-> Install '$MODULE' for $VER in Unity Hub, then retry."
    fi
  fi
  exit 1
fi
