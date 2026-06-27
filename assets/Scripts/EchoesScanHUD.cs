// ============================================================
//  EchoesScanHUD.cs
//  Echoes — Programmable Spatial Experience Platform
//  Version: v2.4.8 | 27 June 2026
//
//  v2.4.8 — version-paired with the controller. No HUD logic change
//  this version: the two field-test fixes (diagnostic overlay moved
//  off the SET POSE button, light estimation requested) are both
//  controller-side. The HUD button layout was already correct; the
//  diagnostic box that was covering SET POSE is built by the
//  controller, not here.
//
//  v2.4.7 — Test Instrumentation & Bug Fixes
//  ----------------------------------------------------------
//  FIX 2 (border-render) — EdgeBar() previously centred each
//              thin bar on the screen edge with anchoredPosition
//              zero, so the guided zone borders rendered as large
//              slabs reaching toward mid-screen instead of thin
//              bands hugging the edges. EdgeBar() now pins each
//              bar flush inside its edge using pivot-aware
//              anchoring, so all four borders (and the pose-gate
//              frame, which shares the builder) sit as thin edge
//              strips. No data change.
//
//  FREE-SCAN SUPPORT — GuidedOverlayRelay gains SetGuidedVisible(bool).
//              In free-scan mode the controller hides the zone
//              borders, arrows and instruction banner. The
//              crosshair (and the controller's corner readout)
//              stay visible.
//
//  ----------------------------------------------------------
//  Inherited from v2.4.6 — Guided Scan Overlay UI:
//    - Crosshair (static white dot, flashes on capture)
//    - Four independent zone borders (per-edge addressable)
//    - Four directional arrows (left/right/up/down)
//    - Instruction banner (bottom-centre, above control bar)
//    - Progress bar (time-based fill, swappable source later)
//  All wired through GuidedOverlayRelay, injected into the
//  controller the same one-frame-deferred way as the existing
//  refs. The original single-colour BorderColorRelay is retained
//  for the pose-gate border behaviour.
// ============================================================

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

[RequireComponent(typeof(RectTransform))]
public class EchoesScanHUD : MonoBehaviour
{
    private static Color C(float r,float g,float b,float a=1f)=>new Color(r,g,b,a);
    private static readonly Color Deep   = C(0.076f,0.020f,0.137f);
    private static readonly Color Purple = C(0.439f,0.251f,0.722f);
    private static readonly Color Light  = C(0.949f,0.902f,1.000f);
    private static readonly Color Msg    = C(0.784f,0.659f,0.941f);

    private static readonly Color ZoneNeutral = new Color(1f,1f,1f,0.18f);
    private static readonly Color ZoneAmber   = new Color(0.900f,0.600f,0.100f,0.95f);
    private static readonly Color ZoneGreen   = new Color(0.200f,0.800f,0.400f,0.95f);
    private static readonly Color White       = new Color(1f,1f,1f,0.9f);

