# WebGL build

Compile UmaViewer to a hostable WebGL page. WebGL is IL2CPP-only (already the Standalone default).

## Build

Needs the "WebGL Build Support" editor module (Unity Hub → add modules).

```
bash tools/build-web.sh          # -> Build/Web/ (serve: cd Build/Web && python3 -m http.server)
```

Uses `Assets/Editor/HeadlessWebBuild.cs`.

## Native plugins on WebGL

WebGL links native code statically and resolves P/Invoke through `__Internal`, so every
`[DllImport]` symbol must exist at link time. Status per plugin:

- **CySpring** — done. The managed `CySpringSolver` runs on WebGL (`CySpringNative.isNative` is false there); `Assets/Plugins/WebGL/CySpring.jslib` stubs the unused native symbols so the link succeeds.
- **sqlite3mc** (decrypts the `meta` DB) — **required**, the binding is set to `__Internal` (`Sqlite3MC.cs`); build the [SQLite3MultipleCiphers](https://github.com/utelle/SQLite3MultipleCiphers) amalgamation with Emscripten and add it as a WebGL plugin under `Assets/Plugins/WebGL/`. `Mono.Data.Sqlite`'s `sqlite3_*` imports resolve to the same statically-linked lib.
- **lame** (MP3 export) — not needed by a viewer; stub it (a jslib like CySpring's) or `#if !UNITY_WEBGL` the audio-export code.
- **StandaloneFileBrowser** (native file dialog) — replace with a browser picker (File System Access API) via a jslib.

## Runtime work (loading game data in a browser)

- **Data folder**: browsers have no filesystem. Pick the `Persistent` folder with the File System Access API, read bytes in JS, and feed them to the sqlite3mc DB and the asset loaders (the synchronous `System.IO` paths need an async browser-FS shim).
- **Threading**: ~13 `Thread`/`Task.Run` sites; move viewer-path ones to coroutine/main-thread, or enable pthreads (SharedArrayBuffer + COOP/COEP headers).
