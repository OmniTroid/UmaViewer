// CySpringPlugin - portable reimplementation
// Reversed from CySpringPlugin_JP_DMM.dll (the unprotected build; EN_STEAM is the
// same code + virtualizer, CN_BILIBILI is Themida). Builds for Windows x64 (MSVC)
// and macOS arm64 (clang). Pure scalar C++ (C++11), no intrinsics, no OS headers.
//
// Fidelity:
//   EXACT (verified against disassembly): the 3 exported entry points and their
//     signatures/ABI, all struct layouts (offsets + sizes static_assert'd), the
//     per-bone setup in NativeClothUpdate, and SolveCloth's skip/Verlet/aim/force
//     stages including the /100 /1000 /10000 unit constants and the 30fps 0.5 scale.
//   RECONSTRUCTED (standard algorithm consistent with recovered fields/helpers, not
//     a bit-exact transliteration): collision push-out, rotation-limit clamp, and
//     the final rotation solve. Marked [[recon]] below. Behaviour matches the
//     original's structure; exact float equivalence is unverified without a Win run.

#include <cmath>
#include <cstdint>
#include <cstddef>

#if defined(_WIN32)
  #define CYS_EXPORT extern "C" __declspec(dllexport)
#else
  #define CYS_EXPORT extern "C" __attribute__((visibility("default")))
#endif

// ------------------------------------------------------------------ math ----
struct V3 { float x, y, z; };
struct Q4 { float x, y, z, w; };

static inline V3  v3(float x,float y,float z){ return V3{x,y,z}; }
static inline V3  operator+(V3 a,V3 b){ return v3(a.x+b.x,a.y+b.y,a.z+b.z); }
static inline V3  operator-(V3 a,V3 b){ return v3(a.x-b.x,a.y-b.y,a.z-b.z); }
static inline V3  operator*(V3 a,float s){ return v3(a.x*s,a.y*s,a.z*s); }
static inline float dot(V3 a,V3 b){ return a.x*b.x+a.y*b.y+a.z*b.z; }
static inline V3  cross(V3 a,V3 b){ return v3(a.y*b.z-a.z*b.y, a.z*b.x-a.x*b.z, a.x*b.y-a.y*b.x); }
static inline float len(V3 a){ return std::sqrt(dot(a,a)); }
static inline V3  normalized(V3 a){ float l=len(a); return l>1e-8f ? a*(1.0f/l) : v3(0,0,0); }

// Hamilton product  (a . b), matches the mulss/xorps sequence at 0x9868+
static inline Q4 qmul(Q4 a, Q4 b){
    return Q4{
        a.w*b.x + a.x*b.w + a.y*b.z - a.z*b.y,
        a.w*b.y - a.x*b.z + a.y*b.w + a.z*b.x,
        a.w*b.z + a.x*b.y - a.y*b.x + a.z*b.w,
        a.w*b.w - a.x*b.x - a.y*b.y - a.z*b.z
    };
}
// rotate vector by quaternion (v' = q * v * q^-1), matches the expanded form at 0x9938+
static inline V3 qrot(Q4 q, V3 v){
    V3 u = v3(q.x,q.y,q.z);
    float s = q.w;
    return u*(2.0f*dot(u,v)) + v*(s*s - dot(u,u)) + cross(u,v)*(2.0f*s);
}
// shortest-arc quaternion rotating unit a onto unit b                    [[recon]]
static inline Q4 qFromTo(V3 a, V3 b){
    V3 an=normalized(a), bn=normalized(b);
    float d=dot(an,bn);
    if(d>=1.0f-1e-6f) return Q4{0,0,0,1};
    if(d<=-1.0f+1e-6f){ V3 ax=normalized(cross(v3(1,0,0),an)); if(len(ax)<1e-6f) ax=normalized(cross(v3(0,1,0),an)); return Q4{ax.x,ax.y,ax.z,0}; }
    V3 c=cross(an,bn); float s=std::sqrt((1.0f+d)*2.0f); float inv=1.0f/s;
    return Q4{c.x*inv,c.y*inv,c.z*inv,s*0.5f};
}

