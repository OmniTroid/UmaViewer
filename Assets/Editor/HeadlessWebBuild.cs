using System;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

public static class HeadlessWebBuild
{
    // WebGL only supports IL2CPP, so there's no backend to switch (unlike the mac/win Mono
    // builds). Outputs a hostable folder: Build/Web/index.html + Build/Web/Build/*.
    // Pass -umaRelease (tools/build-web.sh --release) for a lean prod build; the default is a
    // debug-friendly build with full stack traces.
    public static void Build()
    {
        bool release = Environment.GetCommandLineArgs().Contains("-umaRelease");

        // Bake the 7-char git SHA in as the version, so a build is traceable to its commit and
        // WebGL data caching (below) invalidates per commit. A dirty tree shares its commit SHA,
        // so append a timestamp there to keep local rebuilds from serving a stale cached .data.
        // Restored after the build so it doesn't leave ProjectSettings.asset modified in git.
        string originalVersion = PlayerSettings.bundleVersion;
        PlayerSettings.bundleVersion = BuildVersion();

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

        PlayerSettings.bundleVersion = originalVersion;
        AssetDatabase.SaveAssets();

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

    static string BuildVersion()
    {
        string sha = Git("rev-parse --short=7 HEAD");
        if (string.IsNullOrEmpty(sha)) return "0000000";
        bool dirty = !string.IsNullOrEmpty(Git("status --porcelain"));
        return dirty ? $"{sha}.{DateTime.UtcNow:yyyyMMddHHmmss}" : sha;
    }

    static string Git(string args)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("git", args)
            {
                WorkingDirectory = System.IO.Path.GetDirectoryName(Application.dataPath),
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = System.Diagnostics.Process.Start(psi);
            string outp = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit();
            return outp;
        }
        catch { return ""; }
    }
}
