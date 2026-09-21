using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using SFB;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// The "Export" section of the settings sidebar: the loaded character as a PMX, and the
/// loaded animation as VMDs, written the same way the CLI writes them (MotionExporter).
///
///  * Export Model   -- the PMX (+ textures) in MMDRestPose, spring bones baked (no rigid bodies).
///  * Export Preanim -- the clip's _s build-up, from the shared neutral stance into the pose.
///  * Export Loop    -- the loaded _loop clip, one period, starting at the clip's frame 0
///                      (a non-loop clip: start to end, with its camera as <name>_camera.vmd).
///  * Export Postanim -- the clip's _e wind-down, from the pose back to the neutral stance.
///  * Export Camera  -- the camera as it is now, as a one-key camera VMD.
///
/// The section is built at start-up from the scene's own sidebar pieces (a header button, a
/// button row and a label cloned from the Other section), so the scene needs no edit.
public class UISettingsExport : MonoBehaviour
{
    static UmaViewerBuilder Builder => UmaViewerBuilder.Instance;
    static UmaViewerMain Main => UmaViewerMain.Instance;
    static UmaViewerUI UI => UmaViewerUI.Instance;

    const float RowHeight = 50f;    // the Other section's row height
    const float InfoHeight = 80f;   // three short lines

    Button modelButton, idleButton, preanimButton, postanimButton, cameraButton;
    enum Part { Loop, Preanim, Postanim }
    TMP_Text infoText;
    bool busy;
    string describedClip;

    /// Build the section under the settings content, right after `after` (a sibling section).
    /// `headerTemplate` is a collapsible section header, `sectionTemplate` the Other section
    /// (its Image and VerticalLayoutGroup are kept, its rows and component dropped),
    /// `rowTemplate` one of its button rows and `labelTemplate` one of its row labels.
    public static UISettingsExport Create(Transform after, Button headerTemplate, Transform sectionTemplate,
                                          GameObject rowTemplate, TMP_Text labelTemplate)
    {
        var content = after.parent;

        var header = Instantiate(headerTemplate, content);
        header.name = "--Export--";
        SetLabel(header.gameObject, "Export");
        header.transform.SetSiblingIndex(after.GetSiblingIndex() + 1);

        var section = Instantiate(sectionTemplate.gameObject, content);
        section.name = "Export";
        section.transform.SetSiblingIndex(header.transform.GetSiblingIndex() + 1);
        foreach (var mb in section.GetComponents<MonoBehaviour>())
            if (!(mb is LayoutGroup) && !(mb is Graphic)) Destroy(mb);   // keep the background and the layout
        for (int i = section.transform.childCount - 1; i >= 0; i--) Destroy(section.transform.GetChild(i).gameObject);
        // Rows keep their own heights instead of sharing the box: the info row is not a button.
        var layout = section.GetComponent<VerticalLayoutGroup>();
        if (layout != null)
        {
            layout.childControlHeight = false;
            layout.childForceExpandHeight = false;
            layout.childAlignment = TextAnchor.UpperCenter;
        }
        section.SetActive(false);

        header.onClick = new Button.ButtonClickedEvent();   // not the template's persistent call
        header.onClick.AddListener(() => UI.ToggleUIPanel(section));

        var panel = section.AddComponent<UISettingsExport>();
        panel.infoText = panel.AddInfoRow(rowTemplate, labelTemplate);
        panel.modelButton = panel.AddButtonRow(rowTemplate, "Export Model (PMX)", panel.ExportModel);
        panel.preanimButton = panel.AddButtonRow(rowTemplate, "Export Preanim", () => panel.StartCoroutine(panel.ExportCurrentMotion(Part.Preanim)));
        panel.idleButton = panel.AddButtonRow(rowTemplate, "Export Loop", () => panel.StartCoroutine(panel.ExportCurrentMotion(Part.Loop)));
        panel.postanimButton = panel.AddButtonRow(rowTemplate, "Export Postanim", () => panel.StartCoroutine(panel.ExportCurrentMotion(Part.Postanim)));
        panel.cameraButton = panel.AddButtonRow(rowTemplate, "Export Camera", panel.ExportCamera);

        var rt = section.GetComponent<RectTransform>();
        rt.sizeDelta = new Vector2(rt.sizeDelta.x, InfoHeight + 5f * RowHeight);
        return panel;
    }

    Button AddButtonRow(GameObject rowTemplate, string label, UnityEngine.Events.UnityAction onClick)
    {
        var row = Instantiate(rowTemplate, transform);
        row.name = label;
        row.SetActive(true);
        var rt = row.GetComponent<RectTransform>();
        rt.sizeDelta = new Vector2(rt.sizeDelta.x, RowHeight);
        var button = row.GetComponentInChildren<Button>(true);
        button.onClick = new Button.ButtonClickedEvent();
        button.onClick.AddListener(onClick);
        SetLabel(row, label);
        return button;
    }

