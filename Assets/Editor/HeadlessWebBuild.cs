using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

public static class HeadlessWebBuild
{
    // WebGL only supports IL2CPP, so there's no backend to switch (unlike the mac/win Mono
    // builds). Outputs a hostable folder: Build/Web/index.html + Build/Web/Build/*.
    public static void Build()
    {
        PlayerSettings.WebGL.template = "PROJECT:UmaViewer";
        // Decompress gzipped build files in the loader so any static host works, even one
        // that doesn't send Content-Encoding: gzip (e.g. python3 -m http.server).
        PlayerSettings.WebGL.decompressionFallback = true;
        // Without this Unity caches Build/Web.data in IndexedDB, so a rebuilt .data (all
        // compiled C# and scenes) keeps serving stale over the same URL during local dev.
        PlayerSettings.WebGL.dataCaching = false;
        // Default WebGL exception support strips managed stack traces (e.StackTrace is null),
        // so caught exceptions can't be located. Full stack traces are needed while porting.
        PlayerSettings.WebGL.exceptionSupport = WebGLExceptionSupport.FullWithStacktrace;

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
