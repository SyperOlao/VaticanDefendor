#if !UNITY_SERVER || UNITY_EDITOR
using UnityEngine;
using UnityEngine.UI;

namespace VaticanDefendor.Client.Accounts
{
    internal sealed partial class AccountReliefGraphic
    {
        private enum Portrait { Unselected, Guard, Knight, Monk }

        private Portrait _portrait;
        private Color _avatarColor;
        private bool _monogram;
        private float _experienceFill;

        internal void ConfigureExperience(float fraction)
        {
            _surface = Surface.Experience;
            _experienceFill = Mathf.Clamp01(fraction);
            SetVerticesDirty();
        }

        internal static bool HasAvatarPortrait(string assetKey) =>
            assetKey is "avatars/guard" or "avatars/knight" or "avatars/monk";

        internal void ConfigureAvatar(string assetKey, Color background, bool monogram)
        {
            _surface = Surface.Icon;
            _isAvatar = true;
            _portrait = assetKey switch
            {
                "avatars/guard" => Portrait.Guard,
                "avatars/knight" => Portrait.Knight,
                "avatars/monk" => Portrait.Monk,
                _ => Portrait.Unselected
            };
            _avatarColor = background;
            _monogram = monogram;
            SetVerticesDirty();
        }

        private void DrawAvatar(IconMesh art)
        {
            DrawShieldBase(art, ColorCode(Color.Lerp(_avatarColor, Color.white, 0.18f)),
                ColorCode(Color.Lerp(_avatarColor, Color.black, 0.48f)));
            if (_monogram)
            {
                return;
            }
            switch (_portrait)
            {
                case Portrait.Guard:
                    DrawHelmet(art, 0xe8e1cc, 0x5f6868);
                    break;
                case Portrait.Knight:
                    DrawHelmet(art, 0xf3cc8e, 0x84643f);
                    art.Box(-2, -7, 2, 17, 0xffe8af, 0x946945);
                    art.Solid(0xbd665d, new(-5, 17), new(0, 27), new(7, 20), new(3, 17));
                    break;
                case Portrait.Monk:
                    art.Face(0xb1b184, 0x4b573f, new(-22, -10), new(-20, 7), new(-12, 20), new(0, 25),
                        new(12, 20), new(20, 7), new(22, -10), new(14, -20), new(0, -26), new(-15, -20));
                    art.Face(0x574843, 0x322529, new(-11, -3), new(-12, 9), new(0, 17), new(12, 9),
                        new(10, -3), new(0, -12));
                    art.Face(0xe5b89b, 0x92664b, new(-8, -2), new(-8, 5), new(4, 10), new(9, 4),
                        new(7, -3), new(0, -8));
                    art.Box(-2, -23, 2, -15, 0xe5bb75, 0x95704c);
                    break;
                default:
                    art.Face(0xa9aaa0, 0x5c686d, new(-10, 16), new(0, 21), new(10, 16),
                        new(11, 7), new(7, 0), new(-7, 0), new(-11, 7));
                    art.Face(0x899396, 0x495459, new(-21, -15), new(-16, -8), new(-8, -4),
                        new(8, -4), new(16, -8), new(21, -15), new(0, -25));
                    break;
            }
        }

        private static int ColorCode(Color32 value) => (value.r << 16) | (value.g << 8) | value.b;

        private static void DrawLevel(IconMesh art)
        {
            art.Face(0xffdf9c, 0x86502e, new(-30, 17), new(0, 35), new(31, 17), new(31, -15), new(0, -35), new(-30, -15));
            art.Face(0xb87f4d, 0x56312e, new(-24, 14), new(0, 28), new(24, 14), new(24, -12), new(0, -27), new(-24, -12));
            art.Face(0xffeab9, 0xc58b4c, new(0, 15), new(-16, -3), new(-7, -5), new(0, 3));
            art.Face(0xf4bf74, 0x95643b, new(0, 15), new(16, -3), new(7, -5), new(0, 3));
            art.Face(0xf3c47a, 0xae7040, new(0, -3), new(-13, -13), new(-5, -16), new(0, -12));
            art.Face(0xd8a261, 0x8b5836, new(0, -3), new(13, -13), new(5, -16), new(0, -12));
        }

