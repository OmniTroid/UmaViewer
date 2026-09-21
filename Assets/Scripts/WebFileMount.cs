using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;

// On WebGL the game-data folder lives behind a File System Access API handle picked in the
// template. Every reader (sqlite by path, AssetBundle.LoadFromFile, the encrypted FileStream)
// opens files by path against MEMFS, so callers on coroutine paths yield on Ensure() to fault
// each file in before the synchronous open runs. No-op everywhere but WebGL players.
public static class WebFileMount
{
    public const string MountRoot = "/uma";

#if UNITY_WEBGL && !UNITY_EDITOR
    [DllImport("__Internal")] private static extern int UmaFS_Ready();
    [DllImport("__Internal")] private static extern int UmaFS_IsMounted(string path);
    [DllImport("__Internal")] private static extern int UmaFS_BeginMount(string path);
    [DllImport("__Internal")] private static extern int UmaFS_PollMount(int id);

    public static bool Active => true;
    public static bool Ready => UmaFS_Ready() == 1;

    public static IEnumerator WaitForPick()
    {
        while (UmaFS_Ready() != 1) yield return null;
    }

    public static IEnumerator Ensure(string path)
    {
        if (string.IsNullOrEmpty(path) || UmaFS_IsMounted(path) == 1) yield break;
        int id = UmaFS_BeginMount(path);
        while (UmaFS_PollMount(id) == 0) yield return null;
    }

    public static IEnumerator EnsureMany(IEnumerable<string> paths)
    {
        foreach (var p in paths) yield return Ensure(p);
    }
#else
    public static bool Active => false;
    public static bool Ready => true;
    public static IEnumerator WaitForPick() { yield break; }
    public static IEnumerator Ensure(string path) { yield break; }
    public static IEnumerator EnsureMany(IEnumerable<string> paths) { yield break; }
#endif
}
