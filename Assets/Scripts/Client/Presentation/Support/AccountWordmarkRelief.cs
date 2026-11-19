#if !UNITY_SERVER || UNITY_EDITOR
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace VaticanDefendor.Client.Presentation
{
    [RequireComponent(typeof(Text))]
    internal sealed class AccountWordmarkRelief : BaseMeshEffect
    {
        private static readonly Color32 Outline = new Color32(45, 24, 18, 255);
        private static readonly Color32 Shadow = new Color32(21, 12, 13, 225);
        private static readonly Color32 Bronze = new Color32(150, 83, 42, 255);
        private static readonly Color32 DarkBronze = new Color32(76, 40, 26, 255);
        private static readonly Color32 Rim = new Color32(255, 237, 191, 255);
        private static readonly Color32 Gold = new Color32(255, 219, 161, 255);
        private static readonly Color32 GoldShade = new Color32(189, 121, 60, 255);

        private readonly List<UIVertex> _glyphs = new();

        public override void ModifyMesh(VertexHelper mesh)
        {
            if (!IsActive() || mesh.currentVertCount == 0)
            {
                return;
            }

            _glyphs.Clear();
            mesh.GetUIVertexStream(_glyphs);
            if (_glyphs.Count == 0)
            {
                return;
            }
            float bottom = _glyphs[0].position.y;
            float top = bottom;
            foreach (UIVertex vertex in _glyphs)
            {
                bottom = Mathf.Min(bottom, vertex.position.y);
                top = Mathf.Max(top, vertex.position.y);
            }
            int lineCount = Mathf.Max(1, ((Text)graphic).cachedTextGenerator.lineCount);
            float depth = (top - bottom) * 0.095f / lineCount;
            var range = new Vector2(bottom, top);
            mesh.Clear();

            AppendLayer(mesh, new Vector2(depth * 0.8f, -depth * 1.45f), range, Shadow, Shadow);
            AppendLayer(mesh, new Vector2(depth * -0.3f, depth * 0.35f), range, Outline, Outline);
            AppendLayer(mesh, new Vector2(depth * 0.35f, depth * 0.3f), range, Outline, Outline);
            AppendLayer(mesh, new Vector2(depth * -0.28f, -depth), range, Outline, Outline);
            AppendLayer(mesh, new Vector2(depth * 0.72f, -depth), range, Outline, Outline);
            const int steps = 5;
            for (int step = steps; step > 0; step--)
            {
                float offset = depth * step / steps;
                AppendLayer(mesh, new Vector2(offset * 0.48f, -offset), range, Bronze, DarkBronze);
            }
            AppendLayer(mesh, new Vector2(-depth * 0.15f, depth * 0.19f), range, Rim, Bronze);
            AppendLayer(mesh, Vector2.zero, range, Gold, GoldShade);
        }

        private void AppendLayer(VertexHelper mesh, Vector2 offset, Vector2 range, Color32 light, Color32 shade)
        {
            int start = mesh.currentVertCount;
            foreach (UIVertex original in _glyphs)
            {
                UIVertex vertex = original;
                float height = Mathf.InverseLerp(range.x, range.y, original.position.y);
                Color tint = Color.Lerp(shade, light, height);
                tint.a *= original.color.a / 255f;
                vertex.color = tint;
                vertex.position.x += offset.x;
                vertex.position.y += offset.y;
                mesh.AddVert(vertex);
            }
            for (int index = 0; index < _glyphs.Count; index += 3)
            {
                mesh.AddTriangle(start + index, start + index + 1, start + index + 2);
            }
        }
    }
}
#endif