    void Start()
    {
        EchoesBootstrap.Diag("HUD.Start() FIRED v2.4.6");

        var ctrl = GetComponent<EchoesScanController>();
        EchoesBootstrap.Diag($"HUD: GetComponent<EchoesScanController>() null={ctrl == null}");
        if (ctrl == null)
        {
            EchoesBootstrap.Diag("HUD: *** CONTROLLER IS NULL -- cannot inject refs ***");
            return;
        }

        var root = GetComponent<RectTransform>();
        Stretch(root);

        // --- Existing pose-gate border frame (single-colour relay) ---
        var border = NewImage("Border", root, new Color(0.6f,0.6f,0.6f,0.4f));
        Stretch(border.rectTransform);
        var frameParent = NewEmpty("BorderFrame", root);
        Stretch(frameParent);
        Image top = EdgeBar("BTop",   frameParent, new Vector2(0,1), new Vector2(1,1), 14f, true);
        Image bot = EdgeBar("BBot",   frameParent, new Vector2(0,0), new Vector2(1,0), 14f, true);
        Image lft = EdgeBar("BLeft",  frameParent, new Vector2(0,0), new Vector2(0,1), 14f, false);
        Image rgt = EdgeBar("BRight", frameParent, new Vector2(1,0), new Vector2(1,1), 14f, false);
        var relay = frameParent.gameObject.AddComponent<BorderColorRelay>();
        relay.bars = new[] { top, bot, lft, rgt };
        border.gameObject.SetActive(false);
        ctrl.borderRelay = relay;
        EchoesBootstrap.Diag("HUD: borderRelay injected");

        // --- Top prompt ---
        var prompt = NewTMP("Prompt", root, "", 22f, true, Light);
        Anchor(prompt.rectTransform, 0.5f, 0.92f, 1100f, 70f);
        prompt.alignment = TextAlignmentOptions.Center;
        ctrl.promptText = prompt;

        // --- Bottom HUD (status line) ---
        var hud = NewTMP("Hud", root, "", 15f, false, Msg);
        Anchor(hud.rectTransform, 0.5f, 0.14f, 900f, 40f);
        hud.alignment = TextAlignmentOptions.Center;
        ctrl.hudText = hud;

        // ==========================================================
        //  GUIDED OVERLAY (v2.4.6)
        // ==========================================================
        var overlayParent = NewEmpty("GuidedOverlay", root);
        Stretch(overlayParent);

        // Crosshair — static white dot, centre
        var cross = NewImage("Crosshair", overlayParent, White);
        Anchor(cross.rectTransform, 0.5f, 0.5f, 16f, 16f);

        // Four guided zone borders — independently addressable
        var gTop = EdgeBar("ZTop",   overlayParent, new Vector2(0,1), new Vector2(1,1), 18f, true);
        var gBot = EdgeBar("ZBot",   overlayParent, new Vector2(0,0), new Vector2(1,0), 18f, true);
        var gLft = EdgeBar("ZLeft",  overlayParent, new Vector2(0,0), new Vector2(0,1), 18f, false);
        var gRgt = EdgeBar("ZRight", overlayParent, new Vector2(1,0), new Vector2(1,1), 18f, false);
        gTop.color = gBot.color = gLft.color = gRgt.color = ZoneNeutral;

        // Four directional arrows (text glyphs for zero-asset simplicity)
        var aLeft  = NewTMP("ArrowLeft",  overlayParent, "\u25C0", 54f, true, ZoneAmber); // ◀
        var aRight = NewTMP("ArrowRight", overlayParent, "\u25B6", 54f, true, ZoneAmber); // ▶
        var aUp    = NewTMP("ArrowUp",    overlayParent, "\u25B2", 54f, true, ZoneAmber); // ▲
        var aDown  = NewTMP("ArrowDown",  overlayParent, "\u25BC", 54f, true, ZoneAmber); // ▼
        Anchor(aLeft.rectTransform,  0.08f, 0.5f, 80f, 80f);
        Anchor(aRight.rectTransform, 0.92f, 0.5f, 80f, 80f);
        Anchor(aUp.rectTransform,    0.5f, 0.82f, 80f, 80f);
        Anchor(aDown.rectTransform,  0.5f, 0.20f, 80f, 80f);
        aLeft.alignment = aRight.alignment = aUp.alignment = aDown.alignment = TextAlignmentOptions.Center;
        // hidden by default
        SetAlpha(aLeft, 0f); SetAlpha(aRight, 0f); SetAlpha(aUp, 0f); SetAlpha(aDown, 0f);

        // Instruction banner — bottom-centre, above control bar
        var banner = NewTMP("InstructionBanner", overlayParent, "", 20f, true, White);
        Anchor(banner.rectTransform, 0.5f, 0.22f, 1200f, 50f);
        banner.alignment = TextAlignmentOptions.Center;

        // Progress bar — track + fill, bottom
        var track = NewImage("ProgressTrack", overlayParent, new Color(1f,1f,1f,0.12f));
        Anchor(track.rectTransform, 0.5f, 0.165f, 1000f, 10f);
        var fillGo = new GameObject("ProgressFill", typeof(RectTransform), typeof(Image));
        fillGo.transform.SetParent(track.transform, false);
        var fill = fillGo.GetComponent<Image>();
        fill.color = ZoneAmber; fill.raycastTarget = false;
        var fillRt = fill.rectTransform;
        fillRt.anchorMin = new Vector2(0f, 0f);
        fillRt.anchorMax = new Vector2(0f, 1f);   // width driven by anchorMax.x at runtime
        fillRt.pivot = new Vector2(0f, 0.5f);
        fillRt.offsetMin = fillRt.offsetMax = Vector2.zero;

        // --- START/STOP button ---
        var (btn, lbl) = NewButton("StartStop", root, "START", Purple, Light);
        Anchor(btn.GetComponent<RectTransform>(), 0.5f, 0.04f, 220f, 64f);
        ctrl.startStopButton = btn;
        ctrl.startStopLabel  = lbl;

        // --- SET POSE button ---
        var (sbtn, slbl) = NewButton("SetPose", root, "SET POSE",
                                     new Color(0.2f,0.1f,0.35f,0.9f), Msg);
        Anchor(sbtn.GetComponent<RectTransform>(), 0.88f, 0.04f, 150f, 50f);
        ctrl.setPoseButton = sbtn;

        // --- Wire up the guided overlay relay ---
        var gRelay = overlayParent.gameObject.AddComponent<GuidedOverlayRelay>();
        gRelay.borderTop = gTop; gRelay.borderBottom = gBot; gRelay.borderLeft = gLft; gRelay.borderRight = gRgt;
        gRelay.arrowLeft = aLeft; gRelay.arrowRight = aRight; gRelay.arrowUp = aUp; gRelay.arrowDown = aDown;
        gRelay.crosshair = cross; gRelay.banner = banner; gRelay.progressFill = fill;
        gRelay.neutral = ZoneNeutral; gRelay.amber = ZoneAmber; gRelay.green = ZoneGreen; gRelay.white = White;
        gRelay.Init();
        ctrl.overlay = gRelay;
        EchoesBootstrap.Diag("HUD: guided overlay injected");

        EchoesBootstrap.Diag("HUD.Start() COMPLETE -- all refs injected");
    }