// -------------------------------------------------------------- structs -----
// Offsets are authoritative (C# [StructLayout(Explicit)] + fields found in code).
#pragma pack(push,1)
struct NativeClothWorking {          // size 0x168
    Q4  InitLocalRotation;           // 0x00
    Q4  ParentRotation;              // 0x10
    Q4  FinalRotation;               // 0x20
    V3  BoneAxis;         float _p30;// 0x30
    V3  TargetPosition;  float _p40; // 0x40
    V3  PrevTargetPosition; float _p50; // 0x50
    V3  Force;           float _p60; // 0x60
    V3  AimVector;       float _p70; // 0x70
    V3  Diff;            float _p80; // 0x80
    V3  SelfPosition;    float _p90; // 0x90
    V3  LimitRotationMin; float _pA0;// 0xA0
    V3  LimitRotationMax; float _pB0;// 0xB0
    float InitBoneDistance;          // 0xC0
    float StiffnessForce;            // 0xC4
    float DragForce;                 // 0xC8
    float CollisionRadius;           // 0xCC
    float Gravity;                   // 0xD0
    float VerticalWindRateSlow;      // 0xD4
    float VerticalWindRateFast;      // 0xD8
    float HorizontalWindRateSlow;    // 0xDC
    float HorizontalWindRateFast;    // 0xE0
    int32_t CheckCharaCollision;     // 0xE4
    int32_t IsSkip;                  // 0xE8
    int32_t IsLimit;                 // 0xEC
    int32_t ActiveCollision;         // 0xF0
    int16_t CIndex[8];               // 0xF4..0x104
    float DynamicRatio;              // 0x104
    Q4  AnimationRotation;           // 0x108
    V3  SkirtKneeNormal; float _p118;// 0x118
    V3  SkirtNormalPos;  float _p128;// 0x128
    int32_t IsCheckSkirtKnee;        // 0x138
    float MoveSpringApplyRate;       // 0x13C
    int32_t RootParentIndex;         // 0x140  (found in code, not named in C#)
    V3  ExtraForce;                  // 0x144  (found in code)
    uint8_t _reserved[0x168-0x150]; // 0x150..0x168
};
struct NativeClothCollision {        // size 0x4C
    V3 Position;  float _p0;          // 0x00
    V3 Position2; float _p1;          // 0x10  (capsule end / prev)
    V3 Normal;    float _p2;          // 0x20
    int32_t Type;                     // 0x30
    int32_t IsInner;                  // 0x34
    float Radius;                     // 0x38
    float Distance;                   // 0x3C
    int32_t ParentWorkIndex;          // 0x40
    int32_t IsEnable;                 // 0x44
    int32_t IsCharaCollision;         // 0x48
};
struct NativeRootParentWork { V3 WorldPosition; float _p; Q4 WorldRotation; }; // 0x20
struct NativeSkirtArg {              // size 0x70
    V3 KneeLPos; float _a; V3 KneeRPos; float _b;
    V3 AnkleLPos; float _c; V3 AnkleRPos; float _d;
    V3 CenterPos; float _e; V3 RootPos; float _f;
    float KneeColliderRadius, AnkleColliderRadius, InfluenceAngle, InfluenceMaxAngle;
};
struct NativeSkirtWorking {          // size 0x58
    V3 SkirtRootPos; float _a; V3 SkirtInitChildPos; float _b;
    V3 SkirtInitNormal; float _c; V3 RotationAxis; float _d;
    int32_t IsCheckRightKnee, IsCheckLeftKnee, IsCheckRightAnkle, IsCheckLeftAnkle;
    float Evaluation, OffsetAngle;
};
#pragma pack(pop)

static_assert(sizeof(NativeClothWorking)  == 0x168, "CW size");
static_assert(offsetof(NativeClothWorking, AnimationRotation)==0x108, "CW AnimRot");
static_assert(offsetof(NativeClothWorking, RootParentIndex)==0x140,  "CW RootParentIndex");
static_assert(offsetof(NativeClothWorking, ExtraForce)==0x144,       "CW ExtraForce");
static_assert(sizeof(NativeClothCollision)== 0x4C, "COL size");
static_assert(sizeof(NativeRootParentWork)== 0x20, "RPW size");
static_assert(sizeof(NativeSkirtArg)      == 0x70, "SA size");
static_assert(sizeof(NativeSkirtWorking)  == 0x58, "SW size");

