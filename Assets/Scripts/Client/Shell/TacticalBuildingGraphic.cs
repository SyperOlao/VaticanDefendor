#if !UNITY_SERVER || UNITY_EDITOR
using System.Collections.Generic;
using UnityEngine;
using VaticanDefendor.Construction;

namespace VaticanDefendor.Client.Shell
{
    public sealed class TacticalBuildingGraphic : UnityEngine.UI.MaskableGraphic
    {
        private const float BadgeSize = 26;
        private const float BadgeSpacing = BadgeSize + 3;
        private static readonly Color32 Background = new(20, 29, 28, 250);
        private static readonly Vector2[] BadgeCorners =
        {
            new(-0.58f, -1), new(0.58f, -1), new(1, -0.58f), new(1, 0.58f),
            new(0.58f, 1), new(-0.58f, 1), new(-1, 0.58f), new(-1, -0.58f)
        };

        private readonly struct Marker
        {
            public readonly int Identifier;
            public readonly StructureKind Kind;
            public readonly Vector2 Anchor;
            public readonly Vector2 WallEnd;

            public Marker(int Identifier, StructureKind Kind, Vector2 Anchor, Vector2 WallEnd)
            {
                this.Identifier = Identifier;
                this.Kind = Kind;
                this.Anchor = Anchor;
                this.WallEnd = WallEnd;
            }

            public bool Matches(Marker Other)
            {
                return Identifier == Other.Identifier && Kind == Other.Kind &&
                    Anchor.Equals(Other.Anchor) && WallEnd.Equals(Other.WallEnd);
            }
        }

        private List<Marker> _visible = new();
        private List<Marker> _projected = new();
        private readonly List<Rect> _deposits = new();
        private readonly List<Rect> _occupied = new();

        public void Render(Camera Map, ClientStructureSnapshot[] Structures, IReadOnlyList<Rect> Deposits)
        {
            _projected.Clear();
            if (Structures != null)
            {
                foreach (ClientStructureSnapshot Structure in Structures)
                {
                    if (Structure.StructureId <= 0 || Structure.Health <= 0 || !StructureActionRules.CanInteract(Structure.Kind))
                    {
                        continue;
                    }
                    Vector3 Center = Map.WorldToViewportPoint(Structure.Position);
                    if (Center.z <= 0 || Center.x < 0 || Center.x > 1 || Center.y < 0 || Center.y > 1)
                    {
                        continue;
                    }
                    Vector2 Begin = Center;
                    Vector2 End = Center;
                    if (Structure.Kind == StructureKind.Wall)
                    {
                        float Length = ConstructionGeometry.HalfExtents(Structure.Kind, Structure.HalfExtent).y;
                        Vector3 Offset = new Vector3(Mathf.Sin(Structure.RotationRadians), 0, Mathf.Cos(Structure.RotationRadians)) * Length;
                        Begin = Map.WorldToViewportPoint(Structure.Position - Offset);
                        End = Map.WorldToViewportPoint(Structure.Position + Offset);
                    }
                    _projected.Add(new Marker(Structure.StructureId, Structure.Kind, Begin, End));
                }
            }
            bool bChanged = _visible.Count != _projected.Count || _deposits.Count != Deposits.Count;
            for (int Index = 0; !bChanged && Index < _projected.Count; Index++)
            {
                bChanged = !_visible[Index].Matches(_projected[Index]);
            }
            for (int Index = 0; !bChanged && Index < Deposits.Count; Index++)
            {
                bChanged = !_deposits[Index].Equals(Deposits[Index]);
            }
            if (bChanged)
            {
                (_visible, _projected) = (_projected, _visible);
                _deposits.Clear();
                _deposits.AddRange(Deposits);
                SetVerticesDirty();
            }
        }

        protected override void OnPopulateMesh(UnityEngine.UI.VertexHelper Vertices)
        {
            Vertices.Clear();
            Rect Bounds = rectTransform.rect;
            if (Bounds.width < BadgeSize || Bounds.height < BadgeSize)
            {
                return;
            }
            _occupied.Clear();
            _occupied.AddRange(_deposits);
            foreach (Marker Marker in _visible)
            {
                if (Marker.Kind == StructureKind.Wall)
                {
                    Vector2 Start = Bounds.min + Vector2.Scale(Marker.Anchor, Bounds.size);
                    Vector2 End = Bounds.min + Vector2.Scale(Marker.WallEnd, Bounds.size);
                    Line(Vertices, Start, End, 5, Background);
                    Line(Vertices, Start, End, 2.5f, new Color32(220, 225, 207, 255));
                }
            }
            foreach (Marker Marker in _visible)
            {
                if (Marker.Kind == StructureKind.Church)
                {
                    DrawBuilding(Vertices, Bounds, Marker);
                }
            }
            foreach (Marker Marker in _visible)
            {
                if (Marker.Kind != StructureKind.Wall && Marker.Kind != StructureKind.Church)
                {
                    DrawBuilding(Vertices, Bounds, Marker);
                }
            }
        }

