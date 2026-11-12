#if !UNITY_SERVER || UNITY_EDITOR
using TMPro;
using UnityEngine;

namespace VaticanDefendor.Client.Shell
{
    public sealed class ClientCombatHud : MonoBehaviour
    {
        private readonly TMP_Text[] _values = new TMP_Text[3];
        private readonly UnityEngine.UI.Image[] _fills = new UnityEngine.UI.Image[3];
        private UnityEngine.UI.Image _comboTimer;
        private TMP_Text _church;
        private TMP_Text _wave;
        private TMP_Text _ability;

        public void Initialize(Transform BattleArea, TMP_FontAsset Font, Sprite PanelSprite)
        {
            RectTransform Area = (RectTransform)transform;
            Area.anchorMin = Vector2.up;
            Area.anchorMax = Vector2.one;
            Area.pivot = new Vector2(0.5f, 1);
            Area.offsetMin = new Vector2(14, -76);
            Area.offsetMax = new Vector2(-14, -12);
            Color[] Colors = { new Color(1, 0.32f, 0.44f), new Color(0.36f, 0.81f, 0.68f), new Color(1, 0.39f, 0.48f) };
            string[] Names = { "Health", "Stamina", "Special" };
            string[] Labels = { "Здоровье", "Выносливость", "Спецприём" };
            for (int Index = 0; Index < 3; Index++)
            {
                GameObject Pill = CombatUIFactory.Panel(transform, Names[Index], new Color(0.08f, 0.13f, 0.09f, 0.92f), Vector2.zero, Vector2.zero, Vector2.zero);
                var Bounds = (RectTransform)Pill.transform;
                Bounds.anchorMin = new Vector2(Index / 3f, 0);
                Bounds.anchorMax = new Vector2((Index + 1) / 3f, 1);
                Bounds.offsetMin = new Vector2(3, 0);
                Bounds.offsetMax = new Vector2(-3, 0);
                Pill.GetComponent<UnityEngine.UI.Image>().raycastTarget = false;
                TMP_Text Caption = CombatUIFactory.Text(Pill.transform, "Caption", Labels[Index], new Vector2(0, 1), new Vector2(12, -5), new Vector2(180, 22), 15, Font);
                Caption.color = new Color(0.78f, 0.84f, 0.79f);
                _values[Index] = CombatUIFactory.Text(Pill.transform, "Value", "0", new Vector2(1, 0), new Vector2(-10, 6), new Vector2(55, 26), 19, Font);
                _values[Index].alignment = TextAlignmentOptions.MidlineRight;
                _values[Index].textWrappingMode = TextWrappingModes.NoWrap;
                if (Index == 0)
                {
                    _values[Index].rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, 78);
                }
                if (Index == 1)
                {
                    TMP_Text Symbol = CombatUIFactory.Text(Pill.transform, "Icon", "+", new Vector2(0, 0), new Vector2(10, 5), new Vector2(24, 28), 29, Font);
                    Symbol.color = Colors[Index];
                }
                else
                {
                    GameObject Symbol = new GameObject("Icon", typeof(RectTransform), typeof(CanvasRenderer), typeof(PhoneGraphic));
                    Symbol.transform.SetParent(Pill.transform, false);
                    PhoneGraphic Icon = Symbol.GetComponent<PhoneGraphic>();
                    Icon.Icon = PhoneIcon.Heart;
                    if (Index == 2)
                    {
                        Icon.Icon = PhoneIcon.Special;
                    }
                    Icon.color = Colors[Index];
                    Icon.raycastTarget = false;
                    Icon.rectTransform.anchorMin = Icon.rectTransform.anchorMax = Icon.rectTransform.pivot = Vector2.zero;
                    Icon.rectTransform.anchoredPosition = new Vector2(10, 8);
                    Icon.rectTransform.sizeDelta = new Vector2(24, 24);
                }
                GameObject Track = CombatUIFactory.Panel(Pill.transform, "Track", new Color(1, 1, 1, 0.12f), Vector2.zero, Vector2.zero, Vector2.zero);
                var TrackArea = (RectTransform)Track.transform;
                TrackArea.anchorMin = Vector2.zero;
                TrackArea.anchorMax = Vector2.right;
                TrackArea.offsetMin = new Vector2(42, 15);
                TrackArea.offsetMax = new Vector2(-65, 25);
                if (Index == 0)
                {
                    TrackArea.offsetMax = new Vector2(-88, 25);
                }
                Track.GetComponent<UnityEngine.UI.Image>().raycastTarget = false;
                _fills[Index] = CreateFill(Track.transform, "Fill", Colors[Index], PanelSprite);
                if (Index == 2)
                {
                    _comboTimer = CreateFill(Track.transform, "ComboTimer", new Color(1, 0.8f, 0.4f), PanelSprite);
                    _comboTimer.rectTransform.offsetMin = new Vector2(0, -6);
                    _comboTimer.rectTransform.offsetMax = new Vector2(0, -12);
                }
            }
            GameObject ChurchBadge = CombatUIFactory.Panel(BattleArea, "ChurchStatus", new Color(0.08f, 0.13f, 0.09f, 0.85f), Vector2.up, new Vector2(18, -86), new Vector2(230, 36));
            ChurchBadge.GetComponent<UnityEngine.UI.Image>().raycastTarget = false;
            _church = CombatUIFactory.Text(ChurchBadge.transform, "Value", "", Vector2.zero, new Vector2(10, 0), new Vector2(210, 36), 17, Font);
            _wave = CombatUIFactory.Text(BattleArea, "Wave", "", Vector2.zero, new Vector2(18, 72), new Vector2(420, 32), 18, Font);
            _wave.outlineWidth = 0.15f;
            _wave.outlineColor = Color.black;
            _ability = CombatUIFactory.Text(BattleArea, "AbilityStatus", "", Vector2.one, new Vector2(-18, -86), new Vector2(400, 36), 16, Font);
            _ability.alignment = TextAlignmentOptions.MidlineRight;
            _ability.outlineWidth = 0.15f;
            _ability.outlineColor = Color.black;
        }

