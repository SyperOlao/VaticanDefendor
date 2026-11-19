#if !UNITY_SERVER || UNITY_EDITOR
using System;
using UnityEngine;
using UnityEngine.UI;
using VaticanDefendor.Client.Presentation;
using SettingsIcon = VaticanDefendor.Client.Accounts.AccountHubIcon.Symbol;

namespace VaticanDefendor.Client.Accounts
{
    public sealed partial class OnlineAccountView
    {
        public const float SettingsControlsHeight = 626;

        public static void StyleSettingsDialog(RectTransform window, Button close, Button done)
        {
            window.GetComponent<Image>().enabled = false;
            AddSettingsRelief(window, "Window frame").ConfigureFrame();
            StyleSettingsButton(close, primary: false);
            StyleSettingsButton(done, primary: true);
        }

        private static void StyleSettingsButton(Button button, bool primary)
        {
            button.image.enabled = false;
            AccountReliefGraphic surface = AddSettingsRelief(button.transform, "Button plate");
            surface.ConfigurePlate(action: primary);
            SetReliefButtonGraphic(button, surface);
        }

        private static AccountReliefGraphic AddSettingsRelief(Transform parent, string name)
        {
            AccountReliefGraphic surface = AddReliefGraphic(parent, name);
            surface.transform.SetAsFirstSibling();
            Place(surface.rectTransform, Vector2.zero, Vector2.one, 0, 0, 0, 0);
            return surface;
        }

        private void ShowHubSettings()
        {
            RectTransform page = BeginHub(HubPage.Settings);
            RectTransform crest = ReliefPanel(page, "Settings crest");
            Place(crest, TopLeft, TopLeft, 2, -86, 78, -10);
            Place(AddReliefIcon(crest, SettingsIcon.Settings).rectTransform, Vector2.zero, Vector2.one, 7, 8, -7, -6);
            Text heading = ShopHeading(page, "Settings title", "Настройки", 65, relief: true);
            Place(heading.rectTransform, TopLeft, TopRight, 92, -91, -10, -4);
            RectTransform rule = ShopPanel(page, "Settings trim", SiegeUiTheme.Line);
            Place(rule, TopLeft, TopRight, 0, -102, 0, -100);

            RectTransform list = CreateAccountList(page, "Settings list", 115, 8);
            AddSettingsControls(list, ClientMapSettings.InitialFraction, inRun: false);
        }

        public static Action<float> AddSettingsControls(RectTransform parent, float initialMapFraction, bool inRun,
            Action<float> applyMapFraction = null)
        {
            var container = new GameObject("Settings controls", typeof(RectTransform), typeof(LayoutElement));
            container.transform.SetParent(parent, false);
            var content = (RectTransform)container.transform;
            Place(content, TopLeft, TopRight, 0, -SettingsControlsHeight, 0, 0);
            container.GetComponent<LayoutElement>().preferredHeight = SettingsControlsHeight;

            RectTransform map = ReliefPanel(content, "Map settings");
            Place(map, TopLeft, TopRight, 0, -222, 0, 0);
            AddSettingsSectionTitle(map, "Размер карты", SettingsIcon.Map);
            Text hint = SettingsLabel(map, "Map setting", inRun
                    ? "Границу карты можно перетаскивать."
                    : "Доля экрана для карты в следующем забеге",
                23, SiegeUiTheme.Muted);
            Place(hint.rectTransform, TopLeft, TopRight, 18, -126, -18, -80);

            int count = ClientMapSettings.Presets.Count;
            var choices = new AccountReliefGraphic[count];
            void HighlightMapPreset(float selected)
            {
                for (int index = 0; index < count; index++)
                {
                    choices[index].ConfigurePlate(active: Mathf.Abs(ClientMapSettings.Presets[index] - selected) < 0.005f);
                }
            }
            for (int index = 0; index < count; index++)
            {
                float fraction = ClientMapSettings.Presets[index];
                string percent = $"{Mathf.RoundToInt(fraction * 100)}%";
                Button choice = ReliefButton(map, "Map " + percent, () =>
                {
                    ClientMapSettings.InitialFraction = fraction;
                    applyMapFraction?.Invoke(fraction);
                    HighlightMapPreset(fraction);
                });
                RectTransform rect = (RectTransform)choice.transform;
                Place(rect, new Vector2((float)index / count, 0), new Vector2((float)(index + 1) / count, 0),
                    index == 0 ? 18 : 6, 17, index == count - 1 ? -18 : -6, 89);
                Text label = SettingsButtonCaption(rect, percent);
                label.fontSize = 29;
                choices[index] = (AccountReliefGraphic)choice.targetGraphic;
            }
            HighlightMapPreset(initialMapFraction);

            RectTransform effects = ReliefPanel(content, "Combat effects settings");
            Place(effects, TopLeft, TopRight, 0, -622, 0, -236);
            AddSettingsSectionTitle(effects, "Боевые эффекты", SettingsIcon.Energy);
            AddSettingsToggle(effects, 89, "Цифры урона", "Числа над бойцами",
                () => ClientCombatDisplaySettings.DamageNumbers, enabled => ClientCombatDisplaySettings.DamageNumbers = enabled);
            AddSettingsToggle(effects, 185, "Вспышка при уроне", "Красная рамка, когда ранят героя",
                () => ClientCombatDisplaySettings.DamageFlash, enabled => ClientCombatDisplaySettings.DamageFlash = enabled);
            AddSettingsToggle(effects, 281, "Тряска камеры", "Движение камеры при ударах",
                () => ClientCombatDisplaySettings.CameraShake, enabled => ClientCombatDisplaySettings.CameraShake = enabled);
            return HighlightMapPreset;
        }

