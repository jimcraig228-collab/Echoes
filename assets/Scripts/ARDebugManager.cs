using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using TMPro;

// ============================================================
//  ARDebugManager.cs
//  Echoes — Programmable Spatial Experience Platform
//  Version: 2.5.1  |  29 July 2026  [planesChanged/pointCloudsChanged subscriptions removed]
//
//  CHANGELOG v2.5.1 (sign-off fix):
//  Found this session as a third independent subscriber to
//  planeManager.planesChanged, alongside ARPlaneVisualizer.cs (a
//  cosmetic listener, tested and ruled out as the freeze cause) and
//  EchoesLoadingScreen.cs (a real leak, fixed separately). Unlike those
//  two, this component never disabled itself, so its handler ran real
//  allocation work on every single plane change for the entire session.
//  Confirmed nothing in the project reads what either handler computes,
//  see Start() below for the full reasoning. Subscriptions removed.
//  Tested clean across three full end-to-end device runs under motion.
//
//  CHANGELOG v2.3.4:
//  Fixes in this version:
//  1. GetDeduplicatedPlanes() — spatial merge filter so 18 IDs
//     for the same desk surface collapse to 1 real surface count
//  2. Ghost plane filter — ignores planes below Y:30cm
//  3. Fixed 0.0cm delta label — delta now calculated before logging
//  4. Removed confidence field (always 0.0 from ARCore)
//  5. featurePointCount wired to ARPointCloudManager
// ============================================================

public class ARDebugManager : MonoBehaviour
{
    [Header("AR References")]
    public ARSession arSession;
    public ARPlaneManager planeManager;
    public ARPointCloudManager pointCloudManager;

    [Header("UI")]
    public TextMeshProUGUI debugText;

    // Deduplication settings
    [Header("Plane Deduplication")]
    [Tooltip("Planes whose centers are closer than this (metres) at the same Y are treated as one surface")]
    public float mergeRadius = 0.15f;
    [Tooltip("Planes below this Y height (metres) are discarded as ghosts")]
    public float minPlaneY = 0.30f;
    [Tooltip("Max Y difference (metres) for two planes to be considered co-planar")]
    public float coplanarYTol = 0.05f;

    // Delta logging
    [Header("Delta Logging")]
    public float deltaThresholdMetres = 0.01f;

    // Internal
    private int _featurePointCount = 0;
    private int _logCount = 0;
    private float _lastLogTime = 0f;

    // Previous plane centers for delta calculation
    private Dictionary<string, Vector3> _previousCenters = new();

    void Start()
    {
        // v2.5.1 SIGN-OFF FIX: these two subscriptions are deliberately
        // removed, not conditional. Confirmed this session: OnPlanesChanged
        // only increments a private _logCount nothing else reads, and
        // OnPointCloudsChanged only sets a private _featurePointCount with
        // no public getter. UpdateDebugDisplay() (Update(), separately)
        // was also confirmed dead, debugText is unassigned anywhere in
        // the project, so it bails out for free every frame regardless.
        // The two methods the controller actually depends on,
        // GetRealHorizontalPlaneCount() and GetRealVerticalPlaneCount(),
        // recompute fresh on every call and never depended on either
        // subscription. So there was nothing to lose by not subscribing
        // at all, and real allocation-heavy work (two new List<ARPlane>,
        // an O(n2) dedup comparison) to gain back on every single plane
        // update for the entire session, previously a live freeze
        // candidate, tested and cleared across three full device runs.
        // Earlier this session this was handled by having the controller
        // destroy this GameObject at runtime instead, that approach is
        // now reverted in EchoesScanController.cs in favour of this
        // permanent fix at the source, functionally equivalent (neither
        // event ever fires either way) but cleaner: this component stays
        // alive and enabled normally for the methods that are actually used.
        // ROLLBACK: restore the two subscription lines below.
        // if (pointCloudManager != null)
        //     pointCloudManager.pointCloudsChanged += OnPointCloudsChanged;
        // if (planeManager != null)
        //     planeManager.planesChanged += OnPlanesChanged;
    }

    void OnDestroy()
    {
        if (pointCloudManager != null)
            pointCloudManager.pointCloudsChanged -= OnPointCloudsChanged;

        if (planeManager != null)
            planeManager.planesChanged -= OnPlanesChanged;
    }

    void Update()
    {
        UpdateDebugDisplay();
    }

    // ── Feature point count ───────────────────────────────────

    private void OnPointCloudsChanged(ARPointCloudChangedEventArgs args)
    {
        int total = 0;
        foreach (var cloud in pointCloudManager.trackables)
            if (cloud.positions.HasValue)
                total += cloud.positions.Value.Length;
        _featurePointCount = total;
    }

    // ── Plane change handler ──────────────────────────────────

    private void OnPlanesChanged(ARPlanesChangedEventArgs args)
    {
        // Only log if something meaningful changed
        var deduped = GetDeduplicatedPlanes();
        bool shouldLog = args.added.Count > 0 || args.removed.Count > 0;

        if (!shouldLog)
        {
            // Check if any real plane moved more than the threshold
            foreach (var plane in deduped)
            {
                string id = plane.trackableId.ToString();
                if (_previousCenters.TryGetValue(id, out Vector3 prev))
                {
                    float delta = Vector3.Distance(plane.center, prev);
                    if (delta >= deltaThresholdMetres)
                    {
                        shouldLog = true;
                        break;
                    }
                }
                else
                {
                    shouldLog = true; // new plane in deduped set
                }
            }
        }

        if (shouldLog)
        {
            // Update stored centers
            foreach (var plane in deduped)
                _previousCenters[plane.trackableId.ToString()] = plane.center;

            _logCount++;
        }
    }

