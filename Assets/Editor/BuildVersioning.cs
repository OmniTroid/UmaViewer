using System;
using UnityEditor;
using UnityEngine;

// Stamps the 7-char git SHA as PlayerSettings.bundleVersion for a build, then restores it so the
// per-build value doesn't leave the tracked ProjectSettings.asset modified. A dirty tree shares its
// commit SHA, so a timestamp is appended there to keep cached WebGL data from going stale between
// local rebuilds. Call Stamp() before BuildPlayer and Restore() after.
public static class BuildVersioning
{
    public static string Stamp()
    {
        string original = PlayerSettings.bundleVersion;
        PlayerSettings.bundleVersion = Compute();
        return original;
    }

    public static void Restore(string original)
    {
        PlayerSettings.bundleVersion = original;
        AssetDatabase.SaveAssets();
    }

    static string Compute()
    {
        string sha = Git("rev-parse --short=7 HEAD");
        if (string.IsNullOrEmpty(sha)) return "0000000";
        bool dirty = !string.IsNullOrEmpty(Git("status --porcelain"));
        return dirty ? $"{sha}.{DateTime.UtcNow:yyyyMMddHHmmss}" : sha;
    }

    static string Git(string args)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("git", args)
            {
                WorkingDirectory = System.IO.Path.GetDirectoryName(Application.dataPath),
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = System.Diagnostics.Process.Start(psi);
            string outp = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit();
            return outp;
        }
        catch { return ""; }
    }
}
