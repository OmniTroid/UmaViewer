# Exporting models and animations

Headless CLI (`Assets/Scripts/CliExporter.cs`, enabled by `--export`) that writes an MMD
**PMX** (+ textures) and a **VMD** motion for viewers like [babylon-mmd](https://github.com/noname0310/babylon-mmd), reusing the in-app exporters.

## Prerequisites

- A built player. macOS: `tools/build-mac.sh` → `Build/UmaViewer.app`. Windows: `tools/build-win.sh` → `Build/Windows/UmaViewer.exe` (needs the "Windows Build Support (Mono)" editor module), or the CI IL2CPP artifact from `.github/workflows/build.yml`.
- A game data folder (the `Persistent` directory) with the encrypted `meta` DB and asset bundles.
- Run with `-batchmode` but **not** `-nographics`: a null GPU device loads no meshes/textures.

## Command

```
Build/UmaViewer.app/Contents/MacOS/UmaViewer -batchmode --export \
  --data-path /path/to/Persistent --region jp \
  --chara 1127 --costume 00 --anim anm_eve_chr1127_00_idle01_loop \
  --out ./export/1127 -logFile ./logs/export.log
```

On Windows the only changes are the executable (`Build\Windows\UmaViewer.exe`) and the `--data-path`.

### Arguments

| Arg | Meaning |
|---|---|
| `--data-path DIR` | Game data folder (contains `meta`). Required. |
| `--chara ID` | Character id, e.g. `1127`. Required. |
| `--costume NN` | Costume id (default `00`). |
| `--anim NAME` | Motion asset name, e.g. `anm_eve_chr1127_00_idle01_loop`. Omit to export the model only. |
| `--out DIR` | Output directory. |
| `--region jp\|global` | Which DB key/asset set to decrypt. Defaults to `Config.json` (`jp`). The `meta` DB is region-encrypted, so a wrong region fails to load. |
| `--no-model` | Skip the PMX + textures; export only the VMD (see below). |
| `--blink` | Bake a periodic eye-blink (`まばたき`) over the motion. |
| `--no-mouth` | Strip the mouth vowel morphs (`あいうえお▲□`) so a viewer can drive lip-sync at runtime. |
| `--seconds N` | Record N seconds raw (no loop trim) instead of one period; for `tools/loopify_vmd.py`. |

### Output

- `chr<id>_<costume>.pmx` — clean MMD skeleton (game control/IK bones stripped, their weights baked onto standard bones; parents ordered before children).
- `Texture2D/` — the model's textures.
- `chr<id>_<costume>.vmd` — the motion, when `--anim` is given. `_loop` motions are trimmed to one seamless period.

## Model and motion are separate

Export a model once, then batch any number of motions against it:

```
# once: model + textures (no --anim)
UmaViewer --export --chara 1127 --out ./export/1127

# then: VMD only, model untouched (--no-model)
UmaViewer --export --no-model --chara 1127 --anim anm_eve_chr1127_00_idle01_loop --out ./export/1127
UmaViewer --export --no-model --chara 1127 --anim anm_eve_type00_run01_loop      --out ./export/1127
```

VMD recording still loads the character each time (unavoidable headless); `--no-model` just skips re-writing the PMX/textures.

## Seamless loops for fast motions

The CLI trims a `_loop` motion using the clip length as the period. For fast cyclic motions (runs) whose true period differs from the clip length, that leaves a small seam. Use `tools/export-anim.sh`, which records several strides and then finds the real period and crossfades:

```
tools/export-anim.sh --data-path /path/to/Persistent --chara 1127 \
  --anim anm_eve_type00_run01_loop --blink --no-mouth --tiles 4 --out ./export/1127
```

It records `--seconds` of raw motion, runs `tools/loopify_vmd.py` (full-body resample → fundamental-period search → tail crossfade → optional blink/mouth-strip), and leaves the PMX + textures + a seamless VMD. Runs on macOS/Linux and Git Bash/WSL on Windows; set `UMAVIEWER_BIN` to point at a player elsewhere. `--tiles N` repeats the loop (e.g. a less frequent blink).

## Notes for babylon-mmd

- Bone and morph names are MMD-standard Japanese (`センター`, `右足`, `あ`, `まばたき`, …); the PMX also stores the game bone name as each bone's English name.
- Skirt/hair/ear/tail physics is exported as **PMX rigid bodies + joints** derived from the game's CySpring spring bones (kinematic colliders on the body/legs, dynamic bodies on the swaying bones, joints along each chain). Enable babylon-mmd's physics runtime so it simulates them; otherwise those bones stay static and the skirt clips. It's a CySpring→MMD approximation (different solver), so mass/damping/stiffness/collider sizes are heuristic and tunable, not an exact match.
- The VMD carries **no camera** data (model motion only), and no physics-bone tracks (physics is simulated at runtime from the PMX bodies, not baked).
- Facial morphs are a separate channel: a motion with a paired `_face` asset records real expressions; otherwise the mouth stays neutral. With `--no-mouth`, drive the vowel morphs (`あいうえお`) yourself at runtime.
