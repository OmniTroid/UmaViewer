using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

/// What a named motion asset is and what travels with it. Pure lookups against the asset
/// database (plus clip lengths when the bundles are loadable), so the CLI (--probe) and the
/// GUI's Export section describe an animation the same way.
///
/// A body motion is named
///   3d/motion/{family}/body/{owner}/anm_{kind}_{owner}_{motion}[_{variant}]_{part}
/// family: event, mini/event, raceresult, cutin ...   owner: chr1127_00 (character-specific),
/// type00 (shared by body type) ...   part: _loop (hold), _s (build-up from the shared neutral
/// stance into the pose), _e (wind-down back to it), _pose, _sl, or none (a one-shot).
/// Companions are found by swapping the folder: /facial _face and _ear, /camera _cam,
/// /position _pos. A camera is its own axis: some one-shots have one, some do not.
public static class MotionProbe
{
    public class Result
    {
        public string Query;
        public UmaDatabaseEntry Entry;          // the asset asked about (exact or substring match)
        public string Note;                     // e.g. "substring match"
        public string Family, Owner, Kind, Motion, Variant, Part;
        public bool CharacterSpecific, IsMirror;

        // The motion's parts, whichever one was asked about.
        public UmaDatabaseEntry Loop, Start, End, Pose, ShortLoop;
        // Companions of the asked-about part.
        public UmaDatabaseEntry Facial, Ear, Camera, Position;

        public float Seconds = -1f;             // asked-about clip, when loadable
        public float LoopSeconds = -1f, StartSeconds = -1f, EndSeconds = -1f, CameraSeconds = -1f;

        public bool IsLoop => Part == "loop";
        public bool IsTransition => Part == "s" || Part == "e";
        public bool IsOneShot => Part == "";
        public bool HasPreanim => Start != null;
        public bool HasCamera => Camera != null;
    }

    public static Result Probe(UmaViewerMain main, string query, bool loadClips = true)
    {
        var r = new Result { Query = query };
        r.Entry = main.AbMotions.FirstOrDefault(e => e.Name == query)
               ?? main.AbMotions.FirstOrDefault(e => e.Name.Contains(query));
        if (r.Entry == null) { r.Note = "not found"; return r; }
        if (r.Entry.Name != query && !r.Entry.Name.EndsWith("/" + query))
        {
            int alts = main.AbMotions.Count(e => e.Name.Contains(query));
            r.Note = $"substring match" + (alts > 1 ? $", {alts} assets contain '{query}'" : "");
        }

        string name = r.Entry.Name;
        var segs = name.Split('/');
        string file = segs[segs.Length - 1];
        var ownerRx = new Regex(@"^(chr\d+(_\d+)?|type\d+|crd\d+)$");
        // Owner: a folder named for it (event/body/chara/chr1127_00, event/body/type00), else the
        // chr/type token in the file name (cutin/chara/anm_cti_chr1127_001 has no such folder).
        r.Owner = segs.Skip(2).Take(segs.Length - 3).FirstOrDefault(sg => ownerRx.IsMatch(sg));
        if (r.Owner == null)
        {
            var m = Regex.Match(file, @"_(chr\d+|type\d+|crd\d+)(?=_|$)");
            if (m.Success) r.Owner = m.Groups[1].Value;
        }
        r.CharacterSpecific = r.Owner != null && r.Owner.StartsWith("chr");
        // Family: the folders between "motion" and the body/chara/owner folders.
        int famEnd = System.Array.FindIndex(segs, 2, sg => sg == "body" || sg == "chara" || sg == "facial" || sg == "camera" || sg == "position" || (r.Owner != null && sg == r.Owner));
        if (famEnd < 0) famEnd = segs.Length - 1;
        r.Family = string.Join("/", segs.Skip(2).Take(famEnd - 2));

        // File: anm_{kind}_{owner}_{motion}[_{variant}]_{part}[_mirror]
        string stem = file;
        r.IsMirror = stem.EndsWith("_mirror");
        if (r.IsMirror) stem = stem.Substring(0, stem.Length - "_mirror".Length);
        r.Part = "";
        foreach (var part in new[] { "loop", "s", "e", "pose", "sl" })
            if (stem.EndsWith("_" + part)) { r.Part = part; stem = stem.Substring(0, stem.Length - part.Length - 1); break; }
        string rest = stem.StartsWith("anm_") ? stem.Substring(4) : stem;
        if (r.Owner != null)
        {
            int at = rest.IndexOf("_" + r.Owner + "_");
            if (at >= 0) { r.Kind = rest.Substring(0, at); rest = rest.Substring(at + r.Owner.Length + 2); }
            else if (rest.EndsWith("_" + r.Owner)) { r.Kind = rest.Substring(0, rest.Length - r.Owner.Length - 1); rest = ""; }
        }
        if (rest == "" && r.Owner != null && r.Owner.Contains("_"))
        {
            // cutin/raceresult: the folder is chr1127_001, i.e. character + motion index, not a costume.
            int us = r.Owner.IndexOf('_');
            rest = r.Owner.Substring(us + 1); r.Owner = r.Owner.Substring(0, us);
        }
        var motionParts = rest.Split('_');
        r.Motion = motionParts.Length > 0 ? motionParts[0] : rest;
        r.Variant = motionParts.Length > 1 ? string.Join("_", motionParts.Skip(1)) : "";

        // Sibling parts share the stem up to the part suffix.
        string baseName = name.Substring(0, name.Length - file.Length) + stem;
        string mirror = r.IsMirror ? "_mirror" : "";
        UmaDatabaseEntry Find(string n) => main.AbList.TryGetValue(n, out var e) ? e : null;
        r.Loop = Find(baseName + "_loop" + mirror);
        r.Start = Find(baseName + "_s" + mirror);
        r.End = Find(baseName + "_e" + mirror);
        r.Pose = Find(baseName + "_pose" + mirror);
        r.ShortLoop = Find(baseName + "_sl" + mirror);

        // Companions of the queried part, exactly as UmaContainerCharacter.LoadAnimation looks them up.
        r.Facial = Find(name.Replace("/body", "/facial") + "_face");
        r.Ear = Find(name.Replace("/body", "/facial") + "_ear");
        r.Camera = Find(name.Replace("/body", "/camera") + "_cam");
        r.Position = Find(name.Replace("/body", "/position") + "_pos");

        if (loadClips)
        {
            r.Seconds = ClipSeconds(r.Entry);
            r.LoopSeconds = ClipSeconds(r.Loop);
            r.StartSeconds = ClipSeconds(r.Start);
            r.EndSeconds = ClipSeconds(r.End);
            r.CameraSeconds = ClipSeconds(r.Camera);
        }
        return r;
    }

