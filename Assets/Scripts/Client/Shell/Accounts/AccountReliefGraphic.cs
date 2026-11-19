#if !UNITY_SERVER || UNITY_EDITOR
using UnityEngine;
using UnityEngine.UI;
using Symbol = VaticanDefendor.Client.Accounts.AccountHubIcon.Symbol;

namespace VaticanDefendor.Client.Accounts
{
    [RequireComponent(typeof(CanvasRenderer))]
    internal sealed partial class AccountReliefGraphic : MaskableGraphic
    {
        private enum Surface { Dock, Icon, Header, Action, Frame, Inset, Experience, Medal, RatingRow }

        private Symbol _symbol;
        private bool _active;
        private Surface _surface;
        private bool _isAvatar;
        private int _medalPosition;

        internal void ConfigureRatingRow(bool own, int medalPosition = 0)
        {
            _surface = Surface.RatingRow;
            _active = own;
            _medalPosition = medalPosition;
            SetVerticesDirty();
        }

        internal void Configure(Symbol symbol, bool active)
        {
            _symbol = symbol;
            _active = active;
            SetVerticesDirty();
        }

        internal void ConfigureIcon(Symbol symbol)
        {
            _surface = Surface.Icon;
            Configure(symbol, active: false);
        }

        internal void ConfigurePlate(bool active = false, bool action = false)
        {
            _surface = action ? Surface.Action : Surface.Header;
            _active = active;
            SetVerticesDirty();
        }

        internal void ConfigureFrame()
        {
            _surface = Surface.Frame;
            SetVerticesDirty();
        }

        internal void ConfigureInset()
        {
            _surface = Surface.Inset;
            SetVerticesDirty();
        }

        internal void ConfigureMedal(int position)
        {
            _surface = Surface.Medal;
            _medalPosition = position;
            SetVerticesDirty();
        }

        protected override void OnPopulateMesh(VertexHelper mesh)
        {
            mesh.Clear();
            Rect rect = rectTransform.rect;
            if (rect.width < 16 || rect.height < 16)
            {
                return;
            }

            switch (_surface)
            {
                case Surface.Action:
                    DrawActionPlate(mesh, rect);
                    return;
                case Surface.Header:
                    DrawPlate(mesh, rect, false, _active);
                    return;
                case Surface.Frame:
                    DrawFrame(mesh, rect);
                    return;
                case Surface.Inset:
                    DrawInset(mesh, rect);
                    return;
                case Surface.Experience:
                    DrawExperience(mesh, rect);
                    return;
                case Surface.Medal:
                    DrawMedal(new IconMesh(mesh, rect.center, Mathf.Min(rect.width, rect.height) / 80f), _medalPosition);
                    return;
                case Surface.RatingRow:
                    DrawRatingRow(mesh, rect);
                    return;
            }
            bool dock = _surface == Surface.Dock;
            float size = dock ? Mathf.Min(rect.width - 9, rect.height - 15) : Mathf.Min(rect.width, rect.height);
            var art = new IconMesh(mesh, rect.center + Vector2.up * (dock ? 2 : 0), size / 91f);
            if (dock)
            {
                DrawPlate(mesh, rect, _symbol == Symbol.Home, _active);
                art.Solid(0x110f12, new(-44, -31), new(-32, -24), new(29, -24), new(43, -30),
                    new(34, -37), new(-34, -37));
            }
            if (_isAvatar)
            {
                DrawAvatar(art);
                return;
            }
            switch (_symbol)
            {
                case Symbol.Shop: DrawChest(art); break;
                case Symbol.Rating: DrawTrophy(art); break;
                case Symbol.Home: DrawAbbey(art); break;
                case Symbol.Prestige: DrawGem(art); break;
                case Symbol.Profile: DrawShield(art); break;
                case Symbol.Level: DrawLevel(art); break;
                case Symbol.Energy: DrawEnergy(art); break;
                case Symbol.Settings: DrawGear(art); break;
                case Symbol.Play: DrawPlay(art); break;
                case Symbol.Lock: DrawLock(art); break;
                case Symbol.Map: DrawMap(art); break;
                case Symbol.Tower: DrawTower(art); break;
                case Symbol.Wall: DrawWall(art); break;
                case Symbol.Health: DrawHeart(art); break;
                case Symbol.Pause: DrawPause(art); break;
                case Symbol.Sawmill: DrawSawmill(art); break;
                case Symbol.Quarry: DrawQuarry(art); break;
                case Symbol.Mine: DrawMine(art); break;
                case Symbol.Forge: DrawForge(art); break;
                case Symbol.Shrine: DrawShrine(art); break;
                case Symbol.Trebuchet: DrawTrebuchet(art); break;
            }
        }

