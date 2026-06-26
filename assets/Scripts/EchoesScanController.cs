// ============================================================
//  EchoesScanController.cs
//  Echoes — Programmable Spatial Experience Platform
//  Version: v2.4.6 | 26 June 2026
//
//  v2.4.6 — Guided Scan Overlay + Manifest Completion
//  ----------------------------------------------------------
//  THREE PIECES, in dependency order:
//
//  PART ONE  — Manifest writer completed. WriteManifest() now
//              serialises _stills, _planeLog, _cameraIntrinsics,
//              _processingEvents, AND the new guided-scan block.
//              The v2.4.5 skeleton (session_id/version/log only)
//              is gone. Filename now sessionID_timestamp_v246.json
//              so the version is visible before opening.
//
//  PART TWO  — Guided Scan Overlay. The free-paced steady-detect
//              waypoint loop is REPLACED by a fixed, time-based
//              four-zone state machine: Centre, Left, Right, Tilt
//              (tilt = up). One still fired at the end of each
//              zone dwell -> 4 stills (still_00..still_03).
//              Drives crosshair / per-edge borders / arrows /
//              instruction banner / progress bar via the HUD.
//
//  PART THREE— Continuous diagnostic log. A timed sampler (default
//              500ms, configurable) writes a second file
//              sessionID_timestamp_v246_diagnostic.json capturing
//              everything ARCore exposes per sample.
//
//  Diagnostic on-screen overlay shrunk to a small bottom-right
//  corner readout (half previous size) so the guided overlay owns
//  the screen.
//
//  Pose gate unchanged from v2.4.5 (targetPitch -35 confirmed).
// ============================================================

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
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
    private const string VERSION = "v2.4.6";

    [HideInInspector] public BorderColorRelay   borderRelay;
    [HideInInspector] public TextMeshProUGUI    promptText;
    [HideInInspector] public TextMeshProUGUI    hudText;
    [HideInInspector] public Button             startStopButton;
    [HideInInspector] public TextMeshProUGUI    startStopLabel;
    [HideInInspector] public Button             setPoseButton;

    // --- Guided overlay refs (injected by HUD, v2.4.6) ---
    [HideInInspector] public GuidedOverlayRelay overlay;

    [Header("Pose Gate")]
    public bool  gateOnPitch    = true;
    public bool  gateOnHeight   = false;
    public bool  gateOnDistance = false;
    public bool  gateOnHeading  = false;
    public float targetPitch       = -35f;
    public float pitchToleranceDeg = 8f;

    [Header("Guided Scan — Zone Sequence (v2.4.6)")]
    [Tooltip("Dwell seconds per zone. Configurable, not hardcoded. Default 5s each.")]
    public float zoneDurationSeconds = 5f;
    [Tooltip("Half-second transition beat between zones (spec 6.1).")]
    public float zoneTransitionSeconds = 0.5f;
    [Tooltip("Tilt zone direction. Up by default (lift toward far wall/ceiling).")]
    public bool tiltUp = true;

    [Header("Diagnostic Sampler (v2.4.6 Part Three)")]
    [Tooltip("Continuous diagnostic sample interval (seconds). Configurable. Default 0.5s = 500ms.")]
    public float diagnosticSampleSeconds = 0.5f;

    [Header("Steady Detection (legacy, retained for SetPose only)")]
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
    private static readonly Color ColWhite  = new Color(1f, 1f, 1f, 0.85f);

    private bool    _scanning        = false;
    private float   _steadyTimer     = 0f;
    private Vector3 _lastEuler       = Vector3.zero;
    private Vector3 _startPosition;
    private bool    _poseGatePassed  = false;
    private float   _sessionStartTime;
    private float   _savedPitch, _savedHeight, _savedHeading;
    private bool    _hasSavedPose = false;
    private float?  _ambientIntensity = null;
    private float?  _colorTemperature = null;
    private string  _sessionId, _sessionFolder, _sessionStamp;
    private CameraIntrinsicsEntry      _cameraIntrinsics;
    private List<StillEntry>           _stills           = new List<StillEntry>();
    private List<PlaneEntry>           _planeLog         = new List<PlaneEntry>();
    private List<ProcessingEventEntry> _processingEvents = new List<ProcessingEventEntry>();
    private List<string>               _log              = new List<string>();
    private float _startPitch, _startHeight, _startHeading;
    private int   _lastRawPlaneCount = -1;

    // --- Zone state machine (v2.4.6) ---
    public enum Zone { Centre, Left, Right, Tilt }
    private static readonly Zone[] ZONE_ORDER = { Zone.Centre, Zone.Left, Zone.Right, Zone.Tilt };
    private int    _zoneIndex      = -1;       // -1 before sequence start
    private float  _zoneTimer      = 0f;
    private bool   _inTransition   = false;
    private float  _transitionTimer = 0f;
    private bool   _stillFiredThisZone = false;
    private string _instructionState = "idle"; // idle | active | transitioning | complete
    private List<ZoneRecord> _zoneRecords = new List<ZoneRecord>();

    // --- Diagnostic sampler (v2.4.6) ---
    private List<DiagnosticSample> _diagSamples = new List<DiagnosticSample>();
    private float _diagTimer = 0f;
    private string _diagnosticPath;

    // --- Diagnostic on-screen overlay (shrunk to corner in v2.4.6) ---
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

        // v2.4.6: small bottom-right corner readout (~quarter screen, half prior size).
        var bg = new GameObject("Bg", typeof(RectTransform), typeof(Image));
        bg.transform.SetParent(go.transform, false);
        var bgImg = bg.GetComponent<Image>();
        bgImg.color = new Color(0, 0, 0, 0.55f);
        bgImg.raycastTarget = false;
        var bgRt = bg.GetComponent<RectTransform>();
        bgRt.anchorMin = new Vector2(0.62f, 0.0f);   // right ~38% wide
        bgRt.anchorMax = new Vector2(1.0f, 0.30f);   // bottom ~30% tall
        bgRt.offsetMin = bgRt.offsetMax = Vector2.zero;

        var textGo = new GameObject("DiagText", typeof(RectTransform), typeof(TextMeshProUGUI));
        textGo.transform.SetParent(go.transform, false);
        var rt = textGo.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0.62f, 0.0f);
        rt.anchorMax = new Vector2(1.0f, 0.30f);
        rt.offsetMin = new Vector2(6, 4);
        rt.offsetMax = new Vector2(-6, -4);

        _diagText = textGo.GetComponent<TextMeshProUGUI>();
        _diagText.fontSize = 12f;                    // half of prior 24
        _diagText.color = new Color(1f, 1f, 1f, 0.85f);
        _diagText.alignment = TextAlignmentOptions.BottomLeft;
        _diagText.text = "diag…";
    }

    // --- Data classes ---
    [Serializable] public class CameraIntrinsicsEntry
    {
        public float focal_length_x, focal_length_y, principal_point_x, principal_point_y;
        public int   image_width, image_height;
    }
    [Serializable] public class StillEntry
    {
        public int index; public string file, tracking, zone;
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
    // v2.4.6 — per-zone record for the guided-scan manifest block
    [Serializable] public class ZoneRecord
    {
        public string zone;
        public double start_timestamp, end_timestamp, still_timestamp;
        public bool   still_confirmed;
        public string instruction_state_at_still;
    }
    // v2.4.6 — one continuous diagnostic sample
    [Serializable] public class DiagnosticSample
    {
        public double timestamp_unix_ms;
        public string current_zone, instruction_state;
        public float  zone_elapsed_ms;
        public string tracking;
        public int    horizontal_planes, vertical_planes, feature_point_count;
        public bool   depth_available;
        public float? ambient_intensity, color_temperature;
        public float  pos_x, pos_y, pos_z, rot_x, rot_y, rot_z;
    }

    // --- Lifecycle ---

    private void Awake()
    {
        Diag("Controller.Awake() FIRED v2.4.6");
    }

    private void OnEnable()
    {
        if (_cameraManager != null) _cameraManager.frameReceived += OnCameraFrame;
    }

    private void OnDisable()
    {
        if (_cameraManager != null) _cameraManager.frameReceived -= OnCameraFrame;
    }

    private void Start()
    {
        Diag("Controller.Start() FIRED v2.4.6");

        BuildDiagOverlay();

        _cameraManager     = FindObjectOfType<ARCameraManager>();
        _planeManager      = FindObjectOfType<ARPlaneManager>();
        _pointCloudManager = FindObjectOfType<ARPointCloudManager>();
        _occlusionManager  = FindObjectOfType<AROcclusionManager>();
        _debugManager      = FindObjectOfType<ARDebugManager>();

        Diag($"  ARCameraManager null={_cameraManager == null}  ARPlaneManager null={_planeManager == null}");

        if (PlayerPrefs.HasKey("echoes_saved_pitch"))
        {
            _savedPitch = PlayerPrefs.GetFloat("echoes_saved_pitch");
            _savedHeight = PlayerPrefs.GetFloat("echoes_saved_height");
            _savedHeading = PlayerPrefs.GetFloat("echoes_saved_heading");
            _hasSavedPose = true;
        }

        if (_cameraManager != null) _cameraManager.frameReceived += OnCameraFrame;

        StartCoroutine(WireListenersNextFrame());

        InitSession();
        SetPrompt("Pose to ~-35° and press START");
        SetBorder(ColDim);

        if (Camera.main != null) EvaluatePoseGate();
        else SetPrompt("Waiting for camera...");

        Diag("Controller.Start() COMPLETE");
    }

    private IEnumerator WireListenersNextFrame()
    {
        yield return null;

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
        // Corner diag readout (cheap, every 30 frames)
        if (_diagText != null && Time.frameCount % 30 == 0)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"ECHOES {VERSION}");
            sb.AppendLine($"scan:{_scanning} zone:{(_zoneIndex>=0 && _zoneIndex<ZONE_ORDER.Length ? ZONE_ORDER[_zoneIndex].ToString() : "-")}");
            sb.AppendLine($"btn:{(startStopButton!=null?"OK":"NULL")} planes:{_lastRawPlaneCount}");
            _diagText.text = sb.ToString();
        }

        // Pose gate (pre-scan only)
        if (!_poseGatePassed && Camera.main != null && !_scanning) EvaluatePoseGate();
        if (!_scanning) return;

        // Plane-count-change snapshot (retained from v2.4.5)
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

        // Continuous diagnostic sampler (v2.4.6 Part Three)
        _diagTimer += Time.deltaTime;
        if (_diagTimer >= diagnosticSampleSeconds)
        {
            _diagTimer = 0f;
            RecordDiagnosticSample();
        }

        // Zone state machine (v2.4.6 Part Two)
        TickZoneSequence();
    }

    // ----------------------------------------------------------
    //  ZONE SEQUENCE STATE MACHINE (v2.4.6)
    // ----------------------------------------------------------
    private void TickZoneSequence()
    {
        if (_zoneIndex < 0 || _zoneIndex >= ZONE_ORDER.Length) return;

        if (_inTransition)
        {
            _transitionTimer += Time.deltaTime;
            if (_transitionTimer >= zoneTransitionSeconds)
            {
                _inTransition = false;
                _transitionTimer = 0f;
                AdvanceZone();
            }
            return;
        }

        _zoneTimer += Time.deltaTime;
        float pct = Mathf.Clamp01(_zoneTimer / Mathf.Max(0.0001f, zoneDurationSeconds));
        UpdateProgressBar();
        SetHud($"{ZONE_ORDER[_zoneIndex]} — {(_zoneTimer):F1}s / {zoneDurationSeconds:F0}s");

        // Fire the still once, at end of dwell
        if (!_stillFiredThisZone && _zoneTimer >= zoneDurationSeconds)
        {
            _stillFiredThisZone = true;
            CaptureStillForZone(_zoneIndex);
            // close out this zone record
            if (_zoneRecords.Count > 0)
            {
                var zr = _zoneRecords[_zoneRecords.Count - 1];
                zr.end_timestamp = GetUnixTimestamp();
                zr.still_timestamp = GetUnixTimestamp();
                zr.still_confirmed = true;
                zr.instruction_state_at_still = _instructionState;
            }
            BeginTransition();
        }
    }

    private void StartZoneSequence()
    {
        _zoneIndex = -1;
        _zoneRecords.Clear();
        AdvanceZone();
    }

    private void AdvanceZone()
    {
        _zoneIndex++;
        _zoneTimer = 0f;
        _stillFiredThisZone = false;

        if (_zoneIndex >= ZONE_ORDER.Length)
        {
            // Sequence complete
            _instructionState = "complete";
            SetPrompt($"Sequence done — {_stills.Count} stills. Press STOP.");
            SetHud("");
            if (overlay != null) overlay.SetAllBordersComplete();
            return;
        }

        Zone z = ZONE_ORDER[_zoneIndex];
        _instructionState = "active";

        var rec = new ZoneRecord {
            zone = z.ToString(),
            start_timestamp = GetUnixTimestamp(),
            still_confirmed = false
        };
        _zoneRecords.Add(rec);

        SetPrompt(ZoneInstruction(z));
        SetBorder(ColGreen);
        if (overlay != null)
        {
            overlay.SetActiveZone(z, ColAmber);   // amber on the active edge + arrow
            overlay.SetInstruction(ZoneInstruction(z), ColAmber);
        }
        LogEvent($"ZONE {z} START");
    }

    private void BeginTransition()
    {
        _inTransition = true;
        _transitionTimer = 0f;
        _instructionState = "transitioning";
        if (_zoneIndex >= 0 && _zoneIndex < ZONE_ORDER.Length && overlay != null)
            overlay.SetZoneComplete(ZONE_ORDER[_zoneIndex], ColGreen);  // flash/stay green
        LogEvent($"ZONE {ZONE_ORDER[_zoneIndex]} COMPLETE");
    }

    private string ZoneInstruction(Zone z)
    {
        switch (z)
        {
            case Zone.Centre: return "CENTRE — hold still on the reference object";
            case Zone.Left:   return "LEFT — sweep left, keep crosshair on reference";
            case Zone.Right:  return "RIGHT — sweep right, keep crosshair on reference";
            case Zone.Tilt:   return tiltUp ? "TILT UP — lift toward the far wall"
                                            : "TILT DOWN — lower toward the floor";
            default:          return z.ToString();
        }
    }

    private void UpdateProgressBar()
    {
        // Time-based fill across the whole session (spec 4.5).
        // Total = nZones * (dwell) + (nZones-1) * transition.
        int n = ZONE_ORDER.Length;
        float total = n * zoneDurationSeconds + (n - 1) * zoneTransitionSeconds;
        float elapsed = (n > 0 ? _zoneIndex : 0) * (zoneDurationSeconds + zoneTransitionSeconds) + _zoneTimer;
        float p = Mathf.Clamp01(total <= 0f ? 0f : elapsed / total);
        if (overlay != null) overlay.SetProgress(p);
    }

    private void OnCameraFrame(ARCameraFrameEventArgs args)
    {
        var le = args.lightEstimation;
        if (le.averageBrightness.HasValue)       _ambientIntensity = le.averageBrightness.Value;
        if (le.averageColorTemperature.HasValue) _colorTemperature = le.averageColorTemperature.Value;
    }

    private void InitSession()
    {
        _sessionStamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        _sessionId = $"echoes_session_{VERSION}_{_sessionStamp}";
        _sessionFolder = Path.Combine(Application.persistentDataPath, _sessionId);
        Directory.CreateDirectory(_sessionFolder);
        _stills.Clear(); _planeLog.Clear(); _processingEvents.Clear(); _log.Clear();
        _zoneRecords.Clear(); _diagSamples.Clear();
        _cameraIntrinsics = null; _lastRawPlaneCount = -1;
        _zoneIndex = -1; _zoneTimer = 0f; _inTransition = false; _transitionTimer = 0f;
        _stillFiredThisZone = false; _instructionState = "idle"; _diagTimer = 0f;
        _scanning = false; _poseGatePassed = false;
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
        SetHud("Starting guided scan..."); SetBorder(ColGreen);
        if (startStopLabel != null) startStopLabel.text = "STOP";
        if (overlay != null) overlay.ResetForSession();
        LogEvent("SCAN STARTED");
        StartZoneSequence();
    }

    private void StopScan()
    {
        _scanning = false; LogEvent($"SESSION STOP — {_stills.Count} stills");
        WriteDiagnosticLog();
        WriteManifest(); OnScanComplete?.Invoke(_sessionFolder);
        var uploader = FindObjectOfType<EchoesScanUploader>();
        uploader?.QueueSession(_sessionFolder, _sessionId);
        SetPrompt($"Session saved — {_stills.Count} stills"); SetHud(_sessionId); SetBorder(ColDim);
        if (startStopLabel != null) startStopLabel.text = "START";
        if (overlay != null) overlay.ResetForSession();
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

    // v2.4.6: capture a still tagged with the current zone, flash crosshair+arrow
    private void CaptureStillForZone(int zoneIdx)
    {
        if (Camera.main == null) return;
        Zone z = ZONE_ORDER[zoneIdx];
        int idx = _stills.Count;
        string filename = $"still_{idx:D2}_wp.png";
        string filepath = Path.Combine(_sessionFolder, filename);
        if (!TryCaptureImage(filepath)) { LogEvent($"{z} STILL FAILED"); return; }
        Vector3 pos = Camera.main.transform.position; Vector3 euler = Camera.main.transform.eulerAngles;
        _stills.Add(new StillEntry { index=idx, file=filename, zone=z.ToString(), pitch=NormalisePitch(euler.x), height=pos.y, heading=euler.y,
            distance_to_start=Vector3.Distance(pos,_startPosition), timestamp=GetUnixTimestamp(), tracking=ARSession.state.ToString(), pose_matrix_4x4=GetCameraPoseMatrix() });
        LogEvent($"{z} STILL {filename}");
        RecordPlaneSnapshot("still_sync", idx);
        LogProcessingEvent($"still_{idx}_captured", idx==0 ? new[]{"yolo_inference","scale_calibration","srl_query"} : new[]{"yolo_inference","mvs_partial_reconstruction"});
        if (overlay != null) overlay.FlashCapture();   // crosshair + active arrow white flash
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

    // v2.4.6 Part Three — one continuous diagnostic sample
    private void RecordDiagnosticSample()
    {
        int horz=0, vert=0;
        if (_planeManager != null)
        {
            foreach (var plane in _planeManager.trackables)
            {
                switch (plane.alignment)
                {
                    case PlaneAlignment.HorizontalUp:
                    case PlaneAlignment.HorizontalDown: horz++; break;
                    case PlaneAlignment.Vertical:       vert++; break;
                }
            }
        }
        Vector3 pos = Camera.main != null ? Camera.main.transform.position : Vector3.zero;
        Vector3 rot = Camera.main != null ? Camera.main.transform.eulerAngles : Vector3.zero;
        float zoneElapsedMs = (_zoneIndex >= 0 && !_inTransition) ? _zoneTimer * 1000f : 0f;
        string zoneName = (_zoneIndex >= 0 && _zoneIndex < ZONE_ORDER.Length) ? ZONE_ORDER[_zoneIndex].ToString() : "none";

        _diagSamples.Add(new DiagnosticSample {
            timestamp_unix_ms = GetUnixTimestamp() * 1000.0,
            current_zone = zoneName,
            instruction_state = _instructionState,
            zone_elapsed_ms = zoneElapsedMs,
            tracking = ARSession.state.ToString(),
            horizontal_planes = horz, vertical_planes = vert,
            feature_point_count = GetFeaturePointCount(),
            depth_available = CheckDepthAvailable(),
            ambient_intensity = _ambientIntensity, color_temperature = _colorTemperature,
            pos_x = pos.x, pos_y = pos.y, pos_z = pos.z,
            rot_x = rot.x, rot_y = rot.y, rot_z = rot.z
        });
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

    // ----------------------------------------------------------
    //  PART ONE — MANIFEST WRITER (completed in v2.4.6)
    //  Serialises the full in-memory dataset, not just the log.
    //  Built with JsonUtility on a serialisable wrapper so field
    //  names exactly match the C# class definitions (snake_case),
    //  which the v2.4.7 parser and the Diff Engine will read.
    // ----------------------------------------------------------
    [Serializable] private class GuidedScanBlock
    {
        public bool   guided_scan_mode;
        public string[] zone_sequence;
        public float  zone_duration_setting;
        public bool   tilt_up;
        public List<ZoneRecord> zones = new List<ZoneRecord>();
    }
    [Serializable] private class SessionManifest
    {
        public string session_id;
        public string version;
        public double written_unix;
        public GuidedScanBlock guided_scan;
        public CameraIntrinsicsEntry camera_intrinsics;
        public List<StillEntry> stills = new List<StillEntry>();
        public List<PlaneEntry> plane_snapshots = new List<PlaneEntry>();
        public List<ProcessingEventEntry> processing_events = new List<ProcessingEventEntry>();
        public List<string> log = new List<string>();
    }

    private void WriteManifest()
    {
        var seq = new string[ZONE_ORDER.Length];
        for (int i = 0; i < ZONE_ORDER.Length; i++) seq[i] = ZONE_ORDER[i].ToString();

        var manifest = new SessionManifest
        {
            session_id = _sessionId,
            version = VERSION,
            written_unix = GetUnixTimestamp(),
            guided_scan = new GuidedScanBlock
            {
                guided_scan_mode = true,
                zone_sequence = seq,
                zone_duration_setting = zoneDurationSeconds,
                tilt_up = tiltUp,
                zones = _zoneRecords
            },
            camera_intrinsics = _cameraIntrinsics,   // may be null -> serialises as default object
            stills = _stills,
            plane_snapshots = _planeLog,
            processing_events = _processingEvents,
            log = _log
        };

        string json = JsonUtility.ToJson(manifest, true);
        string filename = $"{_sessionId}_{_sessionStamp}_v246.json";
        File.WriteAllText(Path.Combine(_sessionFolder, filename), json);
        Diag($"Manifest written: {filename}  stills={_stills.Count} planes={_planeLog.Count}");
    }

    // PART THREE — write the continuous diagnostic file
    [Serializable] private class DiagnosticFile
    {
        public string session_id;
        public string version;
        public float  sample_interval_seconds;
        public List<DiagnosticSample> samples = new List<DiagnosticSample>();
    }

    private void WriteDiagnosticLog()
    {
        var df = new DiagnosticFile
        {
            session_id = _sessionId,
            version = VERSION,
            sample_interval_seconds = diagnosticSampleSeconds,
            samples = _diagSamples
        };
        string json = JsonUtility.ToJson(df, true);
        string filename = $"{_sessionId}_{_sessionStamp}_v246_diagnostic.json";
        _diagnosticPath = Path.Combine(_sessionFolder, filename);
        File.WriteAllText(_diagnosticPath, json);
        Diag($"Diagnostic log written: {filename}  samples={_diagSamples.Count}");
    }

    private void LogEvent(string msg) { string e=$"{DateTime.Now:HH:mm:ss.fff} {msg}"; _log.Add(e); Debug.Log("[Echoes] "+e); }
    private double GetUnixTimestamp() => (DateTime.UtcNow - new DateTime(1970,1,1,0,0,0,DateTimeKind.Utc)).TotalSeconds;
    private float NormalisePitch(float rawX) { if (rawX > 180f) rawX -= 360f; return -rawX; }
    private float HeadingDelta(float a, float b) { float d=Mathf.Abs(a-b)%360f; return d>180f?360f-d:d; }
    private string EscapeJson(string s) => s.Replace("\\","\\\\").Replace("\"","\\\"");
}
