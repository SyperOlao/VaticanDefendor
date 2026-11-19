#if !UNITY_SERVER || UNITY_EDITOR
using UnityEngine;
using UnityEngine.UI;
using VaticanDefendor.Client.Presentation;

namespace VaticanDefendor.Client.Shell
{
    [RequireComponent(typeof(CanvasRenderer))]
    internal sealed class WeaponWheelSectorGraphic : MaskableGraphic
    {
        private float _centerAngle;
        private float _sweep;
        private bool _selected;

        public void Configure(float centerAngle, float sweep, bool selected)
        {
            if (_centerAngle == centerAngle && _sweep == sweep && _selected == selected)
            {
                return;
            }

            _centerAngle = centerAngle;
            _sweep = sweep;
            _selected = selected;
            SetVerticesDirty();
        }

        public override bool Raycast(Vector2 screenPoint, Camera eventCamera)
        {
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(rectTransform, screenPoint, eventCamera, out Vector2 point))
            {
                return false;
            }

            Rect rect = rectTransform.rect;
            Vector2 offset = point - rect.center;
            float radius = Mathf.Min(rect.width, rect.height) * 0.5f;
            float angle = Mathf.Atan2(offset.x, offset.y) * Mathf.Rad2Deg;
            return offset.sqrMagnitude <= radius * radius &&
                (_sweep >= 360 || Mathf.Abs(Mathf.DeltaAngle(_centerAngle, angle)) <= _sweep * 0.5f) &&
                base.Raycast(screenPoint, eventCamera);
        }

        protected override void OnPopulateMesh(VertexHelper mesh)
        {
            mesh.Clear();
            if (_sweep <= 0)
            {
                return;
            }

            Rect rect = rectTransform.rect;
            Vector2 center = rect.center;
            float radius = Mathf.Min(rect.width, rect.height) * 0.5f;
            float faceRadius = radius - Mathf.Min(8, radius * 0.24f);
            float bevelRadius = radius - Mathf.Min(4, radius * 0.12f);
            float edgeRadius = radius - Mathf.Min(2, radius * 0.06f);
            Color faceLight = _selected ? new Color32(151, 70, 67, 255) : new Color32(89, 69, 62, 255);
            Color faceShade = _selected ? new Color32(62, 29, 38, 255) : new Color32(35, 27, 31, 255);
            Color rimLight = _selected ? new Color32(255, 216, 153, 255) : new Color32(178, 145, 108, 255);
            Color rimShade = new Color32(126, 77, 50, 255);
            Color shadow = new Color32(35, 22, 26, 255);
            int steps = Mathf.CeilToInt(_sweep / 5);
            float start = _centerAngle - _sweep * 0.5f;

            mesh.AddVert(center, Color.Lerp(faceShade, faceLight, 0.52f), Vector2.zero);
            for (int step = 0; step <= steps; step++)
            {
                Vector2 direction = Direction(start + _sweep * step / steps);
                float light = (direction.y + 1) * 0.5f;
                mesh.AddVert(center + direction * faceRadius, Color.Lerp(faceShade, faceLight, light), Vector2.zero);
                mesh.AddVert(center + direction * bevelRadius, Color.Lerp(rimShade, rimLight, light), Vector2.zero);
                mesh.AddVert(center + direction * edgeRadius, Color.Lerp(rimShade, rimLight, light * 0.65f), Vector2.zero);
                mesh.AddVert(center + direction * radius, shadow, Vector2.zero);

                if (step > 0)
                {
                    int previous = 1 + (step - 1) * 4;
                    int next = previous + 4;
                    mesh.AddTriangle(0, previous, next);
                    for (int ring = 0; ring < 3; ring++)
                    {
                        int first = previous + ring;
                        int second = next + ring;
                        mesh.AddTriangle(first, first + 1, second + 1);
                        mesh.AddTriangle(first, second + 1, second);
                    }
                }
            }

            if (_sweep < 360)
            {
                Vector2 direction = Direction(start);
                Color32 divider = SiegeUiTheme.Line;
                int index = mesh.currentVertCount;
                mesh.AddVert(center, divider, Vector2.zero);
                mesh.AddVert(center + direction * edgeRadius, divider, Vector2.zero);
                mesh.AddVert(center + Direction(start + 1) * edgeRadius, divider, Vector2.zero);
                mesh.AddTriangle(index, index + 1, index + 2);
            }
        }

        public static Vector2 Direction(float angle)
        {
            float radians = angle * Mathf.Deg2Rad;
            return new Vector2(Mathf.Sin(radians), Mathf.Cos(radians));
        }
    }
}
#endif