        private static void DrawEnergy(IconMesh art)
        {
            art.Face(0xaa6843, 0x754330, new(15, 32), new(-23, -3), new(-5, -6), new(-14, -36));
            art.Face(0xba7947, 0x7f492e, new(31, 10), new(11, 8), new(-14, -36));
            art.Face(0xffe4a1, 0xdb982d, new(12, 37), new(-27, 0), new(-4, 0));
            art.Face(0xffedb4, 0xd89b3d, new(-4, 0), new(-18, -33), new(27, 14), new(6, 14));
            art.Face(0xe6b057, 0xc18037, new(27, 14), new(6, 14), new(-18, -33));
        }

        private static void DrawGear(IconMesh art)
        {
            const int teeth = 8;
            var vertices = new Vector2[teeth * 4 + 2];
            for (int index = 0; index < teeth * 4; index++)
            {
                float angle = (index + 0.5f) * (Mathf.PI * 2 / (teeth * 4));
                int edge = index % 4;
                float radius = edge is 1 or 2 ? 36 : 27;
                vertices[index + 1] = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius;
            }
            vertices[^1] = vertices[1];
            art.Face(0xbd8657, 0x5a3832, vertices);
            art.Face(0xffd89b, 0x7d4d36, new(-23, 0), new(-17, 16), new(0, 24),
                new(17, 16), new(23, 0), new(17, -16), new(0, -24), new(-17, -16));
            art.Face(0x513437, 0x211b24, new(-12, 0), new(-8, 9), new(0, 13), new(8, 9),
                new(12, 0), new(8, -9), new(0, -13), new(-8, -9));
        }

        private static void DrawPlay(IconMesh art)
        {
            art.Face(0xae6c45, 0x663829, new(-19, 19), new(29, -4), new(-19, -28));
            art.Face(0xffe7ac, 0xcc955b, new(-22, 26), new(26, 0), new(-22, -24));
            art.Face(0xeaba7b, 0xa36a42, new(-16, 17), new(16, 0), new(-16, -16));
        }

        private static void DrawActionPlate(VertexHelper mesh, Rect rect)
        {
            var art = new IconMesh(mesh, Vector2.zero, 1);
            art.Cut(Rect.MinMaxRect(rect.xMin, rect.yMin, rect.xMax, rect.yMax - 4), 20, 0x442731, 0x1b1317);
            Rect rim = Rect.MinMaxRect(rect.xMin + 1, rect.yMin + 6, rect.xMax - 1, rect.yMax);
            art.Cut(rim, 20, 0xffd795, 0x8b5338);
            Rect face = Rect.MinMaxRect(rim.xMin + 5, rim.yMin + 6, rim.xMax - 5, rim.yMax - 5);
            art.Cut(face, 14, 0xa54d4d, 0x49212b);
            art.Box(face.xMin + 15, face.yMax - 2, face.xMax - 15, face.yMax, 0xce7770, 0xa75353);
            art.Box(rim.center.x - 48, rim.yMax - 2, rim.center.x + 48, rim.yMax, 0xffebbb, 0xd6a36f);
        }

        private void DrawExperience(VertexHelper mesh, Rect rect)
        {
            var art = new IconMesh(mesh, Vector2.zero, 1);
            float corner = Mathf.Min(9, rect.height * 0.25f, rect.width * 0.25f);
            art.Cut(Rect.MinMaxRect(rect.xMin, rect.yMin, rect.xMax, rect.yMax - 2), corner, 0x432b25, 0x171216);
            art.Cut(Rect.MinMaxRect(rect.xMin + 1, rect.yMin + 3, rect.xMax - 1, rect.yMax), corner, 0xebc184, 0x654132);

            Rect trough = Rect.MinMaxRect(rect.xMin + 5, rect.yMin + 7, rect.xMax - 5, rect.yMax - 5);
            art.Cut(trough, 4, 0x211820, 0x4c3836);
            float edge = Mathf.Lerp(trough.xMin, trough.xMax, _experienceFill);
            if (edge > trough.xMin)
            {
                float cap = Mathf.Min(4, (edge - trough.xMin) * 0.5f);
                art.Cut(Rect.MinMaxRect(trough.xMin, trough.yMin, edge, trough.yMax), cap, 0xf7cb83, 0x975534);
                if (edge - trough.xMin > 2 * cap)
                {
                    art.Box(trough.xMin + cap, trough.yMax - 3, edge - cap, trough.yMax - 1, 0xffe5b0, 0xe8a966);
                }
            }
        }
    }
}
#endif
