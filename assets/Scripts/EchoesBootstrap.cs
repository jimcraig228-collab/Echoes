// ============================================================
//  EchoesBootstrap.cs
//  Echoes — Programmable Spatial Experience Platform
//  Version: v2.4.5-diag | 17 June 2026
//
//  DIAGNOSTIC BUILD
// ============================================================

using System.Collections;
using System.IO;
using System;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class EchoesBootstrap : MonoBehaviour
{
    [Header("Logo flash")]
    public float logoFlashSeconds = 0.5f;

    [Header("Orientation (POC = landscape)")]
    public bool lockLandscape = true;

    [Header("Optional HTTP relay for uploads (blank = local)")]
    public string uploadUrl = "";

    private static Color C(float r,float g,float b,float a=1f)=>new Color(r,g,b,a);
    private static readonly Color Deep  = C(0.076f,0.020f,0.137f);
    private static readonly Color Light = C(0.949f,0.902f,1.000f);

    private GameObject _logoCanvas;

    // Shared diag log -- Bootstrap writes here before Controller exists
    public static System.Collections.Generic.List<string> DiagLog = new System.Collections.Generic.List<string>();
    public static string DiagPath;

    public static void Diag(string msg)
    {
        string line = $"{DateTime.Now:HH:mm:ss.fff} {msg}";
        DiagLog.Add(line);
        Debug.Log("[DIAG] " + line);
        if (!string.IsNullOrEmpty(DiagPath))
            try { File.AppendAllText(DiagPath, line + "\n"); } catch {}
    }

    void Awake()
    {
        DiagPath = Path.Combine(Application.persistentDataPath, "echoes_diag.txt");
        try { File.WriteAllText(DiagPath, $"=== ECHOES DIAG v2.4.5-diag {DateTime.Now} ===\n"); } catch {}

        Diag("Bootstrap.Awake() FIRED");

        if (lockLandscape)
        {
            Screen.orientation = ScreenOrientation.LandscapeLeft;
            Screen.autorotateToPortrait = false;
            Screen.autorotateToPortraitUpsideDown = false;
            Screen.autorotateToLandscapeLeft = true;
            Screen.autorotateToLandscapeRight = true;
            Diag("Bootstrap: landscape locked");
        }

        BuildLogoFlash();
        Diag("Bootstrap: logo flash built, starting HandOver coroutine");
        StartCoroutine(HandOver());
    }

    private void BuildLogoFlash()
    {
        _logoCanvas = new GameObject("EchoesLogoFlash");
        var canvas = _logoCanvas.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 200;
        var scaler = _logoCanvas.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        _logoCanvas.AddComponent<GraphicRaycaster>();

        var bg = new GameObject("Bg", typeof(RectTransform), typeof(Image));
        bg.transform.SetParent(_logoCanvas.transform, false);
        bg.GetComponent<Image>().color = Deep;
        var bgrt = bg.GetComponent<RectTransform>();
        bgrt.anchorMin = Vector2.zero; bgrt.anchorMax = Vector2.one;
        bgrt.offsetMin = bgrt.offsetMax = Vector2.zero;

        var wm = new GameObject("Wordmark", typeof(RectTransform), typeof(TextMeshProUGUI));
        wm.transform.SetParent(_logoCanvas.transform, false);
        var t = wm.GetComponent<TextMeshProUGUI>();
        t.text = "ECHOES"; t.fontSize = 44f; t.fontStyle = FontStyles.Bold;
        t.color = Light; t.alignment = TextAlignmentOptions.Center;
        t.characterSpacing = 32f;
        var wmrt = wm.GetComponent<RectTransform>();
        wmrt.anchorMin = wmrt.anchorMax = wmrt.pivot = new Vector2(0.5f, 0.5f);
        wmrt.sizeDelta = new Vector2(800, 80);
        wmrt.anchoredPosition = Vector2.zero;
    }

    private IEnumerator HandOver()
    {
        Diag($"Bootstrap.HandOver() START -- waiting {logoFlashSeconds}s");
        yield return new WaitForSeconds(logoFlashSeconds);
        Diag("Bootstrap.HandOver() wait complete -- destroying logo");

        if (_logoCanvas != null) Destroy(_logoCanvas);

        // Build the scan controller canvas
        Diag("Bootstrap: creating EchoesScanCanvas");
        var canvasGO = new GameObject("EchoesScanCanvas");
        var canvas = canvasGO.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 100;
        var scaler = canvasGO.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.matchWidthOrHeight = 0.5f;
        canvasGO.AddComponent<GraphicRaycaster>();

        var screenGO = new GameObject("ScanController", typeof(RectTransform));
        screenGO.transform.SetParent(canvasGO.transform, false);
        var rt = screenGO.GetComponent<RectTransform>();
        rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
        rt.offsetMin = rt.offsetMax = Vector2.zero;

        // Controller MUST be added before HUD
        Diag("Bootstrap: AddComponent<EchoesScanController>() -- CONTROLLER FIRST");
        screenGO.AddComponent<EchoesScanController>();

        Diag("Bootstrap: AddComponent<EchoesScanHUD>() -- HUD SECOND");
        screenGO.AddComponent<EchoesScanHUD>();

        Diag("Bootstrap: both components added");

        // Uploader
        var up = new GameObject("EchoesScanUploader").AddComponent<EchoesScanUploader>();
        up.uploadUrl = uploadUrl;
        DontDestroyOnLoad(up.gameObject);
        Diag("Bootstrap.HandOver() COMPLETE");
    }
}
