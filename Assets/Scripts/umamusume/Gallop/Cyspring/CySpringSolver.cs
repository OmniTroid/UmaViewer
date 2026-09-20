using UnityEngine;

namespace Gallop
{
    // Managed C# port of native/CySpring/CySpringPlugin.cpp (the reversed CySpring solver),
    // operating in place on the same Native* structs. Lets CySpring run with no native plugin
    // (WebGL/IL2CPP, or any platform via CySpringNative.UseNativePlugin = false). Mirrors the
    // .cpp math, unit constants, and stages; see that file for the fidelity notes.
    public static class CySpringSolver
    {
        // 30fps baseline divisor applied to every force term; see SolveCloth.
        const float FORCE_DIV = 30f;

        static Quaternion Qmul(Quaternion a, Quaternion b) => new Quaternion(
            a.w * b.x + a.x * b.w + a.y * b.z - a.z * b.y,
            a.w * b.y - a.x * b.z + a.y * b.w + a.z * b.x,
            a.w * b.z + a.x * b.y - a.y * b.x + a.z * b.w,
            a.w * b.w - a.x * b.x - a.y * b.y - a.z * b.z);

        static Vector3 Qrot(Quaternion q, Vector3 v)
        {
            Vector3 u = new Vector3(q.x, q.y, q.z);
            float s = q.w;
            return u * (2f * Vector3.Dot(u, v)) + v * (s * s - Vector3.Dot(u, u)) + Vector3.Cross(u, v) * (2f * s);
        }

        static float Len(Vector3 a) => Mathf.Sqrt(a.x * a.x + a.y * a.y + a.z * a.z);
        static Vector3 Norm(Vector3 a) { float l = Len(a); return l > 1e-8f ? a * (1f / l) : Vector3.zero; }

        // shortest-arc quaternion rotating unit a onto unit b
        static Quaternion QFromTo(Vector3 a, Vector3 b)
        {
            Vector3 an = Norm(a), bn = Norm(b);
            float d = Vector3.Dot(an, bn);
            if (d >= 1f - 1e-6f) return new Quaternion(0, 0, 0, 1);
            if (d <= -1f + 1e-6f)
            {
                Vector3 ax = Norm(Vector3.Cross(new Vector3(1, 0, 0), an));
                if (Len(ax) < 1e-6f) ax = Norm(Vector3.Cross(new Vector3(0, 1, 0), an));
                return new Quaternion(ax.x, ax.y, ax.z, 0);
            }
            Vector3 c = Vector3.Cross(an, bn);
            float s = Mathf.Sqrt((1f + d) * 2f), inv = 1f / s;
            return new Quaternion(c.x * inv, c.y * inv, c.z * inv, s * 0.5f);
        }

        // Sixteen slots, not eight. The plugin picks the base off the slot number at 0x180007370:
        // slots 0-7 come from CIndex0 at 0xf4, slots 8-15 from 0x144, both indexed by i*2 --
        // which lands on CIndex8..15 at 0x154..0x162. The .cpp only ever read the first eight,
        // so every collider past the eighth was silently ignored.
        static int CIndex(ref NativeClothWorking b, int i)
        {
            switch (i)
            {
                case 0: return b.CIndex0; case 1: return b.CIndex1; case 2: return b.CIndex2; case 3: return b.CIndex3;
                case 4: return b.CIndex4; case 5: return b.CIndex5; case 6: return b.CIndex6; case 7: return b.CIndex7;
                case 8: return b.CIndex8; case 9: return b.CIndex9; case 10: return b.CIndex10; case 11: return b.CIndex11;
                case 12: return b.CIndex12; case 13: return b.CIndex13; case 14: return b.CIndex14; default: return b.CIndex15;
            }
        }

        /// Colliders are stored in their root parent's space and transformed to world on every
        /// use: 0x180007c3e reads the collider's own ParentWorkIndex (0x40), scales by the 0x20
        /// NativeRootParentWork stride, rotates Position by WorldRotation (+0x10) and adds
        /// WorldPosition (+0x00) at 0x180007d7f/0x180007db8/0x180007e07. The .cpp used Position
        /// raw, which is only right when that parent happens to be identity.
        static Vector3 ColliderWorld(Vector3 local, NativeRootParentWork[] parents, int pi)
        {
            if (parents == null || pi < 0 || pi >= parents.Length) return local;
            return Qrot(parents[pi].WorldRotation, local) + parents[pi].WorldPosition;
        }

