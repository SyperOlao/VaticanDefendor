#if !UNITY_SERVER || UNITY_EDITOR
namespace VaticanDefendor.Client.Accounts
{
    internal sealed partial class AccountReliefGraphic
    {
        private static void DrawTrebuchet(IconMesh art)
        {
            // Timber base and the rear side of the frame.
            art.Face(0xc2966f, 0x67433c, new(-42, -30), new(-28, -22), new(42, -22), new(28, -32));
            art.Face(0xb88a65, 0x79523e, new(28, -39), new(28, -32), new(42, -22), new(42, -29));
            art.Box(-42, -39, 28, -30, 0xf0ba7e, 0x8b5036);
            art.Face(0xa97e62, 0x64443b, new(2, 17), new(10, 17), new(34, -25), new(23, -25));
            art.Face(0xb38a66, 0x6d493d, new(-23, -25), new(2, 17), new(10, 17), new(-12, -25));
            // The lighter near supports leave the center open.
            art.Face(0xe3b483, 0x935f44, new(-11, 12), new(-2, 12), new(25, -31), new(13, -32));
            art.Face(0xf5c890, 0xa16a47, new(-32, -32), new(-11, 12), new(-2, 12), new(-20, -32));
            // The hanging counterweight and the highlighted throwing arm.
            art.Box(-33, -15, -27, 0, 0xe1bd8b, 0x9d6343);
            art.Face(0xc8c6b4, 0x58656a, new(-38, -23), new(-40, -13), new(-36, -9), new(-23, -9),
                new(-19, -14), new(-21, -23));
            art.Face(0xbe8455, 0x784b3a, new(-36, -7), new(-37, 1), new(31, 42), new(37, 40), new(-29, -8));
            art.Face(0xffd797, 0xbe8758, new(-37, 1), new(-34, 5), new(31, 44), new(37, 40), new(-30, 0));
            art.Box(-9, 5, -2, 12, 0xffe3a8, 0x906247);
            // A pale stone at the end of the sling reads against the burgundy plate.
            art.Solid(0xd7ad7b, new(33, 41), new(36, 40), new(41, 27), new(38, 26));
            art.Face(0xecede0, 0x8b9da1, new(32, 23), new(34, 30), new(41, 32), new(46, 27),
                new(44, 21), new(36, 19));
        }

        private static void DrawSawmill(IconMesh art)
        {
            art.Face(0x85644f, 0x51392f, new(-27, -25), new(-27, 16), new(26, 18), new(38, 8), new(35, -20));
            art.Face(0xd7af7d, 0x885739, new(-36, -24), new(-36, 10), new(-15, 25), new(12, 8), new(12, -23));
            art.Face(0xc1715c, 0x703a3d, new(-43, 9), new(-17, 35), new(17, 29), new(41, 12), new(13, 6), new(-17, 23));
            art.Solid(0xe3a276, new(-42, 9), new(-18, 35), new(17, 29), new(-16, 31));
            art.Box(-30, -22, -16, 0, 0x695044, 0x442c2f);
            art.Box(4, -24, 32, -14, 0xc99868, 0x714630);
            art.Face(0x483235, 0x261f25, new(0, -24), new(-2, -9), new(6, 3), new(22, 3),
                new(31, -6), new(30, -19), new(19, -28), new(8, -29));
            art.Face(0xf2e6c6, 0x6f939e, new(3, -17), new(0, -8), new(9, 1), new(21, 2),
                new(28, -6), new(27, -17), new(18, -24), new(9, -23));
            art.Box(13, -13, 18, -8, 0x667482, 0x46525d);
            art.Face(0xdbac70, 0x75482e, new(-42, -24), new(-30, -18), new(-7, -21), new(-6, -31),
                new(-13, -36), new(-38, -34));
            art.Face(0xf3cc90, 0xa96f46, new(-42, -24), new(-37, -20), new(-31, -23), new(-30, -30),
                new(-36, -34), new(-41, -30));
        }

        private static void DrawQuarry(IconMesh art)
        {
            art.Face(0xaeb2ad, 0x51565b, new(-40, -23), new(-35, 5), new(-18, 25), new(9, 29),
                new(30, 18), new(41, -7), new(29, -30), new(-11, -36));
            art.Face(0xd8cdb2, 0x827e73, new(-35, 4), new(-18, 24), new(9, 28), new(29, 17), new(11, 6), new(-17, 4));
            art.Face(0xb3b6ae, 0x686e6d, new(-35, -8), new(-17, 4), new(11, 6), new(31, 16), new(38, 2), new(17, -10), new(-13, -12));
            art.Face(0x939b99, 0x5c6265, new(-39, -22), new(-35, -8), new(-13, -12), new(17, -10), new(34, -2), new(36, -14),
                new(11, -25), new(-16, -27));
            art.Face(0xe0d6b6, 0x888d88, new(-38, -27), new(-30, -16), new(-17, -16), new(-9, -27), new(-17, -34), new(-34, -34));
            art.Face(0xc79262, 0x74472e, new(9, -27), new(3, -23), new(26, 16), new(31, 13));
            art.Face(0xdbe3de, 0x677e89, new(15, 26), new(25, 24), new(35, 19), new(43, 8),
                new(33, 12), new(23, 18));
            art.Solid(0xf4dfb8, new(16, 25), new(25, 24), new(34, 19), new(25, 21));
        }

