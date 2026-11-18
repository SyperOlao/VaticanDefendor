#if !UNITY_SERVER || UNITY_EDITOR
using TMPro;
using UnityEngine;
using VaticanDefendor.Client.Accounts;
using VaticanDefendor.Combat;

namespace VaticanDefendor.Client.Shell
{
    [RequireComponent(typeof(CanvasRenderer))]
    public sealed class ClientWeaponDockGraphic : UnityEngine.UI.MaskableGraphic
    {
        private const float ReferenceWidth = 460;
        private const float DockWidth = 112;
        private const float DockHeight = 96;
        private const float HiddenHeight = 34;
        private const float VisibleHeight = DockHeight - HiddenHeight;
        private static readonly Color Amber = new Color32(245, 158, 11, 255);

        private TMP_Text _weaponName;
        private WeaponPreviewGraphic _weaponIcon;
        private WeaponPreviewGraphic _crossedSword;
        private RectTransform _chevron;
        private RectTransform _viewport;
        private float _maximumViewportAspect;
        private int _modelIndex = -1;
        private float _viewportScale = 1;
        private float _pressScale = 1;
        private bool _pressed;

        public void Initialize(TMP_FontAsset Font, float MaximumViewportAspect)
        {
            _viewport = (RectTransform)transform.parent;
            _maximumViewportAspect = MaximumViewportAspect;
            rectTransform.anchorMin = rectTransform.anchorMax = rectTransform.pivot = new Vector2(0.5f, 0);
            rectTransform.anchoredPosition = Vector2.zero;
            rectTransform.sizeDelta = new Vector2(DockWidth, VisibleHeight);

            const string Caption = "ОРУЖИЕ";
            Color CaptionColor = new Color32(252, 211, 77, 230);
            TMP_Text Header = CombatUIFactory.Text(transform, "WeaponCategory", Caption, new Vector2(0.5f, 0),
                Vector2.zero, new Vector2(52, 14), 8, Font);
            Header.fontStyle = FontStyles.Bold;
            Header.alignment = TextAlignmentOptions.Center;
            Header.color = CaptionColor;
            float HeaderWidth = Header.GetPreferredValues(Caption).x;
            Header.rectTransform.pivot = new Vector2(0.5f, 0.5f);
            Header.rectTransform.anchoredPosition = new Vector2(4, VisibleHeight - 16);

            var ChevronObject = new GameObject("WeaponChevron", typeof(RectTransform), typeof(PhoneGraphic));
            ChevronObject.transform.SetParent(transform, false);
            PhoneGraphic Chevron = ChevronObject.GetComponent<PhoneGraphic>();
            Chevron.Icon = PhoneIcon.Chevron;
            Chevron.color = CaptionColor;
            Chevron.raycastTarget = false;
            _chevron = Chevron.rectTransform;
            _chevron.anchorMin = _chevron.anchorMax = new Vector2(0.5f, 0);
            _chevron.anchoredPosition = new Vector2(-HeaderWidth * 0.5f - 4, VisibleHeight - 16);
            _chevron.sizeDelta = new Vector2(12, 12);

            _weaponName = CombatUIFactory.Text(transform, "EquippedWeapon", "", new Vector2(0.5f, 0),
                Vector2.zero, new Vector2(62, 18), 11, Font);
            _weaponName.fontStyle = FontStyles.Bold;
            _weaponName.alignment = TextAlignmentOptions.Center;
            _weaponName.color = Color.white;
            _weaponName.rectTransform.pivot = new Vector2(0.5f, 0.5f);

            _weaponIcon = CreateWeaponIcon("EquippedWeaponIcon");
            _crossedSword = CreateWeaponIcon("CrossedSword");
            _crossedSword.Configure(0, Amber);
            _crossedSword.rectTransform.localRotation = Quaternion.Euler(0, 0, 39);
            _crossedSword.gameObject.SetActive(false);
        }

        private WeaponPreviewGraphic CreateWeaponIcon(string Name)
        {
            var IconObject = new GameObject(Name, typeof(RectTransform), typeof(WeaponPreviewGraphic));
            IconObject.transform.SetParent(transform, false);
            WeaponPreviewGraphic Icon = IconObject.GetComponent<WeaponPreviewGraphic>();
            Icon.raycastTarget = false;
            Icon.rectTransform.anchorMin = Icon.rectTransform.anchorMax = new Vector2(0.5f, 0);
            Icon.rectTransform.sizeDelta = new Vector2(20, 22);
            return Icon;
        }

        public void Render(EquippedWeapon Weapon)
        {
            if (_modelIndex == Weapon.ModelIndex)
            {
                return;
            }
            _modelIndex = Weapon.ModelIndex;
            _weaponName.text = ClientShellView.WeaponName(Weapon.ModelIndex);
            _weaponIcon.Configure(Weapon.ModelIndex, Amber);
            float NameWidth = _weaponName.GetPreferredValues(_weaponName.text).x;
            float RowCenter = VisibleHeight - 33;
            _weaponName.rectTransform.anchoredPosition = new Vector2(11, RowCenter);
            Vector2 IconCenter = new Vector2(-NameWidth * 0.5f - 2, RowCenter);
            _weaponIcon.rectTransform.anchoredPosition = IconCenter;
            _weaponIcon.rectTransform.localRotation = Quaternion.identity;
            _crossedSword.gameObject.SetActive(Weapon.ModelIndex == 0);
            if (Weapon.ModelIndex == 0)
            {
                _weaponIcon.rectTransform.localRotation = Quaternion.Euler(0, 0, -39);
                _crossedSword.rectTransform.anchoredPosition = IconCenter;
            }
        }