    TMP_Text AddInfoRow(GameObject rowTemplate, TMP_Text labelTemplate)
    {
        var row = Instantiate(rowTemplate, transform);
        row.name = "Info";
        row.SetActive(true);
        for (int i = row.transform.childCount - 1; i >= 0; i--) Destroy(row.transform.GetChild(i).gameObject);
        var rt = row.GetComponent<RectTransform>();
        rt.sizeDelta = new Vector2(rt.sizeDelta.x, InfoHeight);

        var text = Instantiate(labelTemplate, row.transform);
        text.name = "Text";
        var trt = text.GetComponent<RectTransform>();
        trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one;
        trt.offsetMin = new Vector2(10f, 4f); trt.offsetMax = new Vector2(-10f, -4f);
        text.fontSize = 14f;
        text.enableAutoSizing = false;
        text.enableWordWrapping = true;
        text.overflowMode = TextOverflowModes.Ellipsis;
        text.alignment = TextAlignmentOptions.TopLeft;
        text.text = "No animation loaded";
        return text;
    }

    static void SetLabel(GameObject root, string label)
    {
        var tmp = root.GetComponentInChildren<TMP_Text>(true);
        if (tmp) { tmp.text = label; return; }
        var text = root.GetComponentInChildren<Text>(true);
        if (text) text.text = label;
    }

    static string GetLabel(Button b)
    {
        if (!b) return null;
        var tmp = b.GetComponentInChildren<TMP_Text>(true);
        if (tmp) return tmp.text;
        var text = b.GetComponentInChildren<Text>(true);
        return text ? text.text : null;
    }

    void SetBusy(bool on, Button which = null, string label = null)
    {
        busy = on;
        foreach (var b in new[] { modelButton, idleButton, preanimButton, postanimButton, cameraButton }) if (b) b.interactable = !on;
        if (which && label != null) SetLabel(which.gameObject, label);
    }

    /// Three short lines about the loaded animation, from MotionProbe.
    static string Summary(MotionProbe.Result r)
    {
        string Short(UmaDatabaseEntry e) => e == null ? null : e.Name.Substring(e.Name.LastIndexOf('/') + 1);
        string Frames(float s) => s < 0f ? "?" : Mathf.RoundToInt(s * 30f) + "f";
        var parts = new List<string>();
        if (r.Loop != null) parts.Add("loop " + Frames(r.LoopSeconds));
        if (r.Start != null) parts.Add("preanim " + Frames(r.StartSeconds));
        if (r.End != null) parts.Add("postanim " + Frames(r.EndSeconds));
        if (r.IsOneShot) parts.Add("one-shot " + Frames(r.Seconds));
        var extras = new List<string>();
        if (r.HasCamera) extras.Add("camera");
        if (r.Position != null) extras.Add("root motion");
        if (r.IsChain) extras.Add(r.Chain.Count + "-cut chain");
        if (r.Facial != null) extras.Add("face");
        return Short(r.Entry) + "\n"
             + (parts.Count > 0 ? string.Join(" | ", parts) : r.Part) + "\n"
             + (extras.Count > 0 ? string.Join(" | ", extras) : "no companions");
    }

    void Update()
    {
        // Keep the summary and the buttons in step with whatever animation is loaded.
        var container = Builder != null ? Builder.CurrentUMAContainer : null;
        var clip = container != null && container.OverrideController != null ? container.OverrideController["clip_2"] : null;
        string name = clip != null && clip.name != "clip_2" ? clip.name : null;
        if (name == describedClip) return;
        describedClip = name;
        if (name == null || Main == null)
        {
            if (infoText) infoText.text = "No animation loaded";
            if (preanimButton) preanimButton.interactable = false;
            if (postanimButton) postanimButton.interactable = false;
            if (idleButton) idleButton.interactable = false;
            return;
        }
        var r = MotionProbe.Probe(Main, name, loadClips: true);
        if (infoText) infoText.text = Summary(r);
        if (preanimButton) preanimButton.interactable = !busy && r.HasPreanim;
        if (postanimButton) postanimButton.interactable = !busy && r.End != null;
        if (idleButton)
        {
            idleButton.interactable = !busy && r.Entry != null;
            SetLabel(idleButton.gameObject, r.IsLoop ? "Export Loop" : r.IsChain ? "Export Cut-in Chain" : "Export Animation");
        }
    }

