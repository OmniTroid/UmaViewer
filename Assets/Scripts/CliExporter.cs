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
//     -logFile ./logs/export.log [--region jp|global] [--blink] [--no-mouth]
//
// Model and motion are separable: omit --anim to export just the PMX (+textures); pass
// --no-model with --anim to export just the VMD (reusing a model you exported once).
// --blink injects a periodic eye-blink; --no-mouth strips the mouth vowel morphs so a
// viewer can drive lip-sync live (together: a talkable loop). --seconds N records N
// seconds raw (no loop trim) instead of one period.
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
        // Optional: record this many seconds raw (no loop trimming) instead of one
        // period, e.g. to capture several strides of a run for external loop-finding.
        float.TryParse(Opt("--seconds", "0"), out float recordSeconds);
        bool addBlink = Flag("--blink");    // inject a periodic まばたき (blink) track
        bool dropMouth = Flag("--no-mouth"); // strip mouth vowel morphs (drive them live)
        bool noModel = Flag("--no-model");   // skip the PMX+textures, export only the VMD

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

        if (noModel && string.IsNullOrEmpty(animId)) { Fail("--no-model needs --anim (nothing to export)"); yield break; }

        // --- PMX (+ textures) --- unless --no-model (reuse a previously exported model)
        string pmxPath = Path.Combine(outDir, $"chr{charaId}_{costume}.pmx");
        if (!noModel)
        {
            try { ModelExporter.ExportModel(container, pmxPath); }
            catch (Exception ex) { Fail("PMX export threw: " + ex); yield break; }
            Debug.Log("CLI_EXPORT: wrote " + pmxPath);
        }

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
            // Capture a full period plus a small margin so frame P (== phase 0 of the
            // next cycle) is present; the loop trim below keeps exactly one period.
            // --seconds overrides this to record raw for external loop-finding.
            float recLen = recordSeconds > 0f ? recordSeconds : (isLoop ? len + 0.1f : len);
            float e2 = 0f;
            while (e2 < recLen) { e2 += Time.deltaTime; yield return null; }
            rec.StopRecording();
            string vmdPath = Path.Combine(outDir, $"{Path.GetFileNameWithoutExtension(pmxPath)}.vmd");
            try { rec.SaveVMD(Path.GetFileNameWithoutExtension(pmxPath), vmdPath); }
            catch (Exception ex) { Fail("VMD save threw: " + ex); yield break; }
            // Drop the recorder's transient first frame (a stale pose at capture start).
            // For a loop, keep exactly frames 1..P (P = one period) so the seam is a
            // single step regardless of how fast the motion is; the wrap is phase0<-phaseP-1.
            // With --seconds we keep everything (raw multi-period capture).
            int period = (recordSeconds > 0f || !isLoop) ? int.MaxValue : Mathf.RoundToInt(len * 30f);
            try { TrimLoop(vmdPath, period, dropMouth, addBlink); }
            catch (Exception ex) { Debug.LogWarning("CLI_EXPORT: loop trim skipped: " + ex); }
            Debug.Log("CLI_EXPORT: wrote " + vmdPath);
        }

        Debug.Log("CLI_EXPORT_DONE " + outDir);
        Quit(0);
    }

    // Standard MMD mouth-shape morphs, dropped by --no-mouth so a viewer can drive
    // lip-sync live. Expression morphs (笑い/怒り/まばたき…) are kept.
    static readonly string[] MouthMorphs =
        { "あ", "い", "う", "え", "お", "あ2", "い2", "う2", "え2", "お2", "▲", "□" };

    // Drop the transient frame 0 and keep frames 1..period, renumbered to 0..period-1,
    // for the bone and morph sections; camera/light/shadow/IK sections are copied as-is.
    // A non-loop clip passes period=int.MaxValue (keep every frame after 0).
    // dropMouth removes mouth morphs; addBlink replaces any blink track with one periodic
    // blink over the loop.
    static void TrimLoop(string path, int period, bool dropMouth, bool addBlink)
    {
        var enc = ShiftJisOrUtf8();
        byte[] d = File.ReadAllBytes(path);
        var outp = new List<byte>(d.Length);
        outp.AddRange(new ArraySegment<byte>(d, 0, 50)); // header(30) + model name(20)
        int o = 50;
        int maxFrame = 0;
        o = TrimBones(d, o, period, outp, ref maxFrame);
        o = TrimMorphs(d, o, period, dropMouth, addBlink, maxFrame, enc, outp);
        outp.AddRange(new ArraySegment<byte>(d, o, d.Length - o)); // remaining sections
        File.WriteAllBytes(path, outp.ToArray());
    }

    static System.Text.Encoding ShiftJisOrUtf8()
    {
        try { return System.Text.Encoding.GetEncoding("shift_jis"); }
        catch { return System.Text.Encoding.UTF8; }
    }

    // Bone section (111-byte records, frame at offset 15): keep 1<=frame<=period,
    // renumber frame-1; report the highest kept (renumbered) frame.
    static int TrimBones(byte[] d, int o, int period, List<byte> outp, ref int maxFrame)
    {
        int count = BitConverter.ToInt32(d, o); o += 4;
        var kept = new List<byte[]>(count);
        for (int i = 0; i < count; i++, o += 111)
        {
            uint fr = BitConverter.ToUInt32(d, o + 15);
            if (fr < 1 || fr > (uint)period) continue;
            var r = new byte[111];
            Array.Copy(d, o, r, 0, 111);
            uint nf = fr - 1;
            Array.Copy(BitConverter.GetBytes(nf), 0, r, 15, 4);
            if (nf > (uint)maxFrame) maxFrame = (int)nf;
            kept.Add(r);
        }
        outp.AddRange(BitConverter.GetBytes(kept.Count));
        foreach (var r in kept) outp.AddRange(r);
        return o;
    }

    // Morph section (23-byte records: 15-byte name, 4-byte frame, 4-byte weight). Same
    // keep/renumber as bones, minus mouth morphs (dropMouth) and any existing blink when
    // addBlink is set; a synthesized periodic blink is then appended.
    static int TrimMorphs(byte[] d, int o, int period, bool dropMouth, bool addBlink,
                          int maxFrame, System.Text.Encoding enc, List<byte> outp)
    {
        byte[] blinkName = enc.GetBytes("まばたき");
        int count = BitConverter.ToInt32(d, o); o += 4;
        var kept = new List<byte[]>(count);
        for (int i = 0; i < count; i++, o += 23)
        {
            string name = NameAt(d, o, enc);
            uint fr = BitConverter.ToUInt32(d, o + 15);
            if (fr < 1 || fr > (uint)period) continue;
            if (dropMouth && Array.IndexOf(MouthMorphs, name) >= 0) continue;
            if (addBlink && name == "まばたき") continue;
            var r = new byte[23];
            Array.Copy(d, o, r, 0, 23);
            Array.Copy(BitConverter.GetBytes(fr - 1), 0, r, 15, 4);
            kept.Add(r);
        }
        if (addBlink)
            foreach (var (f, w) in BlinkKeys(maxFrame))
                kept.Add(MorphRecord(blinkName, f, w));
        outp.AddRange(BitConverter.GetBytes(kept.Count));
        foreach (var r in kept) outp.AddRange(r);
        return o;
    }

    static string NameAt(byte[] d, int o, System.Text.Encoding enc)
    {
        int n = 0; while (n < 15 && d[o + n] != 0) n++;
        return enc.GetString(d, o, n);
    }

    // One 0->1->0 blink at the loop midpoint (clamped for very short loops).
    static (int, float)[] BlinkKeys(int maxFrame)
    {
        if (maxFrame < 6) return new[] { (0, 0f), (Math.Max(1, maxFrame / 2), 1f), (maxFrame, 0f) };
        int c = maxFrame / 2;
        return new[] { (0, 0f), (c - 2, 0f), (c, 1f), (c + 3, 0f), (maxFrame, 0f) };
    }

    static byte[] MorphRecord(byte[] name, int frame, float weight)
    {
        var r = new byte[23];
        Array.Copy(name, 0, r, 0, Math.Min(name.Length, 15));
        Array.Copy(BitConverter.GetBytes((uint)frame), 0, r, 15, 4);
        Array.Copy(BitConverter.GetBytes(weight), 0, r, 19, 4);
        return r;
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
