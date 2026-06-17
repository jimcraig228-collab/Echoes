// ============================================================
//  EchoesScanController.cs
//  Version: v2.4.3
//
//  Architecture (from EchoesBootstrap.cs):
//    Bootstrap creates "ScanController" GameObject on a Canvas,
//    then AddComponent<EchoesScanController>() then AddComponent<EchoesScanHUD>().
//    HUD.Awake() runs immediately and injects UI refs into this
//    controller's public fields. Controller.Start() runs next frame
//    and wires button listeners + initialises the session.
//    EchoesScanUploader lives on a SEPARATE GameObject →
//    FindObjectOfType, not GetComponent.
//
//  Changes from v2.4.1:
//    - Vertical planes, plane classification, feature point count,
//      light estimation and depth availability logged per snapshot
//    - Camera intrinsics logged once per session
//    - Full 4x4 camera pose matrix logged per still
//    - Streaming pipeline events for future Spatial Fusion Layer
//    - processing_events[] timing log in manifest
//    - AR Foundation 6: trackablesChanged is read-only → plane
//      count polled in Update() instead of event subscription
// ============================================================

using System;
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
    private const string VERSION = "v2.4.3";

    // -------------------------------------------------------
    //  HUD refs — injected by EchoesScanHUD.Awake()
    // -------------------------------------------------------
    [HideInInspector] public BorderColorRelay   borderRelay;
    [HideInInspector] public TextMeshProUGUI    promptText;
    [HideInInspector] public TextMeshProUGUI    hudText;
    [HideInInspector] public Button             startStopButton;
    [HideInInspector] public TextMeshProUGUI    startStopLabel;
    [HideInInspector] public Button             setPoseButton;

    // -------------------------------------------------------
    //  Inspector tunables
    // -------------------------------------------------------
    [Header("Pose Gate")]
    public bool  gateOnPitch    = true;
    public bool  gateOnHeight   = false;
    public bool  gateOnDistance = false;
    public bool  gateOnHeading  = false;
    public float targetPitch       = 35f;
    public float pitchToleranceDeg = 8f;

    [Header("Waypoints")]
    public float[]  waypointOffsets = { 0f, -0.4f, 0.4f };
    public string[] waypointPrompts = { "CENTRE", "STEP LEFT", "STEP RIGHT" };

    [Header("Steady Detection")]
    public float steadyAngularThreshold = 5f;
    public float steadyHoldTime         = 1.5f;

    // -------------------------------------------------------
    //  Streaming pipeline events (Spatial Fusion Layer hooks)
    // -------------------------------------------------------
    public event Action<int, string>  OnStillCaptured;
    public event Action<PlaneEntry>   OnPlaneSnapshotReady;
    public event Action<string>       OnScanComplete;

    // -------------------------------------------------------
    //  AR components (auto-found)
    // -------------------------------------------------------
    private ARCameraManager     _cameraManager;
    private ARPlaneManager      _planeManager;
    private ARPointCloudManager _pointCloudManager;
    private AROcclusionManager  _occlusionManager;
    private ARDebugManager      _debugManager;

    // -------------------------------------------------------
    //  UI colours
    // -------------------------------------------------------
    private static readonly Color ColPurple = new Color(0.439f, 0.251f, 0.722f, 0.7f);
    private static readonly Color ColGreen  = new Color(0.200f, 0.800f, 0.400f, 0.7f);
    private static readonly Color ColAmber  = new Color(0.900f, 0.600f, 0.100f, 0.7f);
    private static readonly Color ColDim    = new Color(0.400f, 0.400f, 0.400f, 0.4f);

    // -------------------------------------------------------
    //  Session state
    // -------------------------------------------------------
    private bool    _scanning        = false;
    private int     _currentWaypoint = 0;
    private float   _steadyTimer     = 0f;
    private Vector3 _lastEuler       = Vector3.zero;
    private Vector3 _startPosition;
    private bool    _poseGatePassed  = false;
    private float   _sessionStartTime;

    private float _savedPitch;
    private float _savedHeight;
    private float _savedHeading;
    private bool  _hasSavedPose = false;

    // -------------------------------------------------------
    //  Light estimation cache
    // -------------------------------------------------------
    private float? _ambientIntensity = null;
    private float? _colorTemperature = null;

    // -------------------------------------------------------
    //  Manifest data
    // -------------------------------------------------------
    private string _sessionId;
    private string _sessionFolder;

    private CameraIntrinsicsEntry      _cameraIntrinsics;
    private List<StillEntry>           _stills           = new List<StillEntry>();
    private List<PlaneEntry>           _planeLog         = new List<PlaneEntry>();
    private List<ProcessingEventEntry> _processingEvents = new List<ProcessingEventEntry>();
    private List<string>               _log              = new List<string>();

    private float _startPitch;
    private float _startHeight;
    private float _startHeading;
    private int   _lastRawPlaneCount = -1;

    // -------------------------------------------------------
    //  Data classes
    // -------------------------------------------------------

    [Serializable] public class CameraIntrinsicsEntry
    {
        public float focal_length_x, focal_length_y;
        public float principal_point_x, principal_point_y;
        public int   image_width, image_height;
    }

    [Serializable] public class StillEntry
    {
        public int    index;
        public string file;
        public float  pitch, height, heading, distance_to_start;
        public double timestamp;
        public string tracking;
        public float[] pose_matrix_4x4;
    }

    [Serializable] public class PlaneDetail
    {
        public string id, type, classification;
        public float  width, height;
    }

    [Serializable] public class PlaneEntry
    {
        public double timestamp;
        public int    raw_plane_ids, surfaces, synced_still;
        public string reason, tracking;
        public int    horizontal_planes, vertical_planes, feature_point_count;
        public float? ambient_intensity, color_temperature;
        public bool   depth_available;
        public List<PlaneDetail> plane_details = new List<PlaneDetail>();
    }

    [Serializable] public class ProcessingEventEntry
    {
        public double   timestamp;
        public string   trigger;
        public string[] hooks_available;
        public float    elapsed_ms;
    }

    // -------------------------------------------------------
    //  Lifecycle
    // -------------------------------------------------------

    private void Awake()
    {
        _cameraManager     = FindObjectOfType<ARCameraManager>();
        _planeManager      = FindObjectOfType<ARPlaneManager>();
        _pointCloudManager = FindObjectOfType<ARPointCloudManager>();
        _occlusionManager  = FindObjectOfType<AROcclusionManager>();
        _debugManager      = FindObjectOfType<ARDebugManager>();

        if (PlayerPrefs.HasKey("echoes_saved_pitch"))
        {
            _savedPitch   = PlayerPrefs.GetFloat("echoes_saved_pitch");
            _savedHeight  = PlayerPrefs.GetFloat("echoes_saved_height");
            _savedHeading = PlayerPrefs.GetFloat("echoes_saved_heading");
            _hasSavedPose = true;
        }
    }

    private void OnEnable()
    {
        if (_cameraManager != null)
            _cameraManager.frameReceived += OnCameraFrame;
    }

    private void OnDisable()
    {
        if (_cameraManager != null)
            _cameraManager.frameReceived -= OnCameraFrame;
    }

    private void Start()
    {
        // Wire buttons — HUD.Awake() already injected the Button refs
        if (startStopButton != null)
            startStopButton.onClick.AddListener(OnStartStopPressed);
        if (setPoseButton != null)
            setPoseButton.onClick.AddListener(OnSetPosePressed);

        InitSession();
        SetPrompt("Press START to begin scanning");
        SetBorder(ColDim);
        LogEvent("SESSION INIT " + _sessionId);

        // Evaluate pose gate only once a camera is available
        if (Camera.main != null)
            EvaluatePoseGate();
        else
            SetPrompt("Waiting for camera...");
    }

    private void Update()
    {
        // Retry pose gate if camera wasn't ready in Start
        if (!_poseGatePassed && Camera.main != null && !_scanning)
        {
            EvaluatePoseGate();
        }

        if (!_scanning) return;

        // AR Foundation 6: poll plane count (trackablesChanged is read-only)
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

        // Steady-detect for waypoint capture
        if (Camera.main == null) return;

        Vector3 currentEuler = Camera.main.transform.eulerAngles;
        float angularRate = Vector3.Angle(
            Quaternion.Euler(currentEuler) * Vector3.forward,
            Quaternion.Euler(_lastEuler)   * Vector3.forward
        ) / Time.deltaTime;
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
                else
                {
                    SetPrompt($"Done — {_stills.Count} stills. Press STOP.");
                    SetHud("");
                }
            }
        }
        else
        {
            _steadyTimer = 0f;
            SetHud("Move slowly...");
        }
    }

    // -------------------------------------------------------
    //  Camera frame
    // -------------------------------------------------------

    private void OnCameraFrame(ARCameraFrameEventArgs args)
    {
        var le = args.lightEstimation;
        if (le.averageBrightness.HasValue)     _ambientIntensity = le.averageBrightness.Value;
        if (le.averageColorTemperature.HasValue) _colorTemperature = le.averageColorTemperature.Value;
    }

    // -------------------------------------------------------
    //  Session
    // -------------------------------------------------------

    private void InitSession()
    {
        string ts  = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        _sessionId     = $"echoes_session_{VERSION}_{ts}";
        _sessionFolder = Path.Combine(Application.persistentDataPath, _sessionId);
        Directory.CreateDirectory(_sessionFolder);
        _stills.Clear(); _planeLog.Clear(); _processingEvents.Clear(); _log.Clear();
        _cameraIntrinsics = null; _lastRawPlaneCount = -1;
        _currentWaypoint = 0; _scanning = false; _poseGatePassed = false;
    }

    // -------------------------------------------------------
    //  Pose gate
    // -------------------------------------------------------

    private void EvaluatePoseGate()
    {
        if (Camera.main == null) return;

        Vector3 euler = Camera.main.transform.eulerAngles;
        float pitch   = NormalisePitch(euler.x);
        float height  = Camera.main.transform.position.y;
        float heading = euler.y;

        bool ok = !gateOnPitch || Mathf.Abs(pitch - targetPitch) <= pitchToleranceDeg;
        ok &= !gateOnHeight  || (_hasSavedPose && Mathf.Abs(height - _savedHeight) < 0.15f);
        ok &= !gateOnHeading || (_hasSavedPose && HeadingDelta(heading, _savedHeading) < 20f);

        if (!_hasSavedPose) SaveCurrentPose();

        if (ok)
        {
            _poseGatePassed = true;
            SetPrompt("Pose locked — press START");
            SetBorder(ColPurple);
            LogEvent($"Pose gate PASSED pitch={pitch:F0}");
        }
        else
        {
            SetPrompt($"Tilt camera down to ~{targetPitch:F0}° (now {pitch:F0}°)");
            SetBorder(ColAmber);
        }
    }

    private void SaveCurrentPose()
    {
        if (Camera.main == null) return;
        Vector3 euler = Camera.main.transform.eulerAngles;
        _savedPitch   = NormalisePitch(euler.x);
        _savedHeight  = Camera.main.transform.position.y;
        _savedHeading = euler.y;
        _hasSavedPose = true;
        PlayerPrefs.SetFloat("echoes_saved_pitch",   _savedPitch);
        PlayerPrefs.SetFloat("echoes_saved_height",  _savedHeight);
        PlayerPrefs.SetFloat("echoes_saved_heading", _savedHeading);
        PlayerPrefs.Save();
    }

    // -------------------------------------------------------
    //  Button handlers
    // -------------------------------------------------------

    private void OnStartStopPressed()
    {
        if (_scanning) StopScan();
        else StartScan();
    }

    private void StartScan()
    {
        if (!_poseGatePassed) { EvaluatePoseGate(); return; }
        if (Camera.main == null) return;

        _scanning         = true;
        _sessionStartTime = Time.time;
        _startPosition    = Camera.main.transform.position;
        _lastEuler        = Camera.main.transform.eulerAngles;
        _steadyTimer      = 0f;

        Vector3 euler = Camera.main.transform.eulerAngles;
        _startPitch   = NormalisePitch(euler.x);
        _startHeight  = Camera.main.transform.position.y;
        _startHeading = euler.y;

        CaptureCameraIntrinsics();

        SetPrompt(waypointPrompts[0]);
        SetHud("Hold steady...");
        SetBorder(ColGreen);
        if (startStopLabel != null) startStopLabel.text = "STOP";
        LogEvent("SCAN STARTED");
    }

    private void StopScan()
    {
        _scanning = false;
        LogEvent($"SESSION STOP — {_stills.Count} stills");

        LogProcessingEvent("scan_complete", new[]
        {
            "spatial_fusion_final_pass", "srl_graduation_evaluation",
            "diff_engine_world_patch",   "unity_scene_build"
        });

        WriteManifest();
        OnScanComplete?.Invoke(_sessionFolder);

        // Uploader is on a separate GameObject (EchoesBootstrap creates it independently)
        var uploader = FindObjectOfType<EchoesScanUploader>();
        uploader?.QueueSession(_sessionFolder, _sessionId);

        SetPrompt($"Session saved — {_stills.Count} stills");
        SetHud(_sessionId);
        SetBorder(ColDim);
        if (startStopLabel != null) startStopLabel.text = "START";
    }

    private void OnSetPosePressed()
    {
        SaveCurrentPose();
        LogEvent("Pose saved");
        EvaluatePoseGate();
    }

    // -------------------------------------------------------
    //  Camera intrinsics
    // -------------------------------------------------------

    private void CaptureCameraIntrinsics()
    {
        if (_cameraManager == null) return;
        if (_cameraManager.TryGetIntrinsics(out XRCameraIntrinsics intr))
        {
            _cameraIntrinsics = new CameraIntrinsicsEntry
            {
                focal_length_x    = intr.focalLength.x,
                focal_length_y    = intr.focalLength.y,
                principal_point_x = intr.principalPoint.x,
                principal_point_y = intr.principalPoint.y,
                image_width       = intr.resolution.x,
                image_height      = intr.resolution.y
            };
            LogEvent($"Intrinsics: {intr.resolution.x}x{intr.resolution.y} fx={intr.focalLength.x:F1}");
        }
        else LogEvent("WARNING: camera intrinsics unavailable");
    }

    private float[] GetCameraPoseMatrix()
    {
        if (Camera.main == null) return null;
        Matrix4x4 m = Camera.main.transform.localToWorldMatrix;
        return new float[]
        {
            m.m00, m.m01, m.m02, m.m03,
            m.m10, m.m11, m.m12, m.m13,
            m.m20, m.m21, m.m22, m.m23,
            m.m30, m.m31, m.m32, m.m33
        };
    }

    private void LogProcessingEvent(string trigger, string[] hooks)
    {
        float elapsed = _scanning ? (Time.time - _sessionStartTime) * 1000f : 0f;
        _processingEvents.Add(new ProcessingEventEntry
        {
            timestamp = GetUnixTimestamp(), trigger = trigger,
            hooks_available = hooks, elapsed_ms = elapsed
        });
    }

    // -------------------------------------------------------
    //  Still capture
    // -------------------------------------------------------

    private void CaptureStill(int idx)
    {
        if (Camera.main == null) return;
        string filename = $"still_{idx:D2}_wp.png";
        string filepath = Path.Combine(_sessionFolder, filename);

        if (!TryCaptureImage(filepath)) { LogEvent($"WP{idx} STILL FAILED"); return; }

        Vector3 pos   = Camera.main.transform.position;
        Vector3 euler = Camera.main.transform.eulerAngles;

        var entry = new StillEntry
        {
            index             = idx,
            file              = filename,
            pitch             = NormalisePitch(euler.x),
            height            = pos.y,
            heading           = euler.y,
            distance_to_start = Vector3.Distance(pos, _startPosition),
            timestamp         = GetUnixTimestamp(),
            tracking          = ARSession.state.ToString(),
            pose_matrix_4x4   = GetCameraPoseMatrix()
        };
        _stills.Add(entry);
        LogEvent($"WP{idx} STILL {filename}");

        RecordPlaneSnapshot("still_sync", idx);
        LogProcessingEvent($"still_{idx}_captured",
            idx == 0
                ? new[] { "yolo_inference", "scale_calibration", "srl_query" }
                : new[] { "yolo_inference", "mvs_partial_reconstruction" });

        OnStillCaptured?.Invoke(idx, filepath);
    }

    private bool TryCaptureImage(string filepath)
    {
        if (_cameraManager == null) return false;
        if (!_cameraManager.TryAcquireLatestCpuImage(out XRCpuImage image)) return false;

        using (image)
        {
            var cp = new XRCpuImage.ConversionParams
            {
                inputRect        = new RectInt(0, 0, image.width, image.height),
                outputDimensions = new Vector2Int(image.width, image.height),
                outputFormat     = TextureFormat.RGBA32,
                transformation   = XRCpuImage.Transformation.MirrorY
            };
            var buf = new NativeArray<byte>(image.GetConvertedDataSize(cp), Allocator.Temp);
            image.Convert(cp, buf);
            var tex = new Texture2D(image.width, image.height, TextureFormat.RGBA32, false);
            tex.LoadRawTextureData(buf); tex.Apply(); buf.Dispose();
            File.WriteAllBytes(filepath, tex.EncodeToPNG());
            Destroy(tex);
        }
        return true;
    }

    // -------------------------------------------------------
    //  Plane snapshot
    // -------------------------------------------------------

    private PlaneEntry RecordPlaneSnapshot(string reason, int syncedStill)
    {
        int raw = 0, horz = 0, vert = 0;
        var details = new List<PlaneDetail>();

        if (_planeManager != null)
        {
            foreach (var plane in _planeManager.trackables)
            {
                raw++;
                string t;
                switch (plane.alignment)
                {
                    case PlaneAlignment.HorizontalUp:   t = "HorizontalUp";   horz++; break;
                    case PlaneAlignment.HorizontalDown: t = "HorizontalDown"; horz++; break;
                    case PlaneAlignment.Vertical:       t = "Vertical";       vert++; break;
                    default:                            t = "NotAxisAligned"; break;
                }
                details.Add(new PlaneDetail
                {
                    id             = plane.trackableId.ToString(),
                    type           = t,
                    classification = plane.classifications.ToString(),
                    width          = plane.size.x,
                    height         = plane.size.y
                });
            }
        }

        int deduped = _debugManager != null ? _debugManager.GetRealHorizontalPlaneCount() : horz;

        var entry = new PlaneEntry
        {
            timestamp           = GetUnixTimestamp(),
            raw_plane_ids       = raw,
            surfaces            = deduped,
            synced_still        = syncedStill,
            reason              = reason,
            tracking            = ARSession.state.ToString(),
            horizontal_planes   = horz,
            vertical_planes     = vert,
            feature_point_count = GetFeaturePointCount(),
            ambient_intensity   = _ambientIntensity,
            color_temperature   = _colorTemperature,
            depth_available     = CheckDepthAvailable(),
            plane_details       = details
        };

        _planeLog.Add(entry);
        return entry;
    }

    private int GetFeaturePointCount()
    {
        if (_pointCloudManager == null) return 0;
        int total = 0;
        foreach (var cloud in _pointCloudManager.trackables)
            if (cloud.positions.HasValue) total += cloud.positions.Value.Length;
        return total;
    }

    private bool CheckDepthAvailable()
    {
        if (_occlusionManager == null || !_occlusionManager.enabled) return false;
        if (_occlusionManager.TryAcquireEnvironmentDepthCpuImage(out XRCpuImage img))
        { img.Dispose(); return true; }
        return false;
    }

    // -------------------------------------------------------
    //  UI helpers
    // -------------------------------------------------------

    private void SetPrompt(string msg) { if (promptText != null) promptText.text = msg; }
    private void SetHud(string msg)    { if (hudText    != null) hudText.text    = msg; }
    private void SetBorder(Color c)    { borderRelay?.SetColor(c); }

    // -------------------------------------------------------
    //  Manifest
    // -------------------------------------------------------

    private void WriteManifest()
    {
        int finalRaw = 0;
        if (_planeManager != null) foreach (var _ in _planeManager.trackables) finalRaw++;
        int finalSurfaces = _debugManager != null
            ? _debugManager.GetRealHorizontalPlaneCount() : finalRaw;

        var sb = new StringBuilder();
        sb.AppendLine("{");
        sb.AppendLine($"  \"session_id\": \"{_sessionId}\",");
        sb.AppendLine($"  \"version\": \"{VERSION}\",");
        sb.AppendLine($"  \"device\": \"{SystemInfo.deviceModel}\",");

        if (_cameraIntrinsics != null)
        {
            var ci = _cameraIntrinsics;
            sb.AppendLine($"  \"camera_intrinsics\": {{ \"focal_length_x\": {ci.focal_length_x:F4}, \"focal_length_y\": {ci.focal_length_y:F4}, \"principal_point_x\": {ci.principal_point_x:F4}, \"principal_point_y\": {ci.principal_point_y:F4}, \"image_width\": {ci.image_width}, \"image_height\": {ci.image_height} }},");
        }
        else sb.AppendLine("  \"camera_intrinsics\": null,");

        sb.AppendLine($"  \"summary\": {{ \"still_count\": {_stills.Count}, \"final_surfaces\": {finalSurfaces}, \"final_raw_plane_ids\": {finalRaw}, \"plane_samples\": {_planeLog.Count}, \"processing_triggers_fired\": {_processingEvents.Count} }},");
        sb.AppendLine($"  \"start_pose\": {{ \"pitch\": {_startPitch:F1}, \"height\": {_startHeight:F2}, \"heading\": {_startHeading:F1} }},");
        sb.AppendLine($"  \"still_count\": {_stills.Count},");
        sb.AppendLine($"  \"stills\": [");
        for (int i = 0; i < _stills.Count; i++)
        {
            var s = _stills[i];
            string mtx = s.pose_matrix_4x4 != null
                ? "[" + string.Join(", ", Array.ConvertAll(s.pose_matrix_4x4, v => v.ToString("F6"))) + "]"
                : "null";
            sb.AppendLine($"    {{ \"index\": {s.index}, \"file\": \"{s.file}\", \"pitch\": {s.pitch:F1}, \"height\": {s.height:F2}, \"heading\": {s.heading:F1}, \"distance_to_start\": {s.distance_to_start:F2}, \"timestamp\": {s.timestamp:F2}, \"tracking\": \"{s.tracking}\", \"pose_matrix_4x4\": {mtx} }}{(i < _stills.Count - 1 ? "," : "")}");
        }
        sb.AppendLine($"  ],");
        sb.AppendLine($"  \"planes\": [");
        for (int i = 0; i < _planeLog.Count; i++)
        {
            var p    = _planeLog[i];
            string a = p.ambient_intensity.HasValue  ? p.ambient_intensity.Value.ToString("F2")  : "null";
            string t = p.color_temperature.HasValue  ? p.color_temperature.Value.ToString("F0")  : "null";
            sb.AppendLine($"    {{");
            sb.AppendLine($"      \"timestamp\": {p.timestamp:F2}, \"raw_plane_ids\": {p.raw_plane_ids}, \"surfaces\": {p.surfaces}, \"synced_still\": {p.synced_still}, \"reason\": \"{p.reason}\", \"tracking\": \"{p.tracking}\",");
            sb.AppendLine($"      \"horizontal_planes\": {p.horizontal_planes}, \"vertical_planes\": {p.vertical_planes}, \"feature_point_count\": {p.feature_point_count}, \"ambient_intensity\": {a}, \"color_temperature\": {t}, \"depth_available\": {p.depth_available.ToString().ToLower()},");
            sb.Append("      \"plane_details\": [");
            for (int j = 0; j < p.plane_details.Count; j++)
            {
                var d = p.plane_details[j];
                sb.Append($" {{ \"id\": \"{d.id}\", \"type\": \"{d.type}\", \"classification\": \"{d.classification}\", \"width\": {d.width:F3}, \"height\": {d.height:F3} }}{(j < p.plane_details.Count - 1 ? "," : "")}");
            }
            sb.AppendLine(" ]");
            sb.AppendLine($"    }}{(i < _planeLog.Count - 1 ? "," : "")}");
        }
        sb.AppendLine($"  ],");
        sb.AppendLine($"  \"processing_events\": [");
        for (int i = 0; i < _processingEvents.Count; i++)
        {
            var pe = _processingEvents[i];
            sb.AppendLine($"    {{ \"timestamp\": {pe.timestamp:F2}, \"trigger\": \"{pe.trigger}\", \"hooks_available\": [\"{string.Join("\", \"", pe.hooks_available)}\"], \"elapsed_ms\": {pe.elapsed_ms:F1} }}{(i < _processingEvents.Count - 1 ? "," : "")}");
        }
        sb.AppendLine($"  ],");
        sb.AppendLine($"  \"log\": [");
        for (int i = 0; i < _log.Count; i++)
            sb.AppendLine($"    \"{EscapeJson(_log[i])}\"{(i < _log.Count - 1 ? "," : "")}");
        sb.AppendLine($"  ]");
        sb.AppendLine("}");

        File.WriteAllText(Path.Combine(_sessionFolder, "manifest.json"), sb.ToString());
        LogEvent("Manifest written");
    }

    // -------------------------------------------------------
    //  Utilities
    // -------------------------------------------------------

    private void LogEvent(string msg)
    {
        string entry = $"{DateTime.Now:HH:mm:ss.fff} {msg}";
        _log.Add(entry);
        Debug.Log("[Echoes] " + entry);
    }

    private double GetUnixTimestamp()
        => (DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;

    private float NormalisePitch(float rawX)
    { if (rawX > 180f) rawX -= 360f; return -rawX; }

    private float HeadingDelta(float a, float b)
    { float d = Mathf.Abs(a - b) % 360f; return d > 180f ? 360f - d : d; }

    private string EscapeJson(string s)
        => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
