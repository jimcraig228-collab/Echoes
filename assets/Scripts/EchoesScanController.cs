// ============================================================
//  EchoesScanController.cs
//  Echoes — Programmable Spatial Experience Platform
//  Version: v2.4.9 | 01 July 2026
//
//  v2.4.10 — Depth Density Instrumentation (cheap version)
//  ----------------------------------------------------------
//  FIX DEPTH-STATS — depth was a boolean only. Now also captures the
//              valid-point count from the environment depth CPU image
//              and computes points-per-square-metre against total
//              deduped plane area. depth_available is UNCHANGED and
//              still written, so this is purely additive.
//              ROLLBACK: delete GetDepthStats(), remove the two new
//              fields (depth_points, depth_density_per_m2) and their
//              two assignments. CheckDepthAvailable() is untouched.
//
//  v2.4.9 — Sensor Enablement Fixes (from v2.4.8 office+living-room data)
//  ----------------------------------------------------------
//  FIX V (vertical planes never detected) — three scans across two
//              rooms returned ZERO vertical planes despite walls in
//              frame. Root cause: requestedPlaneDetectionMode was
//              never set in code, so detection ran at whatever the
//              Inspector defaulted to (Horizontal only). Start() now
//              sets Horizontal | Vertical explicitly and logs it, so
//              walls are actually detected and the setting is
//              version-controlled, not silently Inspector-driven.
//
//  FIX D (plane dimensions reading 0.0) — living-room scans reported
//              many planes with size 0.0 x 0.0. ARCore returns size 0
//              for a plane whose boundary has not yet matured. The
//              snapshot now flags immature (zero-size) planes and the
//              dedup ignores them, so counts reflect real surfaces.
//
//  FIX G (granted light mode logging) — log currentLightEstimation
//              (granted) alongside requested, so a null colour
//              temperature is explained as platform vs code.
//
//  ----------------------------------------------------------
//  v2.4.8 — Field-Test Fixes (from v2.4.7 device test)
//  ----------------------------------------------------------
//  FIX A (diagnostic overlay overlap) — the bottom-right corner
//              readout was sitting on top of the SET POSE button,
//              making it untappable. The readout moves to the
//              TOP-LEFT corner, clearing the whole bottom control
//              bar for START/STOP and SET POSE.
//
//  FIX B (null light estimation) — ambient_intensity and
//              color_temperature were null in every snapshot and
//              every diagnostic sample. ARCore returns no light
//              data unless the mode is explicitly requested on the
//              ARCameraManager. Start() now sets
//              requestedLightEstimation = AmbientIntensity |
//              AmbientColor once the camera manager is found.
//
//  ----------------------------------------------------------
//  Inherited from v2.4.7:
//  FIX 1 (session-merge) — InitSession() runs at the top of
//              StartScan(); confirmed working in device test.
//  FREE-SCAN MODE — freeScanMode toggle (default off).
//  SCENARIO TAG — scenarioTag string, top-level in manifest.
//  free_scan_mode written top-level alongside scenario_tag.
//
//  Inherited from v2.4.6:
//  PART ONE  — Full manifest writer.
//  PART TWO  — Guided four-zone state machine (Centre/Left/Right/Tilt).
//  PART THREE— Continuous diagnostic log (500ms sampler).
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
    private const string VERSION = "v2.4.10";

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

    [Header("Test Instrumentation (v2.4.7)")]
    [Tooltip("Free-scan mode. Off = guided four-zone sequence (v2.4.6). On = operator-controlled scan, steady-detect stills, guided overlay hidden.")]
    public bool freeScanMode = false;
    [Tooltip("Scenario label written top-level into the manifest and diagnostic header (e.g. A, B, C). Set once per block of repeats. Empty serialises as an empty string.")]
    public string scenarioTag = "";

    [Header("Steady Detection (free-scan still capture, v2.4.7)")]
    [Tooltip("Angular movement (deg) below which the phone counts as held steady.")]
    public float steadyAngularThreshold = 5f;
    [Tooltip("Seconds the phone must be held steady before a free-scan still fires.")]
    public float steadyHoldTime = 1.5f;
    [Tooltip("Minimum seconds between two free-scan steady-detect stills, so a long hold does not spam captures.")]
    public float freeScanStillCooldown = 2.0f;

    public event Action<int, string>  OnStillCaptured;
    public event Action<PlaneEntry>   OnPlaneSnapshotReady;
    public event Action<string>       OnScanComplete;

    private ARCameraManager     _cameraManager;
    private ARPlaneManager      _planeManager;
    private ARPointCloudManager _pointCloudManager;
    private AROcclusionManager  _occlusionManager;
    private int   _lastDepthPoints  = 0;   // v2.4.10 cache
    private float _lastDepthDensity = 0f;  // v2.4.10 cache
    private ARDebugManager      _debugManager;

    private static readonly Color ColPurple = new Color(0.439f, 0.251f, 0.722f, 0.7f);
    private static readonly Color ColGreen  = new Color(0.200f, 0.800f, 0.400f, 0.7f);
    private static readonly Color ColAmber  = new Color(0.900f, 0.600f, 0.100f, 0.7f);
    private static readonly Color ColDim    = new Color(0.400f, 0.400f, 0.400f, 0.4f);
    private static readonly Color ColWhite  = new Color(1f, 1f, 1f, 0.85f);

    private bool    _scanning        = false;
    private float   _steadyTimer     = 0f;
    private float   _lastFreeStillTime = -999f;   // v2.4.7 free-scan still cooldown tracker
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

        // v2.4.8 FIX A: moved to TOP-LEFT (was bottom-right, where it sat on
        // top of the SET POSE button and blocked the tap). Top-left band:
        // x 0-38%, y 70-100%. Clears the entire bottom control bar.
        var bg = new GameObject("Bg", typeof(RectTransform), typeof(Image));
        bg.transform.SetParent(go.transform, false);
        var bgImg = bg.GetComponent<Image>();
        bgImg.color = new Color(0, 0, 0, 0.55f);
        bgImg.raycastTarget = false;
        var bgRt = bg.GetComponent<RectTransform>();
        bgRt.anchorMin = new Vector2(0.0f, 0.70f);   // left ~38% wide
        bgRt.anchorMax = new Vector2(0.38f, 1.0f);   // top ~30% tall
        bgRt.offsetMin = bgRt.offsetMax = Vector2.zero;

        var textGo = new GameObject("DiagText", typeof(RectTransform), typeof(TextMeshProUGUI));
        textGo.transform.SetParent(go.transform, false);
        var rt = textGo.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0.0f, 0.70f);
        rt.anchorMax = new Vector2(0.38f, 1.0f);
        rt.offsetMin = new Vector2(6, 4);
        rt.offsetMax = new Vector2(-6, -4);

        _diagText = textGo.GetComponent<TextMeshProUGUI>();
        _diagText.fontSize = 12f;
        _diagText.color = new Color(1f, 1f, 1f, 0.85f);
        _diagText.alignment = TextAlignmentOptions.TopLeft;
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
        public string id, type, classification; public float width, height; public bool immature;
    }
    [Serializable] public class PlaneEntry
    {
        public double timestamp; public int raw_plane_ids, surfaces, vertical_surfaces, synced_still;
        public string reason, tracking;
        public int horizontal_planes, vertical_planes, feature_point_count;
        public float? ambient_intensity, color_temperature; public bool depth_available;
        public int depth_points; public float depth_density_per_m2; // v2.4.10 additive
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
        public int    depth_points;          // v2.4.10 additive
        public float  depth_density_per_m2;  // v2.4.10 additive
        public float? ambient_intensity, color_temperature;
        public float  pos_x, pos_y, pos_z, rot_x, rot_y, rot_z;
    }

    // --- Lifecycle ---

    private void Awake()
    {
        Diag("Controller.Awake() FIRED v2.4.8");
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
        Diag("Controller.Start() FIRED v2.4.8");

        BuildDiagOverlay();

        _cameraManager     = FindObjectOfType<ARCameraManager>();
        _planeManager      = FindObjectOfType<ARPlaneManager>();
        _pointCloudManager = FindObjectOfType<ARPointCloudManager>();
        _occlusionManager  = FindObjectOfType<AROcclusionManager>();
        _debugManager      = FindObjectOfType<ARDebugManager>();

        Diag($"  ARCameraManager null={_cameraManager == null}  ARPlaneManager null={_planeManager == null}");

        // v2.4.8 FIX B: ARCore returns no light data unless the mode is
        // requested. Ask for ambient intensity + ambient colour so
        // averageBrightness and averageColorTemperature populate in
        // OnCameraFrame. Without this both stay null all session.
        if (_cameraManager != null)
        {
            _cameraManager.requestedLightEstimation =
                LightEstimation.AmbientIntensity | LightEstimation.AmbientColor;
            Diag($"  Light estimation requested: {_cameraManager.requestedLightEstimation}");
            // v2.4.9 FIX G: log what the subsystem GRANTED, not just what we asked.
            // A null colour temperature is then explained (platform limit vs code bug).
            Diag($"  Light estimation GRANTED:  {_cameraManager.currentLightEstimation}");
        }

        // v2.4.9 FIX V: explicitly enable vertical plane detection. Without this,
        // detection defaults to whatever the Inspector holds (was Horizontal-only),
        // so walls were never detected. Set in code so it is version-controlled.
        if (_planeManager != null)
        {
            _planeManager.requestedDetectionMode =
                PlaneDetectionMode.Horizontal | PlaneDetectionMode.Vertical;
            Diag($"  Plane detection mode requested: {_planeManager.requestedDetectionMode}");
            Diag($"  Plane detection mode current:   {_planeManager.currentDetectionMode}");
        }

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

        if (freeScanMode)
        {
            // v2.4.7 free-scan: steady-detect still capture, no zone sequence
            TickFreeScanSteady();
        }
        else
        {
            // Zone state machine (v2.4.6 Part Two)
            TickZoneSequence();
        }
    }

    // ----------------------------------------------------------
    //  FREE-SCAN STEADY DETECT (v2.4.7)
    //  Fires a still when the phone is held steady for steadyHoldTime,
    //  rate-limited by freeScanStillCooldown. Replaces the guided
    //  zone capture path when freeScanMode is on.
    // ----------------------------------------------------------
    private void TickFreeScanSteady()
    {
        if (Camera.main == null) return;

        Vector3 euler = Camera.main.transform.eulerAngles;
        float angular = HeadingDelta(euler.y, _lastEuler.y)
                      + Mathf.Abs(Mathf.DeltaAngle(euler.x, _lastEuler.x));
        _lastEuler = euler;

        if (angular <= steadyAngularThreshold)
        {
            _steadyTimer += Time.deltaTime;
            if (_steadyTimer >= steadyHoldTime
                && (Time.time - _lastFreeStillTime) >= freeScanStillCooldown)
            {
                _lastFreeStillTime = Time.time;
                _steadyTimer = 0f;
                CaptureStillFreeScan();
            }
        }
        else
        {
            _steadyTimer = 0f;
        }

        SetHud($"Free scan — {_stills.Count} stills  (hold steady to capture)");
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

        // --- v2.4.7 FIX 1: session-merge ---
        // Re-initialise the session at the top of every START so back-to-back
        // scans each get a fresh id/folder/filename and empty data lists.
        // InitSession() clears _poseGatePassed, so capture and restore it —
        // the gate has already passed by this point and must stay passed.
        bool gateWasPassed = _poseGatePassed;
        InitSession();
        _poseGatePassed = gateWasPassed;

        _scanning = true; _sessionStartTime = Time.time;
        _startPosition = Camera.main.transform.position;
        _lastEuler = Camera.main.transform.eulerAngles; _steadyTimer = 0f;
        _lastFreeStillTime = -999f;
        Vector3 euler = Camera.main.transform.eulerAngles;
        _startPitch = NormalisePitch(euler.x); _startHeight = Camera.main.transform.position.y; _startHeading = euler.y;
        CaptureCameraIntrinsics();

        if (freeScanMode)
        {
            // --- v2.4.7 FREE-SCAN ---
            // No zone state machine. Operator-controlled duration. Stills fire
            // on steady-detect. Guided overlay hidden; crosshair stays.
            SetHud("Free scan — hold steady to capture, STOP to end");
            SetBorder(ColGreen);
            if (startStopLabel != null) startStopLabel.text = "STOP";
            if (overlay != null) { overlay.ResetForSession(); overlay.SetGuidedVisible(false); }
            SetPrompt("FREE SCAN — move freely, hold steady for a still");
            LogEvent("SCAN STARTED (free-scan)");
        }
        else
        {
            SetHud("Starting guided scan..."); SetBorder(ColGreen);
            if (startStopLabel != null) startStopLabel.text = "STOP";
            if (overlay != null) { overlay.SetGuidedVisible(true); overlay.ResetForSession(); }
            LogEvent("SCAN STARTED");
            StartZoneSequence();
        }
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
        if (overlay != null) { overlay.SetGuidedVisible(true); overlay.ResetForSession(); }
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

    // v2.4.7: free-scan still — steady-detect triggered, tagged zone="free".
    // Mirrors CaptureStillForZone minus the zone-record bookkeeping.
    private void CaptureStillFreeScan()
    {
        if (Camera.main == null) return;
        int idx = _stills.Count;
        string filename = $"still_{idx:D2}_wp.png";
        string filepath = Path.Combine(_sessionFolder, filename);
        if (!TryCaptureImage(filepath)) { LogEvent("FREE STILL FAILED"); return; }
        Vector3 pos = Camera.main.transform.position; Vector3 euler = Camera.main.transform.eulerAngles;
        _stills.Add(new StillEntry { index=idx, file=filename, zone="free", pitch=NormalisePitch(euler.x), height=pos.y, heading=euler.y,
            distance_to_start=Vector3.Distance(pos,_startPosition), timestamp=GetUnixTimestamp(), tracking=ARSession.state.ToString(), pose_matrix_4x4=GetCameraPoseMatrix() });
        LogEvent($"FREE STILL {filename}");
        RecordPlaneSnapshot("still_sync", idx);
        LogProcessingEvent($"still_{idx}_captured", idx==0 ? new[]{"yolo_inference","scale_calibration","srl_query"} : new[]{"yolo_inference","mvs_partial_reconstruction"});
        if (overlay != null) overlay.FlashCapture();
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
        // v2.4.10: refresh depth stats once here so both the snapshot and any
        // subsequent diagnostic sample read a consistent cached value.
        GetDepthStats(out _lastDepthPoints, out _lastDepthDensity);
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
                // v2.4.9 FIX D: a plane whose boundary has not matured reports size 0.
                // Tag it so downstream can distinguish "immature" from a real 0-size error.
                bool immature = (plane.size.x <= 0.0001f || plane.size.y <= 0.0001f);
                details.Add(new PlaneDetail { id=plane.trackableId.ToString(), type=t, classification=plane.classifications.ToString(), width=plane.size.x, height=plane.size.y, immature=immature });
            }
        }
        int deduped     = _debugManager != null ? _debugManager.GetRealHorizontalPlaneCount() : horz;
        int dedupedVert = _debugManager != null ? _debugManager.GetRealVerticalPlaneCount()   : vert;
        var entry = new PlaneEntry { timestamp=GetUnixTimestamp(), raw_plane_ids=raw, surfaces=deduped, vertical_surfaces=dedupedVert, synced_still=syncedStill, reason=reason, tracking=ARSession.state.ToString(),
            horizontal_planes=horz, vertical_planes=vert, feature_point_count=GetFeaturePointCount(), ambient_intensity=_ambientIntensity, color_temperature=_colorTemperature, depth_available=CheckDepthAvailable(), depth_points=_lastDepthPoints, depth_density_per_m2=_lastDepthDensity, plane_details=details };
        _planeLog.Add(entry); return entry;
    }

    // v2.4.6 Part Three — one continuous diagnostic sample
    private void RecordDiagnosticSample()
    {
        // v2.4.10: refresh depth stats for this sample.
        GetDepthStats(out _lastDepthPoints, out _lastDepthDensity);
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
            depth_points = _lastDepthPoints,
            depth_density_per_m2 = _lastDepthDensity,
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

    // v2.4.10: cheap depth density. Counts valid (non-zero, finite) depth samples
    // in the environment depth CPU image, and divides by total deduped plane area
    // to give points-per-square-metre. Returns (0,0) if depth unavailable.
    // ROLLBACK: delete this whole method and its two callers; CheckDepthAvailable
    // above is unchanged and the scan still runs exactly as v2.4.9.
    private void GetDepthStats(out int validPoints, out float densityPerM2)
    {
        validPoints  = 0;
        densityPerM2 = 0f;
        if (_occlusionManager == null || !_occlusionManager.enabled) return;

        if (!_occlusionManager.TryAcquireEnvironmentDepthCpuImage(out XRCpuImage img)) return;
        try
        {
            // Depth CPU image is single-plane float32 (metres) on ARCore.
            // Reinterpret the byte NativeArray as floats with zero per-pixel allocation.
            var plane = img.GetPlane(0);
            var floats = plane.data.Reinterpret<float>(1); // 1 byte -> element size, view as float
            int count  = floats.Length;
            int valid  = 0;
            // Subsample every Nth pixel to keep this cheap at 2Hz. Density is a ratio,
            // so a consistent stride does not bias points-per-m2 (we scale back up).
            const int STRIDE = 4;
            int sampled = 0;
            for (int i = 0; i < count; i += STRIDE)
            {
                float d = floats[i];
                if (d > 0.0001f && !float.IsNaN(d) && !float.IsInfinity(d)) valid++;
                sampled++;
            }
            // scale sampled valid count back to full-image estimate
            validPoints = (sampled > 0) ? (int)((long)valid * count / sampled) : 0;

            float area = GetTotalPlaneArea();
            if (area > 0.01f) densityPerM2 = validPoints / area;
        }
        catch (System.Exception e)
        {
            Diag($"  Depth stats error: {e.Message}");
        }
        finally
        {
            img.Dispose();
        }
    }

    // Total area of currently tracked planes (rough denominator for depth density).
    private float GetTotalPlaneArea()
    {
        float area = 0f;
        if (_planeManager == null) return 0f;
        foreach (var plane in _planeManager.trackables)
        {
            if (plane.size.x <= 0.0001f || plane.size.y <= 0.0001f) continue;
            area += plane.size.x * plane.size.y;
        }
        return area;
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
        public string scenario_tag;     // v2.4.7 — top-level, default empty string
        public bool   free_scan_mode;   // v2.4.7 — top-level
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
            scenario_tag = scenarioTag ?? "",
            free_scan_mode = freeScanMode,
            written_unix = GetUnixTimestamp(),
            guided_scan = new GuidedScanBlock
            {
                guided_scan_mode = !freeScanMode,
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
        string filename = $"{_sessionId}_{_sessionStamp}_v248.json";
        File.WriteAllText(Path.Combine(_sessionFolder, filename), json);
        Diag($"Manifest written: {filename}  stills={_stills.Count} planes={_planeLog.Count}");
    }

    // PART THREE — write the continuous diagnostic file
    [Serializable] private class DiagnosticFile
    {
        public string session_id;
        public string version;
        public string scenario_tag;     // v2.4.7 — pairs with the manifest
        public bool   free_scan_mode;   // v2.4.7 — pairs with the manifest
        public float  sample_interval_seconds;
        public List<DiagnosticSample> samples = new List<DiagnosticSample>();
    }

    private void WriteDiagnosticLog()
    {
        var df = new DiagnosticFile
        {
            session_id = _sessionId,
            version = VERSION,
            scenario_tag = scenarioTag ?? "",
            free_scan_mode = freeScanMode,
            sample_interval_seconds = diagnosticSampleSeconds,
            samples = _diagSamples
        };
        string json = JsonUtility.ToJson(df, true);
        string filename = $"{_sessionId}_{_sessionStamp}_v248_diagnostic.json";
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