        /// Push pos onto the sphere of radius R about center, from whichever side IsInner asks.
        static void PushToSphere(Vector3 center, float R, bool inner, ref Vector3 pos)
        {
            Vector3 d = pos - center;
            float dist = Len(d);
            if (dist <= 1e-6f) return;
            if (inner) { if (dist > R) pos = center + d * (R / dist); }
            else       { if (dist < R) pos = center + d * (R / dist); }
        }

        static void ResolveOne(ref NativeClothWorking bne, ref NativeClothCollision c,
                               NativeRootParentWork[] parents, ref Vector3 pos)
        {
            if (c.IsEnable == 0) return;
            // Character colliders only apply to bones that asked for them (0x180007397): if the
            // bone's CheckCharaCollision is clear, every IsCharaCollision collider is skipped.
            if (bne.CheckCharaCollision == 0 && c.IsCharaCollision != 0) return;

            // An inner collider SUBTRACTS the bone radius where an outer one adds it
            // (0x180007e26 subss against 0xcc on the IsInner branch). The .cpp added it on both.
            bool inner = c.IsInner != 0;
            float R = inner ? c.Radius - bne.CollisionRadius : c.Radius + bne.CollisionRadius;

            // Type dispatch at 0x1800073b6: 0 sphere, 2 capsule, 3 plane, and 1 deliberately
            // falls through unhandled. The .cpp treated every non-zero type as a capsule, so
            // planes were solved as capsules, and type 1 was solved when it should be skipped.
            switch (c.Type)
            {
                case 0:
                    PushToSphere(ColliderWorld(c.Position, parents, c.ParentWorkIndex), R, inner, ref pos);
                    break;

                case 2:
                {
                    Vector3 p0 = ColliderWorld(c.Position, parents, c.ParentWorkIndex);
                    Vector3 p1 = ColliderWorld(c.Position2, parents, c.ParentWorkIndex);
                    Vector3 ab = p1 - p0;
                    float t = Vector3.Dot(pos - p0, ab) / (Vector3.Dot(ab, ab) + 1e-8f);
                    t = t < 0 ? 0 : (t > 1 ? 1 : t);
                    PushToSphere(p0 + ab * t, R, inner, ref pos);
                    break;
                }

                case 3:
                {
                    // Half-space. Normal is used untransformed here -- this branch never reads
                    // the collider's ParentWorkIndex, unlike the sphere and capsule ones.
                    Vector3 n = c.Normal;
                    float dot = pos.x * n.x + pos.y * n.y + pos.z * n.z - c.Distance;
                    if (dot > bne.CollisionRadius) break;               // comiss/ja at 0x180007412
                    pos = pos + n * (bne.CollisionRadius - dot);
                    ConstrainLength(ref bne, bne.SelfPosition, ref pos); // 0x18000744e
                    break;
                }
            }
        }

        /// ActiveCollision is a COUNT of occupied collider slots, not a flag: the loop at
        /// 0x180008018 increments and compares against it. The .cpp read it as a boolean and
        /// then walked a fixed eight slots, so it both over- and under-ran the real list.
        static void ResolveCollisions(ref NativeClothWorking bne, NativeClothCollision[] col,
                                      NativeRootParentWork[] parents, ref Vector3 pos)
        {
            if (col == null) return;
            int n = bne.ActiveCollision;
            if (n > 16) n = 16;
            for (int i = 0; i < n; i++)
            {
                int idx = CIndex(ref bne, i);
                if (idx < 0 || idx >= col.Length) continue; // -1 marks an empty slot
                ResolveOne(ref bne, ref col[idx], parents, ref pos);
            }
        }

        static Quaternion QConj(Quaternion q) => new Quaternion(-q.x, -q.y, -q.z, q.w);