        private void DrawRatingRow(VertexHelper mesh, Rect rect)
        {
            var plate = new IconMesh(mesh, Vector2.zero, 1);
            var (rim, edge) = _medalPosition switch
            {
                1 => (0xb59058, 0x54412d),
                2 => (0x8d9294, 0x48464a),
                3 => (0xaa795b, 0x5e4036),
                _ => (0x6e5b49, 0x3c2e2c)
            };
            plate.Cut(rect, 11, 0x21191a, 0x100d0e);
            Rect frame = Rect.MinMaxRect(rect.xMin, rect.yMin + 3, rect.xMax, rect.yMax);
            plate.Cut(frame, 11, _active ? 0xe3b770 : rim, _active ? 0x8d553d : edge);
            Rect face = Rect.MinMaxRect(frame.xMin + 2, frame.yMin + 2, frame.xMax - 2, frame.yMax - 2);
            plate.Cut(face, 9, _active ? 0x633039 : 0x37302d, _active ? 0x351f26 : 0x251f20);
            if (_active)
            {
                plate.Box(face.xMin + 5, face.center.y - 16, face.xMin + 8, face.center.y + 16, 0xf4cf8b, 0xb57949);
            }
        }

        private static void DrawPlate(VertexHelper mesh, Rect rect, bool home, bool active)
        {
            var plate = new IconMesh(mesh, Vector2.zero, 1);
            float bevel = home ? 18 : 12;
            Rect shadow = Rect.MinMaxRect(rect.xMin, rect.yMin, rect.xMax, rect.yMax - 3);
            plate.Cut(shadow, bevel, 0x24171a, 0x100f12);
            Rect rim = Rect.MinMaxRect(rect.xMin + 1, rect.yMin + 4, rect.xMax - 1, rect.yMax);
            plate.Cut(rim, bevel, home ? 0xfbd490 : active ? 0xe8bd7d : 0x917656,
                home ? 0x8c4a29 : active ? 0x895333 : 0x443131);
            Rect face = Rect.MinMaxRect(rim.xMin + 4, rim.yMin + 6, rim.xMax - 4, rim.yMax - 4);
            plate.Cut(face, bevel - 3, active ? 0x87373e : home ? 0x553238 : 0x423a39, active ? 0x371b25 : 0x211c22);

            if (home || active)
            {
                float width = face.width * 0.34f;
                plate.Box(face.center.x - width / 2, rim.yMax - 2, face.center.x + width / 2, rim.yMax,
                    0xffe9b2, 0xd89451);
            }
        }

        private static void DrawFrame(VertexHelper mesh, Rect rect)
        {
            if (rect.width < 40 || rect.height < 40)
            {
                return;
            }

            var plate = new IconMesh(mesh, Vector2.zero, 1);
            float corner = Mathf.Min(24, Mathf.Min(rect.width, rect.height) * 0.25f);
            plate.Cut(rect, corner, 0x352429, 0x100d10);
            Rect rim = Rect.MinMaxRect(rect.xMin + 2, rect.yMin + 9, rect.xMax - 2, rect.yMax);
            plate.Cut(rim, corner, 0xe5b978, 0x694237);
            Rect inset = Rect.MinMaxRect(rim.xMin + 5, rim.yMin + 5, rim.xMax - 5, rim.yMax - 5);
            plate.Cut(inset, corner - 4, 0x6d4c3c, 0xb38655);
            Rect face = Rect.MinMaxRect(inset.xMin + 2, inset.yMin + 2, inset.xMax - 2, inset.yMax - 2);
            plate.Cut(face, corner - 5, 0x382b25, 0x21191b);
        }

