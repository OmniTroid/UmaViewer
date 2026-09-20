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

        const int FIELDS = 6;
        static readonly string[] FieldNames =
            { "AimVector", "Force", "Diff", "TargetPosition", "PrevTargetPosition", "FinalRotation" };

        // Below this a difference is float reassociation, not a bug. A step that genuinely
        // matches should be bit-identical or within a couple of ULP at these magnitudes.
        const float EPS = 1e-6f;

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

        static int frame;

        public static void Reset()
        {
            for (int i = 0; i < FIELDS; i++)
            {
                maxDiff[i] = 0; sumDiff[i] = 0; countOver[i] = 0; samples[i] = 0;
                firstFrame[i] = -1; firstBone[i] = -1; worstWhere[i] = "";
            }
            divLimit = divCollision = divSkip = divTotal = bonesSeen = 0;
            frame = 0;
        }

        /// Value-copy of the pre-step state. NativeClothWorking is a blittable struct, so a
        /// shallow array clone is a true snapshot.
        public static NativeClothWorking[] Snapshot(NativeClothWorking[] src)
        {
            return (NativeClothWorking[])src.Clone();
        }

        public static void Compare(NativeClothWorking[] nativeOut, NativeClothWorking[] managedOut, int n)
        {
            if (nativeOut == null || managedOut == null) return;
            n = Mathf.Min(n, Mathf.Min(nativeOut.Length, managedOut.Length));
            for (int i = 0; i < n; i++)
            {
                var a = nativeOut[i];
                var b = managedOut[i];
                bool anyDiverged = false;
                bonesSeen++;

                anyDiverged |= Take(0, i, V(a.AimVector, b.AimVector));
                anyDiverged |= Take(1, i, V(a.Force, b.Force));
                anyDiverged |= Take(2, i, V(a.Diff, b.Diff));
                anyDiverged |= Take(3, i, V(a.TargetPosition, b.TargetPosition));
                anyDiverged |= Take(4, i, V(a.PrevTargetPosition, b.PrevTargetPosition));
                anyDiverged |= Take(5, i, Q(a.FinalRotation, b.FinalRotation));

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

        static bool Take(int f, int bone, double d)
        {
            samples[f]++;
            sumDiff[f] += d;
            if (d > maxDiff[f]) { maxDiff[f] = d; worstWhere[f] = "frame " + frame + " bone " + bone; }
            if (d > EPS)
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
        static double Q(Quaternion a, Quaternion b)
        {
            double dot = System.Math.Abs((double)a.x * b.x + (double)a.y * b.y + (double)a.z * b.z + (double)a.w * b.w);
            if (dot > 1.0) dot = 1.0;
            return 2.0 * System.Math.Acos(dot) * Mathf.Rad2Deg;
        }

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
            sb.AppendLine("attributes of diverging bone-steps (points at the branch that differs):");
            sb.AppendLine($"  diverged           : {divTotal} of {bonesSeen}");
            sb.AppendLine($"  of those IsLimit   : {divLimit}");
            sb.AppendLine($"  of those Collision : {divCollision}");
            sb.AppendLine($"  of those IsSkip    : {divSkip}");
            if (divTotal == 0) sb.AppendLine("\nno divergence above eps -- the port reproduces the plugin step for step.");
            return sb.ToString();
        }

        public static void Write(string path)
        {
            try { File.WriteAllText(path, Report()); Debug.Log("CLI_EXPORT: wrote solver diff " + path); }
            catch (Exception ex) { Debug.LogError("CLI_EXPORT_FAIL: solver diff write threw: " + ex); }
        }
    }
}
