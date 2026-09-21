using System;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

public static class HeadlessMacBuild
{
    public static void BuildMono()
    {
        // --release (tools/build-mac.sh --release) builds a plain release player; the default is
        // a Development build (profiler + script debugging) for local iteration.
        bool release = Environment.GetCommandLineArgs().Contains("--release");
        var group = BuildTargetGroup.Standalone;
        var original = PlayerSettings.GetScriptingBackend(group);
        PlayerSettings.SetScriptingBackend(group, ScriptingImplementation.Mono2x);
        string version = BuildVersioning.Stamp();
        try
        {
            var options = new BuildPlayerOptions
            {
                scenes = new[] { "Assets/Scenes/Version2.unity", "Assets/Scenes/LiveScene.unity" },
                locationPathName = "Build/UmaViewer.app",
                target = BuildTarget.StandaloneOSX,
                options = release ? BuildOptions.None : BuildOptions.Development,
            };
            var report = BuildPipeline.BuildPlayer(options);
            var s = report.summary;
            if (s.result != BuildResult.Succeeded)
            {
                Debug.LogError($"BUILD_FAILED result={s.result} errors={s.totalErrors}");
                PlayerSettings.SetScriptingBackend(group, original);
                EditorApplication.Exit(1);
                return;
            }
            Debug.Log($"BUILD_OK path={s.outputPath} sizeBytes={s.totalSize}");
        }
        finally { PlayerSettings.SetScriptingBackend(group, original); BuildVersioning.Restore(version); }
        EditorApplication.Exit(0);
    }
}
