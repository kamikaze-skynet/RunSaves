using System;
using HarmonyLib;
using Il2CppInterop.Runtime;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using UObject = UnityEngine.Object;

namespace RunSaves;

/// <summary>Shared bits for building UI out of the game's own components (IMGUI is stripped from this game).</summary>
internal static class UiKit
{
    internal static TMP_FontAsset Font()
    {
        TMP_FontAsset font = null;
        try
        {
            var anyText = UObject.FindObjectOfType<TextMeshProUGUI>();
            if (anyText != null) font = anyText.font;
            if (font == null) font = TMP_Settings.defaultFontAsset;
        }
        catch { }
        return font;
    }

    internal static GameObject Canvas(string name, int order)
    {
        var go = new GameObject(name);
        UObject.DontDestroyOnLoad(go);
        var canvas = go.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = order;
        var scaler = go.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;
        go.AddComponent<GraphicRaycaster>();
        return go;
    }

    internal static GameObject Rect(string name, Transform parent, Vector2 pos, Vector2 size)
    {
        var go = new GameObject(name);
        var rt = go.AddComponent<RectTransform>();
        go.transform.SetParent(parent, false);
        rt.anchoredPosition = pos;
        rt.sizeDelta = size;
        return go;
    }

    internal static TextMeshProUGUI Text(string name, Transform parent, TMP_FontAsset font, float size, Vector2 pos, Vector2 box, TextAlignmentOptions align = TextAlignmentOptions.Center)
    {
        var go = Rect(name, parent, pos, box);
        var t = go.AddComponent<TextMeshProUGUI>();
        t.font = font;
        t.fontSize = size;
        t.alignment = align;
        t.textWrappingMode = TextWrappingModes.Normal;
        t.overflowMode = TextOverflowModes.Overflow; // Ellipsis draws nothing if a line is taller than the box
        t.richText = true;
        t.raycastTarget = false;
        t.color = Color.white;
        return t;
    }

    internal static (Button button, TextMeshProUGUI label) Button(string name, Transform parent, TMP_FontAsset font, string label, Vector2 pos, Vector2 size, UnityAction onClick, Color color)
    {
        var go = Rect(name, parent, pos, size);
        var img = go.AddComponent<Image>();
        img.color = color;
        var btn = go.AddComponent<Button>();
        btn.targetGraphic = img;
        btn.onClick.AddListener(onClick);
        var t = Text("Label", go.transform, font, 22f, Vector2.zero, size);
        t.text = label;
        return (btn, t);
    }

    internal static bool Pressed(string keyName)
    {
        var kb = Keyboard.current;
        if (kb == null || string.IsNullOrEmpty(keyName)) return false;
        if (!Enum.TryParse(keyName, true, out Key key) || key == Key.None) return false;
        var control = kb[key];
        return control != null && control.wasPressedThisFrame;
    }
}

/// <summary>Short messages at the top of the screen.</summary>
internal static class Toast
{
    private static GameObject canvas, panel;
    private static TextMeshProUGUI text;
    private static float hideAt;

    internal static void Show(string message, float seconds)
    {
        try
        {
            if (panel == null)
            {
                var font = UiKit.Font();
                if (font == null) { Plugin.Log.LogInfo(message); return; }
                canvas = UiKit.Canvas("RunSaves_Toast", 520);
                panel = UiKit.Rect("Toast", canvas.transform, Vector2.zero, new Vector2(900f, 80f));
                var rt = panel.GetComponent<RectTransform>();
                rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 1f);
                rt.anchoredPosition = new Vector2(0f, -110f);
                var bg = panel.AddComponent<Image>();
                bg.color = new Color(0.08f, 0.05f, 0.14f, 0.88f);
                bg.raycastTarget = false;
                text = UiKit.Text("Text", panel.transform, font, 22f, Vector2.zero, new Vector2(860f, 70f));
            }
            text.text = message;
            int lines = message.Split('\n').Length;
            panel.GetComponent<RectTransform>().sizeDelta = new Vector2(900f, 30f + lines * 28f);
            text.GetComponent<RectTransform>().sizeDelta = new Vector2(860f, 20f + lines * 28f);
            canvas.SetActive(true);
            hideAt = Time.unscaledTime + seconds;
        }
        catch (Exception ex) { Plugin.Log.LogError($"Toast: {ex.Message}"); }
    }

    internal static void Tick()
    {
        if (canvas != null && canvas.activeSelf && Time.unscaledTime >= hideAt) canvas.SetActive(false);
    }
}

/// <summary>"Save Run" button in the pause menu, for the host during a run. A copy of the menu's own Settings button.</summary>
[HarmonyPatch]
internal static class PauseButton
{
    private static GameObject button;
    private static TextMeshProUGUI label;
    private static UnityAction click;
    private static float resetLabelAt;

