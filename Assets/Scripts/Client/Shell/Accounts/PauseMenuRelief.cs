#if !UNITY_SERVER || UNITY_EDITOR
using UnityEngine;
using UnityEngine.UI;
using VaticanDefendor.Client.Presentation;
using ReliefMesh = VaticanDefendor.Client.Accounts.AccountReliefGraphic.IconMesh;
using Symbol = VaticanDefendor.Client.Accounts.AccountHubIcon.Symbol;

namespace VaticanDefendor.Client.Accounts
{
    [RequireComponent(typeof(CanvasRenderer))]
    public sealed class PauseMenuRelief : MaskableGraphic
    {
        public enum Command { Resume, Settings, Shop, MainMenu }

        public static void AddPanel(RectTransform panel)
        {
            panel.GetComponent<Image>().enabled = false;
            var item = new GameObject("Forged pause frame", typeof(RectTransform), typeof(PauseMenuRelief));
            item.transform.SetParent(panel, false);
            item.transform.SetAsFirstSibling();
            var plate = item.GetComponent<PauseMenuRelief>();
            plate.raycastTarget = false;
            SiegeUiTheme.Place(plate.rectTransform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
        }

        public static void AddButtonFace(Button button, Command command)
        {
            button.GetComponent<Image>().enabled = false;
            AccountReliefGraphic face = AddRelief(button.transform, "Bevel");
            face.transform.SetAsFirstSibling();
            face.ConfigurePlate(action: command == Command.Resume);
            face.raycastTarget = true;
            SiegeUiTheme.Place(face.rectTransform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);

            button.targetGraphic = face;
            button.transition = Selectable.Transition.ColorTint;
            ColorBlock colors = ColorBlock.defaultColorBlock;
            colors.highlightedColor = new Color32(255, 242, 224, 255);
            colors.selectedColor = new Color32(255, 228, 189, 255);
            colors.pressedColor = new Color32(194, 157, 140, 255);
            colors.disabledColor = new Color32(134, 121, 114, 255);
            colors.fadeDuration = 0.12f;
            button.colors = colors;

            AccountReliefGraphic icon = AddRelief(face.transform, "Command icon");
            icon.ConfigureIcon(command switch
            {
                Command.Resume => Symbol.Play,
                Command.Settings => Symbol.Settings,
                Command.Shop => Symbol.Shop,
                _ => Symbol.Home
            });
            var leftCenter = new Vector2(0, 0.5f);
            SiegeUiTheme.Place(icon.rectTransform, leftCenter, leftCenter, new Vector2(15, -22), new Vector2(65, 28));
        }

        private static AccountReliefGraphic AddRelief(Transform parent, string name)
        {
            var item = new GameObject(name, typeof(RectTransform), typeof(AccountReliefGraphic));
            item.transform.SetParent(parent, false);
            var graphic = item.GetComponent<AccountReliefGraphic>();
            graphic.raycastTarget = false;
            return graphic;
        }

        protected override void OnPopulateMesh(VertexHelper mesh)
        {
            mesh.Clear();
            Rect panel = rectTransform.rect;
            var relief = new ReliefMesh(mesh, Vector2.zero, 1);
            relief.Cut(Rect.MinMaxRect(panel.xMin - 3, panel.yMin - 8, panel.xMax + 4, panel.yMax - 7),
                25, 0x151012, 0x130f10);
            relief.Cut(Rect.MinMaxRect(panel.xMin, panel.yMin, panel.xMax, panel.yMax - 5),
                24, 0x63473a, 0x25171a);
            Rect rim = Rect.MinMaxRect(panel.xMin + 1, panel.yMin + 10, panel.xMax - 1, panel.yMax);
            relief.Cut(rim, 24, 0xc89f69, 0x63412f);
            relief.Cut(Inset(rim, 4), 21, 0x775742, 0x32242a);
            relief.Cut(Inset(rim, 12), 16, 0x241a20, 0x79573d);
            Rect face = Inset(rim, 14);
            relief.Cut(face, 14, 0x403537, 0x21191e);

            DrawRivet(relief, face.xMin + 10, face.yMax - 11);
            DrawRivet(relief, face.xMax - 10, face.yMax - 11);
            DrawRivet(relief, face.xMin + 10, face.yMin + 10);
            DrawRivet(relief, face.xMax - 10, face.yMin + 10);

            DrawSeal(new ReliefMesh(mesh, new Vector2(panel.center.x, panel.yMax - 43), 1));
            float dividerY = panel.yMax - 158;
            float centerX = panel.center.x;
            relief.Box(face.xMin + 32, dividerY - 1, centerX - 15, dividerY + 1, 0x8e6b4c, 0x4e382e);
            relief.Box(centerX + 15, dividerY - 1, face.xMax - 32, dividerY + 1, 0x8e6b4c, 0x4e382e);
            relief.Face(0xd8af77, 0x775036, new(centerX - 8, dividerY), new(centerX, dividerY + 4),
                new(centerX + 8, dividerY), new(centerX, dividerY - 4));
        }

        private static Rect Inset(Rect rect, float amount) =>
            Rect.MinMaxRect(rect.xMin + amount, rect.yMin + amount, rect.xMax - amount, rect.yMax - amount);

        private static void DrawRivet(ReliefMesh relief, float x, float y)
        {
            relief.Face(0x291d20, 0x160f13, new(x - 5, y - 1), new(x, y + 4), new(x + 5, y - 1), new(x, y - 6));
            relief.Face(0xcb9f6e, 0x684732, new(x - 4, y), new(x, y + 4), new(x + 4, y), new(x, y - 4));
        }

        private static void DrawSeal(ReliefMesh relief)
        {
            relief.Cut(new Rect(-33, -33, 66, 61), 15, 0x2e1d21, 0x180f14);
            relief.Cut(new Rect(-32, -27, 64, 57), 15, 0xf4cc8b, 0x895338);
            relief.Cut(new Rect(-27, -22, 54, 47), 11, 0x863e47, 0x391d28);
            relief.Box(-13, -13, -4, 13, 0xffdfa2, 0xc88b53);
            relief.Box(4, -13, 13, 13, 0xffdfa2, 0xc88b53);
            relief.Box(-6, -13, -4, 11, 0xbc814e, 0x754230);
            relief.Box(11, -13, 13, 11, 0xbc814e, 0x754230);
        }
    }
}
#endif
