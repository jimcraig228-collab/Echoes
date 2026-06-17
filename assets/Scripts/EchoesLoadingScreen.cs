using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using TMPro;

// ============================================================
//  EchoesLoadingScreen.cs  (v2 — uses EchoesRingRenderer)
//
//  WHAT THIS DOES:
//  Builds the entire Echoes branded loading / scanning screen
//  in C# at runtime. No prefabs, no sprites, no editor work.
//
//  HOW TO ADD TO YOUR SCENE:
//  1. Add EchoesLoadingScreenBootstrap.cs to your XR Origin
//     (or any persistent GameObject)
//  2. That's it. Bootstrap creates the Canvas; this fills it.
//
//  PHASE FLOW:
//  Initialising --> (1.8s) --> Scanning --> (1 plane) -->
//  AlmostThere  --> (2 planes) --> Ready --> (fade out)
// ============================================================

[RequireComponent(typeof(RectTransform))]
public class EchoesLoadingScreen : MonoBehaviour
{
    [Header("AR (auto-found if empty)")]
    public ARPlaneManager planeManager;

    [Header("Timing")]
    public float initialisationHoldTime = 1.8f;
    public float fadeOutDuration        = 0.8f;

    public enum ScanPhase { Initialising, Scanning, AlmostThere, Ready }
    private ScanPhase _phase = ScanPhase.Initialising;

    // Brand colours
    private static Color C(float r, float g, float b, float a = 1f) => new Color(r, g, b, a);
    private static readonly Color BgDeep     = C(0.076f, 0.020f, 0.137f);
    private static readonly Color Ring1      = C(0.188f, 0.082f, 0.471f, 0.20f);
    private static readonly Color Ring2      = C(0.282f, 0.114f, 0.549f, 0.38f);
    private static readonly Color Ring3      = C(0.439f, 0.251f, 0.722f, 0.60f);
    private static readonly Color Ring4      = C(0.627f, 0.388f, 0.855f, 0.84f);
    private static readonly Color Ring5      = C(0.851f, 0.706f, 0.973f, 0.96f);
    private static readonly Color WordCol    = C(0.949f, 0.902f, 1.000f);
    private static readonly Color TagCol     = C(0.376f, 0.188f, 0.600f);
    private static readonly Color MsgCol     = C(0.784f, 0.659f, 0.941f);
    private static readonly Color ProgressBg = C(0.282f, 0.114f, 0.549f, 0.20f);
    private static readonly Color ProgressFg = C(0.439f, 0.251f, 0.722f);
    private static readonly Color ArrowIdle  = C(0.627f, 0.388f, 0.855f, 0.35f);
    private static readonly Color ArrowLit   = C(0.851f, 0.706f, 0.973f, 1.00f);
    private static readonly Color StatusCol  = C(0.816f, 0.565f, 0.973f);

    // UI refs
    private CanvasGroup          _group;
    private EchoesRingRenderer[] _rings = new EchoesRingRenderer[5];
    private TextMeshProUGUI      _scanMsg;
    private Image                _progressFill;
    private Image                _progressBg;
    private Image                _statusDot;
    private RectTransform        _arrowsRoot;
    private Image[]              _arrowBtns = new Image[4];
    private TextMeshProUGUI[]    _arrowTxts = new TextMeshProUGUI[4];

    // Animation
    private float _pulseT  = 0f;
    private float _blinkT  = 0f;
    private float _progCur = 0f;
    private float _progTgt = 0f;
    private bool  _doPulse = true;
    private bool  _doBlink = false;

    // Phase data
    private readonly string[] _msgs = {
        "Initialising spatial engine\u2026",
        "Point camera at a flat surface and move slowly",
        "Surface detected \u2014 keep scanning\u2026",
        "Space mapped. Loading experience\u2026"
    };
    private readonly bool[][] _arrowStates = {
        new[] { false, false, false, false },
        new[] { true,  true,  true,  false },
        new[] { true,  false, false, true  },
        new[] { false, false, false, false }
    };
    private readonly float[] _progTargets = { 0f, 0.35f, 0.72f, 1.0f };

    // ── Lifecycle ────────────────────────────────────────────

    void Awake() => BuildUI();

    void Start()
    {
        if (planeManager == null)
            planeManager = FindObjectOfType<ARPlaneManager>();
        if (planeManager != null)
            planeManager.planesChanged += OnPlanesChanged;

        SetPhase(ScanPhase.Initialising);
        StartCoroutine(HoldThenScan());
    }

    void OnDestroy()
    {
        if (planeManager != null)
            planeManager.planesChanged -= OnPlanesChanged;
    }