        private void DrawBuilding(UnityEngine.UI.VertexHelper Vertices, Rect Bounds, Marker Marker)
        {
            Vector2 Anchor = Bounds.min + Vector2.Scale(Marker.Anchor, Bounds.size);
            Color32 Tint = IconColor(Marker.Kind);
            if (!TryPlace(Anchor, Bounds, out Vector2 Center))
            {
                Rectangle(Vertices, Anchor, new Vector2(6, 6), Background);
                Rectangle(Vertices, Anchor, new Vector2(3, 3), Tint);
                return;
            }
            if ((Center - Anchor).sqrMagnitude > BadgeSize * BadgeSize * 0.25f)
            {
                Line(Vertices, Anchor, Center, 3, Background);
                Line(Vertices, Anchor, Center, 1.5f, Tint);
                Rectangle(Vertices, Anchor, new Vector2(3, 3), Tint);
            }
            Badge(Vertices, Center, BadgeSize * 0.5f, Tint);
            Badge(Vertices, Center, BadgeSize * 0.5f - 1.5f, Background);
            DrawIcon(Vertices, Center, Tint, Marker.Kind);
        }

        private bool TryPlace(Vector2 Anchor, Rect Bounds, out Vector2 Center)
        {
            Vector2 HalfSize = Vector2.one * (BadgeSize * 0.5f);
            Vector2 Minimum = Bounds.min + HalfSize;
            Vector2 Maximum = Bounds.max - HalfSize;
            float BestDistance = float.PositiveInfinity;
            Center = Anchor;
            for (int Vertical = -2; Vertical <= 2; Vertical++)
            {
                for (int Horizontal = -2; Horizontal <= 2; Horizontal++)
                {
                    Vector2 Candidate = Anchor + new Vector2(Horizontal, Vertical) * BadgeSpacing;
                    Candidate.x = Mathf.Clamp(Candidate.x, Minimum.x, Maximum.x);
                    Candidate.y = Mathf.Clamp(Candidate.y, Minimum.y, Maximum.y);
                    float Distance = (Candidate - Anchor).sqrMagnitude;
                    if (Distance >= BestDistance)
                    {
                        continue;
                    }
                    Rect Space = new Rect(Candidate - HalfSize - Vector2.one, HalfSize * 2 + Vector2.one * 2);
                    bool bFree = true;
                    foreach (Rect Other in _occupied)
                    {
                        if (Space.Overlaps(Other))
                        {
                            bFree = false;
                            break;
                        }
                    }
                    if (bFree)
                    {
                        BestDistance = Distance;
                        Center = Candidate;
                    }
                }
            }
            if (float.IsPositiveInfinity(BestDistance))
            {
                return false;
            }
            _occupied.Add(new Rect(Center - HalfSize, HalfSize * 2));
            return true;
        }

        private static Color32 IconColor(StructureKind Kind)
        {
            switch (Kind)
            {
                case StructureKind.Church: return new Color32(252, 218, 143, 255);
                case StructureKind.Tower: return new Color32(167, 210, 239, 255);
                case StructureKind.Sawmill: return new Color32(209, 227, 156, 255);
                case StructureKind.Quarry: return new Color32(218, 223, 206, 255);
                case StructureKind.IronMine: return new Color32(190, 199, 231, 255);
                case StructureKind.Forge: return new Color32(247, 169, 128, 255);
                default: return new Color32(137, 225, 200, 255);
            }
        }

