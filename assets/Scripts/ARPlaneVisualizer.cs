using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

[RequireComponent(typeof(ARPlaneManager))]
public class ARPlaneVisualizer : MonoBehaviour
{
    private ARPlaneManager planeManager;
    private Dictionary<TrackableId, GameObject> planeVisuals =
        new Dictionary<TrackableId, GameObject>();

    private Material planeMaterial;
    private Material lineMaterial;

    void Awake()
    {
        planeManager = GetComponent<ARPlaneManager>();

        // Create fill material in code — guaranteed transparent
        planeMaterial = new Material(Shader.Find("Sprites/Default"));
        planeMaterial.color = new Color(0f, 1f, 0f, 0.15f);

        // Create outline material
        lineMaterial = new Material(Shader.Find("Sprites/Default"));
        lineMaterial.color = new Color(0f, 1f, 0f, 0.8f);
    }

    void OnEnable()
    {
        planeManager.planesChanged += OnPlanesChanged;
    }

    void OnDisable()
    {
        planeManager.planesChanged -= OnPlanesChanged;
    }

    void OnPlanesChanged(ARPlanesChangedEventArgs args)
    {
        // Add new planes
        foreach (var plane in args.added)
            CreateVisual(plane);

        // Update existing planes
        foreach (var plane in args.updated)
            UpdateVisual(plane);

        // Remove old planes
        foreach (var plane in args.removed)
            RemoveVisual(plane);
    }

    void CreateVisual(ARPlane plane)
    {
        var go = new GameObject($"PlaneVisual_{plane.trackableId}");
        go.transform.SetParent(plane.transform, false);
        go.transform.localPosition = Vector3.zero;
        go.transform.localRotation = Quaternion.identity;

        // Add mesh fill
        var meshFilter = go.AddComponent<MeshFilter>();
        var meshRenderer = go.AddComponent<MeshRenderer>();
        meshRenderer.material = planeMaterial;
        meshRenderer.shadowCastingMode =
            UnityEngine.Rendering.ShadowCastingMode.Off;
        meshRenderer.receiveShadows = false;

        // Add outline
        var lineRenderer = go.AddComponent<LineRenderer>();
        lineRenderer.material = lineMaterial;
        lineRenderer.startWidth = 0.02f;
        lineRenderer.endWidth = 0.02f;
        lineRenderer.loop = true;
        lineRenderer.useWorldSpace = false;
        lineRenderer.shadowCastingMode =
            UnityEngine.Rendering.ShadowCastingMode.Off;

        planeVisuals[plane.trackableId] = go;
        UpdateVisualMesh(go, plane);
    }

    void UpdateVisual(ARPlane plane)
    {
        if (planeVisuals.TryGetValue(plane.trackableId, out var go))
            UpdateVisualMesh(go, plane);
    }

    void RemoveVisual(ARPlane plane)
    {
        if (planeVisuals.TryGetValue(plane.trackableId, out var go))
        {
            Destroy(go);
            planeVisuals.Remove(plane.trackableId);
        }
    }

    void UpdateVisualMesh(GameObject go, ARPlane plane)
    {
        var boundary = plane.boundary;
        if (boundary.Length < 3) return;

        // Build fill mesh from boundary polygon
        var meshFilter = go.GetComponent<MeshFilter>();
        var mesh = new Mesh();

        // Vertices from boundary
        var vertices = new Vector3[boundary.Length];
        for (int i = 0; i < boundary.Length; i++)
            vertices[i] = new Vector3(boundary[i].x, 0, boundary[i].y);

        // Simple fan triangulation from centre
        var triangles = new int[(boundary.Length - 2) * 3];
        for (int i = 0; i < boundary.Length - 2; i++)
        {
            triangles[i * 3] = 0;
            triangles[i * 3 + 1] = i + 1;
            triangles[i * 3 + 2] = i + 2;
        }

        mesh.vertices = vertices;
        mesh.triangles = triangles;
        mesh.RecalculateNormals();
        meshFilter.mesh = mesh;

        // Update outline from boundary
        var lineRenderer = go.GetComponent<LineRenderer>();
        lineRenderer.positionCount = boundary.Length;
        for (int i = 0; i < boundary.Length; i++)
            lineRenderer.SetPosition(i,
                new Vector3(boundary[i].x, 0.001f, boundary[i].y));
    }

    public void ClearAllPlanes()
    {
        foreach (var kvp in planeVisuals)
            Destroy(kvp.Value);
        planeVisuals.Clear();
    }
}
