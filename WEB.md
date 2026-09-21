# WebGL build

Compile UmaViewer to a hostable WebGL page. WebGL is IL2CPP-only (already the Standalone default).

## Build

Needs the "WebGL Build Support" editor module (Unity Hub → add modules).

```
bash tools/build-web.sh          # -> Build/Web/ (serve: cd Build/Web && python3 -m http.server)
```

Uses `Assets/Editor/HeadlessWebBuild.cs`.

The build links and produces a hostable page. Serve it over HTTP (WebGL can't run from
`file://`):

```
cd Build/Web && python3 -m http.server   # then open http://localhost:8000
```

## Native plugins on WebGL (all resolved)

WebGL links native code statically, so every `[DllImport]` symbol must exist at link time.

- **CySpring** — the managed `CySpringSolver` runs (`CySpringNative.isNative` is false on WebGL); `Assets/Plugins/WebGL/CySpring.jslib` stubs the unused native symbols.
- **sqlite3mc** (decrypts the `meta` DB) — `Assets/Plugins/WebGL/libsqlite3mc.a`, the [SQLite3MultipleCiphers](https://github.com/utelle/SQLite3MultipleCiphers) amalgamation compiled with Unity's Emscripten (`tools/build-sqlite3mc-wasm.sh`). Both `Sqlite3MC.cs` (`__Internal`) and `Mono.Data.Sqlite`'s `sqlite3_*` imports resolve to it.
- **StandaloneFileBrowser** — ships its own `.jslib`; no work needed.
- **lame** (MP3 export) — not reachable from the viewer scenes, so IL2CPP strips it. Only matters if you wire audio export into a WebGL build.

## Loading game data in a browser (Chromium only)

The template (`Assets/WebGLTemplates/UmaViewer/index.html`) gates startup on a folder pick:
the user selects their `Cygames/umamusume` folder via the File System Access API before Unity
boots. Files are read locally, nothing is uploaded. Non-Chromium browsers (no
`showDirectoryPicker`) are told to switch.

The data folder is mounted at `/uma` in Emscripten's MEMFS. Every reader opens by path
(sqlite `meta`/`master.mdb`, `AssetBundle.LoadFromFile`, the encrypted `FileStream`), so files
are faulted in on demand just before each synchronous open:

- `Assets/Plugins/WebGL/UmaWebFS.jslib` — walks the picked directory handle, reads a file,
  writes it into MEMFS at the same path. Async begin/poll so managed coroutines can await it.
- `WebFileMount.cs` — `Ensure(path)` coroutine over that bridge; no-op off WebGL.
- Hooks: `Config` pins `MainPath=/uma` / Default mode / no download; `UmaViewerMain.Start`
  mounts the two DBs then the `livesettings`/`shader` bundles; `UmaAssetManager.PreLoadAsset`
  mounts each bundle (and its deps) before acquiring. Unmounted synchronous loads (e.g. boot
  icons) skip quietly rather than error.

Known gaps: boot character/live icons load synchronously and are not pre-mounted, so they show
blank until wired through a coroutine; `master.mdb` is large and lives fully in MEMFS (heap
pressure). Threading (~13 `Thread`/`Task.Run` sites) is unrelated to the local file path;
the runtime download path is disabled on WebGL.