        private static void AddSettingsSectionTitle(RectTransform panel, string caption, SettingsIcon symbol)
        {
            Place(AddReliefIcon(panel, symbol).rectTransform, TopLeft, TopLeft, 19, -73, 79, -13);
            Text title = ShopHeading(panel, "Section title", caption, 44, relief: true);
            Place(title.rectTransform, TopLeft, TopRight, 90, -74, -18, -9);
        }

        private static void AddSettingsToggle(RectTransform panel, float top, string title, string hint,
            Func<bool> readValue, Action<bool> setValue)
        {
            Image line = SiegeUiTheme.Plate(panel, "Separator", SiegeUiTheme.Line);
            Place(line.rectTransform, TopLeft, TopRight, 18, -top - 2, -18, -top);
            Text label = SettingsLabel(panel, "Setting name", title, 27, SiegeUiTheme.Ink, FontStyle.Bold);
            Place(label.rectTransform, TopLeft, TopRight, 20, -top - 49, -162, -top - 11);
            Text detail = SettingsLabel(panel, "Setting detail", hint, 21, SiegeUiTheme.Muted);
            Place(detail.rectTransform, TopLeft, TopRight, 20, -top - 85, -162, -top - 49);

            Button toggle = ReliefButton(panel, title, () => setValue(!readValue()));
            RectTransform button = (RectTransform)toggle.transform;
            Place(button, TopRight, TopRight, -147, -top - 84, -18, -top - 9);
            Text value = SettingsButtonCaption(button, "");
            void Refresh()
            {
                bool enabled = readValue();
                ((AccountReliefGraphic)toggle.targetGraphic).ConfigurePlate(active: enabled);
                value.text = enabled ? "Вкл." : "Выкл.";
            }
            toggle.onClick.AddListener(Refresh);
            Refresh();
        }

        private static Text SettingsButtonCaption(RectTransform parent, string caption)
        {
            Text label = SettingsLabel(parent, "Caption", caption, 26, SiegeUiTheme.Ink, FontStyle.Bold);
            label.alignment = TextAnchor.MiddleCenter;
            AddTextReliefShadow(label);
            Place(label.rectTransform, Vector2.zero, Vector2.one, 8, 5, -8, -3);
            return label;
        }

        private static Text SettingsLabel(Transform parent, string name, string value, int size, Color tint,
            FontStyle style = FontStyle.Normal)
        {
            var item = new GameObject(name, typeof(RectTransform), typeof(Text));
            item.transform.SetParent(parent, false);
            var label = item.GetComponent<Text>();
            label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            label.fontStyle = style;
            label.fontSize = size;
            label.color = tint;
            label.text = value;
            label.supportRichText = false;
            label.alignment = TextAnchor.MiddleLeft;
            label.raycastTarget = false;
            return label;
        }
    }
}
#endif
