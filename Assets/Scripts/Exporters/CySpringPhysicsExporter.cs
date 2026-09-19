using System.Collections.Generic;
using UnityEngine;
using Gallop;
using LibMMD.Model;
using static LibMMD.Model.MMDRigidBody;

// Builds PMX rigid bodies + joints from UmaViewer's CySpring spring-bone data so a viewer
// (babylon-mmd) can simulate skirt/hair/ear/tail physics at runtime. CySpring has no direct
// MMD equivalent, so this is an approximation: each spring chain becomes a kinematic anchor
// body on the root plus a dynamic sphere body per swaying bone, joined in sequence with
// rotation limits; body colliders become kinematic capsules/spheres. Values are heuristic
// and meant to be plausible, not a bit-exact CySpring match.
public static class CySpringPhysicsExporter
{
    const int BODY_GROUP = 0;         // static body colliders (legs/torso)
    const int CLOTH_GROUP = 1;        // dynamic skirt/hair/tail
    const ushort CLOTH_MASK = 0xFFFE; // collide with group 0 only (not self)
    const ushort BODY_MASK = 0x0001;  // collide with everything except group 0

    struct P { public float drag, radius; public bool limited; public Vector3 lmax; }

    public static void Build(UmaContainerCharacter container, RawMMDModel model)
    {
        if (container == null || container.cySpringDataContainers == null) return;

        var idx = new Dictionary<string, int>();
        for (int i = 0; i < model.Bones.Length; i++)
        {
            if (!string.IsNullOrEmpty(model.Bones[i].NameEn) && !idx.ContainsKey(model.Bones[i].NameEn)) idx[model.Bones[i].NameEn] = i;
            if (!string.IsNullOrEmpty(model.Bones[i].Name) && !idx.ContainsKey(model.Bones[i].Name)) idx[model.Bones[i].Name] = i;
        }
        var kids = new Dictionary<int, List<int>>();
        for (int i = 0; i < model.Bones.Length; i++)
        {
            int p = model.Bones[i].ParentIndex;
            if (p < 0) continue;
            if (!kids.TryGetValue(p, out var l)) kids[p] = l = new List<int>();
            l.Add(i);
        }

        var param = new Dictionary<string, P>();
        var roots = new List<string>();
        foreach (var c in container.cySpringDataContainers)
        {
            if (c == null || c.springParam == null) continue;
            foreach (var e in c.springParam)
            {
                if (e == null || string.IsNullOrEmpty(e.BoneName)) continue;
                roots.Add(e.BoneName);
                param[e.BoneName] = new P { drag = e.DragForce, radius = e.CollisionRadius, limited = e._isLimit, lmax = e._limitAngleMax };
                if (e._childElements == null) continue;
                foreach (var ce in e._childElements)
                {
                    if (ce == null || string.IsNullOrEmpty(ce.Name)) continue;
                    param[ce.Name] = new P { drag = ce.DragForce, radius = ce.CollisionRadius, limited = ce.IsLimit, lmax = ce.LimitAngleMax };
                }
            }
        }

        var bodies = new List<MMDRigidBody>();
        var joints = new List<MMDJoint>();
        var bodyOf = new Dictionary<int, int>();

        int colliders = AddColliders(container, model, idx, bodies);
        if (colliders == 0) AddFallbackLegColliders(model, idx, bodies);

        var rootSet = new HashSet<int>();
        foreach (var rn in roots) if (idx.TryGetValue(rn, out int ri)) rootSet.Add(ri);

        foreach (int ri in rootSet)
        {
            bodyOf[ri] = AddBody(bodies, model, ri, true, default);
            var stack = new Stack<int>();
            if (kids.TryGetValue(ri, out var rk)) foreach (var k in rk) stack.Push(k);
            while (stack.Count > 0)
            {
                int b = stack.Pop();
                if (rootSet.Contains(b)) continue;
                P p = param.TryGetValue(model.Bones[b].NameEn, out var pp) ? pp : new P { drag = 0.05f, radius = 0.02f, limited = false };
                bodyOf[b] = AddBody(bodies, model, b, false, p);
                int par = model.Bones[b].ParentIndex;
                if (bodyOf.TryGetValue(par, out int pbody)) joints.Add(MakeJoint(model, b, pbody, bodyOf[b], p));
                if (kids.TryGetValue(b, out var bk)) foreach (var k in bk) stack.Push(k);
            }
        }

        model.Rigidbodies = bodies.ToArray();
        model.Joints = joints.ToArray();
    }

