#if !UNITY_SERVER || UNITY_EDITOR
using UnityEngine;
using UnityEngine.UI;

namespace VaticanDefendor.Client.Presentation
{
    [RequireComponent(typeof(CanvasRenderer))]
    internal sealed class ConnectionSpinner : MaskableGraphic
    {
        private const int Segments = 56;
        private const int HighlightSegments = 39;

        protected override void Awake()
        {
            base.Awake();
            raycastTarget = false;
        }

        protected override void OnPopulateMesh(VertexHelper mesh)
        {
            mesh.Clear();
            Rect rect = rectTransform.rect;
            float radius = Mathf.Min(rect.width, rect.height) * 0.46f;
            Color track = SiegeUiTheme.WithAlpha(SiegeUiTheme.Brass, 0.22f);
            for (int index = 0; index < Segments; index++)
            {
                Segment(mesh, rect.center, radius, 2.8f, index, track, track);
            }
            for (int index = 0; index < HighlightSegments; index++)
            {
                float start = index / (float)HighlightSegments;
                float end = (index + 1) / (float)HighlightSegments;
                Color low = SiegeUiTheme.WithAlpha(SiegeUiTheme.Brass, start);
                Color high = SiegeUiTheme.WithAlpha(SiegeUiTheme.Brass, end);
                Segment(mesh, rect.center, radius, 4.2f, index, low, high);
            }
        }

        private static void Segment(VertexHelper mesh, Vector2 center, float radius, float thickness, int index, Color start, Color end)
        {
            float step = -2f * Mathf.PI / Segments;
            float first = index * step;
            float next = (index + 1) * step;
            var a = new Vector2(Mathf.Cos(first), Mathf.Sin(first));
            var b = new Vector2(Mathf.Cos(next), Mathf.Sin(next));
            int vertex = mesh.currentVertCount;
            mesh.AddVert(center + a * radius, start, Vector2.zero);
            mesh.AddVert(center + a * (radius - thickness), start, Vector2.zero);
            mesh.AddVert(center + b * (radius - thickness), end, Vector2.zero);
            mesh.AddVert(center + b * radius, end, Vector2.zero);
            mesh.AddTriangle(vertex, vertex + 1, vertex + 2);
            mesh.AddTriangle(vertex, vertex + 2, vertex + 3);
        }
    }
}
#endif
