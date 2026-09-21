#!/usr/bin/env bash
# Build a release player and zip it as dist/UmaViewer-<platform>-<sha>.zip.
# Web is deployed (tools/deploy-pages.sh), not packaged. Usage: tools/package.sh win|mac
set -euo pipefail

PLATFORM="${1:-}"
REPO="$(cd "$(dirname "$0")/.." && pwd)"
cd "$REPO"

case "$PLATFORM" in
  win) BUILD=(tools/build.sh win --release); OUT="Build/Windows";       MODE=contents ;;
  mac) BUILD=(tools/build.sh mac --release); OUT="Build/UmaViewer.app";  MODE=self ;;
  *) echo "Usage: tools/package.sh win|mac"; exit 2 ;;
esac

# Release from a clean commit, so the SHA baked into the build and used in the name is meaningful.
[ -z "$(git status --porcelain)" ] || { echo "Working tree is dirty. Commit or stash before releasing."; exit 1; }
command -v zip >/dev/null 2>&1 || { echo "'zip' is required but not found."; exit 1; }

bash "${BUILD[@]}"
[ -e "$OUT" ] || { echo "Build did not produce $OUT."; exit 1; }

SHA="$(git rev-parse --short=7 HEAD)"
DEST="$REPO/dist/UmaViewer-$PLATFORM-$SHA.zip"
mkdir -p "$REPO/dist"
rm -f "$DEST"

# -y keeps symlinks as symlinks (the mac .app bundle needs them).
if [ "$MODE" = contents ]; then
  (cd "$OUT" && zip -r -y -q "$DEST" .)
else
  (cd "$(dirname "$OUT")" && zip -r -y -q "$DEST" "$(basename "$OUT")")
fi

echo "Released -> dist/UmaViewer-$PLATFORM-$SHA.zip"