    static int AddColliders(UmaContainerCharacter container, RawMMDModel model, Dictionary<string, int> idx, List<MMDRigidBody> bodies)
    {
        int n = 0;
        foreach (var c in container.cySpringDataContainers)
        {
            if (c == null || c.collisionParam == null) continue;
            foreach (var cd in c.collisionParam)
            {
                var rd = cd != null ? cd.RuntimeData : null;
                if (rd == null || rd.TargetTransform == null || rd.TargetTransform.parent == null) continue;
                if (rd.CollisionType == CySpringCollisionData.CollisionType.None || rd.CollisionType == CySpringCollisionData.CollisionType.Plane) continue;
                if (!idx.TryGetValue(rd.TargetTransform.parent.name, out int bi) || rd.Radius <= 0f) continue;

                // CySpring runtime radii are inconsistently scaled (some colliders come back
                // body-height huge); clamp to a sane world range so the cloth doesn't explode.
                float r = Mathf.Clamp(rd.Radius, 0.01f, 0.2f);
                var body = NewBody(rd.Name ?? ("col_" + bi), bi, BODY_GROUP, BODY_MASK, RigidBodyType.RigidTypeKinematic, 0.9f, 0f);
                if (rd.CollisionType == CySpringCollisionData.CollisionType.Capsule)
                {
                    Vector3 a = rd.TargetTransform.position, b = a + rd.CapsuleAxis;
                    body.Shape = RigidBodyShape.RigidShapeCapsule;
                    body.Position = (a + b) * 0.5f;
                    body.Dimemsions = new Vector3(r, (b - a).magnitude, 0);
                    body.Rotation = CapsuleEuler(b - a);
                }
                else
                {
                    body.Shape = RigidBodyShape.RigidShapeSphere;
                    body.Position = rd.TargetTransform.position;
                    body.Dimemsions = new Vector3(r, 0, 0);
                }
                bodies.Add(body); n++;
            }
        }
        return n;
    }

    // If CySpring collision wasn't resolved (e.g. plugin not loaded), put capsules on the legs
    // so the skirt still has something to collide against instead of clipping through.
    static void AddFallbackLegColliders(RawMMDModel model, Dictionary<string, int> idx, List<MMDRigidBody> bodies)
    {
        (string a, string b, float r)[] segs =
        {
            ("Thigh_L", "Knee_L", 0.09f), ("Knee_L", "Ankle_L", 0.07f),
            ("Thigh_R", "Knee_R", 0.09f), ("Knee_R", "Ankle_R", 0.07f),
        };
        foreach (var s in segs)
        {
            if (!idx.TryGetValue(s.a, out int ia) || !idx.TryGetValue(s.b, out int ib)) continue;
            Vector3 pa = model.Bones[ia].Position, pb = model.Bones[ib].Position;
            var body = NewBody("col_" + s.a, ia, BODY_GROUP, BODY_MASK, RigidBodyType.RigidTypeKinematic, 0.9f, 0f);
            body.Shape = RigidBodyShape.RigidShapeCapsule;
            body.Position = (pa + pb) * 0.5f;
            body.Dimemsions = new Vector3(s.r, (pb - pa).magnitude, 0);
            body.Rotation = CapsuleEuler(pb - pa);
            bodies.Add(body);
        }
    }

    static int AddBody(List<MMDRigidBody> bodies, RawMMDModel model, int bone, bool anchor, P p)
    {
        float r = anchor ? 0.02f : Mathf.Clamp(p.radius, 0.015f, 0.1f);
        // High damping keeps the pendulum chain calm (underdamped chains jitter, especially
        // when a fast run yanks the kinematic anchors).
        var body = NewBody(model.Bones[bone].NameEn, bone, CLOTH_GROUP, CLOTH_MASK,
            anchor ? RigidBodyType.RigidTypeKinematic : RigidBodyType.RigidTypePhysics,
            anchor ? 0.99f : Mathf.Clamp(0.9f + p.drag, 0.9f, 0.995f), anchor ? 0f : 1f);
        body.Shape = RigidBodyShape.RigidShapeSphere;
        body.Dimemsions = new Vector3(r, 0, 0);
        body.Position = model.Bones[bone].Position;
        bodies.Add(body);
        return bodies.Count - 1;
    }

    static MMDRigidBody NewBody(string name, int bone, int group, ushort mask, RigidBodyType type, float damp, float mass)
    {
        return new MMDRigidBody
        {
            Name = name, NameEn = name, AssociatedBoneIndex = bone,
            CollisionGroup = group, CollisionMask = mask, Type = type,
            Mass = mass, TranslateDamp = damp, RotateDamp = damp, Restitution = 0f, Friction = 0.5f,
            Position = Vector3.zero, Rotation = Vector3.zero, Dimemsions = Vector3.zero,
            Shape = RigidBodyShape.RigidShapeSphere,
        };
    }

    static MMDJoint MakeJoint(RawMMDModel model, int bone, int bodyA, int bodyB, P p)
    {
        float degLimit = p.limited ? Mathf.Max(1f, Mathf.Min(Mathf.Abs(p.lmax.x), Mathf.Min(Mathf.Abs(p.lmax.y), Mathf.Abs(p.lmax.z)))) : 40f;
        float rad = Mathf.Min(degLimit, 90f) * Mathf.Deg2Rad;
        return new MMDJoint
        {
            Name = model.Bones[bone].NameEn, NameEn = model.Bones[bone].NameEn,
            AssociatedRigidBodyIndex = new[] { bodyA, bodyB },
            Position = model.Bones[bone].Position, Rotation = Vector3.zero,
            PositionLowLimit = Vector3.zero, PositionHiLimit = Vector3.zero,
            RotationLowLimit = new Vector3(-rad, -rad, -rad), RotationHiLimit = new Vector3(rad, rad, rad),
            // No rotational spring: rotation limits + high body damping keep the chain stable.
            // A stiff spring here fights the limits and oscillates (jitter).
            SpringTranslate = Vector3.zero, SpringRotate = Vector3.zero,
        };
    }

    // Euler (degrees) rotating the capsule's local +Y axis onto `dir` (Unity space).
    static Vector3 CapsuleEuler(Vector3 dir)
    {
        if (dir.sqrMagnitude < 1e-8f) return Vector3.zero;
        return Quaternion.FromToRotation(Vector3.up, dir.normalized).eulerAngles;
    }
}
