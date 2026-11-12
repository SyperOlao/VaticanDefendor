#if !UNITY_SERVER || UNITY_EDITOR
using UnityEngine;
using UnityEngine.UI;
using VaticanDefendor;

public sealed class ResourceIconGraphic : MaskableGraphic
{
    private ResourceKind Kind;

    public void SetKind(ResourceKind Value)
    {
        Kind = Value;
        raycastTarget = false;
        SetVerticesDirty();
    }

    protected override void OnPopulateMesh(VertexHelper Vertices)
    {
        Vertices.Clear();
        if (Kind == ResourceKind.Wood)
        {
            for (int Board = 0; Board < 3; Board++)
            {
                float Vertical = (Board - 1) * 0.25f;
                Polygon(Vertices, new Color(0.23f, 0.12f, 0.06f),
                    new Vector2(-0.46f, Vertical - 0.10f), new Vector2(0.32f, Vertical - 0.15f),
                    new Vector2(0.47f, Vertical + 0.04f), new Vector2(-0.31f, Vertical + 0.11f));
                Polygon(Vertices, new Color(0.83f, 0.58f, 0.29f),
                    new Vector2(-0.39f, Vertical - 0.04f), new Vector2(0.29f, Vertical - 0.09f),
                    new Vector2(0.38f, Vertical + 0.02f), new Vector2(-0.29f, Vertical + 0.06f));
                Polygon(Vertices, new Color(0.48f, 0.29f, 0.13f),
                    new Vector2(-0.23f, Vertical), new Vector2(0.24f, Vertical - 0.04f),
                    new Vector2(0.26f, Vertical - 0.015f), new Vector2(-0.20f, Vertical + 0.025f));
            }
        }
        else if (Kind == ResourceKind.Stone)
        {
            Polygon(Vertices, new Color(0.20f, 0.23f, 0.25f), new Vector2(-0.47f, -0.19f),
                new Vector2(-0.30f, 0.24f), new Vector2(0.10f, 0.43f), new Vector2(0.41f, 0.21f),
                new Vector2(0.47f, -0.25f), new Vector2(0.12f, -0.42f));
            Polygon(Vertices, new Color(0.73f, 0.77f, 0.76f), new Vector2(-0.39f, -0.15f),
                new Vector2(-0.25f, 0.19f), new Vector2(0.08f, 0.35f), new Vector2(0.33f, 0.17f), new Vector2(0.03f, -0.03f));
            Polygon(Vertices, new Color(0.46f, 0.53f, 0.55f), new Vector2(-0.39f, -0.15f),
                new Vector2(0.03f, -0.03f), new Vector2(0.38f, -0.22f), new Vector2(0.10f, -0.33f));
        }
        else if (Kind == ResourceKind.Iron)
        {
            Polygon(Vertices, new Color(0.12f, 0.19f, 0.26f), new Vector2(-0.48f, -0.18f),
                new Vector2(-0.29f, 0.24f), new Vector2(0.25f, 0.35f), new Vector2(0.47f, 0.06f),
                new Vector2(0.35f, -0.31f), new Vector2(-0.23f, -0.39f));
            Polygon(Vertices, new Color(0.77f, 0.88f, 0.94f), new Vector2(-0.36f, -0.10f),
                new Vector2(-0.23f, 0.18f), new Vector2(0.21f, 0.27f), new Vector2(0.36f, 0.06f));
            Polygon(Vertices, new Color(0.39f, 0.58f, 0.70f), new Vector2(-0.36f, -0.10f),
                new Vector2(0.36f, 0.06f), new Vector2(0.28f, -0.24f), new Vector2(-0.20f, -0.31f));
        }
    }

    private void Polygon(VertexHelper Vertices, Color Tint, params Vector2[] Points)
    {
        int Start = Vertices.currentVertCount;
        Rect Bounds = rectTransform.rect;
        float Size = Mathf.Min(Bounds.width, Bounds.height);
        foreach (Vector2 Point in Points)
        {
            Vertices.AddVert(Bounds.center + Point * Size, Tint * color, Vector2.zero);
        }
        for (int PointIndex = 1; PointIndex < Points.Length - 1; PointIndex++)
        {
            Vertices.AddTriangle(Start, Start + PointIndex, Start + PointIndex + 1);
        }
    }
}
#endif