        public void Render(ClientShellViewModel Model, string WaveStatus, string AbilityStatus)
        {
            ClientGameplaySnapshot Gameplay = Model.Gameplay;
            _values[0].text = Mathf.CeilToInt(Model.Combat.HeroHealth).ToString();
            _values[1].text = Mathf.FloorToInt(Gameplay.Stamina).ToString();
            _fills[0].fillAmount = 0;
            _fills[1].fillAmount = 0;
            if (Model.Combat.HeroMaximumHealth > 0)
            {
                _fills[0].fillAmount = Model.Combat.HeroHealth / Model.Combat.HeroMaximumHealth;
            }
            if (Gameplay.MaximumStamina > 0)
            {
                _fills[1].fillAmount = Gameplay.Stamina / Gameplay.MaximumStamina;
            }
            float Combo = 0;
            if (Gameplay.Action.ComboRequired > 0)
            {
                Combo = Mathf.Clamp01(Gameplay.Action.Combo / Gameplay.Action.ComboRequired);
            }
            _values[2].text = Mathf.RoundToInt(Combo * 100) + "%";
            if (Gameplay.Action.SpecialRemaining > 0)
            {
                _values[2].text = Gameplay.Action.SpecialRemaining.ToString("0.0") + "с";
            }
            _fills[2].fillAmount = Combo;
            _fills[0].color = new Color(1, 0.32f, 0.44f);
            if (_fills[0].fillAmount < 0.3f)
            {
                _fills[0].color = new Color(1, 0.32f, 0.44f, 0.65f + 0.35f * Mathf.Sin(Time.unscaledTime * 6));
            }
            _fills[1].color = new Color(0.36f, 0.81f, 0.68f);
            if (_fills[1].fillAmount < 0.25f)
            {
                _fills[1].color = new Color(1, 0.72f, 0.28f);
            }
            _fills[2].color = new Color(1, 0.39f, 0.48f);
            if (Combo >= 1 && Gameplay.Action.SpecialRemaining <= 0)
            {
                _fills[2].color = new Color(1, 0.81f, 0.46f, 0.7f + 0.3f * Mathf.Sin(Time.unscaledTime * 6));
            }
            _comboTimer.fillAmount = 0;
            if (Gameplay.ComboIdleSeconds > 0)
            {
                _comboTimer.fillAmount = Mathf.Clamp01(1 - Gameplay.Action.ComboIdle / Gameplay.ComboIdleSeconds);
            }
            _comboTimer.gameObject.SetActive(Combo > 0 && _comboTimer.fillAmount > 0);
            _church.text = "Церковь " + Model.Run.ChurchHealth.ToString("0") + "/" + Model.Run.ChurchMaximumHealth.ToString("0");
            if (Gameplay.Action.Shield > 0)
            {
                _values[0].text += "+" + Gameplay.Action.Shield.ToString("0");
            }
            _wave.text = WaveStatus;
            _ability.text = AbilityStatus;
        }

        private static UnityEngine.UI.Image CreateFill(Transform Parent, string Name, Color Color, Sprite Sprite)
        {
            GameObject Model = new GameObject(Name, typeof(RectTransform), typeof(UnityEngine.UI.Image));
            Model.transform.SetParent(Parent, false);
            UnityEngine.UI.Image Fill = Model.GetComponent<UnityEngine.UI.Image>();
            Fill.sprite = Sprite;
            Fill.type = UnityEngine.UI.Image.Type.Filled;
            Fill.fillMethod = UnityEngine.UI.Image.FillMethod.Horizontal;
            Fill.color = Color;
            Fill.raycastTarget = false;
            CombatUIFactory.Stretch(Fill.rectTransform, Vector2.zero, Vector2.zero);
            return Fill;
        }
    }
}
#endif
