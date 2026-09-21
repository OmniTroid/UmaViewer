using System;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

public static class HeadlessWebBuild
{
    // WebGL only supports IL2CPP, so there's no backend to switch (unlike the mac/win Mono
    // builds). Outputs a hostable folder: Build/Web/index.html + Build/Web/Build/*.
    // --release (tools/build.sh web --release) makes a lean prod build; the default is a
    // debug-friendly build with full stack traces.
    public static void Build()
    {
        bool release = Environment.GetCommandLineArgs().Contains("--release");

        // Bake the git SHA in as the version (also the WebGL data-cache key, so caches bust per
        // commit); restored after the build. See BuildVersioning.
        string originalVersion = BuildVersioning.Stamp();

        // These WebGL settings persist to ProjectSettings.asset, and exceptionSupport differs
        // between debug and release, so capture and restore them or a build leaves the tracked
        // file modified (a release build flipping it to lean would look like a spurious diff).
        var origTemplate = PlayerSettings.WebGL.template;
        var origDecompression = PlayerSettings.WebGL.decompressionFallback;
        var origDataCaching = PlayerSettings.WebGL.dataCaching;
        var origExceptionSupport = PlayerSettings.WebGL.exceptionSupport;

        PlayerSettings.WebGL.template = "PROJECT:UmaViewer";
        // Decompress gzipped build files in the loader so any static host works, even one
        // that doesn't send Content-Encoding: gzip (e.g. python3 -m http.server).
        PlayerSettings.WebGL.decompressionFallback = true;
        // Cache Build/Web.data in the browser so returning visitors don't re-download ~46MB every
        // load. Unity keys the cache by the productVersion set above, which busts it per build.
        PlayerSettings.WebGL.dataCaching = true;
        // Debug: full managed stack traces so caught exceptions can be located. Release: the lean
        // default (explicitly-thrown only), smaller and faster.
        PlayerSettings.WebGL.exceptionSupport = release
            ? WebGLExceptionSupport.ExplicitlyThrownExceptionsOnly
            : WebGLExceptionSupport.FullWithStacktrace;

        Debug.Log($"[HeadlessWebBuild] {(release ? "RELEASE" : "DEBUG")} build");

        var options = new BuildPlayerOptions
        {
            scenes = new[] { "Assets/Scenes/Version2.unity", "Assets/Scenes/LiveScene.unity" },
            locationPathName = "Build/Web",
            target = BuildTarget.WebGL,
            // Release player, not Development: the Development flag hid the in-game top menu bar
            // on WebGL. We don't need it anyway, since ReleaseLogGate keeps WebGL logging on and
            // exceptionSupport above keeps full managed stack traces.
            options = BuildOptions.None,
        };
        var report = BuildPipeline.BuildPlayer(options);

        PlayerSettings.WebGL.template = origTemplate;
        PlayerSettings.WebGL.decompressionFallback = origDecompression;
        PlayerSettings.WebGL.dataCaching = origDataCaching;
        PlayerSettings.WebGL.exceptionSupport = origExceptionSupport;
        BuildVersioning.Restore(originalVersion); // restores bundleVersion and SaveAssets

        var s = report.summary;
        if (s.result != BuildResult.Succeeded)
        {
            Debug.LogError($"BUILD_FAILED result={s.result} errors={s.totalErrors}");
            EditorApplication.Exit(1);
            return;
        }
        Debug.Log($"BUILD_OK path={s.outputPath} sizeBytes={s.totalSize}");
        EditorApplication.Exit(0);
    }
}
