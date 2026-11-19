#if !UNITY_SERVER || UNITY_EDITOR
using UnityEngine;
using UnityEngine.UI;
using VaticanDefendor.Client.Presentation;
using VaticanDefendor.Construction;
using Symbol = VaticanDefendor.Client.Accounts.AccountHubIcon.Symbol;

namespace VaticanDefendor.Client.Accounts
{
    public static class CombatHudRelief
    {
        public enum Emblem { Health, Stamina, Special, Church, Wave, Pause }

        public static RectTransform Plate(Transform parent, string name)
        {
            AccountReliefGraphic surface = Create(parent, name);
            surface.ConfigurePlate();
            return surface.rectTransform;
        }

        public static void AddPanelFace(RectTransform panel)
        {
            RectTransform plate = Plate(panel, "Bevel");
            plate.SetAsFirstSibling();
            CombatUIFactory.Stretch(plate, Vector2.zero, Vector2.zero);
        }

        public static void StyleButton(Button button, bool primary = false)
        {
            button.image.enabled = false;
            AccountReliefGraphic surface = Create(button.transform, "Bevel");
            surface.transform.SetAsFirstSibling();
            surface.ConfigurePlate(action: primary);
            surface.raycastTarget = true;
            CombatUIFactory.Stretch(surface.rectTransform, Vector2.zero, Vector2.zero);

            button.targetGraphic = surface;
            button.transition = Selectable.Transition.ColorTint;
            ColorBlock colors = ColorBlock.defaultColorBlock;
            colors.highlightedColor = new Color32(255, 242, 215, 255);
            colors.selectedColor = new Color32(255, 220, 183, 255);
            colors.pressedColor = new Color32(187, 151, 130, 255);
            colors.disabledColor = new Color32(129, 120, 114, 255);
            colors.fadeDuration = 0.1f;
            button.colors = colors;
        }

        public static void SetSelected(Button button, bool selected)
        {
            ((AccountReliefGraphic)button.targetGraphic).ConfigurePlate(active: selected);
        }

        public static RectTransform Icon(Transform parent, Emblem emblem)
        {
            var graphic = Create(parent, "Emblem");
            graphic.ConfigureIcon(emblem switch
            {
                Emblem.Health => Symbol.Health,
                Emblem.Stamina => Symbol.Energy,
                Emblem.Church => Symbol.Home,
                Emblem.Wave => Symbol.Profile,
                Emblem.Pause => Symbol.Pause,
                _ => Symbol.Level
            });
            return graphic.rectTransform;
        }

        public static RectTransform BuildingIcon(Transform parent, StructureKind kind)
        {
            var graphic = Create(parent, "Building icon");
            graphic.ConfigureIcon(kind switch
            {
                StructureKind.Wall => Symbol.Wall,
                StructureKind.Tower => Symbol.Tower,
                StructureKind.Sawmill => Symbol.Sawmill,
                StructureKind.Quarry => Symbol.Quarry,
                StructureKind.IronMine => Symbol.Mine,
                StructureKind.Forge => Symbol.Forge,
                StructureKind.HealingShrine => Symbol.Shrine,
                StructureKind.Church => Symbol.Home,
                StructureKind.Artillery => Symbol.Trebuchet,
                _ => Symbol.Level
            });
            return graphic.rectTransform;
        }

        private static AccountReliefGraphic Create(Transform parent, string name)
        {
            var item = new GameObject(name, typeof(RectTransform), typeof(AccountReliefGraphic));
            item.transform.SetParent(parent, false);
            var graphic = item.GetComponent<AccountReliefGraphic>();
            graphic.raycastTarget = false;
            return graphic;
        }
    }
}
#endif
