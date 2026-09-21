using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

/// Records one motion of a loaded character as a VMD. The CLI (--export) and the GUI buttons
/// both run this, so a motion comes out the same whichever way it was asked for:
///
///  * a _loop clip: warmed up, aligned so the first frame is the clip's own frame 0, and
///    trimmed to exactly one period;
///  * a _s / _e clip (the build-up into a pose, or the wind-down out of it): played on its
///    own from the rest pose and recorded from its frame 0 to its end.
///
/// Rotations are relative to MMDRestPose, the pose ModelExporter binds the PMX in. With
/// BakePhysics the CySpring spring bones are recorded as extra tracks, relative to the
/// rest CySpring measured at initialization, and the PMX is expected to have been written
/// with ModelExporter.BakeSpringBones so it carries no rigid bodies for them.
public static class MotionExporter
{
    public class Options
    {
        /// Record the spring bones as VMD tracks (the PMX is then written without rigid bodies).
        public bool BakePhysics = true;
        /// Step rate for physics during capture, 60 or 30. CySpring's constants are per-step, so
        /// stepping at 30 instead of the game's 60 moves tail bones tens of degrees; frames are
        /// written at 30fps either way.
        public int PhysicsFps = 60;
        /// Loop only: whole animation periods to settle before capturing. The skeleton is
        /// periodic from the first loop; the damped spring chains need about two.
        public float WarmupPeriods = 2f;
        /// Loop only: frames between the end of the alignment wait and the first kept VMD
        /// frame (measured; CLI_PHASE reports where the frame landed).
        public int AlignLead = 3;
        /// > 0: record this many seconds raw with no loop trim (for loopify_vmd.py).
        public float RecordSeconds = 0f;
        /// Strip mouth vowel morphs so a viewer can drive lip-sync live.
        public bool DropMouth = false;
        /// Replace any blink track with one periodic blink over the loop.
        public bool AddBlink = false;
        /// When the motion has a scripted camera (_cam companion), record it into the same
        /// VMD's camera section. Only one-shots and some transitions have one; loops never do.
        public bool IncludeCamera = true;
    }

    /// How long a non-loop clip's first frame is held, physics running, before capture.
    const float HoldSeconds = 2f;

    public static bool IsLoop(string assetName) => assetName.Contains("_loop");
    public static bool IsTransitionClip(string assetName) => assetName.EndsWith("_s") || assetName.EndsWith("_e");

    /// Exact asset name first, then a substring match. `note` explains a substring match,
    /// which silently exports a different animation than asked for otherwise.
    public static UmaDatabaseEntry FindMotion(UmaViewerMain main, string id, out string note)
    {
        note = null;
        var anim = main.AbMotions.FirstOrDefault(e => e.Name == id)
                ?? main.AbMotions.FirstOrDefault(e => e.Name.Contains(id));
        if (anim != null && anim.Name != id)
        {
            int alts = main.AbMotions.Count(e => e.Name.Contains(id));
            note = $"'{id}' is not an exact asset name; using '{anim.Name}'"
                 + (alts > 1 ? $" -- {alts} assets match, see --list-anims" : "");
        }
        return anim;
    }

    /// The build-up clip the game plays into a loop's pose (anm_..._s for anm_..._loop), or null.
    public static UmaDatabaseEntry StartClipOf(UmaViewerMain main, string loopName)
        => IsLoop(loopName) && main.AbList.TryGetValue(loopName.Replace("_loop", "_s"), out var e) ? e : null;

    /// The wind-down clip out of a loop's pose (anm_..._e), or null.
    public static UmaDatabaseEntry EndClipOf(UmaViewerMain main, string loopName)
        => IsLoop(loopName) && main.AbList.TryGetValue(loopName.Replace("_loop", "_e"), out var e) ? e : null;

    /// The PMX, in MMDRestPose. With bakePhysics the spring bones get no rigid bodies.
    public static void ExportModel(UmaContainer container, string pmxPath, bool bakePhysics)
    {
        ModelExporter.BakeSpringBones = bakePhysics;
        // Overloads are resolved statically: a character must reach the character exporter
        // (rest pose, name, physics), not the prop one.
        if (container is UmaContainerCharacter chara) ModelExporter.ExportModel(chara, pmxPath);
        else ModelExporter.ExportModel(container, pmxPath);
    }