    [HarmonyPatch(typeof(PauseMenuManager), nameof(PauseMenuManager.Open))]
    [HarmonyPostfix]
    private static void Opened(PauseMenuManager __instance)
    {
        try
        {
            bool show = Plugin.Enabled.Value && Plugin.ShowSaveButton.Value && Game.IsServer && Recorder.RunActive; // click says why if it can't save
            if (button is null || button.WasCollected) { button = null; label = null; if (show) Build(__instance); }
            if (button != null) button.SetActive(show);
            if (label != null) label.text = "Save Run";
        }
        catch (Exception ex) { Plugin.Log.LogError($"Pause menu button: {ex}"); }
    }

    private static void Build(PauseMenuManager menu)
    {
        var template = menu.settingsButton != null ? menu.settingsButton : menu.resumeButton;
        if (template == null) return;

        button = UObject.Instantiate(template.gameObject, template.transform.parent);
        button.name = "RunSaves_SaveButton";
        button.transform.SetSiblingIndex(template.transform.GetSiblingIndex() + 1);

        // Drop the copied localisation (it would put "Settings" back) and the copied click handlers.
        foreach (var c in button.GetComponentsInChildren<Component>(true))
            if (c != null && c.GetIl2CppType().Name == "LocalizeStringEvent") UObject.Destroy(c);
        var btn = button.GetComponent<Button>();
        btn.onClick = new Button.ButtonClickedEvent();
        click ??= DelegateSupport.ConvertDelegate<UnityAction>(new Action(OnClick));
        btn.onClick.AddListener(click);

        label = button.GetComponentInChildren<TextMeshProUGUI>(true);
        if (label != null) label.text = "Save Run";
        Plugin.Log.LogInfo("Added Save Run to the pause menu.");
    }

    private static void OnClick()
    {
        bool ok = Saver.SaveManual(out string msg);
        Toast.Show(msg, 3f);
        if (label != null) { label.text = ok ? "Saved!" : "Can't save"; resetLabelAt = Time.unscaledTime + 2f; }
    }

    internal static void Tick()
    {
        if (resetLabelAt > 0f && Time.unscaledTime >= resetLabelAt)
        {
            resetLabelAt = 0f;
            if (label is not null && !label.WasCollected) label.text = "Save Run";
        }
    }
}

/// <summary>
/// Lobby prompt for the host: shows the saved run with Resume / Delete / Hide.
/// Resume arms the restore; it happens when the host next starts a run (in the saved world).
/// </summary>
public class RunSavesUI : MonoBehaviour
{
    public RunSavesUI(IntPtr ptr) : base(ptr) { }

    private GameObject canvas, panel;
    private TextMeshProUGUI title, body, status;
    private TextMeshProUGUI resumeLabel, deleteLabel, hideLabel;
    private bool hidden, inMenu;
    private float nextScan, deleteConfirmUntil, nextErrorLog;
    private static UnityAction resumeAct, deleteAct, hideAct;
    private static RunSavesUI instance;

    private void Update()
    {
        instance = this;
        Step("restore", Restorer.Tick);
        Step("autosave", Saver.Tick);
        Step("client spells", Net.TryEquipPending);
        Step("toast", Toast.Tick);
        Step("pause", PauseButton.Tick);
        Step("save key", () =>
        {
            if (UiKit.Pressed(Plugin.SaveKey.Value) && Saver.CanSave(out _))
            {
                Saver.SaveManual(out string msg);
                Toast.Show(msg, 3f);
            }
        });

        if (Time.unscaledTime >= nextScan)
        {
            nextScan = Time.unscaledTime + 0.5f;
            Step("menu check", () =>
            {
                bool menu = Game.InMainMenu();
                if (!menu && inMenu) hidden = false; // show again next time we're back in the lobby
                inMenu = menu;
            });
        }

        bool show = inMenu && !hidden && Plugin.Enabled.Value && Plugin.ShowLobbyPrompt.Value && !Game.IsClientOnly
                    && (SaveStore.Exists || Restorer.Armed != null);
        if (show && panel == null) Step("build", Build);
        if (canvas != null && canvas.activeSelf != show) Step("show/hide", () => canvas.SetActive(show));
        if (show && panel != null && Time.frameCount % 15 == 0) Step("refresh", Refresh);
    }

    private void Step(string what, Action action)
    {
        try { action(); }
        catch (Exception ex)
        {
            if (Time.unscaledTime < nextErrorLog) return;
            nextErrorLog = Time.unscaledTime + 10f;
            Plugin.Log.LogError($"UI ({what}): {ex}");
        }
    }