        private static void DrawInset(VertexHelper mesh, Rect rect)
        {
            var plate = new IconMesh(mesh, Vector2.zero, 1);
            float corner = Mathf.Min(12, Mathf.Min(rect.width, rect.height) * 0.25f);
            plate.Cut(rect, corner, 0x61463b, 0x9a704f);
            Rect rim = Rect.MinMaxRect(rect.xMin + 2, rect.yMin + 3, rect.xMax - 2, rect.yMax - 2);
            plate.Cut(rim, corner - 1, 0x140e12, 0x432f2c);
            Rect face = Rect.MinMaxRect(rim.xMin + 3, rim.yMin + 2, rim.xMax - 3, rim.yMax - 5);
            plate.Cut(face, corner - 3, 0x241a1c, 0x302322);
        }

        private static void DrawChest(IconMesh art)
        {
            art.Face(0x98664c, 0x3e2529, new(-29, -29), new(-29, 4), new(39, 9), new(42, -20), new(34, -27));
            art.Face(0x8d4538, 0x4c2527, new(-39, -23), new(-39, 9), new(30, 9), new(30, -24), new(23, -31), new(-31, -31));
            art.Face(0xd17a60, 0x5a2d30, new(-39, 8), new(-35, 22), new(-25, 31), new(21, 31),
                new(34, 24), new(40, 12), new(31, 4), new(-36, 4));
            art.Face(0xc38561, 0x71423c, new(-25, 31), new(21, 31), new(34, 24), new(-30, 23));
            art.Box(-37, 3, 34, 9, 0xf5cc82, 0x9c5d36);
            art.Face(0xc2955a, 0x76523c, new(-37, -22), new(29, -22), new(34, -26), new(23, -32), new(-31, -32));
            art.Face(0xeecc8b, 0x95623c, new(-30, -24), new(-30, 13), new(-23, 28), new(-17, 28),
                new(-23, 12), new(-23, -26));
            art.Face(0xe0b679, 0x74472e, new(19, -24), new(19, 12), new(14, 30), new(22, 30),
                new(26, 12), new(26, -25));
            art.Face(0xffdd9a, 0x966138, new(-10, 7), new(6, 7), new(10, 2), new(10, -9), new(-2, -17), new(-11, -9), new(-11, 2));
            art.Solid(0x44292a, new(-5, 0), new(2, 0), new(3, -5), new(1, -9), new(-3, -9), new(-5, -4));
        }

        private static void DrawTrophy(IconMesh art)
        {
            art.Face(0xe4b670, 0x8f522e, new(-22, 22), new(-37, 23), new(-40, 15), new(-39, 5),
                new(-32, 6), new(-32, 17), new(-24, 16));
            art.Face(0xd6a46a, 0x714031, new(22, 22), new(37, 23), new(40, 15), new(39, 5),
                new(32, 6), new(32, 17), new(24, 16));
            art.Face(0xd09856, 0x78502e, new(-39, 6), new(-34, -5), new(-20, -10),
                new(-20, -4), new(-29, 0), new(-32, 6));
            art.Face(0xbc804a, 0x6c412a, new(39, 6), new(34, -5), new(20, -10), new(20, -4), new(29, 0), new(32, 6));
            art.Face(0xe8ae57, 0x94613a, new(-10, -7), new(10, -7), new(7, -19), new(19, -25),
                new(-19, -25), new(-7, -19));
            art.Face(0xb08c61, 0x654035, new(-24, -22), new(24, -22), new(30, -31), new(24, -35),
                new(-24, -35), new(-30, -31));
            art.Box(-21, -30, 21, -25, 0xe0bc7b, 0x946034);
            art.Face(0xe9b05d, 0x8c4728, new(-26, 25), new(28, 25), new(24, 5), new(15, -8),
                new(1, -15), new(-14, -8), new(-23, 5));
            art.Face(0xffd798, 0xb76c35, new(-24, 24), new(4, 24), new(6, -8), new(1, -14), new(-13, -8), new(-22, 5));
            art.Face(0xffe6aa, 0xd49952, new(-28, 25), new(-25, 31), new(25, 31), new(29, 26), new(23, 22), new(-23, 22));
            art.Face(0xb95953, 0x71363b, new(-8, 15), new(9, 15), new(8, 6), new(1, -1), new(-6, 5));
            art.Box(-11, 25, 9, 29, 0xffefc3, 0xe9b36d);
        }

