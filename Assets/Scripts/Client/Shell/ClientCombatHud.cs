#if !UNITY_SERVER || UNITY_EDITOR
using TMPro;
using UnityEngine;
using VaticanDefendor.Client.Accounts;
using VaticanDefendor.Client.Presentation;
using Emblem = VaticanDefendor.Client.Accounts.CombatHudRelief.Emblem;
using Tone = VaticanDefendor.Client.Accounts.CombatMeterGraphic.Tone;

namespace VaticanDefendor.Client.Shell
{
    public sealed class ClientCombatHud : MonoBehaviour
    {
        private readonly CombatMeterGraphic[] _meters = new CombatMeterGraphic[3];
        private CombatMeterGraphic _shieldMeter;
        private TMP_Text _specialCaption;
        private CombatMeterGraphic _comboTimer;
        private CombatMeterGraphic _churchMeter;
        private TMP_Text _wave;
        private CombatMeterGraphic _waveMeter;

        public void Initialize(Transform battleArea, TMP_FontAsset font)
        {
            var area = (RectTransform)transform;
            SiegeUiTheme.Place(area, Vector2.up, Vector2.one, new Vector2(12, -103), new Vector2(-12, -33));
            RectTransform health = AddVital(area, 0, "Health", "Здоровье", Emblem.Health, font);
            _shieldMeter = CombatMeterGraphic.Create(health, "Shield");
            Place(_shieldMeter.rectTransform, Vector2.zero, Vector2.right, 12, 4, -12, 9);
            AddVital(area, 1, "Stamina", "Выносливость", Emblem.Stamina, font);
            RectTransform special = AddVital(area, 2, "Special", "Спецприём", Emblem.Special, font);
            _specialCaption = special.Find("Caption").GetComponent<TMP_Text>();
            _comboTimer = CombatMeterGraphic.Create(special, "ComboTimer");
            Place(_comboTimer.rectTransform, Vector2.zero, Vector2.right, 12, 4, -12, 9);

            RectTransform church = CombatHudRelief.Plate(battleArea, "ChurchStatus");
            Place(church, Vector2.up, new Vector2(0.53f, 1), 12, -162, -4, -109);
            Place(CombatHudRelief.Icon(church, Emblem.Church), Vector2.zero, Vector2.up, 10, 9, 48, -8);
            TMP_Text churchCaption = Readout(church, "Caption", 23, font);
            churchCaption.text = "Церковь";
            Place(churchCaption.rectTransform, Vector2.zero, Vector2.right, 52, 22, -11, 49);
            _churchMeter = CombatMeterGraphic.Create(church, "Church health");
            Place(_churchMeter.rectTransform, Vector2.zero, Vector2.right, 53, 9, -11, 21);

            RectTransform wave = CombatHudRelief.Plate(battleArea, "Wave");
            Place(wave, new Vector2(0.53f, 1), Vector2.one, 4, -162, -12, -109);
            Place(CombatHudRelief.Icon(wave, Emblem.Wave), Vector2.zero, Vector2.up, 11, 8, 47, -7);
            _wave = Readout(wave, "Caption", 22, font);
            _wave.color = SiegeUiTheme.Brass;
            Place(_wave.rectTransform, Vector2.zero, Vector2.one, 53, 4, -11, -3);
            _waveMeter = CombatMeterGraphic.Create(wave, "First wave progress");
            Place(_waveMeter.rectTransform, Vector2.zero, Vector2.right, 53, 9, -11, 21);
            _waveMeter.gameObject.SetActive(false);
        }