        /// One axis of the rotation limit. The plugin normalises with fmod(a + 360, 360) (the
        /// three fmod calls at 0x18000a4ca/0x18000a4e9/0x18000a506, against the 360.0 at
        /// 0x18008eca0), folds anything above 180 back down by 360 (0x18000a516 onwards against
        /// the 180.0 at 0x18008ec84), and only then clamps.
        ///
        /// The clamp is lopsided and that is not a typo: 0x18000a558 NEGATES LimitRotationMin
        /// before comparing, so the low edge is -Min, not Min. The shape at 0x18000a55c is
        /// (-Min > a) ? -Min : min(Max, a).
        static float ClampLimitAngle(float a, float lo, float hi)
        {
            a = a % 360f;
            if (a < 0f) a += 360f;
            if (a > 180f) a -= 360f;
            float min = -lo;
            return (min > a) ? min : Mathf.Min(hi, a);
        }

        /// Clamp the bone's deflection from its rest pose into the per-axis limit box.
        ///
        /// The angles are Unity's own ZXY euler convention in degrees, not an ad-hoc one: the
        /// helper at 0x180008400 is a transliteration of UnityEngine's QuaternionToEuler, down to
        /// the FLT_EPSILON gimbal test (0x18008ebe8) against +-1, the +-pi/2 lock values
        /// (0x18008ec0c/0x18008ec98) and the closing conversion by 360/2pi at 0x180008645. So
        /// Quaternion.eulerAngles and Quaternion.Euler reproduce it directly, degrees and
        /// [0,360) range included, instead of needing the extraction hand-ported.
        static Quaternion ApplyRotationLimit(ref NativeClothWorking b, Quaternion q, Quaternion final)
        {
            Quaternion local = Qmul(QConj(q), final);
            Vector3 e = local.eulerAngles;
            e.x = ClampLimitAngle(e.x, b.LimitRotationMin.x, b.LimitRotationMax.x);
            e.y = ClampLimitAngle(e.y, b.LimitRotationMin.y, b.LimitRotationMax.y);
            e.z = ClampLimitAngle(e.z, b.LimitRotationMin.z, b.LimitRotationMax.z);
            return Qmul(q, Quaternion.Euler(e));
        }

        // Two alternates, scored alongside the primary so one run decides the composition order
        // and the sign of the low edge rather than a rebuild per guess.
        static Quaternion ApplyRotationLimitRight(ref NativeClothWorking b, Quaternion q, Quaternion final)
        {
            Vector3 e = Qmul(final, QConj(q)).eulerAngles;
            e.x = ClampLimitAngle(e.x, b.LimitRotationMin.x, b.LimitRotationMax.x);
            e.y = ClampLimitAngle(e.y, b.LimitRotationMin.y, b.LimitRotationMax.y);
            e.z = ClampLimitAngle(e.z, b.LimitRotationMin.z, b.LimitRotationMax.z);
            return Qmul(Quaternion.Euler(e), q);
        }

        static Quaternion ApplyRotationLimitPosMin(ref NativeClothWorking b, Quaternion q, Quaternion final)
        {
            Vector3 e = Qmul(QConj(q), final).eulerAngles;
            e.x = ClampLimitAngle(e.x, -b.LimitRotationMin.x, b.LimitRotationMax.x);
            e.y = ClampLimitAngle(e.y, -b.LimitRotationMin.y, b.LimitRotationMax.y);
            e.z = ClampLimitAngle(e.z, -b.LimitRotationMin.z, b.LimitRotationMax.z);
            return Qmul(q, Quaternion.Euler(e));
        }

        /// Pull the bone back onto the sphere of radius InitBoneDistance about its anchor. The
        /// plugin runs this twice on the skirt-knee path, so it lives in one place.
        static void ConstrainLength(ref NativeClothWorking b, Vector3 anchor, ref Vector3 pos)
        {
            Vector3 dir = pos - anchor;
            float dl = Len(dir);
            if (dl > 1e-6f) pos = anchor + dir * (b.InitBoneDistance / dl);
        }

