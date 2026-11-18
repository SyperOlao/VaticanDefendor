#if !UNITY_SERVER || UNITY_EDITOR
using TMPro;
using UnityEngine;
using VaticanDefendor.Client.Accounts;
using VaticanDefendor.Combat;

namespace VaticanDefendor.Client.Shell
{
    [RequireComponent(typeof(CanvasRenderer))]
    public sealed class ClientWeaponWheel : UnityEngine.UI.MaskableGraphic
    {
        private readonly TMP_Text[] _labels = new TMP_Text[3];
        private readonly WeaponPreviewGraphic[] _icons = new WeaponPreviewGraphic[3];
        private Vector2 _origin;
        private Vector2 _target;
        private Vector2 _center;
        private Vector2 _pointer;
        private float _fullRadius;
        private float _progress;
        private int _count;
        private int _activeSlot;
        private int _highlightedSlot;

        public void Initialize(TMP_FontAsset Font)
        {
            for (int Slot = 0; Slot < _labels.Length; Slot++)
            {
                TMP_Text Label = CombatUIFactory.Text(transform, "WeaponSlot" + Slot, "", new Vector2(0.5f, 0.5f),
                    Vector2.zero, new Vector2(84, 29), 15, Font);
                Label.alignment = TextAlignmentOptions.Center;
                Label.rectTransform.pivot = new Vector2(0.5f, 0.5f);
                _labels[Slot] = Label;

                GameObject Icon = new GameObject("WeaponIcon" + Slot, typeof(RectTransform), typeof(WeaponPreviewGraphic));
                Icon.transform.SetParent(transform, false);
                RectTransform IconBounds = (RectTransform)Icon.transform;
                IconBounds.sizeDelta = new Vector2(30, 33);
                _icons[Slot] = Icon.GetComponent<WeaponPreviewGraphic>();
            }
            gameObject.SetActive(false);
        }

        public void Open(Vector2 ScreenPoint, EquippedWeapon[] Weapons, int ActiveSlot, float Radius)
        {
            Rect Bounds = rectTransform.rect;
            _fullRadius = Mathf.Min(Radius, Bounds.width * 0.44f, Bounds.height * 0.26f);
            _target = new Vector2(Bounds.center.x, Mathf.Lerp(Bounds.yMin, Bounds.yMax, 0.30f));
            RectTransformUtility.ScreenPointToLocalPointInRectangle(rectTransform, ScreenPoint, null, out _origin);
            _count = Mathf.Min(Weapons.Length, _labels.Length);
            _activeSlot = ActiveSlot;
            for (int Slot = 0; Slot < _count; Slot++)
            {
                EquippedWeapon Weapon = Weapons[Slot];
                _labels[Slot].text = ClientShellView.WeaponName(Weapon.ModelIndex) + "\nур. " + Weapon.Level;
                _icons[Slot].Configure(Weapon.ModelIndex, ClientShellView.RarityColor(Weapon.Rarity));
            }
            gameObject.SetActive(true);
            Move(ScreenPoint);
        }

        public int Move(Vector2 ScreenPoint)
        {
            RectTransformUtility.ScreenPointToLocalPointInRectangle(rectTransform, ScreenPoint, null, out _pointer);
            _progress = Mathf.Clamp01((_pointer.y - _origin.y) / (Mathf.Max(_fullRadius * 0.65f, _target.y - _origin.y) * 0.65f));
            _center = new Vector2(_target.x, Mathf.Lerp(_origin.y, _target.y, _progress));
            Vector2 Offset = _pointer - _center;
            float SectorAngle = 2 * Mathf.PI / _count;
            _highlightedSlot = -1;
            if (_progress > 0.25f && Offset.magnitude > _fullRadius * 0.12f)
            {
                float Angle = Mathf.Repeat(Mathf.Atan2(Offset.x, Offset.y), 2 * Mathf.PI);
                _highlightedSlot = Mathf.Min(_count - 1, Mathf.FloorToInt(Angle / SectorAngle));
            }

            for (int Slot = 0; Slot < _labels.Length; Slot++)
            {
                bool bVisible = Slot < _count && _progress > 0.30f;
                _labels[Slot].gameObject.SetActive(bVisible);
                _icons[Slot].gameObject.SetActive(bVisible);
                if (bVisible)
                {
                    Vector2 LabelCenter = _center + RadialDirection((Slot + 0.5f) * SectorAngle) * Radius * 0.60f;
                    _labels[Slot].rectTransform.localPosition = LabelCenter - Vector2.up * 13;
                    _icons[Slot].rectTransform.localPosition = LabelCenter + Vector2.up * 13;
                }
            }
            SetVerticesDirty();
            return _highlightedSlot;
        }