    // --- Builders ---

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

    // v2.4.7 FIX 2: pin each bar flush inside its screen edge.
    // Previously anchoredPosition was zero with a centre pivot, which
    // centred the bar ON the edge line (half off-screen) and, on the
    // stretched guided overlay, read as a slab reaching mid-screen.
    // Now the pivot is set to the edge and the bar is nudged inward by
    // half its thickness, so the full thin band sits on-screen against
    // the edge. aMin/aMax already collapse the bar to a line along the
    // correct edge; sizeDelta gives it its thickness on the free axis.
    private Image EdgeBar(string n, Transform p, Vector2 aMin, Vector2 aMax, float thickness, bool horizontal)
    {
        var i = NewImage(n, p, new Color(0.6f,0.6f,0.6f,0.4f));
        var rt = i.rectTransform;
        rt.anchorMin = aMin; rt.anchorMax = aMax;

        if (horizontal)
        {
            // top edge: aMax.y == 1; bottom edge: aMax.y == 0
            bool atTop = aMax.y >= 0.999f;
            rt.pivot = new Vector2(0.5f, atTop ? 1f : 0f);
            rt.sizeDelta = new Vector2(0f, thickness);
            rt.anchoredPosition = new Vector2(0f, atTop ? -thickness * 0.5f : thickness * 0.5f);
        }
        else
        {
            // right edge: aMax.x == 1; left edge: aMax.x == 0
            bool atRight = aMax.x >= 0.999f;
            rt.pivot = new Vector2(atRight ? 1f : 0f, 0.5f);
            rt.sizeDelta = new Vector2(thickness, 0f);
            rt.anchoredPosition = new Vector2(atRight ? -thickness * 0.5f : thickness * 0.5f, 0f);
        }
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

    private static void SetAlpha(TextMeshProUGUI t, float a)
    {
        var c = t.color; c.a = a; t.color = c;
    }
}

// Relays one colour to the four border bars (pose-gate frame, unchanged).
public class BorderColorRelay : MonoBehaviour
{
    public Image[] bars;
    public void SetColor(Color c)
    {
        if (bars != null) foreach (var b in bars) if (b) b.color = c;
    }
}

// ============================================================
//  GuidedOverlayRelay (v2.4.6)
//  Drives the guided overlay elements independently. The
//  controller calls these; no scan logic lives here.
// ============================================================
public class GuidedOverlayRelay : MonoBehaviour
{
    public Image borderTop, borderBottom, borderLeft, borderRight;
    public TextMeshProUGUI arrowLeft, arrowRight, arrowUp, arrowDown;
    public Image crosshair;
    public TextMeshProUGUI banner;
    public Image progressFill;

    public Color neutral, amber, green, white;

    // track which borders are "complete" (stay green)
    private readonly HashSet<EchoesScanController.Zone> _complete = new HashSet<EchoesScanController.Zone>();
    private float _flashTimer = -1f;

    // v2.4.7: when false (free-scan), guided elements are hidden and the
    // per-zone setters become no-ops so nothing re-shows them mid-session.
    private bool _guidedVisible = true;

