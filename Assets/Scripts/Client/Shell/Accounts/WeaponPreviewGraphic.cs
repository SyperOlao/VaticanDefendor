#if !UNITY_SERVER || UNITY_EDITOR
using UnityEngine;

namespace VaticanDefendor.Client.Accounts
{
    [RequireComponent(typeof(CanvasRenderer))]
    public sealed class WeaponPreviewGraphic : UnityEngine.UI.MaskableGraphic
    {
        private int ModelIndex;
        private Color AccentColor;

        public void Configure(int WeaponModelIndex, Color WeaponAccentColor)
        {
            ModelIndex = WeaponModelIndex;
            AccentColor = WeaponAccentColor;
            raycastTarget = false;
            SetVerticesDirty();
        }

        protected override void OnPopulateMesh(UnityEngine.UI.VertexHelper Vertices)
        {
            Vertices.Clear();
            Color Steel = new Color(0.74f, 0.83f, 0.85f);
            Color Edge = new Color(0.94f, 0.96f, 0.90f);
            Color Shadow = new Color(0.26f, 0.38f, 0.43f);
            Color Leather = new Color(0.35f, 0.22f, 0.16f);
            Color Brass = new Color(0.70f, 0.51f, 0.27f);
            Polygon(Vertices, new[] { new Vector2(-4, -65), new Vector2(4, -65), new Vector2(4, 65), new Vector2(-4, 65) }, Leather);
            if (ModelIndex == 2)
            {
                Polygon(Vertices, new[] { new Vector2(-3, 19), new Vector2(-37, 12), new Vector2(-43, 25), new Vector2(-42, 47), new Vector2(-32, 60), new Vector2(-3, 48) }, Steel);
                Polygon(Vertices, new[] { new Vector2(-37, 12), new Vector2(-43, 25), new Vector2(-42, 47), new Vector2(-32, 60), new Vector2(-35, 39), new Vector2(-35, 24) }, Edge);
                Polygon(Vertices, new[] { new Vector2(3, 20), new Vector2(24, 27), new Vector2(28, 48), new Vector2(3, 47) }, Shadow);
            }
            else if (ModelIndex == 1)
            {
                Polygon(Vertices, new[] { new Vector2(0, 84), new Vector2(-15, 42), new Vector2(0, 30) }, Edge);
                Polygon(Vertices, new[] { new Vector2(0, 84), new Vector2(15, 42), new Vector2(0, 30) }, Shadow);
                Rectangle(Vertices, new Vector2(-7, 25), new Vector2(7, 32), Brass);
            }
            else
            {
                Polygon(Vertices, new[] { new Vector2(-10, -14), new Vector2(-10, 51), new Vector2(0, 78), new Vector2(0, -19) }, Edge);
                Polygon(Vertices, new[] { new Vector2(0, -19), new Vector2(0, 78), new Vector2(10, 51), new Vector2(10, -14) }, Steel);
                Rectangle(Vertices, new Vector2(-25, -24), new Vector2(25, -17), Brass);
                Polygon(Vertices, new[] { new Vector2(-25, -24), new Vector2(-29, -31), new Vector2(-27, -15), new Vector2(-21, -17) }, Brass);
                Polygon(Vertices, new[] { new Vector2(25, -24), new Vector2(29, -31), new Vector2(27, -15), new Vector2(21, -17) }, Brass);
                Rectangle(Vertices, new Vector2(-6, -56), new Vector2(6, -25), Leather);
                for (int BindingIndex = 0; BindingIndex < 4; BindingIndex++)
                {
                    Rectangle(Vertices, new Vector2(-6, -30 - BindingIndex * 7), new Vector2(6, -28 - BindingIndex * 7), Brass * 0.7f);
                }
                Polygon(Vertices, new[] { new Vector2(0, -71), new Vector2(-9, -62), new Vector2(0, -53), new Vector2(9, -62) }, Brass);
                Polygon(Vertices, new[] { new Vector2(0, -66), new Vector2(-4, -62), new Vector2(0, -58), new Vector2(4, -62) }, AccentColor);
            }
        }

        private void Rectangle(UnityEngine.UI.VertexHelper Vertices, Vector2 Minimum, Vector2 Maximum, Color Tint)
        {
            Polygon(Vertices, new[] { Minimum, new Vector2(Minimum.x, Maximum.y), Maximum, new Vector2(Maximum.x, Minimum.y) }, Tint);
        }

        private void Polygon(UnityEngine.UI.VertexHelper Vertices, Vector2[] Points, Color Tint)
        {
            int StartIndex = Vertices.currentVertCount;
            float Scale = Mathf.Min(rectTransform.rect.width / 110, rectTransform.rect.height / 180);
            foreach (Vector2 Point in Points)
            {
                Vertices.AddVert(new Vector3(Point.x * Scale, Point.y * Scale), Tint, Vector2.zero);
            }
            for (int TriangleIndex = 1; TriangleIndex < Points.Length - 1; TriangleIndex++)
            {
                Vertices.AddTriangle(StartIndex, StartIndex + TriangleIndex, StartIndex + TriangleIndex + 1);
            }
        }
    }
}
#endif