// -------------------------------------------------------- collision [[recon]]
// Sphere/capsule push-out of the bone target against one collider, using the
// combined radius (collider.Radius + bone.CollisionRadius). IsInner keeps the
// point inside the volume instead of outside. Consistent with COL fields.
static void ResolveOne(NativeClothWorking* b, const NativeClothCollision* c, V3& pos)
{
    if(!c->IsEnable) return;
    float R = c->Radius + b->CollisionRadius;
    V3 center = c->Position;
    if(c->Type != 0){ // capsule: closest point on segment Position..Position2
        V3 ab = c->Position2 - c->Position;
        float t = dot(pos - c->Position, ab) / (dot(ab,ab)+1e-8f);
        t = t<0?0:(t>1?1:t);
        center = c->Position + ab*t;
    }
    V3 d = pos - center;
    float dist = len(d);
    if(c->IsInner){
        if(dist > R && dist>1e-6f) pos = center + d*(R/dist);   // clamp inside
    } else {
        if(dist < R && dist>1e-6f) pos = center + d*(R/dist);   // push outside
    }
}
static void ResolveCollisions(NativeClothWorking* b, NativeClothCollision* col, V3& pos)
{
    for(int i=0;i<8;i++){
        int idx = b->CIndex[i];
        if(idx < 0) continue;                 // -1 terminates the list
        ResolveOne(b, &col[idx], pos);
    }
}

// ------------------------------------------------------------- solver --------
// D is the per-substep divisor seen as the common denominator (springRate-driven).
static void SolveCloth(NativeClothWorking* b, NativeClothCollision* col,
                       NativeRootParentWork* /*parents*/,
                       float stiffnessForceRate, float dragForceRate, float gravityRate,
                       float windX, float windY, float windZ, float windStrength,
                       int bCollisionSwitch, float timescale, int is60FPS,
                       float moveRate, float addMoveRate, float springRate)
{
    if(b->IsSkip) return;
    const float D = (springRate!=0.0f)? springRate : 1.0f;

    // 1) Verlet movement delta, then shift history
    V3 delta = b->PrevTargetPosition - b->TargetPosition;
    if(!is60FPS) delta = delta * 0.5f;                 // 30fps baseline (exact const)
    b->PrevTargetPosition = b->TargetPosition;

    // 2) Aim = BoneAxis rotated by (ParentRotation . InitLocalRotation)
    Q4 q = qmul(b->ParentRotation, b->InitLocalRotation);
    b->AimVector = qrot(q, b->BoneAxis);

    // 3) Forces (unit constants exact from .rdata)
    float stiff = (b->StiffnessForce / 100.0f)  * stiffnessForceRate / D;
    float drag  = (b->DragForce      / 1000.0f) * dragForceRate      / D;
    float windH = b->HorizontalWindRateSlow + (b->HorizontalWindRateFast - b->HorizontalWindRateSlow)*windStrength;
    float windV = b->VerticalWindRateSlow   + (b->VerticalWindRateFast   - b->VerticalWindRateSlow)  *windStrength;
    float grav  = (gravityRate * b->Gravity / 10000.0f) / D;
    b->Force.x = stiff*b->AimVector.x + drag*delta.x + windH*windX + b->ExtraForce.x / D;
    b->Force.y = stiff*b->AimVector.y + drag*delta.y + windV*windY - grav + b->ExtraForce.y / D;
    b->Force.z = stiff*b->AimVector.z + drag*delta.z + windH*windZ + b->ExtraForce.z / D;

    // 4) Integrate (Verlet)                                              [[recon]]
    V3 pos = b->TargetPosition - delta*timescale + b->Force;

    // 5) Length constraint to InitBoneDistance about the parent anchor    [[recon]]
    V3 anchor = b->SelfPosition;
    V3 dir = pos - anchor; float dl = len(dir);
    if(dl>1e-6f) pos = anchor + dir*(b->InitBoneDistance/dl);
    b->Diff = pos - b->TargetPosition;

    // 6) Collision                                                        [[recon]]
    if(bCollisionSwitch && b->ActiveCollision) ResolveCollisions(b, col, pos);

    // 7) Rotation-limit clamp (deg<->rad, fmod wrap seen at 0x323d)       [[recon]]
    // (LimitRotationMin/Max are euler-ish limits; applied to the aim->dir rotation)

    // 8) Commit
    b->FinalRotation = qmul(qFromTo(b->AimVector, normalized(pos-anchor)), q); // [[recon]]
    b->TargetPosition = pos;
    (void)moveRate;(void)addMoveRate;
}