    void Update()
    {
        if (_doPulse)
        {
            _pulseT += Time.deltaTime * 0.85f;
            float s = 1f + Mathf.Sin(_pulseT * Mathf.PI * 2f) * 0.032f;
            foreach (var r in _rings)
                if (r) r.transform.localScale = Vector3.one * s;
        }

        if (_doBlink)
        {
            _blinkT += Time.deltaTime * 1.4f;
            float a = 0.2f + (Mathf.Sin(_blinkT * Mathf.PI * 2f) * 0.5f + 0.5f) * 0.8f;
            _statusDot.color = new Color(StatusCol.r, StatusCol.g, StatusCol.b, a);
        }

        if (Mathf.Abs(_progCur - _progTgt) > 0.001f)
        {
            _progCur = Mathf.Lerp(_progCur, _progTgt, Time.deltaTime * 2.5f);
            _progressFill.rectTransform.anchorMax = new Vector2(_progCur, 1f);
        }
    }

    // ── Phase logic ──────────────────────────────────────────

    private IEnumerator HoldThenScan()
    {
        yield return new WaitForSeconds(initialisationHoldTime);
        SetPhase(ScanPhase.Scanning);
    }

    private void OnPlanesChanged(ARPlanesChangedEventArgs args)
    {
        int h = HorizontalCount();
        if (_phase == ScanPhase.Scanning    && h >= 1) SetPhase(ScanPhase.AlmostThere);
        if (_phase == ScanPhase.AlmostThere && h >= 2) SetPhase(ScanPhase.Ready);
        if (_phase == ScanPhase.Ready)                 StartCoroutine(FadeOut());
    }

    private int HorizontalCount()
    {
        int n = 0;
        foreach (var p in planeManager.trackables)
            if (p.alignment == PlaneAlignment.HorizontalUp) n++;
        return n;
    }

    private void SetPhase(ScanPhase phase)
    {
        _phase = phase;
        int i  = (int)phase;

        _scanMsg.text = _msgs[i];
        _progTgt      = _progTargets[i];

        bool showArrows   = phase == ScanPhase.Scanning || phase == ScanPhase.AlmostThere;
        bool showProgress = phase != ScanPhase.Initialising;

        _arrowsRoot.gameObject.SetActive(showArrows);
        _progressBg.gameObject.SetActive(showProgress);

        _doPulse = phase == ScanPhase.Initialising || phase == ScanPhase.Ready;
        _doBlink = phase == ScanPhase.Scanning     || phase == ScanPhase.AlmostThere;

        if (!_doPulse)
            foreach (var r in _rings) if (r) r.transform.localScale = Vector3.one;
        if (!_doBlink)
            _statusDot.color = new Color(StatusCol.r, StatusCol.g, StatusCol.b, 0.3f);

        if (showArrows)
            for (int a = 0; a < 4; a++)
            {
                Color c = _arrowStates[i][a] ? ArrowLit : ArrowIdle;
                _arrowBtns[a].color = c;
                _arrowTxts[a].color = c;
            }
    }

    private IEnumerator FadeOut()
    {
        yield return new WaitForSeconds(0.9f);
        float t = 0f;
        while (t < fadeOutDuration)
        {
            t += Time.deltaTime;
            _group.alpha = Mathf.Lerp(1f, 0f, t / fadeOutDuration);
            yield return null;
        }
        gameObject.SetActive(false);
    }

    // ── Build entire UI in code ───────────────────────────────

