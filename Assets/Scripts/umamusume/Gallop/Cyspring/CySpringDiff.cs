using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace Gallop
{
    /// Single-step differential between the native CySpring plugin and the managed port.
    ///
    /// Free-running both solvers and comparing trajectories does not work: the error compounds,
    /// so within a few frames everything differs and nothing is localised. Instead every frame
    /// runs the managed solver on a COPY of the pre-step state, lets the native plugin advance
    /// the real state, and compares the two outputs. Each residual is then attributable to
    /// exactly one step from identical input, and the simulation continues on the native result
    /// so frame 400's number means as much as frame 1's.
    ///
    /// NativeClothWorking carries its own intermediates, so the first field to diverge names the
    /// step without extra instrumentation:
    ///   AimVector     step 2, quaternion compose/rotate
    ///   Force         step 3, stiffness/drag/wind/gravity
    ///   Diff          steps 4-5, integrate and length constraint
    ///   TargetPosition step 6, collision
    ///   FinalRotation step 7, commit and qFromTo
    public static class CySpringDiff
    {
        public static bool Armed;

        // --solver-diff-collide <scale>: nothing in the running animations ever penetrates a
        // body collider, so the collision path ran zero times in either solver and could not be
        // compared. Scaling every collider's Radius around each call -- identically for the plugin
        // and the port, since both read the same array -- makes bones penetrate, and the
        // differential then judges the pushes. Saved and restored, not multiplied and divided, so
        // it cannot drift across frames.
        public static float CollideScale = 1f;
        // --solver-diff-collide-mode inner|plane: the model has no inner colliders and no Type 3
        // planes, so those two branches cannot be compared on real data. Synthesising them is
        // legitimate for a differential -- the only requirement is that both solvers see the same
        // input. "inner" marks every sphere IsInner (pair with a scale < 1 so bones fall outside
        // and get pulled back in); "plane" turns every collider into a horizontal half-space
        // through its own centre, normal +Y, so bones below it get pushed up.
        public static string CollideMode = "";

        /// Also force-enables every collider. The viewer never writes NativeClothCollision.IsEnable
        /// (the initializer zeroes it and nothing sets it), so the plugin has been handed disabled
        /// colliders on every frame and its collision code has never run here. Enabling them is
        /// the only way to compare that code at all. Whole-struct snapshot, restored after.
        public static NativeClothCollision[] ScaleColliders(NativeClothCollision[] col)
        {
            if (!Armed || col == null || (CollideScale == 1f && string.IsNullOrEmpty(CollideMode))) return null;
            var saved = (NativeClothCollision[])col.Clone();
            for (int i = 0; i < col.Length; i++)
            {
                col[i].Radius = saved[i].Radius * CollideScale;
                col[i].IsEnable = 1;
                if (CollideMode == "inner" && col[i].Type == 0) col[i].IsInner = 1;
                else if (CollideMode == "chara") col[i].IsCharaCollision = 1;   // exercises the CheckCharaCollision filter
                else if (CollideMode == "plane")
                {
                    col[i].Type = 3;
                    col[i].Normal = new Vector3(0f, 1f, 0f);
                    // Normal and Distance are read UNTRANSFORMED by the plugin (the Type 3 branch never
                    // touches ParentWorkIndex), so this is a world height. 0.8 sits through the skirt
                    // and lower hair on this rig, so plenty of bones cross it.
                    col[i].Distance = 0.8f;
                }
            }
            return saved;
        }

        /// "chara" mode also clears CheckCharaCollision on every bone -- the filter at 0x180007397
        /// skips a chara collider only when the BONE flag is clear, and every bone on this rig has
        /// it set, so without this the skip branch never runs in either solver.
        public static int[] ScaleBones(NativeClothWorking[] cond, int n)
        {
            if (!Armed || cond == null || CollideMode != "chara") return null;
            n = System.Math.Min(n, cond.Length);
            var saved = new int[n];
            for (int i = 0; i < n; i++) { saved[i] = cond[i].CheckCharaCollision; cond[i].CheckCharaCollision = 0; }
            return saved;
        }

        public static void RestoreBones(NativeClothWorking[] cond, int[] saved)
        {
            if (saved == null || cond == null) return;
            for (int i = 0; i < saved.Length && i < cond.Length; i++) cond[i].CheckCharaCollision = saved[i];
        }

        public static void RestoreColliders(NativeClothCollision[] col, NativeClothCollision[] saved)
        {
            if (saved == null || col == null) return;
            System.Array.Copy(saved, col, System.Math.Min(saved.Length, col.Length));
        }

        const int FIELDS = 7;
        static readonly string[] FieldNames =
            { "AimVector", "Force", "Diff", "TargetPosition", "PrevTargetPosition", "FinalRotation",
              "AnimationRotation" };

        // Below this a difference is float reassociation, not a bug. A step that genuinely
        // matches should be bit-identical or within a couple of ULP at these magnitudes.
        //
        // Rotations need their own threshold because they are scored as an ANGLE IN DEGREES,
        // not in the linear units the vector fields use. Two quaternions agreeing to eight
        // significant digits still differ by ~1e-5 degrees, so the linear 1e-6 counted every
        // correct rotation as a divergence and made the rotation columns meaningless.
        const float EPS = 1e-6f;
        const float EPS_ANGLE = 1e-2f;   // degrees; real faults here have been 50-180

        static float EpsFor(int field) => (field == 5 || field == 6) ? EPS_ANGLE : EPS;

        static readonly double[] maxDiff = new double[FIELDS];
        static readonly double[] sumDiff = new double[FIELDS];
        static readonly long[] countOver = new long[FIELDS];
        static readonly long[] samples = new long[FIELDS];
        static readonly int[] firstFrame = new int[FIELDS];
        static readonly int[] firstBone = new int[FIELDS];
        static readonly string[] worstWhere = new string[FIELDS];

        // Which bone attributes the divergent bones carry -- if only IsLimit != 0 bones differ,
        // that points straight at the rotation-limit clamp the port does not implement.
        static long divLimit, divCollision, divSkip, divTotal, bonesSeen;

        // Per-field divergence split by whether the bone has a root parent. The plugin reads a
        // quaternion out of pRootParentWork[ParentWorkIndex]; the port ignores its parents
        // argument entirely. If a field only diverges on bones with a parent, that is the cause.
        static readonly long[] divWithParent = new long[FIELDS];
        static readonly long[] divNoParent = new long[FIELDS];
        static long seenWithParent, seenNoParent;

        static int frame;

        // The SKIRT differential. The plugin exposes three entry points -- NativeClothUpdate,
        // NativeClothSkirtUpdate and NativeSkirtUpdate -- and everything above compares only the
        // first. The skirt runs through its own solver, reached from SkirtController via
        // UpdateSkirtNativePluginOne, and was never compared at all. That is exactly the gap a
        // free-running capture exposed: every non-skirt bone came out bit-identical while the
        // skirt drifted tens of degrees.
        static long skSamples, skOverEval, skOverAngle, skBranchMismatch;
        static double skMaxEval, skSumEval, skMaxAngle, skSumAngle;
        static string skWorst = "";
        static readonly StringBuilder skDetail = new StringBuilder();
        static int skDetailCount;

        public static void CompareSkirt(NativeSkirtWorking pre, NativeSkirtWorking nat,
                                        NativeSkirtWorking man, NativeSkirtArg arg)
        {
            skSamples++;
            double de = System.Math.Abs((double)nat.Evaluation - man.Evaluation);
            double da = System.Math.Abs((double)nat.OffsetAngle - man.OffsetAngle);
            skSumEval += de; skSumAngle += da;
            if (de > EPS) skOverEval++;
            if (da > EPS) skOverAngle++;
            // Did the two even take the same collider branches?
            if (nat.IsCheckLeftKnee != man.IsCheckLeftKnee || nat.IsCheckRightKnee != man.IsCheckRightKnee
                || nat.IsCheckLeftAnkle != man.IsCheckLeftAnkle || nat.IsCheckRightAnkle != man.IsCheckRightAnkle)
                skBranchMismatch++;

            double sev = System.Math.Max(de, da);
            if (sev > System.Math.Max(skMaxEval, skMaxAngle) || skDetailCount == 0)
            {
                if (sev > skMaxEval + skMaxAngle || skDetailCount == 0)
                {
                    skWorst = "sample " + skSamples;
                    if (skDetailCount < 3)
                    {
                        skDetailCount++;
                        skDetail.AppendLine($"--- skirt divergence {skDetailCount} (sample {skSamples}) ---");
                        skDetail.AppendLine($"  in  checks LK{pre.IsCheckLeftKnee} RK{pre.IsCheckRightKnee} LA{pre.IsCheckLeftAnkle} RA{pre.IsCheckRightAnkle}");
                        skDetail.AppendLine($"  in  SkirtRootPos      {VS(pre.SkirtRootPos)}");
                        skDetail.AppendLine($"  in  SkirtInitChildPos {VS(pre.SkirtInitChildPos)}");
                        skDetail.AppendLine($"  in  SkirtInitNormal   {VS(pre.SkirtInitNormal)}");
                        skDetail.AppendLine($"  in  RotationAxis      {VS(pre.RotationAxis)}");
                        skDetail.AppendLine($"  in  Evaluation {pre.Evaluation:G9}  OffsetAngle {pre.OffsetAngle:G9}");
                        skDetail.AppendLine($"  arg KneeL {VS(arg.KneeLPos)} KneeR {VS(arg.KneeRPos)}");
                        skDetail.AppendLine($"  arg AnkleL {VS(arg.AnkleLPos)} AnkleR {VS(arg.AnkleRPos)}");
                        skDetail.AppendLine($"  arg Center {VS(arg.CenterPos)} Root {VS(arg.RootPos)}");
                        skDetail.AppendLine($"  arg kneeR {arg.KneeColliderRadius:G9} ankleR {arg.AnkleColliderRadius:G9} infl {arg.InfluenceAngle:G9} inflMax {arg.InfluenceMaxAngle:G9}");
                        skDetail.AppendLine($"  nat Evaluation {nat.Evaluation:G9}   man Evaluation {man.Evaluation:G9}");
                        skDetail.AppendLine($"  nat OffsetAngle {nat.OffsetAngle:G9}  man OffsetAngle {man.OffsetAngle:G9}");
                    }
                }
            }
            if (de > skMaxEval) skMaxEval = de;
            if (da > skMaxAngle) skMaxAngle = da;
        }

        // Integer state the plugin writes during the solve. These are not positions, so the
        // float comparison above ignores them -- and that made the harness blind to a whole
        // branch: IsCheckSkirtKnee is cleared on entry and only set again from inside the solve,
        // so if the plugin raises it and the port never does, the port silently skips the
        // skirt-knee push and the float fields can still agree on the step where it happened.
        static long skirtNat, skirtMan, skirtBoth, skirtNeither, collMismatch;

        // AimVector candidate probe. The plugin's AimVector block (0x1800098xx-0x180009ae8) is
        // register-allocated across spill slots, so reading the exact operands off the listing is
        // slow and easy to get wrong -- and the divergence dump cannot settle it either, because
        // it shows the pre-chain ParentRotation while the solve uses the chained one. So let the
        // solver evaluate several candidate formulas from the live chained state and score each
        // against what the plugin actually produced. Whichever matches everywhere is the formula.
        public const int NCAND = 6;
        public static readonly string[] CandNames =
            { "Qrot(Qmul(Parent,InitLocal),axis)", "Qrot(Anim,axis)", "Qrot(Parent,axis)",
              "Qrot(Qmul(Anim,InitLocal),axis)", "Norm(Target-Self)", "Qrot(Qmul(Parent,Anim),axis)" };
        const int MAXB = 64;
        static readonly Vector3[] cand = new Vector3[MAXB * NCAND];
        static readonly bool[] candSet = new bool[MAXB];
        static readonly long[] candMatch = new long[NCAND];
        static readonly long[] candSamples = new long[NCAND];
        static readonly double[] candMax = new double[NCAND];

        public static void NoteAim(int bone, int k, Vector3 v)
        {
            if (bone < 0 || bone >= MAXB) return;
            cand[bone * NCAND + k] = v;
            candSet[bone] = true;
        }

        // Same idea for the base rotation that FinalRotation composes onto. AimVector cannot
        // identify it: AimVector is Qrot(q, BoneAxis), and rotating the bone axis is blind to any
        // twist ABOUT that axis, so q is free by a roll and AimVector still matches. FinalRotation
        // is not blind to it, which is how a bone can have an exact AimVector and a 70-degree
        // FinalRotation at the same time.
        public const int NQCAND = 4;
        public static readonly string[] QCandNames =
            { "unclamped", "q*clamp(conj(q)*F)", "clamp(F*conj(q))*q", "clamp with +Min" };
        static readonly Quaternion[] qcand = new Quaternion[MAXB * NQCAND];
        static readonly bool[] qcandSet = new bool[MAXB];
        static readonly long[] qcandMatch = new long[NQCAND];
        static readonly long[] qcandSamples = new long[NQCAND];
        static readonly double[] qcandMax = new double[NQCAND];

        public static void NoteFinal(int bone, int k, Quaternion q)
        {
            if (bone < 0 || bone >= MAXB) return;
            qcand[bone * NQCAND + k] = q;
            qcandSet[bone] = true;
        }

        public static void Reset()
        {
            for (int i = 0; i < FIELDS; i++)
            {
                maxDiff[i] = 0; sumDiff[i] = 0; countOver[i] = 0; samples[i] = 0;
                firstFrame[i] = -1; firstBone[i] = -1; worstWhere[i] = "";
            }
            divLimit = divCollision = divSkip = divTotal = bonesSeen = 0;
            skSamples = skOverEval = skOverAngle = skBranchMismatch = 0;
            skMaxEval = skSumEval = skMaxAngle = skSumAngle = 0; skWorst = ""; skDetail.Length = 0; skDetailCount = 0;
            skirtNat = skirtMan = skirtBoth = skirtNeither = collMismatch = 0;
            detail.Length = 0; detailCount = 0;
            worstDetail.Length = 0; worstSeverity = 0; curSeverity = 0;
            for (int i = 0; i < NCAND; i++) { candMatch[i] = 0; candSamples[i] = 0; candMax[i] = 0; }
            for (int i = 0; i < MAXB; i++) candSet[i] = false;
            for (int i = 0; i < NQCAND; i++) { qcandMatch[i] = 0; qcandSamples[i] = 0; qcandMax[i] = 0; }
            for (int i = 0; i < MAXB; i++) qcandSet[i] = false;
            seenWithParent = seenNoParent = 0;
            for (int i = 0; i < FIELDS; i++) { divWithParent[i] = 0; divNoParent[i] = 0; }
            frame = 0;
        }

        /// Value-copy of the pre-step state. NativeClothWorking is a blittable struct, so a
        /// shallow array clone is a true snapshot.
        public static NativeClothWorking[] Snapshot(NativeClothWorking[] src)
        {
            return (NativeClothWorking[])src.Clone();
        }

        // Divergences in full. The aggregate says which field and when; this says what the
        // numbers were, alongside the inputs that produced them.
        //
        // Dumping only the FIRST few was actively misleading: the earliest divergences are the
        // mildest, so all four samples agreed to eight digits while the aggregate reported a
        // 100-degree maximum somewhere else entirely. Keep the worst bone-step as well, scored by
        // how many multiples of its own epsilon each field is off, so the sample shown is the one
        // that actually characterises the bug.
        const int MAX_DETAIL = 2;
        static readonly StringBuilder detail = new StringBuilder();
        static int detailCount;
        static readonly StringBuilder worstDetail = new StringBuilder();
        static double worstSeverity;
        static double curSeverity;

        static string QS(Quaternion q) => string.Format(CultureInfo.InvariantCulture,
            "({0:G9}, {1:G9}, {2:G9}, {3:G9})", q.x, q.y, q.z, q.w);
        static string VS(Vector3 v) => string.Format(CultureInfo.InvariantCulture,
            "({0:G9}, {1:G9}, {2:G9})", v.x, v.y, v.z);

        public static void Compare(NativeClothWorking[] nativeOut, NativeClothWorking[] managedOut,
                                   NativeClothWorking[] pre, int n)
        {
            if (nativeOut == null || managedOut == null) return;
            n = Mathf.Min(n, Mathf.Min(nativeOut.Length, managedOut.Length));
            for (int i = 0; i < n; i++)
            {
                var a = nativeOut[i];
                var b = managedOut[i];
                bool anyDiverged = false;
                bonesSeen++;
                curSeverity = 0;

                bool hasParent = a.ParentWorkIndex >= 0;
                if (hasParent) seenWithParent++; else seenNoParent++;

                if (i < MAXB && candSet[i])
                {
                    for (int k = 0; k < NCAND; k++)
                    {
                        double dc = V(a.AimVector, cand[i * NCAND + k]);
                        candSamples[k]++;
                        if (dc <= EPS) candMatch[k]++;
                        if (dc > candMax[k]) candMax[k] = dc;
                    }
                    candSet[i] = false;
                }
                if (i < MAXB && qcandSet[i])
                {
                    for (int k = 0; k < NQCAND; k++)
                    {
                        double dq = Q(a.FinalRotation, qcand[i * NQCAND + k]);
                        qcandSamples[k]++;
                        if (dq <= EPS_ANGLE) qcandMatch[k]++;
                        if (dq > qcandMax[k]) qcandMax[k] = dq;
                    }
                    qcandSet[i] = false;
                }

                bool sn = a.IsCheckSkirtKnee != 0, sm2 = b.IsCheckSkirtKnee != 0;
                if (sn && sm2) skirtBoth++; else if (sn) skirtNat++; else if (sm2) skirtMan++; else skirtNeither++;
                if (a.ActiveCollision != b.ActiveCollision) collMismatch++;

                anyDiverged |= TakeP(0, i, V(a.AimVector, b.AimVector), hasParent);
                anyDiverged |= TakeP(1, i, V(a.Force, b.Force), hasParent);
                anyDiverged |= TakeP(2, i, V(a.Diff, b.Diff), hasParent);
                anyDiverged |= TakeP(3, i, V(a.TargetPosition, b.TargetPosition), hasParent);
                anyDiverged |= TakeP(4, i, V(a.PrevTargetPosition, b.PrevTargetPosition), hasParent);
                anyDiverged |= TakeP(5, i, Q(a.FinalRotation, b.FinalRotation), hasParent);
                anyDiverged |= TakeP(6, i, Q(a.AnimationRotation, b.AnimationRotation), hasParent);

                if (anyDiverged && detailCount < MAX_DETAIL)
                {
                    detailCount++;
                    Describe(detail, $"--- divergence {detailCount}: frame {frame}, bone {i} of {n} ---",
                             a, b, pre, i);
                }
                if (anyDiverged && curSeverity > worstSeverity)
                {
                    worstSeverity = curSeverity;
                    worstDetail.Length = 0;
                    Describe(worstDetail,
                             $"--- worst: frame {frame}, bone {i} of {n} ({curSeverity:G4}x eps) ---",
                             a, b, pre, i);
                }
                if (anyDiverged)
                {
                    divTotal++;
                    if (a.IsLimit != 0) divLimit++;
                    if (a.ActiveCollision != 0) divCollision++;
                    if (a.IsSkip != 0) divSkip++;
                }
            }
            frame++;
        }

        static void Describe(StringBuilder detail, string header, NativeClothWorking a,
                             NativeClothWorking b, NativeClothWorking[] pre, int i)
        {
            {
                {
                    detail.AppendLine(header);
                    if (pre != null && i < pre.Length)
                    {
                        var s0 = pre[i];
                        detail.AppendLine($"  in  ParentWorkIndex {s0.ParentWorkIndex}  IsLimit {s0.IsLimit}  ActiveCollision {s0.ActiveCollision}  IsCheckSkirtKnee {s0.IsCheckSkirtKnee}");
                        detail.AppendLine($"  in  InitLocalRotation {QS(s0.InitLocalRotation)}");
                        detail.AppendLine($"  in  ParentRotation    {QS(s0.ParentRotation)}");
                        detail.AppendLine($"  in  AnimationRotation {QS(s0.AnimationRotation)}");
                        detail.AppendLine($"  in  BoneAxis          {VS(s0.BoneAxis)}");
                        detail.AppendLine($"  in  TargetPosition    {VS(s0.TargetPosition)}");
                        detail.AppendLine($"  in  SelfPosition      {VS(s0.SelfPosition)}");
                        detail.AppendLine($"  in  PrevTargetPos     {VS(s0.PrevTargetPosition)}");
                        detail.AppendLine($"  in  InitBoneDistance  {s0.InitBoneDistance:G9}  CollisionRadius {s0.CollisionRadius:G9}");
                        detail.AppendLine($"  in  IsAddSpring {s0.IsAddSpring}  MoveSpringApplyRate {s0.MoveSpringApplyRate:G9}  DynamicRatio {s0.DynamicRatio:G9}");
                        detail.AppendLine($"  in  LimitRotMin      {VS(s0.LimitRotationMin)}");
                        detail.AppendLine($"  in  LimitRotMax      {VS(s0.LimitRotationMax)}");
                        detail.AppendLine($"  in  CIndex 0-7        {s0.CIndex0},{s0.CIndex1},{s0.CIndex2},{s0.CIndex3},{s0.CIndex4},{s0.CIndex5},{s0.CIndex6},{s0.CIndex7}");
                    }
                    detail.AppendLine($"  nat AimVector         {VS(a.AimVector)}");
                    detail.AppendLine($"  man AimVector         {VS(b.AimVector)}");
                    detail.AppendLine($"  nat Force             {VS(a.Force)}");
                    detail.AppendLine($"  man Force             {VS(b.Force)}");
                    detail.AppendLine($"  nat AnimationRotation {QS(a.AnimationRotation)}");
                    detail.AppendLine($"  man AnimationRotation {QS(b.AnimationRotation)}");
                    detail.AppendLine($"  nat FinalRotation     {QS(a.FinalRotation)}");
                    detail.AppendLine($"  man FinalRotation     {QS(b.FinalRotation)}");
                    detail.AppendLine($"  nat Diff              {VS(a.Diff)}");
                    detail.AppendLine($"  man Diff              {VS(b.Diff)}");
                    detail.AppendLine($"  nat TargetPosition    {VS(a.TargetPosition)}");
                    detail.AppendLine($"  man TargetPosition    {VS(b.TargetPosition)}");
                }
            }
        }

        static bool TakeP(int f, int bone, double d, bool hasParent)
        {
            bool over = Take(f, bone, d);
            if (over) { if (hasParent) divWithParent[f]++; else divNoParent[f]++; }
            // Score in multiples of the field's own epsilon so a rotation in degrees and a
            // position in metres are comparable, and the worst sample is the worst overall.
            double sev = d / EpsFor(f);
            if (sev > curSeverity) curSeverity = sev;
            return over;
        }

        static bool Take(int f, int bone, double d)
        {
            samples[f]++;
            sumDiff[f] += d;
            if (d > maxDiff[f]) { maxDiff[f] = d; worstWhere[f] = "frame " + frame + " bone " + bone; }
            if (d > EpsFor(f))
            {
                countOver[f]++;
                if (firstFrame[f] < 0) { firstFrame[f] = frame; firstBone[f] = bone; }
                return true;
            }
            return false;
        }

        static double V(Vector3 a, Vector3 b)
        {
            double dx = a.x - b.x, dy = a.y - b.y, dz = a.z - b.z;
            return System.Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        // Quaternion difference as an angle, sign-insensitive (q and -q are the same rotation).
        //
        // NOT acos(dot). acos is catastrophically ill-conditioned at dot ~ 1, which is exactly
        // where a correct port sits: the components are float32, so the dot product carries ~1e-7
        // of round-off, and acos inflates that into an apparent 0.05 degrees. Against a 1e-2
        // threshold that marked essentially every matching rotation as a divergence -- 1780 of
        // 1792 on AnimationRotation, while every sampled bone agreed to eight digits.
        //
        // Use the chord instead. For unit quaternions |a - b| = 2 sin(t/4), so t = 4 asin(|a-b|/2),
        // and asin is well-conditioned near zero. Taking the smaller of |a-b| and |a+b| handles
        // the double cover. Checks out at both ends: d=0 -> 0 deg, d=sqrt(2) -> 180 deg.
        static double Q(Quaternion a, Quaternion b)
        {
            double sm = Chord(a.x - b.x, a.y - b.y, a.z - b.z, a.w - b.w);
            double sp = Chord(a.x + b.x, a.y + b.y, a.z + b.z, a.w + b.w);
            double d = System.Math.Min(sm, sp) * 0.5;
            if (d > 1.0) d = 1.0;
            return 4.0 * System.Math.Asin(d) * Mathf.Rad2Deg;
        }

        static double Chord(double x, double y, double z, double w)
            => System.Math.Sqrt(x * x + y * y + z * z + w * w);

        public static string Report()
        {
            var ci = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.AppendLine("CySpring native vs managed -- single-step differential");
            sb.AppendLine($"frames compared: {frame}   bone-steps: {bonesSeen}");
            sb.AppendLine();
            sb.AppendLine($"{"field",-20}{"max",14}{"mean",14}{"over eps",12}{"first",22}");
            for (int i = 0; i < FIELDS; i++)
            {
                if (samples[i] == 0) continue;
                string first = firstFrame[i] < 0 ? "-" : $"frame {firstFrame[i]} bone {firstBone[i]}";
                sb.AppendLine(string.Format(ci, "{0,-20}{1,14:G6}{2,14:G6}{3,12}{4,22}",
                    FieldNames[i], maxDiff[i], sumDiff[i] / samples[i], countOver[i], first));
            }
            sb.AppendLine();
            sb.AppendLine("worst case per field:");
            for (int i = 0; i < FIELDS; i++)
                if (samples[i] > 0 && maxDiff[i] > 0) sb.AppendLine($"  {FieldNames[i],-20}{worstWhere[i]}");
            sb.AppendLine();
            sb.AppendLine($"divergence split by root parent (bone-steps: {seenWithParent} with, {seenNoParent} without):");
            sb.AppendLine($"  {"field",-20}{"with parent",14}{"without",12}");
            for (int i = 0; i < FIELDS; i++)
            {
                if (samples[i] == 0) continue;
                sb.AppendLine($"  {FieldNames[i],-20}{divWithParent[i],14}{divNoParent[i],12}");
            }
            sb.AppendLine();
            sb.AppendLine("attributes of diverging bone-steps (points at the branch that differs):");
            sb.AppendLine($"  diverged           : {divTotal} of {bonesSeen}");
            sb.AppendLine($"  of those IsLimit   : {divLimit}");
            sb.AppendLine($"  of those Collision : {divCollision}");
            sb.AppendLine($"  of those IsSkip    : {divSkip}");
            if (divTotal == 0) sb.AppendLine("\nno divergence above eps -- the port reproduces the plugin step for step.");
            sb.AppendLine();
            sb.AppendLine("SKIRT solver (NativeSkirtUpdate -- a separate plugin entry point):");
            sb.AppendLine($"  samples                    : {skSamples}");
            if (skSamples > 0)
            {
                sb.AppendLine(string.Format(ci, "  Evaluation  max {0:G6}  mean {1:G6}  over eps {2}", skMaxEval, skSumEval / skSamples, skOverEval));
                sb.AppendLine(string.Format(ci, "  OffsetAngle max {0:G6}  mean {1:G6}  over eps {2}", skMaxAngle, skSumAngle / skSamples, skOverAngle));
                sb.AppendLine($"  collider-branch mismatches : {skBranchMismatch}");
                sb.AppendLine($"  worst                      : {skWorst}");
            }
            if (skDetail.Length > 0) { sb.AppendLine(); sb.Append(skDetail); }

            sb.AppendLine();
            sb.AppendLine("integer state written during the solve:");
            sb.AppendLine($"  IsCheckSkirtKnee set by both      : {skirtBoth}");
            sb.AppendLine($"  IsCheckSkirtKnee set by PLUGIN only: {skirtNat}");
            sb.AppendLine($"  IsCheckSkirtKnee set by PORT only  : {skirtMan}");
            sb.AppendLine($"  IsCheckSkirtKnee set by neither    : {skirtNeither}");
            sb.AppendLine($"  ActiveCollision mismatches         : {collMismatch}");
            if (candSamples[0] > 0)
            {
                sb.AppendLine();
                sb.AppendLine("AimVector candidate formulas scored against the plugin:");
                sb.AppendLine($"  {"formula",-36}{"matched",12}{"of",10}{"max err",14}");
                for (int k = 0; k < NCAND; k++)
                    sb.AppendLine(string.Format(ci, "  {0,-36}{1,12}{2,10}{3,14:G6}",
                        CandNames[k], candMatch[k], candSamples[k], candMax[k]));
            }
            if (qcandSamples[0] > 0)
            {
                sb.AppendLine();
                sb.AppendLine("FinalRotation base-rotation candidates scored against the plugin:");
                sb.AppendLine($"  {"base q",-28}{"matched",12}{"of",10}{"max err deg",14}");
                for (int k = 0; k < NQCAND; k++)
                    sb.AppendLine(string.Format(ci, "  {0,-28}{1,12}{2,10}{3,14:G6}",
                        QCandNames[k], qcandMatch[k], qcandSamples[k], qcandMax[k]));
            }
            if (worstDetail.Length > 0) { sb.AppendLine(); sb.Append(worstDetail); }
            if (detail.Length > 0) { sb.AppendLine(); sb.Append(detail); }
            return sb.ToString();
        }

        public static void Write(string path)
        {
            try { File.WriteAllText(path, Report()); Debug.Log("CLI_EXPORT: wrote solver diff " + path); }
            catch (Exception ex) { Debug.LogError("CLI_EXPORT_FAIL: solver diff write threw: " + ex); }
        }
    }
}