        private float Radius => Mathf.Lerp(_fullRadius * 0.32f, _fullRadius, _progress);

        protected override void OnPopulateMesh(UnityEngine.UI.VertexHelper Vertices)
        {
            Vertices.Clear();
            if (_count <= 0)
            {
                return;
            }
            Rect Bounds = rectTransform.rect;
            AddQuad(Vertices, Bounds.min, new Vector2(Bounds.xMin, Bounds.yMax), Bounds.max,
                new Vector2(Bounds.xMax, Bounds.yMin), new Color(0.025f, 0.035f, 0.045f, _progress * 0.62f));
            const int SegmentsPerSlot = 24;
            float SectorAngle = 2 * Mathf.PI / _count;
            float InnerRadius = _fullRadius * 0.19f * _progress;
            for (int Slot = 0; Slot < _count; Slot++)
            {
                Color Fill = new Color(0.10f, 0.11f, 0.12f, 0.98f);
                Color Edge = new Color(0.56f, 0.46f, 0.31f);
                float OuterRadius = Radius;
                if (Slot == _highlightedSlot)
                {
                    Fill = new Color(0.53f, 0.32f, 0.10f);
                    Edge = new Color(0.99f, 0.79f, 0.42f);
                    OuterRadius += 6;
                }
                else if (Slot == _activeSlot)
                {
                    Fill = new Color(0.18f, 0.23f, 0.21f);
                    Edge = new Color(0.66f, 0.82f, 0.67f);
                }

                for (int Segment = 0; Segment < SegmentsPerSlot; Segment++)
                {
                    Vector2 First = RadialDirection(SectorAngle * (Slot + (float)Segment / SegmentsPerSlot));
                    Vector2 Second = RadialDirection(SectorAngle * (Slot + (float)(Segment + 1) / SegmentsPerSlot));
                    Vector2 BorderStart = _center + First * (OuterRadius - 2);
                    Vector2 BorderEnd = _center + Second * (OuterRadius - 2);
                    AddQuad(Vertices, _center + First * InnerRadius, BorderStart, BorderEnd, _center + Second * InnerRadius, Fill);
                    AddQuad(Vertices, BorderStart, _center + First * OuterRadius, _center + Second * OuterRadius, BorderEnd, Edge);
                }
                Vector2 Boundary = RadialDirection(SectorAngle * Slot);
                Vector2 Thickness = new Vector2(-Boundary.y, Boundary.x);
                Vector2 Inner = _center + Boundary * InnerRadius;
                Vector2 Outer = _center + Boundary * Radius;
                AddQuad(Vertices, Inner - Thickness, Outer - Thickness, Outer + Thickness, Inner + Thickness, Edge);
            }
            if (_highlightedSlot >= 0)
            {
                Vector2 Direction = (_pointer - _center).normalized;
                Vector2 Thickness = new Vector2(-Direction.y, Direction.x);
                Vector2 End = _center + Direction * Mathf.Min((_pointer - _center).magnitude, InnerRadius);
                AddQuad(Vertices, _center - Thickness, End - Thickness, End + Thickness, _center + Thickness,
                    new Color(0.99f, 0.79f, 0.42f));
            }
        }

        private static Vector2 RadialDirection(float Angle)
        {
            return new Vector2(Mathf.Sin(Angle), Mathf.Cos(Angle));
        }

        private static void AddQuad(UnityEngine.UI.VertexHelper Vertices, Vector2 First, Vector2 Second, Vector2 Third, Vector2 Fourth, Color Tint)
        {
            int Start = Vertices.currentVertCount;
            Vertices.AddVert(First, Tint, Vector2.zero);
            Vertices.AddVert(Second, Tint, Vector2.zero);
            Vertices.AddVert(Third, Tint, Vector2.zero);
            Vertices.AddVert(Fourth, Tint, Vector2.zero);
            Vertices.AddTriangle(Start, Start + 1, Start + 2);
            Vertices.AddTriangle(Start, Start + 2, Start + 3);
        }
    }
}
#endif
