// ============================================================
//  EchoesScanController.cs
//  Echoes — Programmable Spatial Experience Platform
//  Version: v2.4.5-final | 19 June 2026
//
//  v2.4.5-final -- Listener wiring fixed via one-frame-delayed
//  coroutine. Closes out the v2.4.5 button listener fix line.
//  Diagnostic overlay retained for this build; strip before v2.4.6.
// ============================================================

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using Unity.Collections;
using TMPro;

public class EchoesScanController : MonoBehaviour
{
    private const string VERSION = "v2.4.5-final";

    [HideInInspector] public BorderColorRelay   borderRelay;
    [HideInInspector] public TextMeshProUGUI    promptText;
    [HideInInspector] public TextMeshProUGUI    hudText;
    [HideInInspector] public Button             startStopButton;
    [HideInInspector] public TextMeshProUGUI    startStopLabel;
    [HideInInspector] public Button             setPoseButton;

    [Header("Pose Gate")]
    public bool  gateOnPitch    = true;
    public bool  gateOnHeight   = false;
    public bool  gateOnDistance = false;
    public bool  gateOnHeading  = false;
    public float targetPitch       = -35f;
    public float pitchToleranceDeg = 8f;

    [Header("Waypoints")]
    public float[]  waypointOffsets = { 0f, -0.4f, 0.4f };
    public string[] waypointPrompts = { "CENTRE", "STEP LEFT", "STEP RIGHT" };

    [Header("Steady Detection")]
    public float steadyAngularThreshold = 5f;
    public float steadyHoldTime         = 1.5f;

    public event Action<int, string>  OnStillCaptured;
    public event Action<PlaneEntry>   OnPlaneSnapshotReady;
    public event Action<string>       OnScanComplete;

    private ARCameraManager     _cameraManager;
    private ARPlaneManager      _planeManager;
    private ARPointCloudManager _pointCloudManager;
    private AROcclusionManager  _occlusionManager;
    private ARDebugManager      _debugManager;

    private static readonly Color ColPurple = new Color(0.439f, 0.251f, 0.722f, 0.7f);
    private static readonly Color ColGreen  = new Color(0.200f, 0.800f, 0.400f, 0.7f);
    private static readonly Color ColAmber  = new Color(0.900f, 0.600f, 0.100f, 0.7f);
    private static readonly Color ColDim    = new Color(0.400f, 0.400f, 0.400f, 0.4f);

    private bool    _scanning        = false;
    private int     _currentWaypoint = 0;
    private float   _steadyTimer     = 0f;
    private Vector3 _lastEuler       = Vector3.zero;
    private Vector3 _startPosition;
    private bool    _poseGatePassed  = false;
    private float   _sessionStartTime;
    private float   _savedPitch, _savedHeight, _savedHeading;
    private bool    _hasSavedPose = false;
    private float?  _ambientIntensity = null;
    private float?  _colorTemperature = null;
    private string  _sessionId, _sessionFolder;
    private CameraIntrinsicsEntry      _cameraIntrinsics;
    private List<StillEntry>           _stills           = new List<StillEntry>();
    private List<PlaneEntry>           _planeLog         = new List<PlaneEntry>();
    private List<ProcessingEventEntry> _processingEvents = new List<ProcessingEventEntry>();
    private List<string>               _log              = new List<string>();
    private float _startPitch, _startHeight, _startHeading;
    private int   _lastRawPlaneCount = -1;

    // --- Diagnostic -- all routing through EchoesBootstrap shared log ---
    private TextMeshProUGUI _diagText;
    private static void Diag(string msg) { EchoesBootstrap.Diag(msg); }

    private void BuildDiagOverlay()
    {
        var go = new GameObject("DiagCanvas");
        var canvas = go.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 999;
        go.AddComponent<CanvasScaler>();
        go.AddComponent<GraphicRaycaster>();

        // Dark background -- top 60% of screen only, leaves buttons clear
        var bg = new GameObject("Bg", typeof(RectTransform), typeof(Image));
        bg.transform.SetParent(go.transform, false);
        var bgImg = bg.GetComponent<Image>();
        bgImg.color = new Color(0, 0, 0, 0.80f);
        bgImg.raycastTarget = false;
        var bgRt = bg.GetComponent<RectTransform>();
        bgRt.anchorMin = new Vector2(0, 0.40f);
        bgRt.anchorMax = Vector2.one;
        bgRt.offsetMin = bgRt.offsetMax = Vector2.zero;

        // Text in same top 60% region
        var textGo = new GameObject("DiagText", typeof(RectTransform), typeof(TextMeshProUGUI));
        textGo.transform.SetParent(go.transform, false);
        var rt = textGo.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0, 0.40f);
        rt.anchorMax = Vector2.one;
        rt.offsetMin = new Vector2(10, 0);
        rt.offsetMax = new Vector2(-10, -10);

