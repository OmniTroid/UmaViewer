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

## Remaining: loading game data in a browser

- **Data folder**: browsers have no filesystem. Pick the `Persistent` folder with the File System Access API, read bytes in JS, and feed them to the sqlite3mc DB and the asset loaders (the synchronous `System.IO` paths need an async browser-FS shim).
- **Threading**: ~13 `Thread`/`Task.Run` sites; move viewer-path ones to coroutine/main-thread, or enable pthreads (SharedArrayBuffer + COOP/COEP headers).