    // v2.4.7: show/hide the guided elements (borders, arrows, banner,
    // progress bar). Crosshair is deliberately left alone — it stays
    // visible in both modes.
    public void SetGuidedVisible(bool visible)
    {
        _guidedVisible = visible;
        SetActiveSafe(borderTop, visible); SetActiveSafe(borderBottom, visible);
        SetActiveSafe(borderLeft, visible); SetActiveSafe(borderRight, visible);
        SetActiveSafe(arrowLeft, visible); SetActiveSafe(arrowRight, visible);
        SetActiveSafe(arrowUp, visible); SetActiveSafe(arrowDown, visible);
        SetActiveSafe(banner, visible);
        if (progressFill != null)
        {
            // hide the whole progress bar (fill + its track parent)
            var track = progressFill.transform.parent;
            if (track != null) track.gameObject.SetActive(visible);
            else progressFill.gameObject.SetActive(visible);
        }
    }

    private static void SetActiveSafe(Component c, bool active)
    {
        if (c != null) c.gameObject.SetActive(active);
    }

    public void Init()
    {
        ResetForSession();
    }

    public void ResetForSession()
    {
        _complete.Clear();
        SetBorder(borderTop, neutral); SetBorder(borderBottom, neutral);
        SetBorder(borderLeft, neutral); SetBorder(borderRight, neutral);
        HideAllArrows();
        if (banner != null) { banner.text = ""; SetTextAlpha(banner, white.a); }
        SetProgress(0f);
        if (crosshair != null) SetImageAlpha(crosshair, white.a);
    }

    // Map a zone to its border edge + arrow
    private Image BorderFor(EchoesScanController.Zone z)
    {
        switch (z)
        {
            case EchoesScanController.Zone.Centre: return borderTop;   // centre uses top edge as its marker
            case EchoesScanController.Zone.Left:   return borderLeft;
            case EchoesScanController.Zone.Right:  return borderRight;
            case EchoesScanController.Zone.Tilt:   return borderBottom; // tilt-up marker on bottom; arrow handles direction
            default: return borderTop;
        }
    }

    private TextMeshProUGUI ArrowFor(EchoesScanController.Zone z)
    {
        switch (z)
        {
            case EchoesScanController.Zone.Left:  return arrowLeft;
            case EchoesScanController.Zone.Right: return arrowRight;
            case EchoesScanController.Zone.Tilt:  return arrowUp;      // tilt = up
            default: return null;                                     // centre = no arrow
        }
    }

    public void SetActiveZone(EchoesScanController.Zone z, Color amberColor)
    {
        HideAllArrows();
        // keep completed borders green, set the active one amber
        var b = BorderFor(z);
        if (b != null && !_complete.Contains(z)) b.color = amberColor;
        var a = ArrowFor(z);
        if (a != null) SetTextAlpha(a, 1f);
    }

    public void SetInstruction(string text, Color c)
    {
        if (banner != null) { banner.text = text; banner.color = new Color(c.r,c.g,c.b, white.a); }
    }

    public void SetZoneComplete(EchoesScanController.Zone z, Color greenColor)
    {
        _complete.Add(z);
        var b = BorderFor(z);
        if (b != null) b.color = greenColor;
        var a = ArrowFor(z);
        if (a != null) SetTextAlpha(a, 0f);
    }

    public void SetAllBordersComplete()
    {
        SetBorder(borderTop, green); SetBorder(borderBottom, green);
        SetBorder(borderLeft, green); SetBorder(borderRight, green);
        HideAllArrows();
        SetProgress(1f);
    }

    public void SetProgress(float p01)
    {
        if (progressFill == null) return;
        var rt = progressFill.rectTransform;
        rt.anchorMax = new Vector2(Mathf.Clamp01(p01), 1f);
        rt.offsetMin = rt.offsetMax = Vector2.zero;
    }

    public void FlashCapture()
    {
        _flashTimer = 0.12f; // brief single white flash
        if (crosshair != null) crosshair.color = Color.white;
    }

    private void Update()
    {
        if (_flashTimer >= 0f)
        {
            _flashTimer -= Time.deltaTime;
            if (_flashTimer < 0f && crosshair != null)
                crosshair.color = white;  // restore
        }
    }

    private void HideAllArrows()
    {
        if (arrowLeft) SetTextAlpha(arrowLeft, 0f);
        if (arrowRight) SetTextAlpha(arrowRight, 0f);
        if (arrowUp) SetTextAlpha(arrowUp, 0f);
        if (arrowDown) SetTextAlpha(arrowDown, 0f);
    }

    private void SetBorder(Image b, Color c) { if (b) b.color = c; }
    private static void SetTextAlpha(TextMeshProUGUI t, float a) { if (!t) return; var c=t.color; c.a=a; t.color=c; }
    private static void SetImageAlpha(Image i, float a) { if (!i) return; var c=i.color; c.a=a; i.color=c; }
}