        static void SolveCloth(int idx, ref NativeClothWorking b, NativeClothCollision[] col,
            NativeRootParentWork[] parents,
            float stiffnessForceRate, float dragForceRate, float gravityRate,
            float windX, float windY, float windZ, float windStrength,
            bool bCollisionSwitch, float timescale, bool is60FPS)
        {
            if (b.IsSkip != 0) return;
            float D = (springRate != 0f) ? springRate : 1f;

            // 1) Verlet movement delta, then shift history
            Vector3 delta = b.PrevTargetPosition - b.TargetPosition;
            if (!is60FPS) delta = delta * 0.5f;  // 30fps baseline
            b.PrevTargetPosition = b.TargetPosition;

            // 2) Aim = BoneAxis rotated by (ParentRotation . InitLocalRotation).
            //
            // This deliberately does NOT write AnimationRotation. An earlier reading had the
            // solve produce it, on the evidence that it equalled Qmul(ParentRotation,
            // InitLocalRotation) to seven digits -- but that only held on the chain root over the
            // first few frames, where the incoming AnimationRotation still happened to equal
            // InitLocalRotation. On bone 4 at frame 289 the two were nothing alike. It is really
            // an accumulator threaded down the chain by the caller loop, so the solve reads it
            // and must leave it alone.
            Quaternion q = Qmul(b.ParentRotation, b.InitLocalRotation);
            b.AimVector = Qrot(q, b.BoneAxis);

            if (CySpringDiff.Armed)
            {
                CySpringDiff.NoteAim(idx, 0, b.AimVector);
                CySpringDiff.NoteAim(idx, 1, Qrot(b.AnimationRotation, b.BoneAxis));
                CySpringDiff.NoteAim(idx, 2, Qrot(b.ParentRotation, b.BoneAxis));
                CySpringDiff.NoteAim(idx, 3, Qrot(Qmul(b.AnimationRotation, b.InitLocalRotation), b.BoneAxis));
                CySpringDiff.NoteAim(idx, 4, Norm(b.TargetPosition - b.SelfPosition));
                CySpringDiff.NoteAim(idx, 5, Qrot(Qmul(b.ParentRotation, b.AnimationRotation), b.BoneAxis));
            }

            // 3) Forces. Divisors read out of the plugin, not guessed: NativeClothUpdate at
            // 0x180009aed multiplies by StiffnessForce (0xc4) then divides by the float at
            // 0x18008ec8c = 1000, and multiplies by DragForce (0xc8) then divides by the one at
            // 0x18008ec80 = 100. The .cpp reconstruction had these two transposed, which made
            // stiffness 10x too strong and drag 10x too weak on every bone.
            // Every force term is also divided by 30, the 30fps baseline the constants are tuned
            // for. The plugin loads that literal into xmm8 at 0x180009ac0 (from 0x18008ec7c) and
            // divides stiffness, drag, gravity and ExtraForce by it -- 0x180009b1f, 0x180009b24,
            // 0x180009bef, 0x180009c26. It is NOT springRate, which the .cpp mistook it for, and
            // wind is pointedly not divided. Omitting it made every force exactly 30x too large.
            float stiff = (b.StiffnessForce / 1000f) * stiffnessForceRate / FORCE_DIV;
            float drag  = (b.DragForce / 100f) * dragForceRate / FORCE_DIV;
            float windH = b.HorizontalWindRateSlow + (b.HorizontalWindRateFast - b.HorizontalWindRateSlow) * windStrength;
            float windV = b.VerticalWindRateSlow + (b.VerticalWindRateFast - b.VerticalWindRateSlow) * windStrength;
            float grav  = (gravityRate * b.Gravity / 10000f) / FORCE_DIV;
            Vector3 ef = b.ConnectedForce; // .cpp ExtraForce (0x144)
            b.Force = new Vector3(
                stiff * b.AimVector.x + drag * delta.x + windH * windX + ef.x / FORCE_DIV,
                stiff * b.AimVector.y + drag * delta.y + windV * windY - grav + ef.y / FORCE_DIV,
                stiff * b.AimVector.z + drag * delta.z + windH * windZ + ef.z / FORCE_DIV);

            // 4) Integrate (Verlet)
            Vector3 pos = b.TargetPosition - delta * timescale + b.Force;

            // 5) Length constraint to InitBoneDistance about the parent anchor
            Vector3 anchor = b.SelfPosition;
            ConstrainLength(ref b, anchor, ref pos);

            // 6) Collision
            if (bCollisionSwitch && b.ActiveCollision > 0) ResolveCollisions(ref b, col, parents, ref pos);

            // 6b) Skirt-knee plane push. A half-space test the .cpp left out entirely: if the
            // bone has fallen within CollisionRadius of the knee plane it is pushed back out
            // along SkirtKneeNormal, and the length constraint is then re-applied because that
            // push moves it off the InitBoneDistance sphere. The plugin gates this on
            // IsCheckSkirtKnee (0x138) at 0x180009f69, dots pos - SkirtNormalPos (0x128) against
            // SkirtKneeNormal (0x118) at 0x180009f7a-0x180009fc4, and takes the branch only when
            // CollisionRadius > dot (comiss/jbe at 0x180009fc8). The second constraint is the
            // block at 0x18000a019, reached on this path alone.
            if (b.IsCheckSkirtKnee != 0)
            {
                Vector3 n = b.SkirtKneeNormal;
                Vector3 d = pos - b.SkirtNormalPos;
                float dot = d.x * n.x + d.y * n.y + d.z * n.z;
                if (b.CollisionRadius > dot)
                {
                    pos = pos + n * (b.CollisionRadius - dot);
                    ConstrainLength(ref b, anchor, ref pos);
                }
            }

            // 7) Commit. Diff holds the NORMALISED direction from the anchor to the settled
            // position, not the raw movement the .cpp assumed. The plugin writes the raw
            // difference to 0x80 at 0x18000a0a7, then divides by its length and overwrites the
            // same three floats at 0x18000a10d -- so the surviving value is a unit vector, which
            // is why the port's small delta sat a full 1.0 away from it. It is also exactly the
            // vector the final rotation needs, so compute it once.
            Vector3 aimTo = Norm(pos - anchor);
            b.Diff = aimTo;
            Quaternion swing = QFromTo(b.AimVector, aimTo);
            Quaternion final = Qmul(swing, q);
            // 8) Rotation limit, gated on IsLimit (0xec) at 0x18000a17e -- absent from the .cpp
            // entirely, and the single largest remaining source of FinalRotation error.
            if (b.IsLimit != 0) final = ApplyRotationLimit(ref b, q, final);
            b.FinalRotation = final;
            b.TargetPosition = pos;

            if (CySpringDiff.Armed)
            {
                Quaternion raw = Qmul(swing, q);
                CySpringDiff.NoteFinal(idx, 0, raw);                                  // unclamped
                CySpringDiff.NoteFinal(idx, 1, final);                                // q * clamp(conj(q)*raw)
                CySpringDiff.NoteFinal(idx, 2, ApplyRotationLimitRight(ref b, q, raw)); // clamp(raw*conj(q)) * q
                CySpringDiff.NoteFinal(idx, 3, ApplyRotationLimitPosMin(ref b, q, raw)); // min not negated
            }
        }

