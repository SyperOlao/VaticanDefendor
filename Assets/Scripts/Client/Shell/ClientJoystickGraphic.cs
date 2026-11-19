#if !UNITY_SERVER || UNITY_EDITOR
using UnityEngine;
using UnityEngine.UI;
using VaticanDefendor.Content;

namespace VaticanDefendor.Client.Shell
{
    public sealed class ClientJoystickGraphic : MaskableGraphic
    {
        private const int Facets = 12;
        private const int ArcSegments = 44;
        private ClientPointerSurface _surface;
        private ClientShellView _view;
        private ClientPresentationDefinition _presentation;

        public void Configure(ClientPointerSurface surface, ClientShellView view, ClientPresentationDefinition presentation)
        {
            _surface = surface;
            _view = view;
            _presentation = presentation;
            raycastTarget = true;
            SetVerticesDirty();
        }

        private void LateUpdate()
        {
            SetVerticesDirty();
        }

        protected override void OnPopulateMesh(VertexHelper mesh)
        {
            mesh.Clear();
            if (_surface == null || _view == null)
            {
                return;
            }

            float movementRadius = _presentation.JoystickRadius;
            float outerRadius = _presentation.RunRadius;
            float centerRadius = _presentation.CenterRadius;
            Vector2 center = rectTransform.rect.min + new Vector2(outerRadius + 46, outerRadius + 122);
            Vector2 offset = Vector2.zero;
            bool active = _surface.IsGestureActive;
            float opacity = active ? 1 : 0.84f;
            if (active)
            {
                RectTransformUtility.ScreenPointToLocalPointInRectangle(rectTransform, _surface.GestureOrigin, null, out center);
                RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    rectTransform, _surface.GestureOrigin + _surface.GestureOffset, null, out Vector2 pointer);
                offset = pointer - center;
            }

            ClientGameplaySnapshot gameplay = _view.Gameplay;
            float combo = gameplay.Action.ComboRequired > 0
                ? Mathf.Clamp01(gameplay.Action.Combo / gameplay.Action.ComboRequired) : 0;
            float charge = gameplay.ChargeSeconds > 0
                ? Mathf.Clamp01(gameplay.Action.ChargeProgress / gameplay.ChargeSeconds) : 0;
            bool ready = combo >= 1 && gameplay.Action.SpecialRemaining <= 0;
            bool pushedOut = offset.sqrMagnitude >= movementRadius * movementRadius;

            DrawBase(mesh, center, outerRadius, movementRadius, opacity, pushedOut);
            DrawRing(mesh, center, centerRadius + 5, centerRadius + 8, ArcSegments, 1, 0x997252, 0x503632, opacity);
            DrawRing(mesh, center, centerRadius + 4, centerRadius + 8, ArcSegments, combo,
                ready ? 0xffebbd : 0xf1c383, 0xb77542, opacity);
            if (charge > 0)
            {
                DrawRing(mesh, center, outerRadius + 5, outerRadius + 9, ArcSegments, 1, 0x7a5b4d, 0x3b292a, opacity);
                DrawRing(mesh, center, outerRadius + 5, outerRadius + 9, ArcSegments, charge, 0xffffdc, 0xe5ab60, opacity);
            }

            Vector2 knob = center + Vector2.ClampMagnitude(offset, (movementRadius + outerRadius) * 0.5f);
            if ((knob - center).sqrMagnitude > 1)
            {
                DrawStem(mesh, center, knob, opacity);
            }
            bool lit = pushedOut || ready && offset.sqrMagnitude <= centerRadius * centerRadius;
            DrawKnob(mesh, knob, centerRadius, opacity, lit);
        }

        private static void DrawBase(VertexHelper mesh, Vector2 center, float radius, float movementRadius, float opacity, bool lit)
        {
            DrawDisc(mesh, center + Vector2.down * 3, radius + 1, Facets, 0x271b20, 0x141013, opacity);
            DrawDisc(mesh, center, radius, Facets, 0x5e3a35, 0x2a1c24, opacity);
            DrawRing(mesh, center, radius - 6, radius, Facets, 1, lit ? 0xffe1a8 : 0xd6ad7b, 0x815439, opacity);
            DrawRing(mesh, center, radius - 9, radius - 6, Facets, 1, 0x48302f, 0xaf815a, opacity);
            DrawRing(mesh, center, movementRadius - 1, movementRadius + 2, ArcSegments, 1, 0xb28a66, 0x4c3432, opacity);
            DrawDisc(mesh, center, movementRadius - 1, ArcSegments, 0x29202a, 0x3b3033, opacity * 0.95f);

            for (int index = 0; index < 4; index++)
            {
                Vector2 direction = Direction(index * Mathf.PI * 0.5f);
                Vector2 cross = new Vector2(-direction.y, direction.x);
                Vector2 point = center + direction * (movementRadius - 8);
                AddTriangle(mesh, point + direction * 4, point - cross * 3, point + cross * 3,
                    Tint(lit ? 0xffd9a0 : 0xb49167, opacity));
            }
        }