        public void SetPressed(bool bPressed)
        {
            _pressed = bPressed;
        }

        private void LateUpdate()
        {
            Vector2 ChevronPosition = _chevron.anchoredPosition;
            ChevronPosition.y = VisibleHeight - 16 + 2 * Mathf.Abs(Mathf.Cos(Mathf.PI * Time.unscaledTime));
            _chevron.anchoredPosition = ChevronPosition;
            float Target = 1;
            if (_pressed)
            {
                Target = 1.06f;
            }
            Rect Viewport = _viewport.rect;
            float ViewportScale = Mathf.Min(Viewport.width, Viewport.height * _maximumViewportAspect) / ReferenceWidth;
            float PressScale = Mathf.MoveTowards(_pressScale, Target, Time.unscaledDeltaTime * 0.3f);
            if (_viewportScale != ViewportScale || _pressScale != PressScale)
            {
                _viewportScale = ViewportScale;
                _pressScale = PressScale;
                rectTransform.localScale = Vector3.one * (_viewportScale * _pressScale);
            }
        }

        protected override void OnDisable()
        {
            _pressed = false;
            _pressScale = 1;
            rectTransform.localScale = Vector3.one * _viewportScale;
            base.OnDisable();
        }

        public override bool Raycast(Vector2 ScreenPoint, Camera EventCamera)
        {
            if (!base.Raycast(ScreenPoint, EventCamera) ||
                !RectTransformUtility.ScreenPointToLocalPointInRectangle(rectTransform, ScreenPoint, EventCamera, out Vector2 Point))
            {
                return false;
            }
            Rect Bounds = DockBounds;
            return Point.y >= 0 && Point.y <= Bounds.yMax &&
                Mathf.Abs(Point.x) <= CapsuleHalfWidth(Bounds, Point.y);
        }

        private static Rect DockBounds => new Rect(-DockWidth * 0.5f, -HiddenHeight, DockWidth, DockHeight);

        protected override void OnPopulateMesh(UnityEngine.UI.VertexHelper Vertices)
        {
            Vertices.Clear();
            Rect Bounds = DockBounds;
            for (int Glow = 6; Glow > 0; Glow--)
            {
                float Spread = Glow * 3;
                Color Shadow = Amber;
                Shadow.a = (7 - Glow) * 0.012f;
                Rect ShadowBounds = Rect.MinMaxRect(Bounds.xMin - Spread, Bounds.yMin - Spread + 4,
                    Bounds.xMax + Spread, Bounds.yMax + Spread + 4);
                AddCapsule(Vertices, ShadowBounds, Shadow, Shadow, Shadow);
            }

            Color GoldBorder = new Color32(245, 158, 11, 204);
            AddCapsule(Vertices, Bounds, GoldBorder, GoldBorder, GoldBorder);
            Rect Inner = Rect.MinMaxRect(Bounds.xMin + 2, Bounds.yMin + 2, Bounds.xMax - 2, Bounds.yMax - 2);
            AddCapsule(Vertices, Inner, new Color32(12, 10, 9, 255), new Color32(28, 25, 23, 255), new Color32(146, 64, 14, 255));
        }

        private static void AddCapsule(UnityEngine.UI.VertexHelper Vertices, Rect Bounds, Color Bottom, Color Middle, Color Top)
        {
            const int Strips = 32;
            int Start = Vertices.currentVertCount;
            for (int Strip = 0; Strip <= Strips; Strip++)
            {
                float Height = Mathf.Lerp(0, Bounds.yMax, (float)Strip / Strips);
                float HalfWidth = CapsuleHalfWidth(Bounds, Height);
                float Gradient = Mathf.InverseLerp(Bounds.yMin, Bounds.yMax, Height) * 2;
                Color Tint = Color.Lerp(Bottom, Middle, Gradient);
                if (Gradient > 1)
                {
                    Tint = Color.Lerp(Middle, Top, Gradient - 1);
                }
                int Row = Start + Strip * 2;
                Vertices.AddVert(new Vector2(-HalfWidth, Height), Tint, Vector2.zero);
                Vertices.AddVert(new Vector2(HalfWidth, Height), Tint, Vector2.zero);
                if (Strip > 0)
                {
                    Vertices.AddTriangle(Row - 2, Row, Row + 1);
                    Vertices.AddTriangle(Row - 2, Row + 1, Row - 1);
                }
            }
        }

        private static float CapsuleHalfWidth(Rect Bounds, float Height)
        {
            float Radius = Bounds.height * 0.5f;
            float Offset = Height - Bounds.center.y;
            return Bounds.width * 0.5f - Radius + Mathf.Sqrt(Mathf.Max(0, Radius * Radius - Offset * Offset));
        }
    }
}
#endif