    /// Record `anim` on `container` to `vmdPath`. Runs inside the player loop (a coroutine);
    /// exceptions propagate to the caller, see RunSafe. Restores what it changes (time step,
    /// physics, look-at, animator speed) before returning.
    public static IEnumerator Record(UmaContainerCharacter container, UmaDatabaseEntry anim, string vmdPath, string modelName, Options opt)
    {
        if (container == null) throw new ArgumentNullException(nameof(container));
        if (anim == null) throw new ArgumentNullException(nameof(anim));
        opt = opt ?? new Options();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(vmdPath)));

        // Spring bones and the rest they are recorded relative to: the local rotation CySpring
        // measured at initialization, which MMDRestPose puts them back to and the PMX binds them in.
        var springXf = new Dictionary<string, Transform>();
        var springBind = new Dictionary<string, Quaternion>();
        bool prevPhysics = container.EnablePhysics;
        if (opt.BakePhysics)
        {
            var rest = container.GetSpringRestRotations();
            foreach (var tr in container.GetComponentsInChildren<Transform>(true))
            {
                if (!SpringBoneNames.IsSpringBone(tr.name) || springXf.ContainsKey(tr.name)) continue;
                springXf[tr.name] = tr;
                springBind[tr.name] = rest.TryGetValue(tr.name, out var q) ? q : tr.localRotation;
            }
            // Baking needs the simulation running (a model export leaves it off).
            container.EnablePhysics = true;
            container.SetDynamicBoneEnable(true);
            Debug.Log($"CLI_EXPORT: baking {springXf.Count} spring bones (PMX written without rigid bodies)");
        }

        AnimationClip clip = null;
        try { clip = anim.Get<AnimationClip>(); } catch { }
        container.LoadAnimation(anim);
        bool isLoop = IsLoop(anim.Name) || (clip != null && IsLoop(clip.name));
        // Non-loop clips are (re)started once the recorder is running so their frame 0 lands
        // on the first kept VMD frame. _s/_e clips need it because LoadAnimation only files
        // them into the transition slots (in the viewer they run inside a loop's chain); a
        // one-shot LoadAnimation already plays, but from an unknown frame by the time capture
        // begins, so it is restarted too, together with its camera and face clips.
        bool playDirect = clip != null && !isLoop;
        bool isTransition = clip != null && IsTransitionClip(clip.name);
        // Scripted camera: LoadAnimation put a one-shot's _cam on the preview camera. Note the
        // viewer also drops the character height scale while a camera clip plays (the clips are
        // framed for the base model), so this motion is recorded at that height.
        var builder = UmaViewerBuilder.Instance;
        var camEntry = opt.IncludeCamera ? MotionProbe.Probe(UmaViewerMain.Instance, anim.Name, loadClips: false).Camera : null;
        Camera cam = camEntry != null && builder != null ? builder.AnimationCamera : null;
        Animator camAnimator = cam != null ? builder.AnimationCameraAnimator : null;
        if (cam != null && !cam.enabled)
        {
            AnimationClip camClip = null; try { camClip = camEntry.Get<AnimationClip>(); } catch { }
            if (camClip != null) builder.SetPreviewCamera(camClip); else cam = null;
        }
        var camSamples = new List<CameraSample>();
        CameraSample.Reset();
        // Head/eye look-at (FinalIK) aims at a camera-following target that is arbitrary and
        // does not loop, so the head would pop. Record the raw animation.
        bool prevHeadTracking = container.IsHeadTracking;
        bool prevEyeTracking = container.EnableEyeTracking;
        container.SetHeadTracking(false);
        container.EnableEyeTracking = false;
        float len = (clip != null && clip.length > 0.01f) ? clip.length : 5f;
        Debug.Log($"CLI_CLIP: {anim.Name} length={len:F3}s ({Mathf.RoundToInt(len * 30f)} frames at 30fps) loop={isLoop} camera={(cam != null ? camEntry.Name : "none")}");

        // Deterministic capture: lock game time to a fixed step (frame count and pose sampling
        // no longer depend on wall-clock speed), and force the job system single-threaded so
        // parallel animation/physics FP reductions are bit-stable (their thread order otherwise
        // jitters poses ~0.3 deg run to run). Restored after.
        var animator = container.UmaAnimator;
        float prevSpeed = animator != null ? animator.speed : 1f;
        if (animator != null) animator.speed = 1f;
        int prevCapture = Time.captureFramerate;
        float prevFixed = Time.fixedDeltaTime;
        int prevWorkers = Unity.Jobs.LowLevel.Unsafe.JobsUtility.JobWorkerCount;
        int recFps = opt.PhysicsFps == 30 ? 30 : 60;
        // Render at the physics rate so FixedUpdate, and with it SaveFrame and the simulation,
        // run at it too; the extra frames are dropped when writing (WriteStride) so the file is
        // still a 30fps track. The recorder's Initialize() assigns Time.fixedDeltaTime itself, so
        // rec.FixedStep has to carry the rate.
        Time.captureFramerate = recFps;
        Time.fixedDeltaTime = 1f / recFps;
        var springCtrl = container.GetComponentInChildren<Gallop.CySpringController>(true);
        bool prevSpringMode = springCtrl != null && springCtrl.Is60FpsMode;
        if (springCtrl != null) springCtrl.Is60FpsMode = (recFps == 60);
        Unity.Jobs.LowLevel.Unsafe.JobsUtility.JobWorkerCount = 0;

        var faceAnimator = container.UmaFaceAnimator;
        float prevFaceSpeed = faceAnimator != null ? faceAnimator.speed : 1f;
        float prevCamSpeed = camAnimator != null ? camAnimator.speed : 1f;
        // (Re)start the clip and everything that runs alongside it at their frame 0. States are
        // named: Play(0, ...) would pin whatever state is current, and right after LoadAnimation
        // the viewer's own Play("motion_2") on the face animator is still pending.
        void StartClips()
        {
            if (isTransition) container.OverrideController["clip_2"] = clip;
            animator.Play("motion_2", 0, 0);
            animator.Play("motion_p", 2, 0);              // position layer (_pos)
            if (faceAnimator != null && faceAnimator.runtimeAnimatorController != null)
            {
                faceAnimator.Play("motion_2", 0, 0);      // _face
                faceAnimator.Play("motion_2", 1, 0);      // _ear
            }
            if (camAnimator != null && camAnimator.runtimeAnimatorController != null) camAnimator.Play("motion_1", 0, 0);
        }
        void SetSpeeds(float v)
        {
            animator.speed = v;
            if (faceAnimator != null) faceAnimator.speed = v;
            if (camAnimator != null) camAnimator.speed = v;
        }

        UnityHumanoidVMDRecorder rec = null;
        try
        {
            if (playDirect)
            {
                // Pre-roll: hold the clip's first frame, physics running, before capture. Without
                // it the recording opens on the cut from the rest pose into that pose: the spring
                // chains are whipped (a skirt moving 60 deg/frame at the start of an _e clip) and
                // the head look-at rig, which lags the pose by a frame, leaves a foreign head
                // rotation on frame 0.
                StartClips();
                SetSpeeds(0f);
                Debug.Log($"CLI_EXPORT: holding frame 0 for {HoldSeconds:0.00}s before capture");
                float hold = 0f;
                while (hold < HoldSeconds) { hold += Time.deltaTime; yield return null; }
            }
            if (isLoop)
            {
                // Settle: let the crossfade finish and the spring chains fall into the orbit that
                // repeats with the animation. Sweeping 2..32 periods moved the cloth wrap by under
                // half a degree, so two is enough; the knob exists for diagnosis.
                float warmPeriods = Mathf.Max(1f, opt.WarmupPeriods);
                float warm = 0f, warmTarget = Mathf.Max(1.5f, len * warmPeriods);
                Debug.Log($"CLI_EXPORT: warming {warmPeriods:0.#} periods ({warmTarget:0.00}s) before capture");
                while (warm < warmTarget) { warm += Time.deltaTime; yield return null; }

                // Phase alignment. LoadAnimation plays the previous clip's _e and this clip's _s
                // ahead of the loop, which shifts the loop's phase against wall time, so the
                // warm-up alone does not land on phase 0. Wait until the loop itself is playing,
                // then until it is AlignLead frames from wrapping: the first kept VMD frame
                // (recorded frame 2, after the transient frame 0 is dropped and 60fps frames are
                // written in pairs) is captured that many frames after this wait ends.
                string clipShort = anim.Name.Substring(anim.Name.LastIndexOf('/') + 1);
                float step = Time.deltaTime / len;
                for (int guard = 0; guard < 100000; guard++)
                {
                    var infos = animator.GetCurrentAnimatorClipInfo(0);
                    bool inLoop = !animator.IsInTransition(0) && infos.Length > 0 && infos[0].clip != null
                                  && infos[0].clip.name.EndsWith(clipShort);
                    if (inLoop)
                    {
                        float frac = animator.GetCurrentAnimatorStateInfo(0).normalizedTime % 1f;
                        if (Mathf.Abs(frac + opt.AlignLead * step - 1f) <= step * 0.5f + 1e-5f) break;
                    }
                    yield return null;
                }
            }

            var rootbone = container.transform.Find("Position");
            rec = rootbone.gameObject.AddComponent<UnityHumanoidVMDRecorder>();
            rec.FixedStep = 1f / recFps;     // Initialize() applies it; it hardcodes 1/30 otherwise
            rec.KeyReductionLevel = 1;       // every frame is a real sample; the default 2 keys the body at 15fps
            rec.Initialize();
            rec.WriteStride = recFps / 30;   // 60fps steps -> one 30fps VMD frame per two
            Debug.Log($"CLI_EXPORT: stepping physics at {recFps}fps, writing every {rec.WriteStride} frame(s)");
            if (opt.BakePhysics)
            {
                // Short names so each track fits the VMD's 15-byte field and matches the PMX
                // primary name ModelExporter wrote for the same bone.
                var shortNames = SpringBoneNames.BuildMap(springXf.Keys);
                foreach (var kv in shortNames)
                    rec.AddExtraBone(kv.Value, springXf[kv.Key], springBind[kv.Key]);
                Debug.Log($"CLI_EXPORT: registered {rec.ExtraBoneCount} baked spring tracks");
            }
            yield return null;            // one frame so the animator is posed at t=0
            {
                var st = animator.GetCurrentAnimatorStateInfo(0);
                string stName = "?";
                foreach (var cand in new[] { "motion_1", "motion_2", "motion_s", "motion_e", "motion_t", "motion_p" })
                    if (st.IsName(cand)) stName = cand;
                string Clip(string k) { var c = container.OverrideController[k]; return c == null ? "-" : $"{c.name.Substring(c.name.LastIndexOf('/') + 1)}[{c.length:F2}s]"; }
                Debug.Log($"CLI_STATE: capture starts in {stName} t={st.normalizedTime:F2} len={st.length:F2}s transition={animator.IsInTransition(0)} clip_1={Clip("clip_1")} clip_2={Clip("clip_2")} clip_s={Clip("clip_s")} clip_e={Clip("clip_e")}");
            }
            rec.StartRecording();
            // A loop: a full period plus a small margin so frame P (phase 0 of the next cycle)
            // is present; the trim below keeps exactly one period. RecordSeconds overrides this
            // to record raw for external loop-finding.
            float recLen = opt.RecordSeconds > 0f ? opt.RecordSeconds : (isLoop ? len + 0.1f : len);
            if (playDirect) recLen += 2f * Time.deltaTime;
            float e2 = 0f;
            for (int i = 0; e2 < recLen; i++)
            {
                // Recorded frame k is captured in the FixedUpdate before iteration k+1 and the
                // first kept VMD frame is recorded frame 2, so a clip started here, at
                // iteration 2, has its frame 0 captured as that first kept frame.
                if (playDirect && i == 2)
                {
                    SetSpeeds(1f);
                    StartClips();
                }
                // The camera pose visible now is what the recorder's FixedUpdate captured this
                // frame as recorded frame i-1.
                if (cam != null && i >= 1) camSamples.Add(CameraSample.Of(cam));
                if (i == 3)   // recorded frame 2 (the first kept VMD frame) is captured in the FixedUpdate before this iteration
                    Debug.Log($"CLI_PHASE: first kept frame at clip phase {animator.GetCurrentAnimatorStateInfo(0).normalizedTime % 1f:F4}");
                e2 += Time.deltaTime; yield return null;
            }
            rec.StopRecording();
        }
        finally
        {
            Time.captureFramerate = prevCapture;
            Time.fixedDeltaTime = prevFixed;
            if (springCtrl != null) springCtrl.Is60FpsMode = prevSpringMode;
            Unity.Jobs.LowLevel.Unsafe.JobsUtility.JobWorkerCount = prevWorkers;
            if (animator != null) animator.speed = prevSpeed;
            if (faceAnimator != null) faceAnimator.speed = prevFaceSpeed;
            if (camAnimator != null) camAnimator.speed = prevCamSpeed;
            container.SetHeadTracking(prevHeadTracking);
            container.EnableEyeTracking = prevEyeTracking;
            if (opt.BakePhysics && !prevPhysics) container.SetDynamicBoneEnable(false);
        }

        try
        {
            if (cam != null)
            {
                // Match the bone frames' numbering: recorded 60fps frame k -> VMD frame k/WriteStride,
                // then the transient frame 0 is dropped and everything shifts down by one; a loop
                // keeps only one period.
                int keep = opt.RecordSeconds > 0f ? int.MaxValue : Mathf.RoundToInt(len * 30f);
                for (int k = 0; k < camSamples.Count; k += rec.WriteStride)
                {
                    int j = k / rec.WriteStride;
                    if (j < 1 || j > keep) continue;
                    rec.CameraFrames.Add(camSamples[k].ToVmdRecord(j - 1));
                }
                Debug.Log($"CLI_EXPORT: camera track: {rec.CameraFrames.Count} keys");
            }
            rec.SaveVMD(modelName, vmdPath);
            // Drop the recorder's transient first frame (a stale pose at capture start). For a
            // loop, keep exactly frames 1..P (P = one period) so the seam is a single step
            // regardless of how fast the motion is; the wrap is phase0<-phaseP-1. A non-loop
            // clip keeps exactly its own frames (a card cut-in's chain event at 0.99*len would
            // otherwise put the next cut's first pose on the frame after). Raw captures
            // (RecordSeconds) keep everything after frame 0.
            int period = opt.RecordSeconds > 0f ? int.MaxValue : Mathf.RoundToInt(len * 30f);
            try { TrimLoop(vmdPath, period, opt.DropMouth, opt.AddBlink); }
            catch (Exception ex) { Debug.LogWarning("CLI_EXPORT: loop trim skipped: " + ex); }
        }
        finally
        {
            UnityEngine.Object.Destroy(rec);
        }
    }

    /// One camera pose, converted to MMD's frame: x and z negated, yaw mirrored, unwrapped
    /// euler angles so keys never jump 360 deg, camera position with zero target distance.
    struct CameraSample
    {
        public Vector3 Position;   // VMD units
        public Vector3 Euler;      // degrees, MMD convention
        public float Fov;
        public bool Orthographic;
        static Vector3 lastEuler; static bool haveLast;

        public static void Reset() { haveLast = false; }

        public static CameraSample Of(Camera cam)
        {
            var t = cam.transform;
            var p = t.position; var q = t.rotation;
            var fixedQ = new Quaternion(-q.x, q.y, -q.z, q.w);
            var e = new Vector3(fixedQ.eulerAngles.x, 180f - fixedQ.eulerAngles.y, fixedQ.eulerAngles.z);
            Vector3 unwrapped = e;
            if (haveLast)
            {
                unwrapped = lastEuler + UnityCameraVMDRecorder.DeltaVector(e, lastWrapped);
            }
            lastWrapped = e; lastEuler = unwrapped; haveLast = true;
            return new CameraSample
            {
                Position = new Vector3(-p.x, p.y, -p.z) * 12.5f,
                Euler = unwrapped, Fov = cam.fieldOfView, Orthographic = cam.orthographic,
            };
        }
        static Vector3 lastWrapped;

        public byte[] ToVmdRecord(int frame)
        {
            var b = new byte[61]; int o = 0;
            void F(float v) { System.Buffer.BlockCopy(BitConverter.GetBytes(v), 0, b, o, 4); o += 4; }
            void U(uint v) { System.Buffer.BlockCopy(BitConverter.GetBytes(v), 0, b, o, 4); o += 4; }
            U((uint)frame); F(0f);
            F(Position.x); F(Position.y); F(Position.z);
            F(Euler.x * Mathf.Deg2Rad); F(Euler.y * Mathf.Deg2Rad); F(Euler.z * Mathf.Deg2Rad);
            for (int i = 0; i < 6; i++) { b[o++] = 20; b[o++] = 107; b[o++] = 20; b[o++] = 107; }   // linear bezier
            U((uint)Mathf.RoundToInt(Fov));
            b[o++] = (byte)(Orthographic ? 1 : 0);
            return b;
        }
    }

    /// Run a coroutine, routing any exception it throws to onError instead of killing the caller.
    public static IEnumerator RunSafe(IEnumerator inner, Action<Exception> onError)
    {
        while (true)
        {
            object cur;
            try { if (!inner.MoveNext()) yield break; cur = inner.Current; }
            catch (Exception ex) { onError(ex); yield break; }
            yield return cur;
        }
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

    // Record CySpring-simulated Sp_* bone local rotations per frame to JSON. Captures at
    // end-of-frame (after the container's LateUpdate runs CySpring), at a fixed 1/30 step.
}