    private void BuildUI()
    {
        _group = gameObject.AddComponent<CanvasGroup>();
        RectTransform root = GetComponent<RectTransform>();
        Stretch(root);

        // Background
        Img("Bg", root, BgDeep, stretch: true);

        // Rings root — centred, upper portion
        RectTransform ringsRoot = Empty("Rings", root);
        Anchor(ringsRoot, 0.5f, 0.63f, 0, 0);

        float[] rx   = { 135f, 104f,  72f,  41f, 16f };
        float[] ry   = {  48f,  37f,  26f,  15f,  6f };
        float[] sw   = { 1.6f, 1.8f, 2.2f, 2.6f, 2.8f };
        Color[] rcol = { Ring1, Ring2, Ring3, Ring4, Ring5 };

        for (int i = 0; i < 5; i++)
        {
            var go = new GameObject("Ring" + i, typeof(RectTransform));
            go.transform.SetParent(ringsRoot, false);
            var r = go.AddComponent<EchoesRingRenderer>();
            r.radiusX = rx[i]; r.radiusY = ry[i];
            r.strokeWidth = sw[i]; r.color = rcol[i]; r.segments = 90;
            var rrt = go.GetComponent<RectTransform>();
            rrt.anchorMin = rrt.anchorMax = rrt.pivot = new Vector2(0.5f, 0.5f);
            rrt.sizeDelta = new Vector2(rx[i] * 2f + 10f, ry[i] * 2f + 10f);
            rrt.anchoredPosition = Vector2.zero;
            _rings[i] = r;
        }

        // Centre dot
        var dot = Img("Dot", ringsRoot, Color.white);
        dot.rectTransform.anchorMin = dot.rectTransform.anchorMax =
            dot.rectTransform.pivot = new Vector2(0.5f, 0.5f);
        dot.rectTransform.sizeDelta = new Vector2(7f, 7f);
        dot.rectTransform.anchoredPosition = Vector2.zero;

        // Wordmark
        var wm = TMP("Wordmark", root, "ECHOES", 30f, bold: true, WordCol);
        Anchor(wm.rectTransform, 0.5f, 0.50f, 320f, 44f);
        wm.characterSpacing = 30f;
        wm.alignment = TextAlignmentOptions.Center;

        // Tagline
        var tl = TMP("Tagline", root, "PROGRAMMABLE SPATIAL PLATFORM", 8f, false, TagCol);
        Anchor(tl.rectTransform, 0.5f, 0.455f, 320f, 20f);
        tl.characterSpacing = 16f;
        tl.alignment = TextAlignmentOptions.Center;

        // Scan message
        _scanMsg = TMP("ScanMsg", root, "", 13f, false, MsgCol);
        Anchor(_scanMsg.rectTransform, 0.5f, 0.355f, 280f, 40f);
        _scanMsg.alignment = TextAlignmentOptions.Center;
        _scanMsg.enableWordWrapping = true;

        // Arrows
        _arrowsRoot = Empty("Arrows", root);
        Anchor(_arrowsRoot, 0.5f, 0.255f, 200f, 80f);

        Vector2[] aPos  = { new Vector2(0,26), new Vector2(-66,-10), new Vector2(66,-10), new Vector2(0,-46) };
        string[]  aChar = { "\u2191", "\u2190", "\u2192", "\u2193" };

        for (int i = 0; i < 4; i++)
        {
            var btn = Img("ABtn"+i, _arrowsRoot, ArrowIdle);
            btn.rectTransform.anchorMin = btn.rectTransform.anchorMax =
                btn.rectTransform.pivot = new Vector2(0.5f, 0.5f);
            btn.rectTransform.sizeDelta = new Vector2(40f, 40f);
            btn.rectTransform.anchoredPosition = aPos[i];
            _arrowBtns[i] = btn;

            var atxt = TMP("ATxt"+i, btn.rectTransform, aChar[i], 20f, false, ArrowIdle);
            Stretch(atxt.rectTransform);
            atxt.alignment = TextAlignmentOptions.Center;
            _arrowTxts[i] = atxt;
        }

        // Progress bar
        _progressBg = Img("ProgressBg", root, ProgressBg);
        Anchor(_progressBg.rectTransform, 0.5f, 0.175f, 130f, 3f);

        _progressFill = Img("ProgressFill", _progressBg.rectTransform, ProgressFg);
        _progressFill.rectTransform.anchorMin = Vector2.zero;
        _progressFill.rectTransform.anchorMax = new Vector2(0f, 1f);
        _progressFill.rectTransform.offsetMin = _progressFill.rectTransform.offsetMax = Vector2.zero;

        // Status dot
        _statusDot = Img("StatusDot", root, new Color(StatusCol.r, StatusCol.g, StatusCol.b, 0.3f));
        Anchor(_statusDot.rectTransform, 0.5f, 0.135f, 7f, 7f);
    }

    // ── Helpers ──────────────────────────────────────────────

    private Image Img(string name, Transform parent, Color col, bool stretch = false)
    {
        var go  = new GameObject(name, typeof(RectTransform), typeof(Image));
        go.transform.SetParent(parent, false);
        var img = go.GetComponent<Image>();
        img.color = col;
        if (stretch) Stretch(go.GetComponent<RectTransform>());
        return img;
    }

    private TextMeshProUGUI TMP(string name, Transform parent, string text,
                                 float size, bool bold, Color col)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI));
        go.transform.SetParent(parent, false);
        var t  = go.GetComponent<TextMeshProUGUI>();
        t.text      = text;
        t.fontSize  = size;
        t.fontStyle = bold ? FontStyles.Bold : FontStyles.Normal;
        t.color     = col;
        return t;
    }

    private RectTransform Empty(string name, Transform parent)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        return go.GetComponent<RectTransform>();
    }

    private void Anchor(RectTransform rt, float ax, float ay, float w, float h)
    {
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(ax, ay);
        rt.sizeDelta = new Vector2(w, h);
        rt.anchoredPosition = Vector2.zero;
    }

    private void Stretch(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = rt.offsetMax = Vector2.zero;
    }
}