        private static void DrawMedal(IconMesh art, int position)
        {
            var (light, rim, center, shade) = position switch
            {
                1 => (0xffe5a3, 0xb37b40, 0xf4c16e, 0xbb7e43),
                2 => (0xe4edf0, 0x6d818c, 0xc8d8dc, 0x889eac),
                _ => (0xffd2a6, 0x945639, 0xe7ad7e, 0xaf734f)
            };
            art.Face(0xaa605b, 0x582931, new(-23, 36), new(-5, 36), new(8, 9), new(-6, 3));
            art.Face(0xc27768, 0x763438, new(5, 36), new(23, 36), new(7, 6), new(-8, 9));
            art.Face(rim, 0x3d2928, new(-21, 8), new(-7, 13), new(11, 13), new(24, 4),
                new(25, -20), new(14, -33), new(-11, -36), new(-24, -23), new(-26, -5));
            art.Face(light, rim, new(-25, -17), new(-24, 1), new(-13, 12), new(11, 12),
                new(23, 1), new(23, -17), new(12, -30), new(-12, -30));
            art.Face(center, shade, new(-19, -16), new(-19, -1), new(-10, 7), new(9, 7),
                new(17, -1), new(17, -16), new(8, -24), new(-10, -24));
            art.Solid(light, new(-20, 2), new(-12, 10), new(6, 10), new(-9, 7), new(-17, 1));
        }

        private static void DrawLock(IconMesh art)
        {
            art.Face(0xd8bea0, 0x534441, new(-24, 5), new(-24, 20), new(-16, 31), new(-5, 36),
                new(10, 36), new(23, 27), new(28, 16), new(28, 5));
            art.Face(0x392d2c, 0x231c21, new(-13, 5), new(-13, 18), new(-8, 25), new(6, 26),
                new(15, 20), new(17, 5));
            art.Solid(0xf3d8aa, new(-22, 20), new(-15, 29), new(-5, 34), new(6, 34),
                new(-5, 31), new(-13, 26), new(-18, 20));
            art.Face(0xa77148, 0x5e352a, new(-23, 9), new(30, 5), new(35, -4), new(35, -27),
                new(26, -36), new(-22, -36), new(-30, -28));
            art.Face(0xf6cd91, 0x96603b, new(-30, -24), new(-30, 5), new(-24, 11), new(23, 11),
                new(29, 5), new(29, -26), new(22, -32), new(-22, -32));
            art.Face(0xc28755, 0x704131, new(-24, -24), new(-24, 4), new(23, 4), new(23, -24),
                new(18, -27), new(-19, -27));
            art.Box(-22, 6, 21, 9, 0xffe2aa, 0xdb9c5d);
            art.Solid(0x462d29, new(-3, -10), new(-7, -7), new(-7, -2), new(-3, 1), new(4, 1),
                new(8, -3), new(8, -7), new(3, -11), new(5, -20), new(-5, -20));
        }

        private static void DrawMap(IconMesh art)
        {
            art.Face(0x9c6548, 0x4d3028, new(-39, 14), new(-15, 30), new(41, 25), new(40, -20),
                new(15, -34), new(-13, -25), new(-39, -34));
            art.Face(0xffe0a7, 0xbc8852, new(-39, -28), new(-39, 18), new(-15, 31), new(-15, -17));
            art.Face(0xe1bb80, 0xa47554, new(-15, -17), new(-15, 31), new(12, 20), new(12, -29));
            art.Face(0xffdaa0, 0xb07d51, new(12, -29), new(12, 20), new(38, 31), new(38, -18));
            art.Solid(0xffedbc, new(-37, 17), new(-16, 29), new(-16, 26), new(-36, 14));
            art.Solid(0xf8d697, new(13, 19), new(36, 29), new(36, 26), new(13, 16));
            art.Face(0x929c82, 0x5c756d, new(-34, -10), new(-19, 6), new(-13, 4),
                new(-26, -12), new(-34, -16));
            art.Face(0x8e9a84, 0x63796f, new(-19, 6), new(-10, 9), new(14, -12), new(12, -20));
            art.Solid(0x956448, new(17, -7), new(30, -2), new(30, -5), new(17, -10));
            art.Face(0xcc7660, 0x752e31, new(17, 11), new(22, 16), new(29, 16), new(33, 11),
                new(32, 6), new(25, -2), new(18, 6));
            art.Box(23, 8, 28, 12, 0xf7ca95, 0xcb8f69);
        }