        private RectTransform AddVital(RectTransform area, int index, string name, string title, Emblem emblem,
            TMP_FontAsset font)
        {
            RectTransform panel = CombatHudRelief.Plate(area, name);
            Place(panel, new Vector2(index / 3f, 0), new Vector2((index + 1) / 3f, 1),
                index == 0 ? 0 : 4, 0, index == 2 ? 0 : -4, 0);
            Place(CombatHudRelief.Icon(panel, emblem), Vector2.zero, Vector2.zero, 9, 29, 44, 64);
            TMP_Text caption = Readout(panel, "Caption", 21, font);
            caption.text = title;
            caption.color = SiegeUiTheme.Muted;
            Place(caption.rectTransform, Vector2.zero, Vector2.right, 46, 29, -10, 64);

            CombatMeterGraphic meter = CombatMeterGraphic.Create(panel, "Meter");
            Place(meter.rectTransform, Vector2.zero, Vector2.right, 11, 10, -11, 28);
            _meters[index] = meter;
            return panel;
        }

        public void Render(ClientShellViewModel model)
        {
            ClientGameplaySnapshot gameplay = model.Gameplay;
            float health = Fraction(model.Combat.HeroHealth, model.Combat.HeroMaximumHealth);
            float stamina = Fraction(gameplay.Stamina, gameplay.MaximumStamina);
            float combo = Fraction(gameplay.Action.Combo, gameplay.Action.ComboRequired);

            bool specialReady = combo >= 1 && gameplay.Action.SpecialRemaining <= 0;
            _specialCaption.text = gameplay.Action.SpecialRemaining > 0 ? "Перезарядка"
                : specialReady ? "Приём готов" : "Спецприём";
            _meters[0].SetLevel(health, HealthTone(health));
            _shieldMeter.SetLevel(Fraction(gameplay.Action.Shield, model.Combat.HeroMaximumHealth), Tone.Shield);
            _meters[1].SetLevel(stamina, stamina < 0.25f ? Tone.Gold : Tone.Stamina);
            _meters[2].SetLevel(combo, specialReady ? Tone.Ready : Tone.Gold);
            float timer = gameplay.ComboIdleSeconds > 0 ? 1 - Fraction(gameplay.Action.ComboIdle, gameplay.ComboIdleSeconds) : 0;
            _comboTimer.SetLevel(combo > 0 ? timer : 0, Tone.Timer);

            float churchHealth = Fraction(model.Run.ChurchHealth, model.Run.ChurchMaximumHealth);
            _churchMeter.SetLevel(churchHealth, HealthTone(churchHealth));
            _wave.text = gameplay.WavePhase switch
            {
                0 => "До первой волны",
                3 => "До следующей волны",
                4 => "Волны пройдены",
                _ => "Волна идёт"
            };
            bool showWaveProgress = gameplay.WavePhase == 0 && gameplay.WaveNumber > 0 &&
                gameplay.InitialWaveDelaySeconds > 0;
            if (_waveMeter.gameObject.activeSelf != showWaveProgress)
            {
                _waveMeter.gameObject.SetActive(showWaveProgress);
                Place(_wave.rectTransform, Vector2.zero, Vector2.one, 53, showWaveProgress ? 22 : 4, -11, -3);
            }
            if (showWaveProgress)
            {
                _waveMeter.SetLevel(1 - Fraction(gameplay.WaveCountdown, gameplay.InitialWaveDelaySeconds), Tone.Gold);
            }
        }

        private static float Fraction(float value, float max) => max > 0 ? Mathf.Clamp01(value / max) : 0;

        private static Tone HealthTone(float fraction) => fraction < 0.3f ? Tone.LowHealth : Tone.Health;

        private static TMP_Text Readout(Transform parent, string name, int size, TMP_FontAsset font)
        {
            TMP_Text text = CombatUIFactory.Text(parent, name, "", Vector2.zero, Vector2.zero, Vector2.zero, size, font);
            text.color = SiegeUiTheme.Ink;
            text.fontStyle = FontStyles.Bold;
            text.enableAutoSizing = true;
            text.fontSizeMin = 15;
            text.fontSizeMax = size;
            text.textWrappingMode = TextWrappingModes.NoWrap;
            return text;
        }

        private static void Place(RectTransform rect, Vector2 min, Vector2 max, float left, float bottom, float right, float top)
            => SiegeUiTheme.Place(rect, min, max, new Vector2(left, bottom), new Vector2(right, top));
    }
}
#endif
