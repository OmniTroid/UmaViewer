#!/usr/bin/env bash
# Export a character PMX (+textures) and a seamless looping VMD in one step.
# Records several strides raw via the player's --export --seconds, then loopify_vmd.py
# finds the true loop period and builds a clean loop (optionally blink / mouth-free).
# Runs anywhere bash + python exist (macOS, Linux, Git Bash/WSL on Windows).
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"; REPO="$(cd "$HERE/.." && pwd)"

DATA=""; CHARA=""; ANIM=""; OUT=""; REGION="jp"; COSTUME="00"; SECS=5; TILES=1; BLINK=""; NOMOUTH=""
while [ $# -gt 0 ]; do case "$1" in
  --data-path) DATA="$2"; shift 2;;
  --chara)     CHARA="$2"; shift 2;;
  --anim)      ANIM="$2"; shift 2;;
  --out)       OUT="$2"; shift 2;;
  --region)    REGION="$2"; shift 2;;
  --costume)   COSTUME="$2"; shift 2;;
  --seconds)   SECS="$2"; shift 2;;
  --tiles)     TILES="$2"; shift 2;;
  --blink)     BLINK="--blink"; shift;;
  --no-mouth)  NOMOUTH="--no-mouth"; shift;;
  *) echo "unknown arg: $1"; exit 1;;
esac; done

if [ -z "$DATA" ] || [ -z "$CHARA" ] || [ -z "$ANIM" ] || [ -z "$OUT" ]; then
  echo "usage: $0 --data-path DIR --chara ID --anim NAME --out DIR \\"
  echo "          [--region jp|global] [--costume 00] [--seconds 5] [--tiles N] [--blink] [--no-mouth]"
  exit 1
fi

BIN="${UMAVIEWER_BIN:-}"
if [ -z "$BIN" ]; then
  for c in \
    "$REPO/Build/UmaViewer.app/Contents/MacOS/UmaViewer" \
    "$REPO/Build/Windows/UmaViewer.exe" \
    "$REPO/Build/UmaViewer.exe" \
    "$REPO/Build/UmaViewer.x86_64"; do
    if [ -e "$c" ]; then BIN="$c"; break; fi
  done
fi
[ -n "$BIN" ] || { echo "UmaViewer player not found; build it (tools/build-mac.sh or build-win.sh) or set UMAVIEWER_BIN"; exit 1; }
PY="$(command -v python3 || command -v python || true)"
[ -n "$PY" ] || { echo "python not found"; exit 1; }

mkdir -p "$OUT" "$REPO/logs"; RAW="$OUT/.raw"; mkdir -p "$RAW"
STEM="chr${CHARA}_${COSTUME}"
echo "Recording ${SECS}s of $ANIM (chara $CHARA) ..."
"$BIN" -batchmode --export --data-path "$DATA" --region "$REGION" \
  --chara "$CHARA" --costume "$COSTUME" --anim "$ANIM" --seconds "$SECS" \
  --out "$RAW" -logFile "$REPO/logs/export-anim.log"

[ -f "$RAW/$STEM.vmd" ] || { echo "raw export failed; see logs/export-anim.log"; exit 1; }
cp -f "$RAW/$STEM.pmx" "$OUT/$STEM.pmx"
[ -d "$RAW/Texture2D" ] && cp -Rf "$RAW/Texture2D" "$OUT/"

echo "Building seamless loop ..."
"$PY" "$HERE/loopify_vmd.py" "$RAW/$STEM.vmd" "$OUT/$STEM.vmd" --tiles "$TILES" $BLINK $NOMOUTH
rm -rf "$RAW"
echo "Done -> $OUT/$STEM.pmx + $OUT/$STEM.vmd"
