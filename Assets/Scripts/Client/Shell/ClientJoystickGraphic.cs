#if !UNITY_SERVER || UNITY_EDITOR
using UnityEngine;
using VaticanDefendor.Content;

namespace VaticanDefendor.Client.Shell
{
    public sealed class ClientJoystickGraphic : UnityEngine.UI.MaskableGraphic
    {
        private ClientPointerSurface _surface;
        private ClientShellView _view;
        private ClientPresentationDefinition _presentation;

        public void Configure(ClientPointerSurface Surface, ClientShellView View, ClientPresentationDefinition Presentation)
        {
            _surface = Surface;
            _view = View;
            _presentation = Presentation;
            raycastTarget = true;
            SetVerticesDirty();
        }

        private void LateUpdate()
        {
            SetVerticesDirty();
        }

        protected override void OnPopulateMesh(UnityEngine.UI.VertexHelper Helper)
        {
            Helper.Clear();
            if (_surface == null || _view == null)
            {
                return;
            }
            float MovementRadius = _presentation.JoystickRadius;
            float OuterRadius = _presentation.RunRadius;
            float CenterRadius = _presentation.CenterRadius;
            Vector2 Center = rectTransform.rect.min + new Vector2(OuterRadius + 46, OuterRadius + 122);
            Vector2 Offset = Vector2.zero;
            float Opacity = 0.55f;
            if (_surface.IsGestureActive)
            {
                RectTransformUtility.ScreenPointToLocalPointInRectangle(rectTransform, _surface.GestureOrigin, null, out Center);
                RectTransformUtility.ScreenPointToLocalPointInRectangle(rectTransform, _surface.GestureOrigin + _surface.GestureOffset, null, out Vector2 Pointer);
                Offset = Pointer - Center;
                Opacity = 1;
            }
            ClientGameplaySnapshot Gameplay = _view.Gameplay;
            float Combo = 0;
            if (Gameplay.Action.ComboRequired > 0)
            {
                Combo = Mathf.Clamp01(Gameplay.Action.Combo / Gameplay.Action.ComboRequired);
            }
            float Charge = 0;
            if (Gameplay.ChargeSeconds > 0)
            {
                Charge = Mathf.Clamp01(Gameplay.Action.ChargeProgress / Gameplay.ChargeSeconds);
            }
            bool bReady = Combo >= 1 && Gameplay.Action.SpecialRemaining <= 0;
            Disc(Helper, Center, OuterRadius, new Color(0.725f, 0.11f, 0.216f, 0.4f * Opacity));
            Ring(Helper, Center, OuterRadius, 2, new Color(0.98f, 0.44f, 0.52f, Opacity), 1, false);
            Disc(Helper, Center, MovementRadius, new Color(0.114f, 0.306f, 0.588f, 0.8f * Opacity));
            Ring(Helper, Center, MovementRadius, 2, new Color(0.38f, 0.65f, 0.98f, Opacity), 1, false);
            Color CenterColor = new Color(0.043f, 0.098f, 0.094f, 0.9f * Opacity);
            Color CenterBorder = new Color(0.65f, 0.78f, 0.75f, Opacity);
            if (bReady)
            {
                CenterColor = new Color(0.72f, 0.04f, 0.1f, 0.9f);
                CenterBorder = new Color(1, 0.8f, 0.45f);
                Disc(Helper, Center, CenterRadius + 7, new Color(1, 0.1f, 0.16f, 0.16f + 0.08f * Mathf.Sin(Time.unscaledTime * 6)));
            }
            Disc(Helper, Center, CenterRadius, CenterColor);
            Ring(Helper, Center, CenterRadius, 2, CenterBorder, 1, !bReady);
            Ring(Helper, Center, CenterRadius + 4, 3, new Color(0.98f, 0.44f, 0.52f), Combo, false);
            Ring(Helper, Center, OuterRadius + 7, 3, new Color(1, 0.9f, 0.65f, 0.2f * Opacity), 1, false);
            Ring(Helper, Center, OuterRadius + 12, 3, new Color(1, 0.9f, 0.65f, 0.2f * Opacity), 1, false);
            Ring(Helper, Center, OuterRadius + 7, 3, new Color(1, 0.9f, 0.65f), Charge, false);
            Ring(Helper, Center, OuterRadius + 12, 3, new Color(1, 0.9f, 0.65f), Charge, false);
            Vector2 Knob = Center + Vector2.ClampMagnitude(Offset, (MovementRadius + OuterRadius) * 0.5f);
            Vector2 Perpendicular = new Vector2(-Offset.y, Offset.x).normalized * 1.5f;
            Quad(Helper, Center - Perpendicular, Center + Perpendicular, Knob + Perpendicular, Knob - Perpendicular, new Color(0.89f, 0.92f, 0.96f, Opacity));
            Color KnobColor = new Color(0.38f, 0.65f, 0.98f, Opacity);
            if (Offset.magnitude >= MovementRadius || bReady && Offset.magnitude <= CenterRadius)
            {
                KnobColor = new Color(0.98f, 0.44f, 0.52f, Opacity);
            }
            Disc(Helper, Knob, 14, KnobColor);
            Ring(Helper, Knob, 14, 2, new Color(1, 1, 1, Opacity), 1, false);
        }

        private static void Ring(UnityEngine.UI.VertexHelper Helper, Vector2 Center, float Radius, float Width, Color Color, float Progress, bool bDotted)
        {
            const int Segments = 96;
            for (int Index = 0; Index < Segments && Index / (float)Segments < Progress; Index++)
            {
                if (bDotted && Index % 7 >= 3)
                {
                    continue;
                }
                float First = Index * Mathf.PI * 2 / Segments;
                float Second = (Index + 1) * Mathf.PI * 2 / Segments;
                Vector2 FirstDirection = new Vector2(Mathf.Sin(First), Mathf.Cos(First));
                Vector2 SecondDirection = new Vector2(Mathf.Sin(Second), Mathf.Cos(Second));
                Quad(Helper, Center + FirstDirection * (Radius - Width * 0.5f), Center + FirstDirection * (Radius + Width * 0.5f),
                    Center + SecondDirection * (Radius + Width * 0.5f), Center + SecondDirection * (Radius - Width * 0.5f), Color);
            }
        }

        private static void Disc(UnityEngine.UI.VertexHelper Helper, Vector2 Center, float Radius, Color Color)
        {
            const int Segments = 64;
            for (int Index = 0; Index < Segments; Index++)
            {
                float First = Index * Mathf.PI * 2 / Segments;
                float Second = (Index + 1) * Mathf.PI * 2 / Segments;
                Quad(Helper, Center, Center + new Vector2(Mathf.Sin(First), Mathf.Cos(First)) * Radius,
                    Center + new Vector2(Mathf.Sin(Second), Mathf.Cos(Second)) * Radius, Center, Color);
            }
        }

        private static void Quad(UnityEngine.UI.VertexHelper Helper, Vector2 First, Vector2 Second, Vector2 Third, Vector2 Fourth, Color Color)
        {
            int Start = Helper.currentVertCount;
            Helper.AddVert(First, Color, Vector2.zero);
            Helper.AddVert(Second, Color, Vector2.zero);
            Helper.AddVert(Third, Color, Vector2.zero);
            Helper.AddVert(Fourth, Color, Vector2.zero);
            Helper.AddTriangle(Start, Start + 1, Start + 2);
            Helper.AddTriangle(Start, Start + 2, Start + 3);
        }
    }
}
#endif