        private static void DrawKnob(VertexHelper mesh, Vector2 center, float radius, float opacity, bool lit)
        {
            DrawDisc(mesh, center + Vector2.down * 2, radius + 1, Facets, 0x2b1b23, 0x171016, opacity);
            DrawDisc(mesh, center, radius, Facets, lit ? 0xffe8b7 : 0xe6c08e, 0x936035, opacity);
            DrawDisc(mesh, center, radius - 4, Facets, lit ? 0xecc07a : 0xb56559, lit ? 0x996031 : 0x562d3b, opacity);
            float seam = radius * 0.36f;
            AddTriangle(mesh, center + new Vector2(-seam, seam * 0.6f), center + new Vector2(-seam * 0.15f, seam),
                center + new Vector2(seam * 0.7f, seam * 0.78f), Tint(lit ? 0xfff0c3 : 0xec9d7f, opacity));
        }

        private static void DrawStem(VertexHelper mesh, Vector2 center, Vector2 knob, float opacity)
        {
            Vector2 direction = (knob - center).normalized;
            Vector2 cross = new Vector2(-direction.y, direction.x);
            int first = mesh.currentVertCount;
            mesh.AddVert(center - cross * 2, Tint(0x87593f, opacity), Vector2.zero);
            mesh.AddVert(center + cross * 2, Tint(0xc49867, opacity), Vector2.zero);
            mesh.AddVert(knob + cross * 5, Tint(0xffdda0, opacity), Vector2.zero);
            mesh.AddVert(knob - cross * 5, Tint(0xa3754d, opacity), Vector2.zero);
            mesh.AddTriangle(first, first + 1, first + 2);
            mesh.AddTriangle(first, first + 2, first + 3);
        }

        private static void DrawDisc(VertexHelper mesh, Vector2 center, float radius, int sides, int upper, int lower, float opacity)
        {
            if (radius <= 0)
            {
                return;
            }

            Color light = Tint(upper, opacity);
            Color shade = Tint(lower, opacity);
            int first = mesh.currentVertCount;
            mesh.AddVert(center, Color.Lerp(shade, light, 0.5f), Vector2.zero);
            for (int side = 0; side <= sides; side++)
            {
                Vector2 direction = Direction(2 * Mathf.PI * side / sides);
                Color tint = Color.Lerp(shade, light, (direction.y + 1) * 0.5f);
                mesh.AddVert(center + direction * radius, tint, Vector2.zero);
                if (side > 0)
                {
                    mesh.AddTriangle(first, first + side, first + side + 1);
                }
            }
        }

        private static void DrawRing(VertexHelper mesh, Vector2 center, float inner, float outer, int sides,
            float fraction, int upper, int lower, float opacity)
        {
            if (fraction <= 0 || inner <= 0)
            {
                return;
            }

            Color light = Tint(upper, opacity);
            Color shade = Tint(lower, opacity);
            int first = mesh.currentVertCount;
            int segments = Mathf.CeilToInt(sides * fraction);
            for (int index = 0; index <= segments; index++)
            {
                float turn = Mathf.Min(index / (float)sides, fraction);
                Vector2 direction = Direction(2 * Mathf.PI * turn);
                Color tint = Color.Lerp(shade, light, (direction.y + 1) * 0.5f);
                mesh.AddVert(center + direction * inner, tint, Vector2.zero);
                mesh.AddVert(center + direction * outer, tint, Vector2.zero);
                if (index > 0)
                {
                    int previous = first + (index - 1) * 2;
                    mesh.AddTriangle(previous, previous + 1, previous + 3);
                    mesh.AddTriangle(previous, previous + 3, previous + 2);
                }
            }
        }

        private static void AddTriangle(VertexHelper mesh, Vector2 a, Vector2 b, Vector2 c, Color tint)
        {
            int first = mesh.currentVertCount;
            mesh.AddVert(a, tint, Vector2.zero);
            mesh.AddVert(b, tint, Vector2.zero);
            mesh.AddVert(c, tint, Vector2.zero);
            mesh.AddTriangle(first, first + 1, first + 2);
        }

        private static Vector2 Direction(float angle) => new Vector2(Mathf.Sin(angle), Mathf.Cos(angle));

        private static Color32 Tint(int rgb, float opacity) =>
            new((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb, (byte)Mathf.RoundToInt(opacity * 255));
    }
}
#endif
