using System;
using System.Collections;
using System.IO;
using System.Linq;
using UnityEngine;

// Headless export mode: reuses the in-app exporters to write a PMX (+textures) and
// a VMD for babylon-mmd, driven by command-line args. Enabled by --export.
//
//   UmaViewer -batchmode --export \
//     --data-path /path/to/Persistent --chara 1127 --costume 00 \
//     --anim anm_eve_chr1127_00_idle01_loop --out ./export/1127 \
//     -logFile ./logs/export.log [--region jp|global]
//
// Run with -batchmode but NOT -nographics: character meshes/textures need a real
// GfxDevice (a Null device loads nothing). macOS batchmode uses an offscreen Metal
// context. Runs in the real player loop so singletons, coroutines, and the recorder work.
public class CliExporter : MonoBehaviour
{
    static string[] Argv => Environment.GetCommandLineArgs();
    static bool Flag(string f) => Argv.Contains(f);
    static string Opt(string name, string def = null)
    {
        var a = Argv;
        for (int i = 0; i < a.Length - 1; i++) if (a[i] == name) return a[i + 1];
        return def;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void PreInit()
    {
        if (!Flag("--export")) return;
        // Point the runtime at the requested data folder before the DB loads in Awake.
        var dp = Opt("--data-path");
        if (!string.IsNullOrEmpty(dp)) Config.Instance.MainPath = dp;
        var region = Opt("--region");
        if (region == "global") Config.Instance.Region = Region.Global;
        else if (region == "jp") Config.Instance.Region = Region.Jp;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (!Flag("--export")) return;
        var go = new GameObject("CliExporter");
        DontDestroyOnLoad(go);
        go.AddComponent<CliExporter>().StartCoroutine(Run());
    }

    static IEnumerator Run()
    {
        string charaId = Opt("--chara");
        string costume = Opt("--costume", "00");
        string animId = Opt("--anim");
        string outDir = Path.GetFullPath(Opt("--out", "export"));

        yield return null; // let scene Awake/Start run

        // Wait for the database (Characters list is built once meta is read).
        float t = 0f;
        while ((UmaViewerMain.Instance == null || UmaViewerMain.Instance.Characters.Count == 0) && t < 60f)
        {
            t += Time.unscaledDeltaTime;
            yield return null;
        }
        var main = UmaViewerMain.Instance;
        if (main == null || main.Characters.Count == 0) { Fail("database not ready (check --data-path / --region)"); yield break; }

        var chara = main.Characters.FirstOrDefault(c => c.Id.ToString() == charaId);
        if (chara == null) { Fail($"character id '{charaId}' not found ({main.Characters.Count} known)"); yield break; }

        Directory.CreateDirectory(outDir);

        Exception buildErr = null;
        yield return RunSafe(UmaViewerBuilder.Instance.LoadUma(chara, costume, false, ""), e => buildErr = e);
        if (buildErr != null) { Fail("LoadUma threw: " + buildErr); yield break; }
        var container = UmaViewerBuilder.Instance.CurrentUMAContainer;
        if (container == null) { Fail("character build produced no container"); yield break; }
        yield return null;

        // --- PMX (+ textures) ---
        string pmxPath = Path.Combine(outDir, $"chr{charaId}_{costume}.pmx");
        try { ModelExporter.ExportModel(container, pmxPath); }
        catch (Exception ex) { Fail("PMX export threw: " + ex); yield break; }
        Debug.Log("CLI_EXPORT: wrote " + pmxPath);

        // --- VMD (one animation) ---
        if (!string.IsNullOrEmpty(animId))
        {
            var anim = main.AbMotions.FirstOrDefault(e => e.Name == animId)
                    ?? main.AbMotions.FirstOrDefault(e => e.Name.Contains(animId));
            if (anim == null) { Fail($"animation '{animId}' not found"); yield break; }

            AnimationClip clip = null;
            try { clip = anim.Get<AnimationClip>(); } catch { }
            container.LoadAnimation(anim);
            float len = (clip != null && clip.length > 0.01f) ? clip.length : 5f;

            var rootbone = container.transform.Find("Position");
            var rec = rootbone.gameObject.AddComponent<UnityHumanoidVMDRecorder>();
            rec.Initialize();
            yield return null;            // one frame so the animator is posed at t=0
            rec.StartRecording();
            float e2 = 0f;
            while (e2 < len) { e2 += Time.deltaTime; yield return null; }
            rec.StopRecording();
            string vmdPath = Path.Combine(outDir, $"{Path.GetFileNameWithoutExtension(pmxPath)}.vmd");
            try { rec.SaveVMD(Path.GetFileNameWithoutExtension(pmxPath), vmdPath); }
            catch (Exception ex) { Fail("VMD save threw: " + ex); yield break; }
            Debug.Log("CLI_EXPORT: wrote " + vmdPath);
        }

        Debug.Log("CLI_EXPORT_DONE " + outDir);
        Quit(0);
    }

    // Run a coroutine to completion, capturing any exception (so we can fail cleanly).
    static IEnumerator RunSafe(IEnumerator inner, Action<Exception> onError)
    {
        while (true)
        {
            object cur;
            try { if (!inner.MoveNext()) yield break; cur = inner.Current; }
            catch (Exception ex) { onError(ex); yield break; }
            yield return cur;
        }
    }

    static void Fail(string msg) { Debug.LogError("CLI_EXPORT_FAIL: " + msg); Quit(1); }

    static void Quit(int code)
    {
        Debug.Log("CLI_EXPORT quitting code=" + code);
        Application.Quit(code);
    }
}