        private static void DrawHeart(IconMesh art)
        {
            art.Face(0xb66a4b, 0x452329, new(4, -37), new(-36, -2), new(-40, 17), new(-27, 33),
                new(21, 33), new(35, 24), new(40, 11), new(36, -3));
            art.Face(0xf6b38c, 0xb65d44, new(-37, -1), new(-37, 22), new(-26, 33), new(-13, 33),
                new(0, 24), new(0, -33));
            art.Face(0xde785f, 0x7f323b, new(0, 24), new(13, 33), new(26, 33), new(36, 23),
                new(36, 3), new(0, -33));
            art.Face(0xec8c74, 0xa84146, new(-33, 0), new(-33, 20), new(-24, 28), new(-14, 28),
                new(0, 17), new(0, -27));
            art.Solid(0xffc695, new(-31, 17), new(-23, 25), new(-14, 24), new(-20, 20), new(-29, 11));
        }

        private static void DrawPause(IconMesh art)
        {
            art.Box(-28, -31, -6, 27, 0x946445, 0x4b312b);
            art.Box(9, -31, 31, 27, 0x946445, 0x4b312b);
            art.Box(-30, -25, -12, 32, 0xffedc0, 0xc88c51);
            art.Box(6, -25, 25, 32, 0xffedc0, 0xc88c51);
        }

        private static void DrawTower(IconMesh art)
        {
            art.Face(0x9b7664, 0x493338, new(-19, -29), new(-19, 18), new(14, 18), new(30, 26),
                new(29, -22), new(14, -30));
            art.Face(0xd0b696, 0x83604e, new(-22, -29), new(-22, 17), new(13, 17), new(13, -29));
            art.Face(0xa28069, 0x5a403e, new(13, -29), new(13, 17), new(29, 25), new(29, -22));
            art.Face(0xb28c72, 0x614949, new(17, 18), new(17, 35), new(34, 42), new(34, 26));
            art.Face(0xe1c5a0, 0xa47c60, new(-29, 13), new(-29, 28), new(17, 28), new(17, 13));
            art.Box(-29, 25, -17, 36, 0xf3d6ac, 0xbb9575);
            art.Box(-9, 25, 2, 36, 0xe7cca5, 0xad8568);
            art.Box(9, 25, 17, 36, 0xe0c49e, 0xa27d64);
            art.Solid(0xf5d6a9, new(17, 35), new(34, 42), new(30, 43), new(17, 38));
            art.Face(0x5b423c, 0x342528, new(-10, -13), new(-10, -2), new(-5, 5), new(0, -2), new(0, -13));
            art.Box(-20, -23, -7, -20, 0xbf9b7c, 0x987152);
            art.Box(3, 4, 12, 7, 0xd9b99b, 0x99765c);
            art.Face(0xd0a78b, 0x744b3d, new(-27, -26), new(16, -26), new(34, -20), new(34, -27),
                new(17, -35), new(-27, -35));
        }

        private static void DrawWall(IconMesh art)
        {
            art.Face(0xa48d7e, 0x584647, new(6, -29), new(6, 10), new(38, 22), new(38, -15));
            art.Face(0xd8bd9f, 0x8c6e5d, new(-36, -20), new(-36, 17), new(6, 10), new(6, -29));
            art.Face(0xf0d5b1, 0xb18f73, new(-36, 17), new(-22, 25), new(23, 22), new(38, 22),
                new(6, 10));
            art.Face(0xd6b99a, 0x8c6e58, new(-36, 14), new(-36, 28), new(-23, 26), new(-23, 12));
            art.Face(0xe6cbaa, 0xa6886e, new(-36, 28), new(-27, 33), new(-14, 31), new(-23, 26));
            art.Face(0xd2b798, 0x967764, new(-18, 11), new(-18, 25), new(-6, 23), new(-6, 10));
            art.Face(0xd9bfa3, 0xa1836b, new(-1, 9), new(-1, 23), new(8, 21), new(8, 7));
            art.Face(0xb49c82, 0x745a50, new(8, 7), new(8, 21), new(17, 24), new(17, 12));
            art.Face(0xba9e82, 0x80655b, new(25, 15), new(25, 27), new(38, 33), new(38, 20));
            art.Face(0xdcc2a3, 0xa08570, new(-34, 1), new(-15, -2), new(-15, -5), new(-34, -2));
            art.Face(0xd3b599, 0x967864, new(-11, -10), new(4, -13), new(4, -16), new(-11, -13));
            art.Face(0xc1a28c, 0x785e57, new(13, -9), new(37, 1), new(37, -2), new(13, -12));
            art.Face(0xc0a087, 0x694d45, new(-39, -17), new(7, -25), new(41, -12), new(41, -18),
                new(7, -33), new(-39, -24));
        }

