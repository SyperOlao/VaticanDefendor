using UnityEngine;
using UnityEngine.UI;
[RequireComponent(typeof(CanvasRenderer))]
public class SpecialReadyEdgeEffect : MaskableGraphic
{
    public bool bParticles = true;
    private bool bReady;
    private float Opacity;
    public void SetReady(bool bValue, float Strength)
    {
        bReady = bValue;
        Opacity = Strength;
        enabled = bReady;
        if (bReady)
        {
            SetVerticesDirty();
        }
    }
    protected override void OnPopulateMesh(VertexHelper Vertices)
    {
        Vertices.Clear();
        if (!bReady)
        {
            return;
        }
        Rect Area = rectTransform.rect;
        float Width = Mathf.Min(Area.width * 0.14f, 52);
        float Alpha = Opacity * (0.82f + 0.18f * Mathf.Sin(Time.unscaledTime * 2.4f));
        Color Edge = new Color(1, 0.12f, 0.035f, Alpha);
        Color Clear = new Color(1, 0.2f, 0.04f, 0);
        Strip(Vertices, new Vector2(Area.xMin, Area.yMin), new Vector2(Area.xMin, Area.yMax), new Vector2(Area.xMin + Width, Area.yMax), new Vector2(Area.xMin + Width, Area.yMin), Edge, Clear);
        Strip(Vertices, new Vector2(Area.xMax, Area.yMax), new Vector2(Area.xMax, Area.yMin), new Vector2(Area.xMax - Width, Area.yMin), new Vector2(Area.xMax - Width, Area.yMax), Edge, Clear);
        Strip(Vertices, new Vector2(Area.xMin, Area.yMax), new Vector2(Area.xMax, Area.yMax), new Vector2(Area.xMax, Area.yMax - Width), new Vector2(Area.xMin, Area.yMax - Width), Edge, Clear);
        Strip(Vertices, new Vector2(Area.xMax, Area.yMin), new Vector2(Area.xMin, Area.yMin), new Vector2(Area.xMin, Area.yMin + Width), new Vector2(Area.xMax, Area.yMin + Width), Edge, Clear);
        for (int Index = 0; bParticles && Index < 18; Index++)
        {
            float Progress = Mathf.Repeat(Time.unscaledTime * (0.07f + Index * 0.002f) + Index * 0.618f, 1);
            float Horizontal = Area.xMin + 5 + Mathf.Repeat(Index * 13.7f, Width * 0.55f);
            if (Index % 2 == 0)
            {
                Horizontal = Area.xMax - 5 - Mathf.Repeat(Index * 11.3f, Width * 0.55f);
            }
            Vector2 Center = new Vector2(Horizontal, Mathf.Lerp(Area.yMin, Area.yMax, Progress));
            Color Spark = new Color(1, 0.42f, 0.1f, Alpha * Mathf.Sin(Progress * Mathf.PI));
            int Start = Vertices.currentVertCount;
            Vertices.AddVert(Center + Vector2.up * 3, Spark, Vector2.zero);
            Vertices.AddVert(Center + Vector2.right * 1.3f, Spark, Vector2.zero);
            Vertices.AddVert(Center - Vector2.up * 3, Spark, Vector2.zero);
            Vertices.AddVert(Center - Vector2.right * 1.3f, Spark, Vector2.zero);
            Vertices.AddTriangle(Start, Start + 1, Start + 2);
            Vertices.AddTriangle(Start, Start + 2, Start + 3);
        }
    }
    private void Strip(VertexHelper Vertices, Vector2 First, Vector2 Second, Vector2 Third, Vector2 Fourth, Color Edge, Color Clear)
    {
        int Start = Vertices.currentVertCount;
        Vertices.AddVert(First, Edge, Vector2.zero);
        Vertices.AddVert(Second, Edge, Vector2.zero);
        Vertices.AddVert(Third, Clear, Vector2.zero);
        Vertices.AddVert(Fourth, Clear, Vector2.zero);
        Vertices.AddTriangle(Start, Start + 1, Start + 2);
        Vertices.AddTriangle(Start, Start + 2, Start + 3);
    }
}
