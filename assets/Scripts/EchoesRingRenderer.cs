using UnityEngine;
using UnityEngine.UI;

// ============================================================
//  EchoesRingRenderer.cs
//
//  Draws a hollow ellipse outline using Unity's Graphic system.
//  This replaces the Image-based rings in EchoesLoadingScreen
//  and gives you clean anti-aliased ellipse outlines matching
//  the logo exactly.
//
//  Usage (called automatically by EchoesLoadingScreen):
//      var ring = go.AddComponent<EchoesRingRenderer>();
//      ring.radiusX     = 80f;
//      ring.radiusY     = 28f;
//      ring.strokeWidth = 2.2f;
//      ring.color       = new Color(...);
//      ring.segments    = 80;
// ============================================================

[RequireComponent(typeof(CanvasRenderer))]
public class EchoesRingRenderer : Graphic
{
    public float radiusX     = 80f;
    public float radiusY     = 28f;
    public float strokeWidth = 2f;
    public int   segments    = 80;

    protected override void OnPopulateMesh(VertexHelper vh)
    {
        vh.Clear();

        float angleStep = 2f * Mathf.PI / segments;

        for (int i = 0; i < segments; i++)
        {
            float a0 = i       * angleStep;
            float a1 = (i + 1) * angleStep;

            // Outer ellipse
            Vector2 outerA = new Vector2(Mathf.Cos(a0) * (radiusX + strokeWidth * 0.5f),
                                          Mathf.Sin(a0) * (radiusY + strokeWidth * 0.5f));
            Vector2 outerB = new Vector2(Mathf.Cos(a1) * (radiusX + strokeWidth * 0.5f),
                                          Mathf.Sin(a1) * (radiusY + strokeWidth * 0.5f));
            // Inner ellipse
            Vector2 innerA = new Vector2(Mathf.Cos(a0) * (radiusX - strokeWidth * 0.5f),
                                          Mathf.Sin(a0) * (radiusY - strokeWidth * 0.5f));
            Vector2 innerB = new Vector2(Mathf.Cos(a1) * (radiusX - strokeWidth * 0.5f),
                                          Mathf.Sin(a1) * (radiusY - strokeWidth * 0.5f));

            int baseIdx = i * 4;

            UIVertex v = UIVertex.simpleVert;
            v.color = color;

            v.position = outerA; vh.AddVert(v);
            v.position = outerB; vh.AddVert(v);
            v.position = innerB; vh.AddVert(v);
            v.position = innerA; vh.AddVert(v);

            vh.AddTriangle(baseIdx,     baseIdx + 1, baseIdx + 2);
            vh.AddTriangle(baseIdx,     baseIdx + 2, baseIdx + 3);
        }
    }

#if UNITY_EDITOR
    protected override void OnValidate()
    {
        base.OnValidate();
        SetVerticesDirty();
    }
#endif
}
