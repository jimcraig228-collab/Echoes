using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

// ============================================================
//  EchoesBootstrap.cs
//  Echoes — Programmable Spatial Experience Platform
//  Version: 2.4.1  |  16 June 2026
//
//  CHANGELOG v2.4.0:
//   - Logo now FLASHES for 0.5s then hands over to the guided
//     scan controller (replaces the phased loading screen).
//   - Locks landscape orientation (POC).
//   - Builds Canvas, logo flash, scan controller + HUD + uploader.
//
//  Attach to: your XR Origin (or any persistent GameObject).
//  Replaces EchoesLoadingScreenBootstrap from v2.3.4.
// ============================================================

public class EchoesBootstrap : MonoBehaviour
{
    [Header("Logo flash")]
    public float logoFlashSeconds = 0.5f;

    [Header("Orientation (POC = landscape)")]
    public bool lockLandscape = true;

    [Header("Optional HTTP relay for uploads (blank = local)")]
    public string uploadUrl = "";

    private static Color C(float r,float g,float b,float a=1f)=>new Color(r,g,b,a);
    private static readonly Color Deep   = C(0.076f,0.020f,0.137f);
    private static readonly Color Light  = C(0.949f,0.902f,1.000f);

    private GameObject _logoCanvas;

    void Awake()
    {
        if (lockLandscape)
        {
            Screen.orientation = ScreenOrientation.LandscapeLeft;
            Screen.autorotateToPortrait = false;
            Screen.autorotateToPortraitUpsideDown = false;
            Screen.autorotateToLandscapeLeft = true;
            Screen.autorotateToLandscapeRight = true;
        }

        BuildLogoFlash();
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
        yield return new WaitForSeconds(logoFlashSeconds);

        if (_logoCanvas != null) Destroy(_logoCanvas);

        // Build the scan controller canvas
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

        // Controller + HUD (HUD builds UI in Awake, before controller Start)
        screenGO.AddComponent<EchoesScanController>();
        screenGO.AddComponent<EchoesScanHUD>();

        // Uploader (separate persistent object)
        var up = new GameObject("EchoesScanUploader").AddComponent<EchoesScanUploader>();
        up.uploadUrl = uploadUrl;
        DontDestroyOnLoad(up.gameObject);
    }
}
