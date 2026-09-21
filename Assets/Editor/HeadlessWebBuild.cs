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

        var options = new BuildPlayerOptions
        {
            scenes = new[] { "Assets/Scenes/Version2.unity", "Assets/Scenes/LiveScene.unity" },
            locationPathName = "Build/Web",
            target = BuildTarget.WebGL,
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
