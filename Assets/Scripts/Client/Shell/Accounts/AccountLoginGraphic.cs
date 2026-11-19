#if !UNITY_SERVER || UNITY_EDITOR
using UnityEngine;
using UnityEngine.UI;
using VaticanDefendor.Client.Presentation;

namespace VaticanDefendor.Client.Accounts
{
    [RequireComponent(typeof(CanvasRenderer))]
    internal sealed class AccountLoginGraphic : MaskableGraphic
    {
        internal enum Shape { Frame, Submit, Seal, Profile, Lock, Arrow }

        private Shape _shape;

        internal void Configure(Shape shape, Color tint)
        {
            _shape = shape;
            color = tint;
            raycastTarget = false;
            SetVerticesDirty();
        }

        protected override void OnPopulateMesh(VertexHelper vertices)
        {
            vertices.Clear();
            Rect bounds = rectTransform.rect;
            if (_shape == Shape.Frame || _shape == Shape.Submit)
            {
                DrawPlate(vertices, bounds, _shape == Shape.Frame);
                return;
            }

            float unit = Mathf.Min(bounds.width, bounds.height) / 64;
            switch (_shape)
            {
                case Shape.Seal:
                    DrawPath(vertices, bounds.center, unit, 1.7f,
                        new(-25, 23), new(0, 29), new(25, 23), new(25, -4),
                        new(17, -20), new(0, -29), new(-17, -20), new(-25, -4), new(-25, 23));
                    DrawPath(vertices, bounds.center, unit, 1.7f,
                        new(-14, -12), new(-14, 4), new(0, 16), new(14, 4), new(14, -12), new(-14, -12));
                    DrawPath(vertices, bounds.center, unit, 1.7f, new(-5, -12), new(-5, -2), new(0, 3), new(5, -2), new(5, -12));
                    DrawPath(vertices, bounds.center, unit, 1.4f, new(-9, -18), new(9, -18));
                    break;
                case Shape.Profile:
                    DrawPath(vertices, bounds.center, unit, 3,
                        new(-10, 12), new(-7, 20), new(0, 23), new(7, 20), new(10, 12),
                        new(7, 4), new(0, 1), new(-7, 4), new(-10, 12));
                    DrawPath(vertices, bounds.center, unit, 3,
                        new(-21, -18), new(-18, -10), new(-10, -5), new(10, -5), new(18, -10), new(21, -18));
                    break;
                case Shape.Lock:
                    DrawPath(vertices, bounds.center, unit, 3, new(-11, 2), new(-11, 14), new(-6, 20), new(6, 20), new(11, 14), new(11, 2));
                    DrawPath(vertices, bounds.center, unit, 3, new(-17, 2), new(17, 2), new(17, -20), new(-17, -20), new(-17, 2));
                    DrawPath(vertices, bounds.center, unit, 3, new(0, -5), new(0, -13));
                    break;
                case Shape.Arrow:
                    DrawPath(vertices, bounds.center, unit, 4, new(-22, 0), new(18, 0));
                    DrawPath(vertices, bounds.center, unit, 4, new(6, 12), new(18, 0), new(6, -12));
                    break;
            }
        }

        private void DrawPlate(VertexHelper vertices, Rect bounds, bool frame)
        {
            float cut = Mathf.Min(frame ? 24 : 14, bounds.width * 0.25f, bounds.height * 0.25f);
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
            if (!frame)
            {
                return;
            }
            for (int index = 0; index < outline.Length; index++)
            {
                Stroke(vertices, outline[index], outline[(index + 1) % outline.Length], 1.35f, SiegeUiTheme.Line);
            }
            Stroke(vertices, outline[1], outline[2], 2, SiegeUiTheme.Brass);
            Stroke(vertices, outline[2], outline[2] + 74 * Vector2.right, 2, SiegeUiTheme.Brass);
        }

        private void DrawPath(VertexHelper vertices, Vector2 origin, float unit, float width, params Vector2[] points)
        {
            for (int index = 1; index < points.Length; index++)
            {
                Stroke(vertices, origin + unit * points[index - 1], origin + unit * points[index], unit * width, color);
            }
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