        // springRate is threaded through the whole update; kept in a field so SolveCloth's
        // signature stays close to the .cpp without re-listing every rate on each call.
        static float springRate;

        public static void NativeClothUpdate(NativeClothWorking[] cond, int nCond,
            NativeClothCollision[] collisions, NativeRootParentWork[] parents,
            float stiffnessForceRate, float dragForceRate, float gravityRate,
            float windX, float windY, float windZ, float windStrength,
            bool bCollisionSwitch, float timescale, bool is60FPS,
            float moveRate, float addMoveRate, float springRateArg)
        {
            if (cond == null) return;
            if (nCond > cond.Length) nCond = cond.Length;
            springRate = springRateArg;
            // Clearing IsCheckSkirtKnee is per bone: the plugin's loop at 0x18000917d writes
            // [r10+rbx+0x138] with r10 = i * 0x168.
            for (int i = 0; i < nCond; i++) cond[i].IsCheckSkirtKnee = 0;

            for (int i = 0; i < nCond; i++)
            {
                // Chain the bone onto the one before it. The .cpp solved every bone from the
                // caller's snapshot, which is only right for the root: the plugin walks the list
                // in order and feeds each bone the PREVIOUS bone's freshly solved result, so a
                // chain moves as a chain instead of every link pivoting about a stale anchor.
                // The loop body at 0x1800093a1 copies with two 16-byte moves, off a 0x168 stride:
                //   [rax+rbx-0x128] -> [r9+0x90]   cond[i-1].TargetPosition -> SelfPosition
                //   [rax+rbx-0x148] -> [r9+0x10]   cond[i-1].FinalRotation  -> ParentRotation
                // and then premultiplies this bone's local AnimationRotation by cond[i-1]'s
                // (0x1800093e5 reads cond[i-1] + 0x114, its .w).
                //
                // The root has no predecessor, so it is seeded from the root-parent array the
                // .cpp ignored entirely: 0x1800091ac reads ParentWorkIndex (0x140), 0x1800091ce
                // scales it by the 0x20 NativeRootParentWork stride, and 0x1800091d2 takes the
                // quaternion at +0x10 -- WorldRotation.
                if (i == 0)
                {
                    int pi = cond[0].ParentWorkIndex;
                    if (parents != null && pi >= 0 && pi < parents.Length)
                        cond[0].AnimationRotation = Qmul(parents[pi].WorldRotation, cond[0].AnimationRotation);
                }
                else
                {
                    cond[i].SelfPosition = cond[i - 1].TargetPosition;
                    cond[i].ParentRotation = cond[i - 1].FinalRotation;
                    cond[i].AnimationRotation = Qmul(cond[i - 1].AnimationRotation, cond[i].AnimationRotation);
                }

                SolveCloth(i, ref cond[i], collisions, parents, stiffnessForceRate, dragForceRate, gravityRate,
                    windX, windY, windZ, windStrength, bCollisionSwitch, timescale, is60FPS);
            }
        }

