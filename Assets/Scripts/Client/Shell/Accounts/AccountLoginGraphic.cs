#if !UNITY_SERVER || UNITY_EDITOR
using UnityEngine;
using UnityEngine.UI;
using VaticanDefendor.Client.Presentation;

namespace VaticanDefendor.Client.Accounts
{
    [RequireComponent(typeof(CanvasRenderer))]
    internal sealed class AccountLoginGraphic : MaskableGraphic
    {
        internal void Configure(Color tint)
        {
            color = tint;
            raycastTarget = false;
            SetVerticesDirty();
        }

        protected override void OnPopulateMesh(VertexHelper vertices)
        {
            vertices.Clear();
            Rect bounds = rectTransform.rect;
            float cut = Mathf.Min(24, bounds.width * 0.25f, bounds.height * 0.25f);
            Vector2[] outline =
            {
                new(bounds.xMin, bounds.yMin),
                new(bounds.xMin, bounds.yMax - cut),
                new(bounds.xMin + cut, bounds.yMax),
                new(bounds.xMax, bounds.yMax),
                new(bounds.xMax, bounds.yMin + cut),
                new(bounds.xMax - cut, bounds.yMin)
            };
            Color shade = color * new Color(0.87f, 0.87f, 0.89f, 1);
            foreach (Vector2 point in outline)
            {
                vertices.AddVert(point, Color.Lerp(shade, color, Mathf.InverseLerp(bounds.yMin, bounds.yMax, point.y)), Vector2.zero);
            }
            for (int index = 1; index < outline.Length - 1; index++)
            {
                vertices.AddTriangle(0, index, index + 1);
            }
            for (int index = 0; index < outline.Length; index++)
            {
                Stroke(vertices, outline[index], outline[(index + 1) % outline.Length], 1.35f, SiegeUiTheme.Line);
            }
            Stroke(vertices, outline[1], outline[2], 2, SiegeUiTheme.Brass);
            Stroke(vertices, outline[2], outline[2] + 74 * Vector2.right, 2, SiegeUiTheme.Brass);
        }

        internal static void Stroke(VertexHelper vertices, Vector2 start, Vector2 end, float width, Color tint)
        {
            Vector2 offset = Vector2.Perpendicular(end - start).normalized * (width * 0.5f);
            int first = vertices.currentVertCount;
            vertices.AddVert(start - offset, tint, Vector2.zero);
            vertices.AddVert(start + offset, tint, Vector2.zero);
            vertices.AddVert(end + offset, tint, Vector2.zero);
            vertices.AddVert(end - offset, tint, Vector2.zero);
            vertices.AddTriangle(first, first + 1, first + 2);
            vertices.AddTriangle(first, first + 2, first + 3);
        }
    }
}
#endif