        private static void DrawMine(IconMesh art)
        {
            art.Face(0xb6b6a8, 0x63696b, new(-42, -27), new(-38, 1), new(-23, 23), new(-3, 35),
                new(15, 32), new(32, 17), new(42, -25), new(28, -32), new(-24, -34));
            art.Face(0xd6cbb2, 0x848f8b, new(-38, 1), new(-23, 23), new(-3, 35), new(15, 32), new(3, 19), new(-15, 12));
            art.Face(0x9daaa6, 0x535e67, new(5, 18), new(15, 32), new(32, 17), new(42, -25), new(20, -23));
            art.Face(0x343036, 0x15171e, new(-24, -28), new(-24, -1), new(-12, 17), new(6, 17),
                new(20, 4), new(21, -28));
            art.Face(0xdebd83, 0x80533d, new(-26, -28), new(-24, 9), new(-18, 15), new(-16, -27));
            art.Face(0xb98c60, 0x684438, new(17, -26), new(13, 10), new(22, 13), new(27, -27));
            art.Face(0xedc895, 0x9c6c46, new(-25, 10), new(-17, 17), new(18, 17), new(24, 11), new(20, 5), new(-22, 5));
            art.Face(0xb3a68c, 0x72665f, new(-22, -31), new(-14, -25), new(11, -24), new(29, -33), new(17, -36), new(-22, -36));
            art.Face(0xc1dce0, 0x526f80, new(22, -26), new(31, -17), new(40, -17), new(45, -24),
                new(34, -33), new(24, -33));
            art.Solid(0xe1e7d5, new(22, -26), new(31, -17), new(40, -17), new(33, -23));
        }

        private static void DrawForge(IconMesh art)
        {
            art.Face(0xa67652, 0x513a33, new(-25, -32), new(-23, -15), new(23, -15), new(30, -25),
                new(21, -34), new(-18, -36));
            art.Face(0xc69263, 0x7d543c, new(-27, -18), new(-10, -11), new(19, -11), new(27, -18), new(19, -23), new(-22, -23));
            art.Face(0x8c9ba0, 0x3c4855, new(-21, -17), new(-21, -12), new(-10, -8), new(-9, 2),
                new(11, 2), new(10, -9), new(24, -10), new(25, -17));
            art.Face(0x95a8af, 0x4c5e68, new(-43, 12), new(-37, 17), new(21, 17), new(41, 10), new(29, 3),
                new(20, -4), new(-12, -4), new(-28, 2));
            art.Face(0xe9e7d6, 0x8eabb2, new(-43, 12), new(-32, 22), new(24, 22), new(41, 10),
                new(29, 8), new(-33, 8));
            art.Face(0xeab880, 0x8c553b, new(4, 22), new(20, 40), new(26, 34), new(13, 19));
            art.Face(0xb8c3bb, 0x617578, new(19, 36), new(27, 43), new(39, 36), new(39, 28), new(29, 26));
            art.Solid(0xffd18d, new(-16, 25), new(-20, 32), new(-14, 31), new(-10, 26));
            art.Solid(0xe99f60, new(-4, 27), new(-5, 34), new(0, 31), new(0, 27));
        }

        private static void DrawShrine(IconMesh art)
        {
            art.Face(0x7b978b, 0x465c58, new(-22, -25), new(-22, 14), new(19, 17), new(32, 9), new(32, -24));
            art.Face(0xe5c9a1, 0x91755e, new(-29, -26), new(-29, 6), new(-6, 30), new(20, 9), new(20, -26));
            art.Face(0x80b4a4, 0x365d61, new(-35, 8), new(-6, 34), new(18, 30), new(36, 11), new(20, 7), new(-6, 23), new(-28, 4));
            art.Solid(0xb0cfb2, new(-33, 8), new(-6, 33), new(17, 29), new(-5, 29));
            art.Face(0x8bad91, 0x426664, new(-19, -25), new(-19, -3), new(-10, 8), new(-1, 8),
                new(7, -3), new(7, -25));
            art.Box(-9, -16, -3, 3, 0xffe9b6, 0xd0ae70);
            art.Box(-15, -8, 3, -3, 0xffe9b6, 0xd0ae70);
            art.Face(0xd5b78b, 0x83644d, new(-33, -25), new(23, -25), new(38, -20), new(38, -26),
                new(21, -34), new(-33, -34));
        }
    }
}
#endif
