using TMPro;
using UnityEngine;

[DefaultExecutionOrder(200)]
public sealed class MergedHudPresenter : MonoBehaviour
{
    public ActiveCombatGame Game;
    public TMP_Text[] Values;
    public UnityEngine.UI.Image[] Fills;
    public UnityEngine.UI.Image ComboTimer;
    public TMP_Text WaveStatus;
    public UnityEngine.UI.Button[] BuildingButtons;
    public SpecialReadyEdgeEffect DamageEdges;
    private TacticalMapAction[] MapActions;
    private PhoneGraphic GoldIcon;

    private void Start()
    {
        MapActions = Game.Hud.GetComponentsInChildren<TacticalMapAction>(true);
        GoldIcon = Game.Hud.Mobile.GoldEnemiesText.transform.parent.Find("Coin").GetComponent<PhoneGraphic>();
    }

    private void LateUpdate()
    {
        if (Game.Rules == null)
        {
            return;
        }
        MobileCombatInterface Mobile = Game.Hud.Mobile;
        foreach (TacticalMapAction Action in MapActions)
        {
            if (Action.name == "CollapseMap")
            {
                Action.gameObject.SetActive(Mobile.bMapFullscreen);
            }
            if (Action.Command == TacticalMapCommand.Fullscreen)
            {
                Action.gameObject.SetActive(Game.bMapExpanded || Mobile.bMapFullscreen);
            }
            if (Action.Command == TacticalMapCommand.Fullscreen)
            {
                string Caption = "Развернуть";
                if (Mobile.bMapFullscreen)
                {
                    Caption = "Свернуть";
                }
                Action.GetComponentInChildren<TMP_Text>().text = Caption;
            }
            if (Action.name == "ExpandMap")
            {
                string Caption = "Открыть\nкарту";
                if (Game.bMapExpanded)
                {
                    Caption = "Закрыть\nкарту";
                }
                Action.GetComponentInChildren<TMP_Text>().text = Caption;
            }
            if (Action.Command == TacticalMapCommand.Overview)
            {
                PhoneGraphic Graphic = Action.GetComponentInChildren<PhoneGraphic>();
                Graphic.Icon = PhoneIcon.Overview;
                if (Game.Root.MapPresenter.bOverview)
                {
                    Graphic.Icon = PhoneIcon.PlayerFocus;
                }
                Graphic.SetVerticesDirty();
            }
            if (Action.Command == TacticalMapCommand.Sell || Action.Command == TacticalMapCommand.Trebuchet)
            {
                Color ButtonColor = new Color(0.09f, 0.15f, 0.11f, 0.91f);
                if (Action.Command == TacticalMapCommand.Trebuchet && Game.bTrebuchet)
                {
                    ButtonColor = new Color(0.4f, 0.28f, 0.12f, 0.95f);
                }
                if (Action.Command == TacticalMapCommand.Sell && Game.bSellBuildings)
                {
                    ButtonColor = new Color(0.39f, 0.21f, 0.17f, 0.95f);
                }
                Action.GetComponent<UnityEngine.UI.Image>().color = ButtonColor;
            }
            if (Action.Command == TacticalMapCommand.Pan)
            {
                Color ButtonColor = new Color(0.12f, 0.16f, 0.18f, 1);
                if (Game.Root.MapPresenter.bPanMode)
                {
                    ButtonColor = new Color(0.5f, 0.3f, 0.08f, 1);
                }
                Action.GetComponent<UnityEngine.UI.Image>().color = ButtonColor;
            }
        }
        Game.Session.GameplayHeader.SetActive(false);
        Mobile.Split.offsetMax = Vector2.zero;
        Game.Hud.HelpText.gameObject.SetActive(false);
        Game.Hud.HealthText.transform.parent.gameObject.SetActive(false);
        Mobile.StaminaPanel.gameObject.SetActive(false);
        Mobile.WeaponText.transform.parent.gameObject.SetActive(false);
        Mobile.RatioText.gameObject.SetActive(false);
        Mobile.SwipeText.transform.parent.gameObject.SetActive(false);
        Mobile.ModeControls.SetActive(false);
        Mobile.BuildHint.transform.parent.gameObject.SetActive(false);
        int RequiredGold = Game.Rules.Structures[Game.StructureIndex].Price;
        if (Game.bTrebuchet)
        {
            RequiredGold = Game.Rules.TrebuchetPrice;
        }
        if (Game.bSellBuildings)
        {
            RequiredGold = 0;
        }
        GoldIcon.color = new Color(0.98f, 0.75f, 0.14f);
        GoldIcon.rectTransform.localScale = Vector3.one;
        if (Game.Gold < RequiredGold)
        {
            float Pulse = 0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * Mathf.PI * 2 / 0.85f);
            GoldIcon.color = new Color(1, 0.27f, 0.37f, 0.65f + Pulse * 0.35f);
            GoldIcon.rectTransform.localScale = Vector3.one * (1 + Pulse * 0.12f);
        }
        RectTransform Notice = Game.Hud.ErrorText.rectTransform;
        Transform NoticeParent = Mobile.BattlePane;
        Vector2 NoticeAnchor = new Vector2(0.5f, 0.5f);
        Vector2 NoticePosition = new Vector2(0, -40);
        if (Game.bMapExpanded || Mobile.bMapFullscreen)
        {
            NoticeParent = Game.Hud.MapPanel;
            NoticeAnchor = Vector2.zero;
            NoticePosition = new Vector2(8, 58);
        }
        if (Notice.parent != NoticeParent)
        {
            Notice.SetParent(NoticeParent, false);
        }
        Notice.anchorMin = NoticeAnchor;
        Notice.anchorMax = NoticeAnchor;
        Notice.pivot = NoticeAnchor;
        Notice.anchoredPosition = NoticePosition;
        Notice.sizeDelta = new Vector2(Mathf.Min(350, Mobile.SafeFrame.rect.width - 78), 40);
        Game.Hud.ErrorText.text = Game.ErrorMessage;
        Game.Hud.ErrorText.gameObject.SetActive(!string.IsNullOrEmpty(Game.ErrorMessage));
        Values[0].text = Mathf.RoundToInt(Game.Health).ToString();
        Values[1].text = Mathf.FloorToInt(Game.Stamina).ToString();
        Values[2].text = $"{Game.Combo.Fraction * 100:0}%";
        Fills[0].fillAmount = Game.Health / Game.Rules.MaximumHealth;
        Game.Hud.HitOverlay.enabled = false;
        if (DamageEdges != null)
        {
            float Injury = Mathf.Max(Game.HitFeedback * 0.6f, Mathf.Clamp01((0.4f - Fills[0].fillAmount) / 0.4f) * 0.65f);
            DamageEdges.SetReady(Injury > 0 && !Game.Session.bMainMenuOpen, Injury);
        }
        Fills[0].color = new Color(1, 0.32f, 0.44f);
        if (Fills[0].fillAmount < 0.3f)
        {
            Fills[0].color = new Color(1, 0.32f, 0.44f, 0.65f + 0.35f * Mathf.Sin(Time.unscaledTime * 6));
        }
        Fills[1].fillAmount = Mathf.Min(1, Game.Stamina / Game.Rules.MaximumStamina);
        Fills[2].fillAmount = Game.Combo.Fraction;
        foreach (UnityEngine.UI.Image Fill in Fills)
        {
            Fill.sprite = Game.Hud.RoundedPanelSprite;
        }
        ComboTimer.sprite = Game.Hud.RoundedPanelSprite;
        Color SpecialColor = new Color(1, 0.39f, 0.48f);
        if (Game.bSpecialReady)
        {
            SpecialColor = new Color(1, 0.81f, 0.46f, 0.7f + 0.3f * Mathf.Sin(Time.unscaledTime * 6));
        }
        Fills[2].color = SpecialColor;
        ComboTimer.fillAmount = Mathf.Clamp01(1 - Game.Combo.IdleSeconds / 3);
        ComboTimer.gameObject.SetActive(Game.Combo.Points > 0 && Game.Combo.IdleSeconds < 3);
        WaveStatus.text = $"Волна {Game.WaveNumber}";
        if (Game.WaveCountdown >= 0)
        {
            WaveStatus.text += $" — следующая через {Mathf.CeilToInt(Game.WaveCountdown)} с";
        }
        for (int Index = 0; Index < BuildingButtons.Length; Index++)
        {
            TMP_Text Label = BuildingButtons[Index].GetComponentInChildren<TMP_Text>();
            Label.text = Game.Rules.Structures[Index].Name + "\n" + Game.Rules.Structures[Index].Price + " зол.";
            Label.color = Color.white;
            if (Game.Gold < Game.Rules.Structures[Index].Price)
            {
                Label.color = new Color(1, 0.45f, 0.45f);
            }
            BuildingButtons[Index].GetComponent<UnityEngine.UI.Image>().color = new Color(0.14f, 0.2f, 0.13f);
            if (Game.StructureIndex == Index && !Game.bTrebuchet && !Game.bSellBuildings)
            {
                BuildingButtons[Index].GetComponent<UnityEngine.UI.Image>().color = new Color(0.3f, 0.23f, 0.12f);
            }
        }
    }
}