    public void ExportModel()
    {
        if (busy) return;
#if UNITY_STANDALONE || UNITY_EDITOR
        var container = Builder.CurrentUMAContainer;
        if (!container || container.IsMini)
        {
            UI.ShowMessage("Load a normal UMA first", UIMessageType.Warning);
            return;
        }
        var entry = container.CharaEntry;
        var path = StandaloneFileBrowser.SaveFilePanel("Save PMX File", Config.Instance.MainPath, $"{entry.Id}_{entry.GetName()}", "pmx");
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            MotionExporter.ExportModel(container, path, bakePhysics: true);
            UI.ShowMessage($"Saved {path}", UIMessageType.Success);
        }
        catch (Exception ex)
        {
            Debug.LogException(ex);
            UI.ShowMessage("Model export failed: " + ex.Message, UIMessageType.Error);
        }
#else
        UI.ShowMessage("Not supported on this platform", UIMessageType.Warning);
#endif
    }

    /// The camera that is rendering now: the animation camera while a camera clip plays, else
    /// the main camera (the same choice Screenshot makes).
    public void ExportCamera()
    {
        if (busy) return;
#if UNITY_STANDALONE || UNITY_EDITOR
        var cam = Builder.AnimationCamera != null && Builder.AnimationCamera.isActiveAndEnabled ? Builder.AnimationCamera : Camera.main;
        if (cam == null)
        {
            UI.ShowMessage("No camera to export", UIMessageType.Warning);
            return;
        }
        var path = StandaloneFileBrowser.SaveFilePanel("Save camera VMD", Config.Instance.MainPath, "camera", "vmd");
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            MotionExporter.ExportCameraSnapshot(cam, Builder.CurrentUMAContainer, path);
            UI.ShowMessage($"Saved {path}", UIMessageType.Success);
        }
        catch (Exception ex)
        {
            Debug.LogException(ex);
            UI.ShowMessage("Camera export failed: " + ex.Message, UIMessageType.Error);
        }
#else
        UI.ShowMessage("Not supported on this platform", UIMessageType.Warning);
#endif
    }

    IEnumerator ExportCurrentMotion(Part part)
    {
        if (busy) yield break;
#if UNITY_STANDALONE || UNITY_EDITOR
        var container = Builder.CurrentUMAContainer;
        if (!container || container.IsMini || container.OverrideController == null)
        {
            UI.ShowMessage("Load a normal UMA and an animation first", UIMessageType.Warning);
            yield break;
        }
        // The loaded animation is whatever the viewer put in the loop slot; LoadAnimation names
        // the clip after its asset, so the entry is a dictionary lookup away.
        var current = container.OverrideController["clip_2"];
        if (current == null || current.name == "clip_2" || !Main.AbList.TryGetValue(current.name, out var loaded))
        {
            UI.ShowMessage("No animation is loaded", UIMessageType.Warning);
            yield break;
        }
        var target = loaded;
        if (part == Part.Preanim)
        {
            target = MotionExporter.StartClipOf(Main, loaded.Name);
            if (target == null)
            {
                UI.ShowMessage($"{Path.GetFileName(loaded.Name)} has no build-up (_s) clip", UIMessageType.Warning);
                yield break;
            }
        }
        else if (part == Part.Postanim)
        {
            target = MotionExporter.EndClipOf(Main, loaded.Name);
            if (target == null)
            {
                UI.ShowMessage($"{Path.GetFileName(loaded.Name)} has no wind-down (_e) clip", UIMessageType.Warning);
                yield break;
            }
        }

        var dirs = StandaloneFileBrowser.OpenFolderPanel("Export motion to", Config.Instance.MainPath, false);
        if (dirs == null || dirs.Length == 0 || string.IsNullOrEmpty(dirs[0])) yield break;
        string vmdPath = Path.Combine(dirs[0], Path.GetFileName(target.Name) + ".vmd");

        var button = part == Part.Preanim ? preanimButton : part == Part.Postanim ? postanimButton : idleButton;
        string label = GetLabel(button);
        SetBusy(true, button, "Exporting...");

        Exception err = null;
        yield return MotionExporter.RunSafe(
            MotionExporter.Record(container, target, vmdPath, container.name, new MotionExporter.Options()), e => err = e);

        if (part != Part.Loop) container.LoadAnimation(loaded);   // Record left the _s/_e clip in the slot
        UI.LoadedAnimation();                             // reapply the panel's speed setting
        SetBusy(false, button, label);
        describedClip = null;                             // re-probe: buttons back to the loaded animation's state
        if (err != null)
        {
            Debug.LogException(err);
            UI.ShowMessage("Export failed: " + err.Message, UIMessageType.Error);
        }
        else
        {
            string camPath = MotionExporter.CameraPathFor(vmdPath);
            UI.ShowMessage($"Saved {vmdPath}" + (File.Exists(camPath) ? $" and {Path.GetFileName(camPath)}" : ""), UIMessageType.Success);
        }
#else
        UI.ShowMessage("Not supported on this platform", UIMessageType.Warning);
        yield break;
#endif
    }
}