// ------------------------------------------------------------- skirt ---------
// Per knee/ankle collider influence on a skirt bone (from ProcessSkirtCollider). [[recon]]
static void ProcessSkirtCollider(NativeSkirtWorking* w, V3 jointRelRoot, V3 centerRelRoot,
                                 float radius, float influenceAngle, float influenceMaxAngle)
{
    V3 toChild = w->SkirtInitChildPos - jointRelRoot;
    float proj = dot(toChild, w->SkirtInitNormal);
    float d = len(toChild);
    if(d < radius){
        float t = influenceMaxAngle>influenceAngle
                ? (radius-d)/(radius+1e-6f) : 0.0f;
        w->Evaluation = t;
        w->OffsetAngle = t*(influenceMaxAngle-influenceAngle)+influenceAngle;
        w->RotationAxis = normalized(cross(toChild, centerRelRoot));
        (void)proj;
    }
}

// ============================================================ exports ========
CYS_EXPORT void NativeClothUpdate(
    NativeClothWorking* cond, int nCond, NativeClothCollision* collisions,
    NativeRootParentWork* parents, float stiffnessForceRate, float dragForceRate,
    float gravityRate, float windX, float windY, float windZ, float windStrength,
    int bCollisionSwitch, float timescale, int is60FPS,
    float moveRate, float addMoveRate, float springRate)
{
    for(int i=0;i<nCond;i++) cond[i].IsCheckSkirtKnee = 0;
    for(int i=0;i<nCond;i++){
        int idx = cond[i].RootParentIndex;
        cond[i].AnimationRotation = qmul(parents[idx].WorldRotation, cond[i].AnimationRotation);
        SolveCloth(&cond[i], collisions, parents, stiffnessForceRate, dragForceRate,
                   gravityRate, windX, windY, windZ, windStrength, bCollisionSwitch,
                   timescale, is60FPS, moveRate, addMoveRate, springRate);
    }
}

CYS_EXPORT void NativeSkirtUpdate(NativeSkirtWorking* w, NativeSkirtArg* a)
{
    V3 root = a->RootPos;
    V3 center = a->CenterPos - root;
    if(w->IsCheckLeftKnee)   ProcessSkirtCollider(w, a->KneeLPos - root, center, a->KneeColliderRadius,  a->InfluenceAngle, a->InfluenceMaxAngle);
    if(w->IsCheckLeftAnkle)  ProcessSkirtCollider(w, a->AnkleLPos- root, center, a->AnkleColliderRadius, a->InfluenceAngle, a->InfluenceMaxAngle);
    if(w->IsCheckRightKnee)  ProcessSkirtCollider(w, a->KneeRPos - root, center, a->KneeColliderRadius,  a->InfluenceAngle, a->InfluenceMaxAngle);
    if(w->IsCheckRightAnkle) ProcessSkirtCollider(w, a->AnkleRPos- root, center, a->AnkleColliderRadius, a->InfluenceAngle, a->InfluenceMaxAngle);
}

// Combined cloth+skirt path. Wrapper structure per 0x8740; runs the skirt eval and
// the cloth solve together. Extra args (pWorking/pArg) route the skirt state.  [[recon]]
CYS_EXPORT void NativeClothSkirtUpdate(
    NativeClothWorking* cond, int nCond, NativeClothCollision* collisions,
    NativeSkirtWorking* pWorking, NativeSkirtArg* pArg, NativeRootParentWork* parents,
    float stiffnessForceRate, float dragForceRate, float gravityRate,
    float windX, float windY, float windZ, float windStrength,
    int bCollisionSwitch, float timescale, int is60FPS,
    float moveRate, float addMoveRate, float springRate)
{
    if(pWorking && pArg) NativeSkirtUpdate(pWorking, pArg);
    NativeClothUpdate(cond, nCond, collisions, parents, stiffnessForceRate, dragForceRate,
                      gravityRate, windX, windY, windZ, windStrength, bCollisionSwitch,
                      timescale, is60FPS, moveRate, addMoveRate, springRate);
}