    // ── Deduplicate planes ────────────────────────────────────
    //
    //  Collapses multiple ARCore plane IDs that represent the
    //  same physical surface (same Y height, centers within
    //  mergeRadius of each other) into one representative plane.
    //  Also filters ghost planes below minPlaneY.

    private List<ARPlane> GetDeduplicatedPlanes()
        => GetDeduplicatedHorizontal();

    // v2.4.9: horizontal dedup, now also skipping immature (zero-size) planes.
    private List<ARPlane> GetDeduplicatedHorizontal()
    {
        var all = new List<ARPlane>();
        var keepers = new List<ARPlane>();

        foreach (var p in planeManager.trackables)
            if (p.alignment == PlaneAlignment.HorizontalUp)
                all.Add(p);

        foreach (var candidate in all)
        {
            // Filter 0 (v2.4.9): skip planes whose boundary has not matured.
            if (candidate.size.x <= 0.0001f || candidate.size.y <= 0.0001f)
                continue;
            // Filter 1: ghost planes below physical floor threshold
            if (candidate.center.y < minPlaneY)
                continue;

            bool isDuplicate = false;
            for (int k = 0; k < keepers.Count; k++)
            {
                float dist = Vector3.Distance(candidate.center, keepers[k].center);
                float yDiff = Mathf.Abs(candidate.center.y - keepers[k].center.y);
                if (dist < mergeRadius && yDiff < coplanarYTol)
                {
                    isDuplicate = true;
                    float areaCandidate = candidate.size.x * candidate.size.y;
                    float areaKeeper = keepers[k].size.x * keepers[k].size.y;
                    if (areaCandidate > areaKeeper)
                        keepers[k] = candidate;
                    break;
                }
            }
            if (!isDuplicate)
                keepers.Add(candidate);
        }
        return keepers;
    }

    // v2.4.9: vertical dedup. Walls merge by horizontal proximity of centre and
    // shared normal direction, rather than by Y height. Immature planes skipped.
    private List<ARPlane> GetDeduplicatedVertical()
    {
        var all = new List<ARPlane>();
        var keepers = new List<ARPlane>();

        foreach (var p in planeManager.trackables)
            if (p.alignment == PlaneAlignment.Vertical)
                all.Add(p);

        foreach (var candidate in all)
        {
            if (candidate.size.x <= 0.0001f || candidate.size.y <= 0.0001f)
                continue;

            bool isDuplicate = false;
            for (int k = 0; k < keepers.Count; k++)
            {
                float dist = Vector3.Distance(candidate.center, keepers[k].center);
                // Same wall = centres close AND normals near-parallel.
                float normDot = Mathf.Abs(Vector3.Dot(candidate.normal, keepers[k].normal));
                if (dist < mergeRadius && normDot > 0.94f) // ~20 deg
                {
                    isDuplicate = true;
                    float areaCandidate = candidate.size.x * candidate.size.y;
                    float areaKeeper = keepers[k].size.x * keepers[k].size.y;
                    if (areaCandidate > areaKeeper)
                        keepers[k] = candidate;
                    break;
                }
            }
            if (!isDuplicate)
                keepers.Add(candidate);
        }
        return keepers;
    }

    // ── Debug display ─────────────────────────────────────────

    private void UpdateDebugDisplay()
    {
        if (debugText == null) return;

        var deduped = GetDeduplicatedPlanes();
        var allPlanes = new List<ARPlane>();
        foreach (var p in planeManager.trackables)
            allPlanes.Add(p);

        int rawCount = allPlanes.Count;
        int realCount = deduped.Count;
        int ghostCount = 0;
        foreach (var p in allPlanes)
            if (p.center.y < minPlaneY) ghostCount++;

        string trackingState = ARSession.state.ToString();

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("ECHOES AR DEBUG");
        sb.AppendLine($"Session: {trackingState}");
        sb.AppendLine($"Features: {_featurePointCount}");
        sb.AppendLine($"Logs: {_logCount} (delta filtered)");
        sb.AppendLine("──────────────────");

        // Show real (deduplicated) count prominently
        sb.AppendLine($"Surfaces:   {realCount}  (raw IDs: {rawCount})");
        if (ghostCount > 0)
            sb.AppendLine($"Ghosts filtered: {ghostCount}");
        sb.AppendLine();

        // List deduplicated surfaces
        foreach (var plane in deduped)
        {
            float w = plane.size.x * 100f;
            float h = plane.size.y * 100f;
            float yCm = plane.center.y * 100f;
            sb.AppendLine($"[H:OK] {w:F1}x{h:F1}cm  Y:{yCm:F0}cm");
        }

        // List ghosts separately
        if (ghostCount > 0)
        {
            sb.AppendLine();
            foreach (var p in allPlanes)
            {
                if (p.center.y >= minPlaneY) continue;
                float w = p.size.x * 100f;
                float h2 = p.size.y * 100f;
                float yCm = p.center.y * 100f;
                sb.AppendLine($"[GHOST] {w:F1}x{h2:F1}cm  Y:{yCm:F0}cm");
            }
        }

        debugText.text = sb.ToString();
    }

    // ── Public accessor for EchoesLoadingScreen phase logic ──
    //  Call this instead of counting planeManager.trackables

    public int GetRealHorizontalPlaneCount()
        => GetDeduplicatedHorizontal().Count;

    // v2.4.9: real (deduped, matured) vertical surface count for wall detection.
    public int GetRealVerticalPlaneCount()
        => GetDeduplicatedVertical().Count;
}