        private static void DrawAbbey(IconMesh art)
        {
            art.Face(0xa18064, 0x503831, new(11, -26), new(37, -26), new(42, -19), new(42, 0), new(17, 9), new(11, -5));
            art.Face(0x80736a, 0x51454a, new(21, -26), new(34, -26), new(37, -20), new(37, 23), new(25, 28), new(21, 18));
            art.Face(0xd2b493, 0x7a5b4a, new(10, -24), new(29, -24), new(29, 25), new(10, 25));
            art.Face(0xd5a361, 0x6d3c2c, new(7, 25), new(22, 40), new(39, 25), new(33, 20), new(11, 20));
            art.Solid(0xffd78e, new(21, 37), new(21, 45), new(23, 45), new(23, 37));
            art.Box(18, 41, 26, 43, 0xffdf9d, 0xc8894d);
            art.Solid(0x443236, new(15, 8), new(15, 15), new(19, 19), new(24, 15), new(24, 8));
            art.Face(0xffcb79, 0xad652d, new(17, 8), new(17, 14), new(19, 16), new(22, 14), new(22, 8));
            art.Face(0xa08268, 0x5f4845, new(-8, -29), new(20, -29), new(20, -4), new(-4, 7), new(-8, 0));
            art.Face(0x77605a, 0x392b32, new(-14, 3), new(1, 15), new(23, 2), new(23, -8), new(-7, -10));
            art.Face(0xdab994, 0x8b6650, new(-39, -28), new(-39, -3), new(-21, 15), new(-2, -3), new(-2, -28));
            art.Face(0xbc8859, 0x673c2f, new(-43, -5), new(-24, 17), new(-18, 17), new(2, -4), new(-3, -8), new(-21, 9), new(-39, -8));
            art.Face(0x52363a, 0x38242a, new(-30, -28), new(-30, -15), new(-23, -7), new(-16, -7), new(-10, -15), new(-10, -28));
            art.Face(0xdb9451, 0x693526, new(-27, -28), new(-27, -16), new(-22, -10), new(-18, -10), new(-13, -16), new(-13, -28));
            art.Box(-23, -26, -22, -16, 0xf1b26a, 0xa15b36);
            art.Face(0xbd6a62, 0x79323b, new(1, -10), new(8, -10), new(8, -22), new(4, -26), new(1, -22));
            art.Face(0xe0b892, 0x755041, new(-39, -27), new(35, -25), new(42, -29),
                new(35, -34), new(-36, -35), new(-43, -32));
        }

        private static void DrawGem(IconMesh art)
        {
            art.Face(0xf3c889, 0x70412a, new(-40, 9), new(-23, 30), new(23, 30), new(40, 9), new(2, -37), new(-3, -37));
            art.Solid(0x5d2438, new(-35, 8), new(-21, 25), new(21, 25), new(35, 8), new(0, -31));
            art.Face(0xffbeae, 0xac4461, new(-35, 8), new(-21, 25), new(-12, 25), new(-17, 8));
            art.Face(0xffa5a1, 0xcf6676, new(-17, 8), new(-12, 25), new(14, 25), new(18, 8));
            art.Face(0xef828a, 0x78283d, new(18, 8), new(14, 25), new(21, 25), new(35, 8));
            art.Face(0xf58288, 0x812f46, new(-35, 8), new(-17, 8), new(0, -31));
            art.Face(0xe37c87, 0xaf4761, new(-17, 8), new(18, 8), new(0, -31));
            art.Face(0x974255, 0x532237, new(18, 8), new(35, 8), new(0, -31));
            art.Solid(0xffdfbf, new(-20, 24), new(-13, 24), new(-17, 17), new(-22, 14), new(-25, 14));
            art.Solid(0xf7a193, new(-13, 8), new(11, 8), new(7, 5), new(-12, 5));
        }