    static float ClipSeconds(UmaDatabaseEntry e)
    {
        if (e == null) return -1f;
        try { var c = e.Get<AnimationClip>(); return c != null ? c.length : -1f; } catch { return -1f; }
    }

    /// One text block, the same for the terminal and the Export section.
    public static string Describe(Result r)
    {
        var sb = new StringBuilder();
        if (r.Entry == null) { sb.Append($"{r.Query}: not found"); return sb.ToString(); }
        string Short(UmaDatabaseEntry e) => e == null ? "-" : e.Name.Substring(e.Name.LastIndexOf('/') + 1);
        string Sec(float s) => s < 0f ? "" : $" ({s:0.00}s, {Mathf.RoundToInt(s * 30f)}f)";
        sb.AppendLine(Short(r.Entry) + Sec(r.Seconds) + (r.Note != null ? $"   [{r.Note}]" : ""));
        sb.AppendLine($"  family {r.Family ?? "?"} | owner {r.Owner ?? "?"} ({(r.CharacterSpecific ? "character-specific" : "shared")}) | kind {r.Kind ?? "?"} | motion {r.Motion}{(r.Variant != "" ? " variant " + r.Variant : "")}{(r.IsMirror ? " | mirror" : "")}");
        string partWord = r.IsLoop ? "loop (hold)" : r.Part == "s" ? "start: preanim from the neutral stance into the pose"
                        : r.Part == "e" ? "end: wind-down from the pose to the neutral stance" : r.IsOneShot ? "one-shot (played start to end)" : r.Part;
        sb.AppendLine($"  part: {partWord}");
        sb.AppendLine($"  preanim (_s): {Short(r.Start)}{Sec(r.StartSeconds)}");
        sb.AppendLine($"  loop:         {Short(r.Loop)}{Sec(r.LoopSeconds)}");
        sb.AppendLine($"  end (_e):     {Short(r.End)}{Sec(r.EndSeconds)}");
        if (r.Pose != null || r.ShortLoop != null) sb.AppendLine($"  also: pose (static hold of the final posture) {Short(r.Pose)} | sl (alternate start, purpose unconfirmed) {Short(r.ShortLoop)}");
        sb.AppendLine($"  camera: {Short(r.Camera)}{Sec(r.CameraSeconds)} | position: {Short(r.Position)} | facial: {Short(r.Facial)} | ear: {Short(r.Ear)}");
        return sb.ToString().TrimEnd();
    }
}
