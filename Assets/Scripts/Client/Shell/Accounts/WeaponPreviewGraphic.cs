#if !UNITY_SERVER || UNITY_EDITOR
using UnityEngine;
using UnityEngine.UI;

namespace VaticanDefendor.Client.Accounts
{
    [RequireComponent(typeof(CanvasRenderer))]
    public sealed class WeaponPreviewGraphic : MaskableGraphic
    {
        private int _modelIndex;
        private Color _accentColor;

        public void Configure(int weaponModelIndex, Color weaponAccentColor)
        {
            raycastTarget = false;
            if (_modelIndex == weaponModelIndex && _accentColor == weaponAccentColor)
            {
                return;
            }

            _modelIndex = weaponModelIndex;
            _accentColor = weaponAccentColor;
            SetVerticesDirty();
        }

        protected override void OnPopulateMesh(VertexHelper mesh)
        {
            mesh.Clear();
            float scale = Mathf.Min(rectTransform.rect.width / 110, rectTransform.rect.height / 180);
            var art = new AccountReliefGraphic.IconMesh(mesh, rectTransform.rect.center, scale);
            if (_modelIndex == 0)
            {
                DrawSword(art);
                return;
            }

            art.Face(0xa36b46, 0x3f2926, new(-7, -65), new(-7, 57), new(4, 60), new(8, 53),
                new(8, -62), new(3, -70), new(-4, -70));
            art.Box(-6, -64, -2, 55, 0xcb996a, 0x6d4630);
            art.Box(3, -64, 7, 55, 0x71442e, 0x38242a);
            art.Box(-7, -63, 8, -57, 0xba915b, 0x69432f);

            if (_modelIndex == 1)
            {
                DrawSpear(art);
            }
            else
            {
                DrawAxe(art);
            }
        }

        private void DrawSword(AccountReliefGraphic.IconMesh art)
        {
            art.Face(0x5c6b70, 0x343641, new(-13, -15), new(-12, 50), new(0, 80), new(7, 72),
                new(17, 48), new(17, -14), new(3, -27));
            art.Face(0xfff0d6, 0xa1b1b5, new(-13, -15), new(-12, 50), new(0, 80), new(0, -22));
            art.Face(0xc4d2ca, 0x657981, new(0, 80), new(12, 50), new(13, -15), new(0, -22));
            art.Face(0xecdec2, 0x81989e, new(-3, 54), new(0, 71), new(3, 54), new(3, -12), new(-3, -12));
            art.Face(0xbb8655, 0x57382c, new(-30, -18), new(28, -18), new(32, -25),
                new(24, -32), new(-25, -32), new(-31, -27));
            art.Face(0xffd697, 0x946341, new(-30, -18), new(-23, -16), new(0, -21), new(23, -16),
                new(30, -18), new(24, -26), new(-24, -26));
            art.Face(0x99644a, 0x4b2d2b, new(-8, -28), new(8, -28), new(7, -60), new(-8, -60));
            for (int index = 0; index < 4; index++)
            {
                float y = -32 - index * 7;
                art.Face(0xc29268, 0x724333, new(-8, y), new(8, y + 3), new(8, y), new(-8, y - 3));
            }
            art.Face(0xffd18b, 0x744b36, new(-12, -62), new(-6, -56), new(6, -56), new(12, -62),
                new(8, -70), new(0, -74), new(-8, -70));
            Color32 accent = _accentColor;
            art.Solid((accent.r << 16) | (accent.g << 8) | accent.b, new(-5, -63), new(0, -59), new(5, -63), new(0, -68));
        }

        private static void DrawSpear(AccountReliefGraphic.IconMesh art)
        {
            art.Box(-8, 20, 9, 36, 0xe5b97b, 0x895333);
            art.Box(-9, 25, 10, 29, 0xffcb8b, 0x9e6540);
            art.Face(0x576a76, 0x303944, new(0, 83), new(6, 77), new(22, 44), new(4, 29), new(-18, 44));
            art.Face(0xfff1d0, 0x95acb2, new(0, 83), new(-18, 44), new(0, 33), new(4, 48));
            art.Face(0xc4dcd8, 0x647b86, new(0, 83), new(16, 45), new(0, 33), new(4, 48));
            art.Solid(0xffedc8, new(0, 76), new(-13, 45), new(-9, 46));
        }

        private static void DrawAxe(AccountReliefGraphic.IconMesh art)
        {
            art.Face(0x96aaac, 0x495661, new(4, 57), new(28, 58), new(34, 49), new(29, 25), new(4, 26));
            art.Face(0xffefd8, 0x758993, new(-41, 19), new(-45, 34), new(-44, 53), new(-33, 65),
                new(4, 55), new(7, 30), new(-30, 18));
            art.Face(0xc2d4ce, 0x72858c, new(-32, 61), new(4, 55), new(7, 30), new(-33, 24), new(-37, 42));
            art.Face(0xffedcb, 0xaebfc1, new(-41, 19), new(-45, 34), new(-44, 53), new(-33, 65),
                new(-37, 48), new(-37, 33), new(-34, 23));
            art.Face(0xf1c88a, 0x825439, new(-10, 65), new(10, 65), new(13, 59),
                new(11, 22), new(-9, 22), new(-13, 28));
            art.Box(-8, 27, 6, 59, 0xbd8f60, 0x673e30);
            art.Box(-8, 29, -5, 58, 0xf6c584, 0x9b6541);
            art.Solid(0xf6d9a0, new(-2, 47), new(2, 49), new(5, 46), new(2, 43), new(-2, 44));
        }
    }
}
#endif