        static void ProcessSkirtCollider(ref NativeSkirtWorking w, Vector3 jointRelRoot, Vector3 centerRelRoot,
            float radius, float influenceAngle, float influenceMaxAngle)
        {
            Vector3 toChild = w.SkirtInitChildPos - jointRelRoot;
            float d = Len(toChild);
            if (d < radius)
            {
                float t = influenceMaxAngle > influenceAngle ? (radius - d) / (radius + 1e-6f) : 0f;
                w.Evaluation = t;
                w.OffsetAngle = t * (influenceMaxAngle - influenceAngle) + influenceAngle;
                w.RotationAxis = Norm(Vector3.Cross(toChild, centerRelRoot));
            }
        }

        public static void NativeSkirtUpdate(ref NativeSkirtWorking w, ref NativeSkirtArg a)
        {
            Vector3 root = a.RootPos;
            Vector3 center = a.CenterPos - root;
            if (w.IsCheckLeftKnee != 0)   ProcessSkirtCollider(ref w, a.KneeLPos - root, center, a.KneeColliderRadius, a.InfluenceAngle, a.InfluenceMaxAngle);
            if (w.IsCheckLeftAnkle != 0)  ProcessSkirtCollider(ref w, a.AnkleLPos - root, center, a.AnkleColliderRadius, a.InfluenceAngle, a.InfluenceMaxAngle);
            if (w.IsCheckRightKnee != 0)  ProcessSkirtCollider(ref w, a.KneeRPos - root, center, a.KneeColliderRadius, a.InfluenceAngle, a.InfluenceMaxAngle);
            if (w.IsCheckRightAnkle != 0) ProcessSkirtCollider(ref w, a.AnkleRPos - root, center, a.AnkleColliderRadius, a.InfluenceAngle, a.InfluenceMaxAngle);
        }

        public static void NativeClothSkirtUpdate(NativeClothWorking[] cond, int nCond,
            NativeClothCollision[] collisions, NativeSkirtWorking[] skirt, int skirtIndex, ref NativeSkirtArg arg,
            NativeRootParentWork[] parents,
            float stiffnessForceRate, float dragForceRate, float gravityRate,
            float windX, float windY, float windZ, float windStrength,
            bool bCollisionSwitch, float timescale, bool is60FPS,
            float moveRate, float addMoveRate, float springRateArg)
        {
            if (skirt != null && (uint)skirtIndex < (uint)skirt.Length)
                NativeSkirtUpdate(ref skirt[skirtIndex], ref arg);
            NativeClothUpdate(cond, nCond, collisions, parents, stiffnessForceRate, dragForceRate, gravityRate,
                windX, windY, windZ, windStrength, bCollisionSwitch, timescale, is60FPS, moveRate, addMoveRate, springRateArg);
        }
    }
}