        private static void DrawShield(IconMesh art)
        {
            DrawShieldBase(art, 0x65777a, 0x252d36);
            DrawHelmet(art, 0xe8e1cc, 0x5f6868);
        }

        private static void DrawShieldBase(IconMesh art, int light, int shade)
        {
            art.Face(0xd9b17a, 0x794a32, new(-32, 26), new(-7, 33), new(8, 33), new(33, 25),
                new(31, -8), new(19, -24), new(1, -36), new(-18, -25), new(-31, -9));
            art.Face(light, shade, new(-27, 22), new(0, 28), new(28, 22), new(26, -7),
                new(15, -21), new(1, -30), new(-14, -21), new(-26, -7));
            art.Solid(0xecc889, new(-25, 22), new(-19, 24), new(-18, 21), new(-23, 19));
        }

        private static void DrawHelmet(IconMesh art, int light, int shade)
        {
            art.Face(0x8fa0a3, 0x3a444b, new(-22, -12), new(-17, -8), new(-7, -4), new(7, -4),
                new(17, -8), new(22, -12), new(20, -18), new(1, -27), new(-19, -18));
            art.Face(light, shade, new(-16, 9), new(-13, 18), new(-5, 23), new(6, 23), new(14, 18),
                new(16, 9), new(13, -5), new(4, -12), new(-7, -12), new(-14, -5));
            art.Face(0x889b9a, 0x3c454a, new(4, 21), new(13, 18), new(16, 9), new(13, -5), new(4, -12), new(4, 0));
            art.Face(0x454b50, 0x27242b, new(-13, 5), new(-8, 7), new(9, 7), new(13, 3), new(9, 0), new(-10, 0));
            art.Box(-11, 8, 8, 10, 0xffe9c0, 0xaaa282);
        }

        internal readonly struct IconMesh
        {
            private readonly VertexHelper _mesh;
            private readonly Vector2 _origin;
            private readonly float _scale;

            public IconMesh(VertexHelper mesh, Vector2 origin, float scale)
            {
                _mesh = mesh;
                _origin = origin;
                _scale = scale;
            }

            public void Cut(Rect rect, float corner, int light, int shade) =>
                Face(light, shade, new(rect.xMin, rect.yMin + corner), new(rect.xMin, rect.yMax - corner),
                    new(rect.xMin + corner, rect.yMax), new(rect.xMax - corner, rect.yMax), new(rect.xMax, rect.yMax - corner),
                    new(rect.xMax, rect.yMin + corner), new(rect.xMax - corner, rect.yMin), new(rect.xMin + corner, rect.yMin));

            public void Box(float left, float bottom, float right, float top, int light, int shade) =>
                Face(light, shade, new(left, bottom), new(left, top), new(right, top), new(right, bottom));

            public void Solid(int rgb, params Vector2[] points) => Face(rgb, rgb, points);

            public void Face(int light, int shade, params Vector2[] points)
            {
                float bottom = points[0].y;
                float top = bottom;
                foreach (Vector2 point in points)
                {
                    bottom = Mathf.Min(bottom, point.y);
                    top = Mathf.Max(top, point.y);
                }
                Color tint = Rgb(light);
                Color shadow = Rgb(shade);
                int first = _mesh.currentVertCount;
                foreach (Vector2 point in points)
                {
                    float height = Mathf.InverseLerp(bottom, top, point.y);
                    _mesh.AddVert(_origin + point * _scale, Color.Lerp(shadow, tint, height), Vector2.zero);
                }
                for (int index = 1; index < points.Length - 1; index++)
                {
                    _mesh.AddTriangle(first, first + index, first + index + 1);
                }
            }

            private static Color32 Rgb(int value) => new((byte)(value >> 16), (byte)(value >> 8), (byte)value, 255);
        }
    }
}
#endif