        private static void DrawIcon(UnityEngine.UI.VertexHelper Vertices, Vector2 Center, Color32 Tint, StructureKind Kind)
        {
            switch (Kind)
            {
                case StructureKind.Church:
                    Rectangle(Vertices, Center + new Vector2(0, -3.5f), new Vector2(12, 7), Tint);
                    Triangle(Vertices, Center, Tint, new Vector2(-8, 0), new Vector2(0, 5.5f), new Vector2(8, 0));
                    Rectangle(Vertices, Center + new Vector2(0, 6.5f), new Vector2(2, 7), Tint);
                    Rectangle(Vertices, Center + new Vector2(0, 7.5f), new Vector2(6, 2), Tint);
                    Rectangle(Vertices, Center + new Vector2(0, -5), new Vector2(3, 4), Background);
                    break;
                case StructureKind.Tower:
                    Rectangle(Vertices, Center + new Vector2(0, -1), new Vector2(12, 12), Tint);
                    Rectangle(Vertices, Center + new Vector2(0, 4.5f), new Vector2(16, 4), Tint);
                    for (int Tooth = -1; Tooth <= 1; Tooth++)
                    {
                        Rectangle(Vertices, Center + new Vector2(Tooth * 6, 7), new Vector2(4, 4), Tint);
                    }
                    Rectangle(Vertices, Center + new Vector2(0, -4), new Vector2(3, 6), Background);
                    break;
                case StructureKind.Sawmill:
                    Line(Vertices, Center + new Vector2(-7, 0), Center + new Vector2(5, 6), 5, Tint);
                    Line(Vertices, Center + new Vector2(-5, -6), Center + new Vector2(7, 0), 5, Tint);
                    Rectangle(Vertices, Center + new Vector2(-6, 0.5f), new Vector2(2, 2), Background);
                    Rectangle(Vertices, Center + new Vector2(-4, -5.5f), new Vector2(2, 2), Background);
                    break;
                case StructureKind.Quarry:
                    for (int Step = 0; Step < 3; Step++)
                    {
                        Rectangle(Vertices, Center + new Vector2(Step * 2.5f - 0.5f, Step * 5 - 5), new Vector2(16 - Step * 5, 4), Tint);
                    }
                    break;
                case StructureKind.IronMine:
                    Line(Vertices, Center + new Vector2(-5, -7), Center + new Vector2(4, 5), 3, Tint);
                    Line(Vertices, Center + new Vector2(-7, 4), Center + new Vector2(0, 7), 3, Tint);
                    Line(Vertices, Center + new Vector2(0, 7), Center + new Vector2(7, 1), 3, Tint);
                    break;
                case StructureKind.Forge:
                    Rectangle(Vertices, Center + new Vector2(-1.5f, 3), new Vector2(10, 5), Tint);
                    Triangle(Vertices, Center, Tint, new Vector2(3, 5.5f), new Vector2(8, 5.5f), new Vector2(3, 0.5f));
                    Rectangle(Vertices, Center + new Vector2(-0.5f, -2), new Vector2(4, 6), Tint);
                    Rectangle(Vertices, Center + new Vector2(-0.5f, -6), new Vector2(10, 3), Tint);
                    break;
                case StructureKind.HealingShrine:
                    Rectangle(Vertices, Center, new Vector2(15, 5), Tint);
                    Rectangle(Vertices, Center, new Vector2(5, 15), Tint);
                    break;
            }
        }

        private static void Badge(UnityEngine.UI.VertexHelper Vertices, Vector2 Center, float Radius, Color32 Tint)
        {
            int Start = Vertices.currentVertCount;
            foreach (Vector2 Corner in BadgeCorners)
            {
                Vertices.AddVert(Center + Corner * Radius, Tint, Vector2.zero);
            }
            for (int Index = 1; Index < BadgeCorners.Length - 1; Index++)
            {
                Vertices.AddTriangle(Start, Start + Index, Start + Index + 1);
            }
        }

        private static void Rectangle(UnityEngine.UI.VertexHelper Vertices, Vector2 Center, Vector2 Size, Color32 Tint)
        {
            Vector2 Half = Size * 0.5f;
            Quad(Vertices, Center - Half, Center + new Vector2(Half.x, -Half.y),
                Center + Half, Center + new Vector2(-Half.x, Half.y), Tint);
        }

        private static void Line(UnityEngine.UI.VertexHelper Vertices, Vector2 Start, Vector2 End, float Width, Color32 Tint)
        {
            Vector2 Direction = End - Start;
            Vector2 Side = new Vector2(-Direction.y, Direction.x).normalized * (Width * 0.5f);
            Quad(Vertices, Start - Side, End - Side, End + Side, Start + Side, Tint);
        }

        private static void Quad(UnityEngine.UI.VertexHelper Vertices, Vector2 BottomLeft, Vector2 BottomRight,
            Vector2 TopRight, Vector2 TopLeft, Color32 Tint)
        {
            int Start = Vertices.currentVertCount;
            Vertices.AddVert(BottomLeft, Tint, Vector2.zero);
            Vertices.AddVert(BottomRight, Tint, Vector2.zero);
            Vertices.AddVert(TopRight, Tint, Vector2.zero);
            Vertices.AddVert(TopLeft, Tint, Vector2.zero);
            Vertices.AddTriangle(Start, Start + 1, Start + 2);
            Vertices.AddTriangle(Start, Start + 2, Start + 3);
        }

        private static void Triangle(UnityEngine.UI.VertexHelper Vertices, Vector2 Center, Color32 Tint,
            Vector2 First, Vector2 Second, Vector2 Third)
        {
            int Start = Vertices.currentVertCount;
            Vertices.AddVert(Center + First, Tint, Vector2.zero);
            Vertices.AddVert(Center + Second, Tint, Vector2.zero);
            Vertices.AddVert(Center + Third, Tint, Vector2.zero);
            Vertices.AddTriangle(Start, Start + 1, Start + 2);
        }
    }
}
#endif
