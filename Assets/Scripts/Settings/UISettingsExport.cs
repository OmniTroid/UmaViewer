using System;
using System.Collections;
using System.IO;
using SFB;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// The "Export" section of the settings sidebar: the loaded character as a PMX, and the
/// loaded animation as VMDs, written the same way the CLI writes them (MotionExporter).
///
///  * Export Model   -- the PMX (+ textures) in MMDRestPose, spring bones baked (no rigid bodies).
///  * Export Idle    -- the loaded _loop clip, one period, starting at the clip's frame 0.
///  * Export Preanim -- the clip's _s build-up, from the shared neutral stance into the pose.
///
/// The section is built at start-up from the scene's own sidebar pieces (a header button and
/// a button row cloned from the Other section), so the scene needs no edit.
public class UISettingsExport : MonoBehaviour
{
    static UmaViewerBuilder Builder => UmaViewerBuilder.Instance;
    static UmaViewerMain Main => UmaViewerMain.Instance;
    static UmaViewerUI UI => UmaViewerUI.Instance;

    Button modelButton, idleButton, preanimButton;
    TMPro.TMP_Text infoText;
    bool busy;
    string describedClip;

    /// Build the section under the settings content, right after `after` (a sibling section),
    /// using `headerTemplate` for the collapsible header and `rowTemplate` for button rows.
    public static UISettingsExport Create(Transform after, Button headerTemplate, GameObject rowTemplate, Transform sectionTemplate)
    {
        var content = after.parent;

        var header = Instantiate(headerTemplate, content);
        header.name = "--Export--";
        SetLabel(header.gameObject, "Export");
        header.transform.SetSiblingIndex(after.GetSiblingIndex() + 1);

        // A section is an Image + VerticalLayoutGroup; copy the template's look and drop its
        // rows and its own settings component.
        var section = Instantiate(sectionTemplate.gameObject, content);
        section.name = "Export";
        section.transform.SetSiblingIndex(header.transform.GetSiblingIndex() + 1);
        foreach (var mb in section.GetComponents<MonoBehaviour>()) Destroy(mb);
        for (int i = section.transform.childCount - 1; i >= 0; i--) Destroy(section.transform.GetChild(i).gameObject);
        section.SetActive(false);

        header.onClick = new Button.ButtonClickedEvent();   // not the template's persistent call
        header.onClick.AddListener(() => UI.ToggleUIPanel(section));

        var panel = section.AddComponent<UISettingsExport>();
        panel.modelButton = panel.AddRow(rowTemplate, "Export Model (PMX)", panel.ExportModel);
        panel.preanimButton = panel.AddRow(rowTemplate, "Export Preanim", () => panel.StartCoroutine(panel.ExportCurrentMotion(true)));
        panel.idleButton = panel.AddRow(rowTemplate, "Export Loop", () => panel.StartCoroutine(panel.ExportCurrentMotion(false)));
        // A text row (cloned from a header's label) describing the loaded animation, from MotionProbe.
        var info = Instantiate(headerTemplate.GetComponentInChildren<TMPro.TMP_Text>(true), section.transform);
        info.name = "Info"; info.text = ""; info.fontSize = Mathf.Max(10f, info.fontSize * 0.75f);
        info.alignment = TMPro.TextAlignmentOptions.TopLeft; info.enableWordWrapping = true;
        panel.infoText = info;
        return panel;
    }

    void Update()
    {
        // Keep the description and the buttons in step with whatever animation is loaded.
        var container = Builder != null ? Builder.CurrentUMAContainer : null;
        var clip = container != null && container.OverrideController != null ? container.OverrideController["clip_2"] : null;
        string name = clip != null && clip.name != "clip_2" ? clip.name : null;
        if (name == describedClip) return;
        describedClip = name;
        if (name == null || Main == null)
        {
            if (infoText) infoText.text = "No animation loaded";
            if (preanimButton) preanimButton.interactable = false;
            if (idleButton) idleButton.interactable = false;
            return;
        }
        var r = MotionProbe.Probe(Main, name, loadClips: false);
        if (infoText) infoText.text = MotionProbe.Describe(r);
        if (preanimButton) preanimButton.interactable = !busy && r.HasPreanim;
        if (idleButton)
        {
            idleButton.interactable = !busy && r.Entry != null;
            SetLabel(idleButton.gameObject, r.IsLoop ? "Export Loop" : "Export Animation" + (r.HasCamera ? " + Camera" : ""));
        }
    }

    Button AddRow(GameObject rowTemplate, string label, UnityEngine.Events.UnityAction onClick)
    {
        var row = Instantiate(rowTemplate, transform);
        row.name = label;
        row.SetActive(true);
        var button = row.GetComponentInChildren<Button>(true);
        button.onClick = new Button.ButtonClickedEvent();
        button.onClick.AddListener(onClick);
        SetLabel(row, label);
        return button;
    }

    static void SetLabel(GameObject root, string label)
    {
        var tmp = root.GetComponentInChildren<TextMeshProUGUI>(true);
        if (tmp) { tmp.text = label; return; }
        var text = root.GetComponentInChildren<Text>(true);
        if (text) text.text = label;
    }

    void SetBusy(bool on, Button which = null, string label = null)
    {
        busy = on;
        foreach (var b in new[] { modelButton, idleButton, preanimButton }) if (b) b.interactable = !on;
        if (which && label != null) SetLabel(which.gameObject, label);
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

    IEnumerator ExportCurrentMotion(bool preanim)
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
        if (preanim)
        {
            target = MotionExporter.StartClipOf(Main, loaded.Name);
            if (target == null)
            {
                UI.ShowMessage($"{Path.GetFileName(loaded.Name)} has no build-up (_s) clip", UIMessageType.Warning);
                yield break;
            }
        }

        var dirs = StandaloneFileBrowser.OpenFolderPanel("Export motion to", Config.Instance.MainPath, false);
        if (dirs == null || dirs.Length == 0 || string.IsNullOrEmpty(dirs[0])) yield break;
        string vmdPath = Path.Combine(dirs[0], Path.GetFileName(target.Name) + ".vmd");

        var button = preanim ? preanimButton : idleButton;
        string label = button ? button.GetComponentInChildren<TMPro.TMP_Text>(true)?.text ?? button.GetComponentInChildren<Text>(true)?.text : null;
        SetBusy(true, button, "Exporting...");

        Exception err = null;
        yield return MotionExporter.RunSafe(
            MotionExporter.Record(container, target, vmdPath, container.name, new MotionExporter.Options()), e => err = e);

        if (preanim) container.LoadAnimation(loaded);   // Record left the _s clip in the slot
        UI.LoadedAnimation();                             // reapply the panel's speed setting
        SetBusy(false, button, label);
        describedClip = null;   // re-probe: buttons back to the loaded animation's state
        if (err != null)
        {
            Debug.LogException(err);
            UI.ShowMessage("Export failed: " + err.Message, UIMessageType.Error);
        }
        else UI.ShowMessage($"Saved {vmdPath}", UIMessageType.Success);
#else
        UI.ShowMessage("Not supported on this platform", UIMessageType.Warning);
        yield break;
#endif
    }
}