    private void Build()
    {
        var font = UiKit.Font();
        if (font == null) return;
        canvas = UiKit.Canvas("RunSaves_Lobby", 505);
        // A narrow column on the side of the lobby (clear of the mission bar at the top, the lobby code
        // bottom-right and the wizard buttons on the left), buttons stacked.
        panel = UiKit.Rect("Panel", canvas.transform, Vector2.zero, new Vector2(440f, 400f));
        var rt = panel.GetComponent<RectTransform>();
        bool left = string.Equals(Plugin.LobbyPanelSide.Value, "Left", StringComparison.OrdinalIgnoreCase);
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(left ? 0f : 1f, 0.5f);
        rt.anchoredPosition = new Vector2(left ? 190f : -28f, 20f);
        var bg = panel.AddComponent<Image>();
        bg.color = new Color(0.08f, 0.05f, 0.14f, 0.9f);

        title = UiKit.Text("Title", panel.transform, font, 23f, new Vector2(0f, 168f), new Vector2(400f, 34f));
        title.color = new Color(1f, 0.85f, 0.35f);
        body = UiKit.Text("Body", panel.transform, font, 18f, new Vector2(0f, 98f), new Vector2(400f, 104f));
        status = UiKit.Text("Status", panel.transform, font, 15f, new Vector2(0f, 20f), new Vector2(400f, 48f));
        status.color = new Color(1f, 1f, 1f, 0.6f);

        resumeAct ??= DelegateSupport.ConvertDelegate<UnityAction>(new Action(() => instance?.OnResume()));
        deleteAct ??= DelegateSupport.ConvertDelegate<UnityAction>(new Action(() => instance?.OnDelete()));
        hideAct ??= DelegateSupport.ConvertDelegate<UnityAction>(new Action(() => instance?.OnHide()));
        resumeLabel = UiKit.Button("Resume", panel.transform, font, "Resume", new Vector2(0f, -50f), new Vector2(360f, 48f), resumeAct, new Color(0.2f, 0.55f, 0.3f, 0.95f)).label;
        deleteLabel = UiKit.Button("Delete", panel.transform, font, "Delete", new Vector2(0f, -110f), new Vector2(360f, 48f), deleteAct, new Color(0.55f, 0.2f, 0.2f, 0.95f)).label;
        hideLabel = UiKit.Button("Hide", panel.transform, font, "Hide", new Vector2(0f, -170f), new Vector2(360f, 48f), hideAct, new Color(0.35f, 0.22f, 0.6f, 0.95f)).label;
        Refresh();
    }

    private void Refresh()
    {
        if (panel == null) return;
        var armed = Restorer.Armed;
        var save = armed ?? SaveStore.Read();
        if (save == null) { title.text = "SAVED RUN"; body.text = "The save file couldn't be read."; status.text = ""; return; }

        if (armed != null)
        {
            title.text = "RESUMING ON NEXT RUN";
            body.text = $"Start a run in <b>{save.World}</b> to continue at wave {save.Wave}.\nPlayers in the save: {string.Join(", ", save.Players.ConvertAll(p => p.Name))}";
            status.text = "Everyone should join and pick the same wizards before you start. Everyone needs RunSaves.";
            resumeLabel.text = "Cancel";
        }
        else
        {
            title.text = "SAVED RUN";
            body.text = save.Summary();
            status.text = $"Saved {save.SavedAt} ({(save.Reason == "auto" ? "autosave" : "saved from the menu")})";
            resumeLabel.text = "Resume";
        }
        deleteLabel.text = Time.unscaledTime < deleteConfirmUntil ? "Sure? Click again" : "Delete";
    }

    private void OnResume()
    {
        if (Restorer.Armed != null)
        {
            Restorer.Armed = null;
            Toast.Show("Resume cancelled. The save is kept.", 3f);
        }
        else
        {
            var save = SaveStore.Read();
            if (save == null) { Toast.Show("The save file couldn't be read.", 4f); return; }
            Restorer.Armed = save;
            Toast.Show($"Start a run in {save.World} to continue the saved run.", 5f);
            Plugin.Log.LogInfo($"Resume armed: {save.World}, wave {save.Wave}.");
        }
        Refresh();
    }

    private void OnDelete()
    {
        if (Time.unscaledTime >= deleteConfirmUntil) { deleteConfirmUntil = Time.unscaledTime + 3f; Refresh(); return; }
        deleteConfirmUntil = 0f;
        Restorer.Armed = null;
        SaveStore.Delete();
        Toast.Show("Saved run deleted.", 3f);
        Plugin.Log.LogInfo("Saved run deleted from the lobby.");
    }

    private void OnHide()
    {
        hidden = true;
        if (canvas != null) canvas.SetActive(false);
    }
}
