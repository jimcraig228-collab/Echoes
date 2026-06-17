using UnityEngine;
using UnityEngine.UI;
using TMPro;

// ============================================================
//  EchoesScanHUD.cs
//  Echoes — Programmable Spatial Experience Platform
//  Version: 2.4.0  |  16 June 2026
//
//  Builds the entire scan UI in code (landscape) and wires the
//  references into EchoesScanController. No prefabs, no editor UI.
//
//  Creates: full-screen border frame, top prompt, bottom HUD,
//  START/STOP button, SET POSE button.
//
//  Attach to: the same GameObject as EchoesScanController, OR
//  let EchoesBootstrap add both. Order doesn't matter; this runs
//  in Awake and injects refs before the controller's Start.
// ============================================================

[RequireComponent(typeof(RectTransform))]
[RequireComponent(typeof(EchoesScanController))]
public class EchoesScanHUD : MonoBehaviour
{
    private static Color C(float r,float g,float b,float a=1f)=>new Color(r,g,b,a);
    private static readonly Color Deep    = C(0.076f,0.020f,0.137f);
    private static readonly Color Purple  = C(0.439f,0.251f,0.722f);
    private static readonly Color Light   = C(0.949f,0.902f,1.000f);
    private static readonly Color Msg     = C(0.784f,0.659f,0.941f);

    void Awake()
    {
        var ctrl = GetComponent<EchoesScanController>();
        var root = GetComponent<RectTransform>();
        Stretch(root);

        // ── Border frame (4 thin edge images so centre stays clear) ──
        var border = NewImage("Border", root, new Color(0.6f,0.6f,0.6f,0.4f));
        Stretch(border.rectTransform);
        // Make it a hollow frame by using a sprite-less Image with a thick outline:
        // simplest reliable approach = 4 edge bars
        var frameParent = NewEmpty("BorderFrame", root);
        Stretch(frameParent);
        Image top = EdgeBar("BTop", frameParent, new Vector2(0,1), new Vector2(1,1), 14f, true);
        Image bot = EdgeBar("BBot", frameParent, new Vector2(0,0), new Vector2(1,0), 14f, true);
        Image lft = EdgeBar("BLeft", frameParent, new Vector2(0,0), new Vector2(0,1), 14f, false);
        Image rgt = EdgeBar("BRight", frameParent, new Vector2(1,0), new Vector2(1,1), 14f, false);
        // Drive all four from one logical colour via a tiny relay
        var relay = frameParent.gameObject.AddComponent<BorderColorRelay>();
        relay.bars = new[] { top, bot, lft, rgt };
        border.gameObject.SetActive(false); // hide the full-screen tint, use frame only
        ctrl.borderRelay = relay;

        // ── Top prompt ──
        var prompt = NewTMP("Prompt", root, "", 22f, true, Light);
        Anchor(prompt.rectTransform, 0.5f, 0.90f, 900f, 80f);
        prompt.alignment = TextAlignmentOptions.Center;
        ctrl.promptText = prompt;

        // ── Bottom HUD line ──
        var hud = NewTMP("Hud", root, "", 15f, false, Msg);
        Anchor(hud.rectTransform, 0.5f, 0.10f, 900f, 60f);
        hud.alignment = TextAlignmentOptions.Center;
        ctrl.hudText = hud;

        // ── START / STOP button (bottom centre) ──
        var (btn, lbl) = NewButton("StartStop", root, "START", Purple, Light);
        Anchor(btn.GetComponent<RectTransform>(), 0.5f, 0.04f, 220f, 64f);
        ctrl.startStopButton = btn;
        ctrl.startStopLabel  = lbl;

        // ── SET POSE button (bottom right, smaller) ──
        var (sbtn, slbl) = NewButton("SetPose", root, "SET POSE",
                                     new Color(0.2f,0.1f,0.35f,0.9f), Msg);
        Anchor(sbtn.GetComponent<RectTransform>(), 0.88f, 0.04f, 150f, 50f);
        ctrl.setPoseButton = sbtn;
    }

    // ── Builders ──

    private Image NewImage(string n, Transform p, Color c)
    {
        var go = new GameObject(n, typeof(RectTransform), typeof(Image));
        go.transform.SetParent(p, false);
        var i = go.GetComponent<Image>(); i.color = c; i.raycastTarget = false;
        return i;
    }

    private RectTransform NewEmpty(string n, Transform p)
    {
        var go = new GameObject(n, typeof(RectTransform));
        go.transform.SetParent(p, false);
        return go.GetComponent<RectTransform>();
    }

    private Image EdgeBar(string n, Transform p, Vector2 aMin, Vector2 aMax, float thickness, bool horizontal)
    {
        var i = NewImage(n, p, new Color(0.6f,0.6f,0.6f,0.4f));
        var rt = i.rectTransform;
        rt.anchorMin = aMin; rt.anchorMax = aMax;
        if (horizontal) { rt.sizeDelta = new Vector2(0, thickness); }
        else            { rt.sizeDelta = new Vector2(thickness, 0); }
        rt.anchoredPosition = Vector2.zero;
        return i;
    }

    private TextMeshProUGUI NewTMP(string n, Transform p, string t, float size, bool bold, Color c)
    {
        var go = new GameObject(n, typeof(RectTransform), typeof(TextMeshProUGUI));
        go.transform.SetParent(p, false);
        var x = go.GetComponent<TextMeshProUGUI>();
        x.text=t; x.fontSize=size; x.color=c;
        x.fontStyle = bold ? FontStyles.Bold : FontStyles.Normal;
        x.raycastTarget = false;
        return x;
    }

    private (Button, TextMeshProUGUI) NewButton(string n, Transform p, string label, Color bg, Color fg)
    {
        var go = new GameObject(n, typeof(RectTransform), typeof(Image), typeof(Button));
        go.transform.SetParent(p, false);
        go.GetComponent<Image>().color = bg;
        var lblGo = new GameObject("Label", typeof(RectTransform), typeof(TextMeshProUGUI));
        lblGo.transform.SetParent(go.transform, false);
        var lbl = lblGo.GetComponent<TextMeshProUGUI>();
        lbl.text = label; lbl.fontSize = 20f; lbl.color = fg;
        lbl.alignment = TextAlignmentOptions.Center; lbl.fontStyle = FontStyles.Bold;
        Stretch(lbl.rectTransform);
        return (go.GetComponent<Button>(), lbl);
    }

    private void Anchor(RectTransform rt, float ax, float ay, float w, float h)
    {
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(ax, ay);
        rt.sizeDelta = new Vector2(w, h);
        rt.anchoredPosition = Vector2.zero;
    }
    private void Stretch(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
        rt.offsetMin = rt.offsetMax = Vector2.zero;
    }
}

// Relays one colour to the four border bars.
// The controller calls SetColor() directly each frame.
public class BorderColorRelay : MonoBehaviour
{
    public Image[] bars;
    public void SetColor(Color c)
    {
        if (bars != null) foreach (var b in bars) if (b) b.color = c;
    }
}
