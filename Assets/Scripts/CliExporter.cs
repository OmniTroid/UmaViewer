using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Globalization;
using UnityEngine;

// Headless export mode: writes a PMX (+textures) via ModelExporter and a VMD via
// MotionExporter (shared with the GUI's Export section), driven by command-line args.
// Enabled by --export.
//
//   UmaViewer -batchmode --export \
//     --data-path /path/to/Persistent --chara 1127 --costume 00 \
//     --anim anm_eve_chr1127_00_idle01_loop --out ./export/1127 \
//     -logFile ./logs/export.log [--region jp|global] [--blink] [--no-mouth]
//
// --pmx-name / --vmd-name override the generated chr<id>_<costume> filenames; the
// extension is optional, and the VMD defaults to the model's stem.
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
        // This runs BeforeSceneLoad, ahead of the Awake that normally creates the Config,
        // so Config.Instance is still null here. Create it now: the overrides below have to
        // be in place before the database loads, and UmaViewerMain.Awake leaves an existing
        // instance alone.
        if (Config.Instance == null) new Config();
        // Point the runtime at the requested data folder before the DB loads in Awake.
        var dp = Opt("--data-path");
        if (!string.IsNullOrEmpty(dp)) Config.Instance.MainPath = dp;
        var region = Opt("--region");
        if (region == "global") Config.Instance.Region = Region.Global;
        else if (region == "jp") Config.Instance.Region = Region.Jp;
        // Force the managed CySpring solver (the C# port) instead of the native plugin.
        if (Flag("--managed-physics")) Gallop.CySpringNative.isNative = false;
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
        // --bake-physics: record the CySpring-simulated spring bones as VMD tracks and write
        // the PMX without rigid bodies, instead of shipping bodies for a runtime to simulate.
        // CySpring is a Verlet solver with a hard length constraint; PMX physics is rigid
        // bodies and 6DOF springs, so the runtime can only approximate it. Baking ships the
        // solver's own output. The cost is that the cloth no longer reacts at runtime.
        bool bakePhysics = Flag("--bake-physics");

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
        yield return MotionExporter.RunSafe(UmaViewerBuilder.Instance.LoadUma(chara, costume, false, ""), e => buildErr = e);
        if (buildErr != null) { Fail("LoadUma threw: " + buildErr); yield break; }
        var container = UmaViewerBuilder.Instance.CurrentUMAContainer;
        if (container == null) { Fail("character build produced no container"); yield break; }
        yield return null;

        // --physics-ref <file.json>: record the CySpring-simulated Sp_* bone rotations per
        // frame (physics ON) to JSON, for comparing/calibrating against the exported PMX
        // physics or baking the exact motion. Run on Windows for the real CySpring.dll.
        // --spring-dump <file.json>: write the raw CySpring per-bone parameters. The solver
        // scales them (StiffnessForce/1000, DragForce/100, Gravity/10000; CySpringPlugin.dll
        // constants at 0x18008ec8c, 0x18008ec80, 0x18008ec90), so the raw ranges decide how they
        // map onto PMX.
        string dumpPath = Opt("--spring-dump");
        if (!string.IsNullOrEmpty(dumpPath))
        {
            var sbp = new StringBuilder();
            var ciD = CultureInfo.InvariantCulture;
            sbp.Append("[");
            bool first = true;
            foreach (var c in container.cySpringDataContainers)
            {
                if (c == null || c.springParam == null) continue;
                foreach (var e in c.springParam)
                {
                    if (e == null || string.IsNullOrEmpty(e.BoneName)) continue;
                    if (!first) sbp.Append(",");
                    first = false;
                    sbp.AppendFormat(ciD, "{{\"n\":\"{0}\",\"root\":1,\"stiff\":{1},\"drag\":{2},\"grav\":{3},\"rad\":{4},\"lim\":{5}}}",
                        e.BoneName, e.StiffnessForce, e.DragForce, e.Gravity, e.CollisionRadius, e._isLimit ? 1 : 0);
                    if (e._childElements == null) continue;
                    foreach (var ce in e._childElements)
                    {
                        if (ce == null || string.IsNullOrEmpty(ce.Name)) continue;
                        sbp.AppendFormat(ciD, ",{{\"n\":\"{0}\",\"root\":0,\"stiff\":{1},\"drag\":{2},\"grav\":{3},\"rad\":{4},\"lim\":{5}}}",
                            ce.Name, ce.StiffnessForce, ce.DragForce, ce.Gravity, ce.CollisionRadius, ce.IsLimit ? 1 : 0);
                    }
                }
            }
            sbp.Append("]");
            try { File.WriteAllText(dumpPath, sbp.ToString()); }
            catch (Exception ex) { Fail("spring-dump threw: " + ex); yield break; }
            Debug.Log("CLI_EXPORT: wrote spring dump " + dumpPath);
            Quit(0); yield break;
        }

        // --probe <name>: describe a motion (parts, companions, lengths) and quit. See MotionProbe.
        string probeName = Opt("--probe");
        if (!string.IsNullOrEmpty(probeName))
        {
            Debug.Log("CLI_PROBE\n" + MotionProbe.Describe(MotionProbe.Probe(main, probeName)));
            Quit(0); yield break;
        }

        // --list-anims <substring>: print the motion assets whose name contains the substring.
        // Asset names are not guessable -- a character often has both a generic type00 motion
        // and its own chr<id> variant, and picking the wrong one exports a different animation
        // than the viewer shows.
        string listPat = Opt("--list-anims");
        if (!string.IsNullOrEmpty(listPat))
        {
            var hits = main.AbMotions.Where(e => e.Name.Contains(listPat)).Select(e => e.Name).OrderBy(n => n).ToList();
            Debug.Log($"CLI_EXPORT: {hits.Count} motion(s) containing '{listPat}'");
            foreach (var n in hits) Debug.Log("CLI_ANIM " + n);
            Quit(0); yield break;
        }

        // --solver-diff <file.txt>: single-step differential of CySpringSolver against
        // CySpringPlugin.dll (see CySpringDiff). Windows only; forces the native path.
        string diffPath = Opt("--solver-diff");
        if (!string.IsNullOrEmpty(diffPath))
        {
            if (string.IsNullOrEmpty(animId)) { Fail("--solver-diff needs --anim"); yield break; }
            Gallop.CySpringNative.isNative = true;
            Gallop.CySpringDiff.Reset();
            Gallop.CySpringSolver.ResetCounters();
            float.TryParse(Opt("--solver-diff-collide", "1"), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float collideScale);
            Gallop.CySpringDiff.CollideScale = collideScale;
            Gallop.CySpringDiff.CollideMode = Opt("--solver-diff-collide-mode", "");
            if (collideScale != 1f) Debug.Log($"CLI_EXPORT: solver-diff scaling collider radii by {collideScale}");
            Gallop.CySpringDiff.Armed = true;
            yield return MotionExporter.RunSafe(RecordPhysicsRef(container, main, animId, diffPath + ".ref.json"), e => buildErr = e);
            Gallop.CySpringDiff.Armed = false;
            if (buildErr != null) { Fail("solver-diff threw: " + buildErr); yield break; }
            Debug.Log($"CLI_BRANCH clampCalls={Gallop.CySpringSolver.ClampCalls} clampBites={Gallop.CySpringSolver.ClampBites} collCalls={Gallop.CySpringSolver.CollisionCalls} collHits={Gallop.CySpringSolver.CollisionHits} sphere={Gallop.CySpringSolver.HitSphere} capMid={Gallop.CySpringSolver.HitCapsuleMid} capEnd={Gallop.CySpringSolver.HitCapsuleEnd} plane={Gallop.CySpringSolver.HitPlane} inner={Gallop.CySpringSolver.SeenInner} skipChara={Gallop.CySpringSolver.SkipChara} skirtKnee={Gallop.CySpringSolver.SkirtKneeHits}");
            Debug.Log("CLI_COLL types=" + string.Join(",", Gallop.CySpringSolver.TypeSeen) + " disabled=" + Gallop.CySpringSolver.SkipDisabled);
            Gallop.CySpringDiff.Write(diffPath);
            Debug.Log("DIFF" + System.Environment.NewLine + Gallop.CySpringDiff.Report());
            Debug.Log("CLI_EXPORT_DONE " + diffPath);
            Quit(0); yield break;
        }

        // --solver-trace <file.csv>: per-call solver state, inputs and outputs, for either
        // solver (see CySpringTrace).
        string tracePath = Opt("--solver-trace");

        string physRefPath = Opt("--physics-ref");
        if (!string.IsNullOrEmpty(physRefPath))
        {
            if (!string.IsNullOrEmpty(tracePath)) { Gallop.CySpringTrace.Reset(); Gallop.CySpringTrace.Armed = true; }
            yield return MotionExporter.RunSafe(RecordPhysicsRef(container, main, animId, physRefPath), e => buildErr = e);
            if (buildErr != null) { Fail("physics-ref threw: " + buildErr); yield break; }
            if (!string.IsNullOrEmpty(tracePath)) { Gallop.CySpringTrace.Armed = false; Gallop.CySpringTrace.Write(tracePath); }
            Debug.Log($"CLI_BRANCH clampCalls={Gallop.CySpringSolver.ClampCalls} clampBites={Gallop.CySpringSolver.ClampBites} collCalls={Gallop.CySpringSolver.CollisionCalls} collHits={Gallop.CySpringSolver.CollisionHits} sphere={Gallop.CySpringSolver.HitSphere} capMid={Gallop.CySpringSolver.HitCapsuleMid} capEnd={Gallop.CySpringSolver.HitCapsuleEnd} plane={Gallop.CySpringSolver.HitPlane} inner={Gallop.CySpringSolver.SeenInner} skipChara={Gallop.CySpringSolver.SkipChara} skirtKnee={Gallop.CySpringSolver.SkirtKneeHits}");
            Debug.Log("CLI_EXPORT_DONE " + physRefPath);
            Quit(0); yield break;
        }

        if (noModel && string.IsNullOrEmpty(animId)) { Fail("--no-model needs --anim (nothing to export)"); yield break; }

        // --- PMX (+ textures) --- unless --no-model (reuse a previously exported model)
        // --pmx-name / --vmd-name override the generated names; the extension is optional.
        string pmxPath = Path.Combine(outDir, WithExtension(Opt("--pmx-name"), $"chr{charaId}_{costume}", ".pmx"));
        if (!noModel)
        {
            try { MotionExporter.ExportModel(container, pmxPath, bakePhysics); }
            catch (Exception ex) { Fail("PMX export threw: " + ex); yield break; }
            Debug.Log("CLI_EXPORT: wrote " + pmxPath);
        }

        // --- VMD (one animation) --- see MotionExporter for the capture itself
        if (!string.IsNullOrEmpty(animId))
        {
            var anim = MotionExporter.FindMotion(main, animId, out string note);
            if (note != null) Debug.LogWarning("CLI_EXPORT: " + note);
            if (anim == null) { Fail($"animation '{animId}' not found"); yield break; }
            Debug.Log("CLI_EXPORT: motion asset = " + anim.Name);

            var opt = new MotionExporter.Options
            {
                BakePhysics = bakePhysics, RecordSeconds = recordSeconds, DropMouth = dropMouth, AddBlink = addBlink,
                IncludeCamera = !Flag("--no-camera"),   // a _cam companion is recorded into the same VMD unless told not to
            };
            int.TryParse(Opt("--physics-fps", "60"), out opt.PhysicsFps);
            float.TryParse(Opt("--warmup", "2"), out opt.WarmupPeriods);
            int.TryParse(Opt("--align-lead", "3"), out opt.AlignLead);
            // Defaults to the model's stem so a model/motion pair stays matched.
            string vmdPath = Path.Combine(outDir, WithExtension(Opt("--vmd-name"), Path.GetFileNameWithoutExtension(pmxPath), ".vmd"));
            Exception recErr = null;
            yield return MotionExporter.RunSafe(
                MotionExporter.Record(container, anim, vmdPath, Path.GetFileNameWithoutExtension(pmxPath), opt), e => recErr = e);
            if (recErr != null) { Fail("VMD export threw: " + recErr); yield break; }
            Debug.Log("CLI_EXPORT: wrote " + vmdPath);
        }

        Debug.Log("CLI_EXPORT_DONE " + outDir);
        Quit(0);
    }

    static IEnumerator RecordPhysicsRef(UmaContainerCharacter container, UmaViewerMain main, string animId, string outPath)
    {
        if (string.IsNullOrEmpty(animId)) { Debug.LogError("CLI_EXPORT_FAIL: --physics-ref needs --anim"); yield break; }
        var anim = main.AbMotions.FirstOrDefault(e => e.Name == animId) ?? main.AbMotions.FirstOrDefault(e => e.Name.Contains(animId));
        if (anim == null) { Debug.LogError("CLI_EXPORT_FAIL: animation not found: " + animId); yield break; }
        AnimationClip clip = null; try { clip = anim.Get<AnimationClip>(); } catch { }
        float len = (clip != null && clip.length > 0.01f) ? clip.length : 5f;

        container.SetDynamicBoneEnable(true);   // ensure CySpring physics is running
        container.LoadAnimation(anim);

        // --physics-fps <30|60>: step rate for the capture. CySpringController hardcodes its
        // fps mode to 60 and never reads the config, so a 30fps capture tells the solver 60
        // while stepping it at 1/30. Keep the two in agreement, and sample every other frame
        // when stepping at 60 so the output stays a 30fps track either way.
        // Default 60: that is what the game runs at, and CySpring's per-step constants make
        // its output strongly rate-dependent -- stepping the same motion at 30 instead moves
        // the tail bones by ~50 degrees on average.
        int.TryParse(Opt("--physics-fps", "60"), out int physFps);
        if (physFps != 60) physFps = 30;
        int stride = physFps / 30;

        int prevCapture = Time.captureFramerate; float prevFixed = Time.fixedDeltaTime;
        Time.captureFramerate = physFps; Time.fixedDeltaTime = 1f / physFps;
        var ctrl = container.GetComponentInChildren<Gallop.CySpringController>(true);
        bool prevMode = ctrl != null && ctrl.Is60FpsMode;
        if (ctrl != null) ctrl.Is60FpsMode = (physFps == 60);
        Debug.Log($"CLI_EXPORT: physics-ref stepping at {physFps}fps, solver 60fps-mode={(ctrl != null ? ctrl.Is60FpsMode.ToString() : "n/a")}");

        // --warmup <periods>: settle time before recording, in clip lengths. 0 records from
        // the bind pose.
        float.TryParse(Opt("--warmup", "2"), out float warmP);
        float warm = 0f, warmTarget = Mathf.Max(warmP <= 0f ? 0f : 1.5f, len * warmP);
        while (warm < warmTarget) { warm += Time.deltaTime; yield return null; }

        var sp = container.GetComponentsInChildren<Transform>(true).Where(t => t.name.StartsWith("Sp_")).ToList();
        int nframes = Mathf.Max(1, Mathf.RoundToInt(len * 30f));
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append("{\"fps\":30,\"frames\":").Append(nframes).Append(",\"bones\":[");
        sb.Append(string.Join(",", sp.Select(t => "\"" + t.name + "\"")));
        sb.Append("],\"rot\":[");
        for (int f = 0; f < nframes; f++)
        {
            // Next Update: bones hold the previous frame's LateUpdate (CySpring) result.
            // (WaitForEndOfFrame never fires in batchmode, so it can't be used here.)
            for (int s2 = 0; s2 < stride; s2++) yield return null;
            if (f > 0) sb.Append(",");
            sb.Append("[");
            for (int i = 0; i < sp.Count; i++)
            {
                var q = sp[i].localRotation;
                if (i > 0) sb.Append(",");
                sb.Append("[").Append(q.x.ToString(ci)).Append(",").Append(q.y.ToString(ci)).Append(",")
                  .Append(q.z.ToString(ci)).Append(",").Append(q.w.ToString(ci)).Append("]");
            }
            sb.Append("]");
        }
        sb.Append("]}");
        Time.captureFramerate = prevCapture; Time.fixedDeltaTime = prevFixed;
        if (ctrl != null) ctrl.Is60FpsMode = prevMode;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath)));
        File.WriteAllText(outPath, sb.ToString());
        Debug.Log($"CLI_EXPORT: wrote physics ref ({sp.Count} bones x {nframes} frames) " + outPath);
    }

    // Run a coroutine to completion, capturing any exception (so we can fail cleanly).
    // Pick an output filename: the caller's if given, else the generated fallback. The
    // extension is optional ("model" and "model.pmx" both work). Any directory part is
    // dropped so --out stays the only thing deciding where files land.
    static string WithExtension(string name, string fallbackStem, string ext)
    {
        string file = string.IsNullOrWhiteSpace(name) ? fallbackStem : Path.GetFileName(name.Trim());
        if (string.IsNullOrEmpty(file)) file = fallbackStem;
        if (!file.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) file += ext;
        return file;
    }

    static void Fail(string msg) { Debug.LogError("CLI_EXPORT_FAIL: " + msg); Quit(1); }

    static void Quit(int code)
    {
        Debug.Log("CLI_EXPORT quitting code=" + code);
        Application.Quit(code);
    }
}
