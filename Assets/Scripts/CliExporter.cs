using System;
using System.Collections;
using System.Collections.Generic;
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
            // Head/eye look-at (FinalIK) aims at a camera-following target; headless
            // that target is arbitrary and won't loop, so the head pops. Export the
            // raw animation and let the consumer add look-at themselves.
            container.SetHeadTracking(false);
            container.EnableEyeTracking = false;
            float len = (clip != null && clip.length > 0.01f) ? clip.length : 5f;
            bool isLoop = animId.Contains("loop") || (clip != null && clip.name.Contains("loop"));

            // For a loop, let the crossfade finish and the motion settle so frame 0
            // matches where the loop ends (warming whole periods also starts capture
            // at the clip's own phase 0); otherwise the bind->idle blend makes the
            // seam pop. Non-loop clips are recorded from the start.
            if (isLoop)
            {
                float warm = 0f, warmTarget = Mathf.Max(1.5f, len * 2f);
                while (warm < warmTarget) { warm += Time.deltaTime; yield return null; }
            }

            var rootbone = container.transform.Find("Position");
            var rec = rootbone.gameObject.AddComponent<UnityHumanoidVMDRecorder>();
            rec.Initialize();
            yield return null;            // one frame so the animator is posed at t=0
            rec.StartRecording();
            // For a loop, record one period minus a frame so the wrap (last frame ->
            // frame 0) is a single step instead of a duplicate/hold.
            float recLen = isLoop ? Mathf.Max(len - (1f / 30f), 0.1f) : len;
            float e2 = 0f;
            while (e2 < recLen) { e2 += Time.deltaTime; yield return null; }
            rec.StopRecording();
            string vmdPath = Path.Combine(outDir, $"{Path.GetFileNameWithoutExtension(pmxPath)}.vmd");
            try { rec.SaveVMD(Path.GetFileNameWithoutExtension(pmxPath), vmdPath); }
            catch (Exception ex) { Fail("VMD save threw: " + ex); yield break; }
            // Drop the recorder's transient first frame (a stale head pose at capture
            // start), so a loop wraps cleanly and frame 0 is the settled pose.
            try { TrimFirstFrame(vmdPath); }
            catch (Exception ex) { Debug.LogWarning("CLI_EXPORT: frame-0 trim skipped: " + ex); }
            Debug.Log("CLI_EXPORT: wrote " + vmdPath);
        }

        Debug.Log("CLI_EXPORT_DONE " + outDir);
        Quit(0);
    }

    // Drop every keyframe at frame 0 and shift the rest down by one, for the bone and
    // morph sections; the camera/light/shadow/IK sections (empty here) are copied as-is.
    static void TrimFirstFrame(string path)
    {
        byte[] d = File.ReadAllBytes(path);
        var outp = new List<byte>(d.Length);
        outp.AddRange(new ArraySegment<byte>(d, 0, 50)); // header(30) + model name(20)
        int o = 50;
        o = TrimSection(d, o, 111, outp);  // bones
        o = TrimSection(d, o, 23, outp);   // morphs
        outp.AddRange(new ArraySegment<byte>(d, o, d.Length - o)); // remaining sections
        File.WriteAllBytes(path, outp.ToArray());
    }

    // A VMD keyframe section: int32 count, then `count` records of `recSize` bytes with
    // the frame number at byte offset 15. Keeps frame>=1 records, renumbered frame-1.
    static int TrimSection(byte[] d, int o, int recSize, List<byte> outp)
    {
        int count = BitConverter.ToInt32(d, o); o += 4;
        var kept = new List<byte[]>(count);
        for (int i = 0; i < count; i++, o += recSize)
        {
            uint fr = BitConverter.ToUInt32(d, o + 15);
            if (fr < 1) continue;
            var r = new byte[recSize];
            Array.Copy(d, o, r, 0, recSize);
            Array.Copy(BitConverter.GetBytes(fr - 1), 0, r, 15, 4);
            kept.Add(r);
        }
        outp.AddRange(BitConverter.GetBytes(kept.Count));
        foreach (var r in kept) outp.AddRange(r);
        return o;
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