        _diagText = textGo.GetComponent<TextMeshProUGUI>();
        _diagText.fontSize = 24f;
        _diagText.color = Color.white;
        _diagText.alignment = TextAlignmentOptions.TopLeft;
        _diagText.text = "DIAG STARTING...";
    }

    // --- Data classes ---
    [Serializable] public class CameraIntrinsicsEntry
    {
        public float focal_length_x, focal_length_y, principal_point_x, principal_point_y;
        public int   image_width, image_height;
    }
    [Serializable] public class StillEntry
    {
        public int index; public string file, tracking;
        public float pitch, height, heading, distance_to_start;
        public double timestamp; public float[] pose_matrix_4x4;
    }
    [Serializable] public class PlaneDetail
    {
        public string id, type, classification; public float width, height;
    }
    [Serializable] public class PlaneEntry
    {
        public double timestamp; public int raw_plane_ids, surfaces, synced_still;
        public string reason, tracking;
        public int horizontal_planes, vertical_planes, feature_point_count;
        public float? ambient_intensity, color_temperature; public bool depth_available;
        public List<PlaneDetail> plane_details = new List<PlaneDetail>();
    }
    [Serializable] public class ProcessingEventEntry
    {
        public double timestamp; public string trigger; public string[] hooks_available; public float elapsed_ms;
    }

    // --- Lifecycle ---

    private void Awake()
    {
        Diag("Controller.Awake() FIRED");
        Diag($"  borderRelay null={borderRelay == null}");
        Diag($"  promptText null={promptText == null}");
        Diag($"  startStopButton null={startStopButton == null}");
        Diag($"  setPoseButton null={setPoseButton == null}");
    }

    private void OnEnable()
    {
        Diag("Controller.OnEnable() FIRED");
        if (_cameraManager != null) _cameraManager.frameReceived += OnCameraFrame;
    }

    private void OnDisable()
    {
        if (_cameraManager != null) _cameraManager.frameReceived -= OnCameraFrame;
    }

    private void Start()
    {
        Diag("Controller.Start() FIRED");
        Diag($"  borderRelay null={borderRelay == null}");
        Diag($"  promptText null={promptText == null}");
        Diag($"  startStopButton null={startStopButton == null}");
        Diag($"  setPoseButton null={setPoseButton == null}");

        BuildDiagOverlay();

        _cameraManager     = FindObjectOfType<ARCameraManager>();
        _planeManager      = FindObjectOfType<ARPlaneManager>();
        _pointCloudManager = FindObjectOfType<ARPointCloudManager>();
        _occlusionManager  = FindObjectOfType<AROcclusionManager>();
        _debugManager      = FindObjectOfType<ARDebugManager>();

        Diag($"  ARCameraManager null={_cameraManager == null}");
        Diag($"  ARPlaneManager null={_planeManager == null}");

        if (PlayerPrefs.HasKey("echoes_saved_pitch"))
        {
            _savedPitch = PlayerPrefs.GetFloat("echoes_saved_pitch");
            _savedHeight = PlayerPrefs.GetFloat("echoes_saved_height");
            _savedHeading = PlayerPrefs.GetFloat("echoes_saved_heading");
            _hasSavedPose = true;
        }

        if (_cameraManager != null) _cameraManager.frameReceived += OnCameraFrame;

        // Listener wiring deferred -- HUD injects button refs in its own Start(),
        // which is not guaranteed to run before this Start() in the same frame.
        // See WireListenersNextFrame().
        StartCoroutine(WireListenersNextFrame());

        InitSession();
        SetPrompt("DIAG MODE -- check screen log");
        SetBorder(ColDim);

        if (Camera.main != null) EvaluatePoseGate();
        else SetPrompt("Waiting for camera...");

        Diag("Controller.Start() COMPLETE");
    }

    private IEnumerator WireListenersNextFrame()
    {
        // Wait one frame so EchoesScanHUD.Start() has run and injected button refs,
        // regardless of MonoBehaviour Start() ordering within this frame.
        yield return null;

        Diag("Controller.WireListenersNextFrame() FIRED -- 1 frame after Start()");
        Diag($"  startStopButton null={startStopButton == null}");
        Diag($"  setPoseButton null={setPoseButton == null}");

        if (startStopButton != null)
            startStopButton.onClick.AddListener(OnStartStopPressed);
        else
            Diag("  *** startStopButton STILL NULL after 1 frame -- listener NOT wired ***");

        if (setPoseButton != null)
            setPoseButton.onClick.AddListener(OnSetPosePressed);
        else
            Diag("  *** setPoseButton STILL NULL after 1 frame -- listener NOT wired ***");

        Diag("Controller.WireListenersNextFrame() COMPLETE");
    }

    private void Update()
    {
        if (_diagText != null && Time.frameCount % 30 == 0)
        {
            int start = Mathf.Max(0, EchoesBootstrap.DiagLog.Count - 18);
            var sb = new StringBuilder();
            sb.AppendLine($"ECHOES {VERSION}  frame={Time.frameCount}");
            sb.AppendLine($"startStopBtn: {(startStopButton != null ? "OK" : "NULL !!!")}  setPoseBtn: {(setPoseButton != null ? "OK" : "NULL !!!")}");
            sb.AppendLine($"promptText: {(promptText != null ? "OK" : "NULL")}  scanning: {_scanning}");
            sb.AppendLine("---");
            for (int i = start; i < EchoesBootstrap.DiagLog.Count; i++)
                sb.AppendLine(EchoesBootstrap.DiagLog[i]);
            _diagText.text = sb.ToString();
        }

        if (!_poseGatePassed && Camera.main != null && !_scanning) EvaluatePoseGate();
        if (!_scanning) return;

        if (_planeManager != null)
        {
            int rawCount = 0;
            foreach (var _ in _planeManager.trackables) rawCount++;
            if (rawCount != _lastRawPlaneCount)
            {
                _lastRawPlaneCount = rawCount;
                var pe = RecordPlaneSnapshot("count_change", -1);
                OnPlaneSnapshotReady?.Invoke(pe);
            }
        }

        if (Camera.main == null) return;
        Vector3 currentEuler = Camera.main.transform.eulerAngles;
        float angularRate = Vector3.Angle(
            Quaternion.Euler(currentEuler) * Vector3.forward,
            Quaternion.Euler(_lastEuler)   * Vector3.forward) / Time.deltaTime;
        _lastEuler = currentEuler;

        if (angularRate < steadyAngularThreshold)
        {
            _steadyTimer += Time.deltaTime;
            float pct = Mathf.Clamp01(_steadyTimer / steadyHoldTime);
            SetHud($"Hold steady... {pct * 100f:F0}%");
            if (_steadyTimer >= steadyHoldTime && _currentWaypoint < waypointOffsets.Length)
            {
                CaptureStill(_currentWaypoint);
                _currentWaypoint++;
                _steadyTimer = 0f;
                if (_currentWaypoint < waypointOffsets.Length)
                    SetPrompt(waypointPrompts[_currentWaypoint]);
                else { SetPrompt($"Done — {_stills.Count} stills. Press STOP."); SetHud(""); }
            }
        }
        else { _steadyTimer = 0f; SetHud("Move slowly..."); }
    }

    private void OnCameraFrame(ARCameraFrameEventArgs args)
    {
        var le = args.lightEstimation;
        if (le.averageBrightness.HasValue)       _ambientIntensity = le.averageBrightness.Value;
        if (le.averageColorTemperature.HasValue) _colorTemperature = le.averageColorTemperature.Value;
    }

    private void InitSession()
    {
        string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        _sessionId = $"echoes_session_{VERSION}_{ts}";
        _sessionFolder = Path.Combine(Application.persistentDataPath, _sessionId);
        Directory.CreateDirectory(_sessionFolder);
        _stills.Clear(); _planeLog.Clear(); _processingEvents.Clear(); _log.Clear();
        _cameraIntrinsics = null; _lastRawPlaneCount = -1;
        _currentWaypoint = 0; _scanning = false; _poseGatePassed = false;
    }

    private void EvaluatePoseGate()
    {
        if (Camera.main == null) return;
        Vector3 euler = Camera.main.transform.eulerAngles;
        float pitch = NormalisePitch(euler.x);
        bool ok = !gateOnPitch || Mathf.Abs(pitch - targetPitch) <= pitchToleranceDeg;
        if (!_hasSavedPose) SaveCurrentPose();
        if (ok)
        {
            _poseGatePassed = true;
            SetPrompt("Pose locked — press START");
            SetBorder(ColPurple);
        }
        else
        {
            SetPrompt($"Tilt camera to ~{targetPitch:F0}° (now {pitch:F0}°)");
            SetBorder(ColAmber);
        }
    }

    private void SaveCurrentPose()
    {
        if (Camera.main == null) return;
        Vector3 euler = Camera.main.transform.eulerAngles;
        _savedPitch = NormalisePitch(euler.x);
        _savedHeight = Camera.main.transform.position.y;
        _savedHeading = euler.y;
        _hasSavedPose = true;
        PlayerPrefs.SetFloat("echoes_saved_pitch", _savedPitch);
        PlayerPrefs.SetFloat("echoes_saved_height", _savedHeight);
        PlayerPrefs.SetFloat("echoes_saved_heading", _savedHeading);
        PlayerPrefs.Save();
    }

    private void OnStartStopPressed() { Diag("OnStartStopPressed()"); if (_scanning) StopScan(); else StartScan(); }

    private void StartScan()
    {
        if (!_poseGatePassed) { EvaluatePoseGate(); return; }
        if (Camera.main == null) return;
        _scanning = true; _sessionStartTime = Time.time;
        _startPosition = Camera.main.transform.position;
        _lastEuler = Camera.main.transform.eulerAngles; _steadyTimer = 0f;
        Vector3 euler = Camera.main.transform.eulerAngles;
        _startPitch = NormalisePitch(euler.x); _startHeight = Camera.main.transform.position.y; _startHeading = euler.y;
        CaptureCameraIntrinsics();
        SetPrompt(waypointPrompts[0]); SetHud("Hold steady..."); SetBorder(ColGreen);
        if (startStopLabel != null) startStopLabel.text = "STOP";
        LogEvent("SCAN STARTED");
    }

    private void StopScan()
    {
        _scanning = false; LogEvent($"SESSION STOP — {_stills.Count} stills");
        WriteManifest(); OnScanComplete?.Invoke(_sessionFolder);
        var uploader = FindObjectOfType<EchoesScanUploader>();
        uploader?.QueueSession(_sessionFolder, _sessionId);
        SetPrompt($"Session saved — {_stills.Count} stills"); SetHud(_sessionId); SetBorder(ColDim);
        if (startStopLabel != null) startStopLabel.text = "START";
    }

    private void OnSetPosePressed() { Diag("OnSetPosePressed()"); SaveCurrentPose(); EvaluatePoseGate(); }

    private void CaptureCameraIntrinsics()
    {
        if (_cameraManager == null) return;
        if (_cameraManager.TryGetIntrinsics(out XRCameraIntrinsics intr))
        {
            _cameraIntrinsics = new CameraIntrinsicsEntry
            {
                focal_length_x = intr.focalLength.x, focal_length_y = intr.focalLength.y,
                principal_point_x = intr.principalPoint.x, principal_point_y = intr.principalPoint.y,
                image_width = intr.resolution.x, image_height = intr.resolution.y
            };
        }
    }

    private float[] GetCameraPoseMatrix()
    {
        if (Camera.main == null) return null;
        Matrix4x4 m = Camera.main.transform.localToWorldMatrix;
        return new float[] { m.m00,m.m01,m.m02,m.m03, m.m10,m.m11,m.m12,m.m13, m.m20,m.m21,m.m22,m.m23, m.m30,m.m31,m.m32,m.m33 };
    }

    private void LogProcessingEvent(string trigger, string[] hooks)
    {
        float elapsed = _scanning ? (Time.time - _sessionStartTime) * 1000f : 0f;
        _processingEvents.Add(new ProcessingEventEntry { timestamp = GetUnixTimestamp(), trigger = trigger, hooks_available = hooks, elapsed_ms = elapsed });
    }

    private void CaptureStill(int idx)
    {
        if (Camera.main == null) return;
        string filename = $"still_{idx:D2}_wp.png";
        string filepath = Path.Combine(_sessionFolder, filename);
        if (!TryCaptureImage(filepath)) { LogEvent($"WP{idx} STILL FAILED"); return; }
        Vector3 pos = Camera.main.transform.position; Vector3 euler = Camera.main.transform.eulerAngles;
        _stills.Add(new StillEntry { index=idx, file=filename, pitch=NormalisePitch(euler.x), height=pos.y, heading=euler.y,
            distance_to_start=Vector3.Distance(pos,_startPosition), timestamp=GetUnixTimestamp(), tracking=ARSession.state.ToString(), pose_matrix_4x4=GetCameraPoseMatrix() });
        LogEvent($"WP{idx} STILL {filename}");
        RecordPlaneSnapshot("still_sync", idx);
        LogProcessingEvent($"still_{idx}_captured", idx==0 ? new[]{"yolo_inference","scale_calibration","srl_query"} : new[]{"yolo_inference","mvs_partial_reconstruction"});
        OnStillCaptured?.Invoke(idx, filepath);
    }

    private bool TryCaptureImage(string filepath)
    {
        if (_cameraManager == null) return false;
        if (!_cameraManager.TryAcquireLatestCpuImage(out XRCpuImage image)) return false;
        using (image)
        {
            var cp = new XRCpuImage.ConversionParams { inputRect=new RectInt(0,0,image.width,image.height), outputDimensions=new Vector2Int(image.width,image.height), outputFormat=TextureFormat.RGBA32, transformation=XRCpuImage.Transformation.MirrorY };
            var buf = new NativeArray<byte>(image.GetConvertedDataSize(cp), Allocator.Temp);
            image.Convert(cp, buf);
            var tex = new Texture2D(image.width, image.height, TextureFormat.RGBA32, false);
            tex.LoadRawTextureData(buf); tex.Apply(); buf.Dispose();
            File.WriteAllBytes(filepath, tex.EncodeToPNG()); Destroy(tex);
        }
        return true;
    }

    private PlaneEntry RecordPlaneSnapshot(string reason, int syncedStill)
    {
        int raw=0, horz=0, vert=0; var details = new List<PlaneDetail>();
        if (_planeManager != null)
        {
            foreach (var plane in _planeManager.trackables)
            {
                raw++; string t;
                switch (plane.alignment)
                {
                    case PlaneAlignment.HorizontalUp:   t="HorizontalUp";   horz++; break;
                    case PlaneAlignment.HorizontalDown: t="HorizontalDown"; horz++; break;
                    case PlaneAlignment.Vertical:       t="Vertical";       vert++; break;
                    default:                            t="NotAxisAligned"; break;
                }
                details.Add(new PlaneDetail { id=plane.trackableId.ToString(), type=t, classification=plane.classifications.ToString(), width=plane.size.x, height=plane.size.y });
            }
        }
        int deduped = _debugManager != null ? _debugManager.GetRealHorizontalPlaneCount() : horz;
        var entry = new PlaneEntry { timestamp=GetUnixTimestamp(), raw_plane_ids=raw, surfaces=deduped, synced_still=syncedStill, reason=reason, tracking=ARSession.state.ToString(),
            horizontal_planes=horz, vertical_planes=vert, feature_point_count=GetFeaturePointCount(), ambient_intensity=_ambientIntensity, color_temperature=_colorTemperature, depth_available=CheckDepthAvailable(), plane_details=details };
        _planeLog.Add(entry); return entry;
    }

    private int GetFeaturePointCount()
    {
        if (_pointCloudManager == null) return 0; int total = 0;
        foreach (var cloud in _pointCloudManager.trackables) if (cloud.positions.HasValue) total += cloud.positions.Value.Length;
        return total;
    }

    private bool CheckDepthAvailable()
    {
        if (_occlusionManager == null || !_occlusionManager.enabled) return false;
        if (_occlusionManager.TryAcquireEnvironmentDepthCpuImage(out XRCpuImage img)) { img.Dispose(); return true; }
        return false;
    }

    private void SetPrompt(string msg) { if (promptText != null) promptText.text = msg; }
    private void SetHud(string msg)    { if (hudText    != null) hudText.text    = msg; }
    private void SetBorder(Color c)    { borderRelay?.SetColor(c); }

    private void WriteManifest()
    {
        var sb = new StringBuilder();
        sb.AppendLine("{");
        sb.AppendLine($"  \"session_id\": \"{_sessionId}\",");
        sb.AppendLine($"  \"version\": \"{VERSION}\",");
        sb.AppendLine($"  \"log\": [");
        for (int i = 0; i < _log.Count; i++)
            sb.AppendLine($"    \"{EscapeJson(_log[i])}\"{(i < _log.Count - 1 ? "," : "")}");
        sb.AppendLine($"  ]");
        sb.AppendLine("}");
        File.WriteAllText(Path.Combine(_sessionFolder, "manifest.json"), sb.ToString());
    }

    private void LogEvent(string msg) { string e=$"{DateTime.Now:HH:mm:ss.fff} {msg}"; _log.Add(e); Debug.Log("[Echoes] "+e); }
    private double GetUnixTimestamp() => (DateTime.UtcNow - new DateTime(1970,1,1,0,0,0,DateTimeKind.Utc)).TotalSeconds;
    private float NormalisePitch(float rawX) { if (rawX > 180f) rawX -= 360f; return -rawX; }
    private float HeadingDelta(float a, float b) { float d=Mathf.Abs(a-b)%360f; return d>180f?360f-d:d; }
    private string EscapeJson(string s) => s.Replace("\\","\\\\").Replace("\"","\\\"");
}
