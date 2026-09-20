using UnityEngine;

namespace Gallop
{
    // Managed C# port of native/CySpring/CySpringPlugin.cpp (the reversed CySpring solver),
    // operating in place on the same Native* structs. Lets CySpring run with no native plugin
    // (WebGL/IL2CPP, or any platform via CySpringNative.UseNativePlugin = false). Mirrors the
    // .cpp math, unit constants, and stages; see that file for the fidelity notes.
    public static class CySpringSolver
    {
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

        static int CIndex(ref NativeClothWorking b, int i)
        {
            switch (i)
            {
                case 0: return b.CIndex0; case 1: return b.CIndex1; case 2: return b.CIndex2; case 3: return b.CIndex3;
                case 4: return b.CIndex4; case 5: return b.CIndex5; case 6: return b.CIndex6; default: return b.CIndex7;
            }
        }

        static void ResolveOne(ref NativeClothWorking bne, ref NativeClothCollision c, ref Vector3 pos)
        {
            if (c.IsEnable == 0) return;
            float R = c.Radius + bne.CollisionRadius;
            Vector3 center = c.Position;
            if (c.Type != 0) // capsule: closest point on segment Position..Position2
            {
                Vector3 ab = c.Position2 - c.Position;
                float t = Vector3.Dot(pos - c.Position, ab) / (Vector3.Dot(ab, ab) + 1e-8f);
                t = t < 0 ? 0 : (t > 1 ? 1 : t);
                center = c.Position + ab * t;
            }
            Vector3 dd = pos - center;
            float dist = Len(dd);
            if (c.IsInner != 0) { if (dist > R && dist > 1e-6f) pos = center + dd * (R / dist); }   // clamp inside
            else                { if (dist < R && dist > 1e-6f) pos = center + dd * (R / dist); }   // push outside
        }

        static void ResolveCollisions(ref NativeClothWorking bne, NativeClothCollision[] col, ref Vector3 pos)
        {
            if (col == null) return;
            for (int i = 0; i < 8; i++)
            {
                int idx = CIndex(ref bne, i);
                if (idx < 0 || idx >= col.Length) continue; // -1 marks an empty slot
                ResolveOne(ref bne, ref col[idx], ref pos);
            }
        }

        static void SolveCloth(ref NativeClothWorking b, NativeClothCollision[] col,
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

            // 2) Aim = BoneAxis rotated by (ParentRotation . InitLocalRotation)
            Quaternion q = Qmul(b.ParentRotation, b.InitLocalRotation);
            b.AimVector = Qrot(q, b.BoneAxis);

            // 3) Forces. Divisors read out of the plugin, not guessed: NativeClothUpdate at
            // 0x180009aed multiplies by StiffnessForce (0xc4) then divides by the float at
            // 0x18008ec8c = 1000, and multiplies by DragForce (0xc8) then divides by the one at
            // 0x18008ec80 = 100. The .cpp reconstruction had these two transposed, which made
            // stiffness 10x too strong and drag 10x too weak on every bone.
            float stiff = (b.StiffnessForce / 1000f) * stiffnessForceRate / D;
            float drag  = (b.DragForce / 100f) * dragForceRate / D;
            float windH = b.HorizontalWindRateSlow + (b.HorizontalWindRateFast - b.HorizontalWindRateSlow) * windStrength;
            float windV = b.VerticalWindRateSlow + (b.VerticalWindRateFast - b.VerticalWindRateSlow) * windStrength;
            float grav  = (gravityRate * b.Gravity / 10000f) / D;
            Vector3 ef = b.ConnectedForce; // .cpp ExtraForce (0x144)
            b.Force = new Vector3(
                stiff * b.AimVector.x + drag * delta.x + windH * windX + ef.x / D,
                stiff * b.AimVector.y + drag * delta.y + windV * windY - grav + ef.y / D,
                stiff * b.AimVector.z + drag * delta.z + windH * windZ + ef.z / D);

            // 4) Integrate (Verlet)
            Vector3 pos = b.TargetPosition - delta * timescale + b.Force;

            // 5) Length constraint to InitBoneDistance about the parent anchor
            Vector3 anchor = b.SelfPosition;
            Vector3 dir = pos - anchor;
            float dl = Len(dir);
            if (dl > 1e-6f) pos = anchor + dir * (b.InitBoneDistance / dl);
            b.Diff = pos - b.TargetPosition;

            // 6) Collision
            if (bCollisionSwitch && b.ActiveCollision != 0) ResolveCollisions(ref b, col, ref pos);

            // 7) Commit
            b.FinalRotation = Qmul(QFromTo(b.AimVector, Norm(pos - anchor)), q);
            b.TargetPosition = pos;
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
            for (int i = 0; i < nCond; i++) cond[i].IsCheckSkirtKnee = 0;
            for (int i = 0; i < nCond; i++)
            {
                int idx = cond[i].ParentWorkIndex; // .cpp RootParentIndex (0x140)
                if (parents != null && (uint)idx < (uint)parents.Length)
                    cond[i].AnimationRotation = Qmul(parents[idx].WorldRotation, cond[i].AnimationRotation);
                SolveCloth(ref cond[i], collisions, stiffnessForceRate, dragForceRate, gravityRate,
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
