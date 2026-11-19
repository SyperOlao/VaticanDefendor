#if !UNITY_SERVER || UNITY_EDITOR
using UnityEngine;
using UnityEngine.UI;

namespace VaticanDefendor.Client.Accounts
{
    [RequireComponent(typeof(CanvasRenderer))]
    internal sealed class AccountHubIcon : MaskableGraphic
    {
        internal enum Symbol
        {
            Shop, Rating, Home, Prestige, Profile, Energy, Level, Settings, Play, Lock, Map,
            Tower, Wall, Health, Pause, Sawmill, Quarry, Mine, Forge, Shrine, Trebuchet
        }

        private Symbol _symbol;

        internal void SetSymbol(Symbol symbol, Color tint)
        {
            _symbol = symbol;
            color = tint;
            raycastTarget = false;
            SetVerticesDirty();
        }

        protected override void OnPopulateMesh(VertexHelper mesh)
        {
            mesh.Clear();
            switch (_symbol)
            {
                case Symbol.Shop:
                    Path(mesh, new(-22, 5), new(-22, -21), new(22, -21), new(22, 5));
                    Path(mesh, new(-28, 5), new(-20, 22), new(20, 22), new(28, 5), new(-28, 5));
                    Path(mesh, new(-9, 22), new(-13, 5));
                    Path(mesh, new(9, 22), new(13, 5));
                    Path(mesh, new(-9, -21), new(-9, -5), new(9, -5), new(9, -21));
                    break;
                case Symbol.Rating:
                    Path(mesh, new(-15, 23), new(15, 23), new(15, 7), new(9, -4), new(0, -9), new(-9, -4), new(-15, 7), new(-15, 23));
                    Path(mesh, new(-15, 16), new(-24, 16), new(-24, 8), new(-19, 3), new(-13, 2));
                    Path(mesh, new(15, 16), new(24, 16), new(24, 8), new(19, 3), new(13, 2));
                    Path(mesh, new(0, -9), new(0, -18));
                    Path(mesh, new(-12, -23), new(12, -23));
                    break;
                case Symbol.Home:
                    Path(mesh, new(-25, -23), new(-25, 4), new(-15, 4), new(-15, 16), new(0, 26),
                        new(15, 16), new(15, 4), new(25, 4), new(25, -23), new(-25, -23));
                    Path(mesh, new(-7, -23), new(-7, -8), new(0, -2), new(7, -8), new(7, -23));
                    Path(mesh, new(-17, -4), new(-17, -12));
                    Path(mesh, new(17, -4), new(17, -12));
                    break;
                case Symbol.Prestige:
                    Path(mesh, new(-24, 13), new(-12, 23), new(12, 23), new(24, 13), new(0, -24), new(-24, 13));
                    Path(mesh, new(-24, 13), new(24, 13));
                    Path(mesh, new(-12, 23), new(-7, 13), new(0, -24), new(7, 13), new(12, 23));
                    break;
                case Symbol.Profile:
                    Path(mesh, new(-12, 12), new(-8, 21), new(0, 24), new(8, 21), new(12, 12),
                        new(8, 3), new(0, 0), new(-8, 3), new(-12, 12));
                    Path(mesh, new(-23, -23), new(-21, -14), new(-13, -7), new(13, -7), new(21, -14), new(23, -23), new(-23, -23));
                    break;
                case Symbol.Energy:
                    Path(mesh, new(6, 27), new(-18, -3), new(-2, -3), new(-6, -26), new(19, 7), new(3, 7), new(6, 27));
                    break;
                case Symbol.Level:
                    Path(mesh, new(-21, 14), new(0, 23), new(21, 14), new(21, -6), new(0, -24), new(-21, -6), new(-21, 14));
                    Path(mesh, new(-9, -4), new(0, 6), new(9, -4));
                    break;
                case Symbol.Settings:
                    Path(mesh, new(-7, 25), new(7, 25), new(8, 17), new(14, 14), new(21, 17), new(27, 6), new(20, 0),
                        new(27, -6), new(21, -17), new(14, -14), new(8, -17), new(7, -25), new(-7, -25),
                        new(-8, -17), new(-14, -14), new(-21, -17), new(-27, -6), new(-20, 0), new(-27, 6),
                        new(-21, 17), new(-14, 14), new(-8, 17), new(-7, 25));
                    Path(mesh, new(0, 9), new(8, 4), new(8, -4), new(0, -9), new(-8, -4), new(-8, 4), new(0, 9));
                    break;
                case Symbol.Play:
                    Path(mesh, new(-13, 20), new(20, 0), new(-13, -20), new(-13, 20));
                    break;
            }
        }

        private void Path(VertexHelper mesh, params Vector2[] points)
        {
            Rect rect = rectTransform.rect;
            float scale = Mathf.Min(rect.width, rect.height) / 64;
            for (int index = 1; index < points.Length; index++)
            {
                AccountLoginGraphic.Stroke(mesh, rect.center + points[index - 1] * scale,
                    rect.center + points[index] * scale, 3.4f * scale, color);
            }
        }
    }
}
#endif
