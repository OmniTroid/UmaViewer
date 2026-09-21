#!/usr/bin/env bash
# Zip a built player as dist/UmaViewer-<platform>-<sha>.zip. Build first with tools/build-<x>.sh.
# Usage: tools/package.sh windows|mac|web
set -euo pipefail

PLATFORM="${1:-}"
REPO="$(cd "$(dirname "$0")/.." && pwd)"
cd "$REPO"

case "$PLATFORM" in
  # Candidate output dirs: local build script first, then the game-ci path used in CI.
  windows) CANDS=(Build/Windows build/StandaloneWindows64); MODE=contents; BUILD=tools/build-win.sh ;;
  mac)     CANDS=(Build/UmaViewer.app build/StandaloneOSX/UmaViewer.app); MODE=self; BUILD=tools/build-mac.sh ;;
  web)     CANDS=(Build/Web build/WebGL/WebGL); MODE=contents; BUILD=tools/build-web.sh ;;
  *) echo "Usage: tools/package.sh windows|mac|web"; exit 2 ;;
esac

# Package only a clean commit, so the SHA in the name matches exactly what was built.
[ -z "$(git status --porcelain)" ] || { echo "Working tree is dirty. Commit or stash before packaging."; exit 1; }
command -v zip >/dev/null 2>&1 || { echo "'zip' is required but not found."; exit 1; }

SRC=""
for c in "${CANDS[@]}"; do [ -e "$c" ] && SRC="$c" && break; done
[ -n "$SRC" ] || { echo "No build found (looked in: ${CANDS[*]}). Run $BUILD first."; exit 1; }

SHA="$(git rev-parse --short=7 HEAD)"
OUT="$REPO/dist/UmaViewer-$PLATFORM-$SHA.zip"
mkdir -p "$REPO/dist"
rm -f "$OUT"

# -y keeps symlinks as symlinks (the mac .app bundle needs them).
if [ "$MODE" = contents ]; then
  (cd "$SRC" && zip -r -y -q "$OUT" .)
else
  (cd "$(dirname "$SRC")" && zip -r -y -q "$OUT" "$(basename "$SRC")")
fi

echo "Packaged -> dist/UmaViewer-$PLATFORM-$SHA.zip"
