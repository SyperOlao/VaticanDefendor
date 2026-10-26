using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public sealed class BuildingStatusPresenter : MonoBehaviour
{
    public ActiveCombatGame Game;

    private sealed class BuildingDisplay
    {
        public RectTransform Panel;
        public TMP_Text Health;
        public TMP_Text Status;
        public RectTransform Fill;
        public Image FillImage;
        public TMP_Text[] UpgradePads;
    }

    private readonly Dictionary<ActiveCombatGame.StructureState, BuildingDisplay> Displays = new Dictionary<ActiveCombatGame.StructureState, BuildingDisplay>();
    private readonly List<ActiveCombatGame.StructureState> Removed = new List<ActiveCombatGame.StructureState>();

    private BuildingDisplay CreateDisplay(ActiveCombatGame.StructureState Structure)
    {
        bool bProduction = Structure.Rules.Kind == CombatStructureKind.Sawmill || Structure.Rules.Kind == CombatStructureKind.Armory;
        float Height = 64;
        float Width = 1.65f;
        if (bProduction)
        {
            Height = 100;
            Width = 2.3f;
        }
        GameObject Model = new GameObject("BuildingStatus", typeof(RectTransform), typeof(Canvas));
        Model.transform.SetParent(Structure.Model.transform, false);
        Canvas Canvas = Model.GetComponent<Canvas>();
        Canvas.renderMode = RenderMode.WorldSpace;
        Canvas.worldCamera = Game.WorldCamera;
        RectTransform Panel = (RectTransform)Model.transform;
        Panel.sizeDelta = new Vector2(256, Height);
        Panel.localScale = Vector3.one * (Width / 256);
        GameObject Background = CombatUIFactory.Panel(Panel, "Background", new Color(0.067f, 0.094f, 0.125f, 0.87f), Vector2.zero, Vector2.zero, Vector2.zero);
        CombatUIFactory.Stretch((RectTransform)Background.transform, Vector2.zero, Vector2.zero);
        Background.GetComponent<Image>().sprite = null;
        Background.GetComponent<Image>().raycastTarget = false;
        BuildingDisplay Display = new BuildingDisplay { Panel = Panel };
        Display.Health = CombatUIFactory.Text(Panel, "Health", "", new Vector2(0.5f, 1), new Vector2(0, -2), new Vector2(256, 36), 28, Game.Hud.Font);
        Display.Health.alignment = TextAlignmentOptions.Center;
        Display.Health.fontStyle = FontStyles.Bold;
        GameObject Track = CombatUIFactory.Panel(Panel, "Track", new Color(0.22f, 0.25f, 0.29f), Vector2.up, new Vector2(8, -40), new Vector2(240, 16));
        Track.GetComponent<Image>().sprite = null;
        Track.GetComponent<Image>().raycastTarget = false;
        GameObject Fill = CombatUIFactory.Panel(Track.transform, "Fill", Color.green, Vector2.zero, Vector2.zero, Vector2.zero);
        Display.Fill = (RectTransform)Fill.transform;
        CombatUIFactory.Stretch(Display.Fill, Vector2.zero, Vector2.zero);
        Display.FillImage = Fill.GetComponent<Image>();
        Display.FillImage.sprite = null;
        Display.FillImage.raycastTarget = false;
        Display.Status = CombatUIFactory.Text(Panel, "Status", "", new Vector2(0.5f, 0), Vector2.zero, new Vector2(256, 38), 25, Game.Hud.Font);
        Display.Status.alignment = TextAlignmentOptions.Center;
        Display.Status.color = new Color(1, 0.84f, 0.4f);
        Display.Status.gameObject.SetActive(bProduction);
        if (Structure.Rules.Kind == CombatStructureKind.Armory)
        {
            Display.UpgradePads = new TMP_Text[2];
            for (int Index = 0; Index < 2; Index++)
            {
                GameObject Ground = new GameObject("UpgradePad", typeof(RectTransform), typeof(Canvas));
                Ground.transform.SetParent(Structure.Model.transform, false);
                Ground.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
                RectTransform Area = (RectTransform)Ground.transform;
                Area.sizeDelta = new Vector2(512, 256);
                Area.localScale = new Vector3(2.4f / 512, 1.5f / 256, 1);
                Area.localPosition = new Vector3(-1.3f + Index * 2.6f, 0.08f, 3.1f);
                Area.localRotation = Quaternion.Euler(90, 180, 0);
                GameObject Border = CombatUIFactory.Panel(Area, "Border", new Color(0.87f, 0.74f, 0.42f), Vector2.zero, Vector2.zero, Vector2.zero);
                CombatUIFactory.Stretch((RectTransform)Border.transform, Vector2.zero, Vector2.zero);
                Border.GetComponent<Image>().raycastTarget = false;
                GameObject GroundBackground = CombatUIFactory.Panel(Area, "Background", new Color(0.09f, 0.14f, 0.2f), Vector2.zero, Vector2.zero, Vector2.zero);
                CombatUIFactory.Stretch((RectTransform)GroundBackground.transform, new Vector2(12, 12), new Vector2(-12, -12));
                GroundBackground.GetComponent<Image>().raycastTarget = false;
                TMP_Text Label = CombatUIFactory.Text(Area, "Upgrade", "", new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(480, 230), 36, Game.Hud.Font);
                Label.alignment = TextAlignmentOptions.Center;
                Display.UpgradePads[Index] = Label;
            }
        }
        return Display;
    }

    private void LateUpdate()
    {
        if (Game.Rules == null)
        {
            return;
        }
        foreach (ActiveCombatGame.StructureState Structure in Game.Structures)
        {
            if (!Displays.TryGetValue(Structure, out BuildingDisplay Display))
            {
                Display = CreateDisplay(Structure);
                Displays.Add(Structure, Display);
            }
            Vector3 Offset = Vector3.up * 3.1f;
            if (Structure.Rules.Kind == CombatStructureKind.Church)
            {
                Offset.y = 5.3f;
            }
            else if (Structure.Rules.Kind == CombatStructureKind.Wall)
            {
                Offset.y = 1.85f;
                if (Structure.HalfExtents.x > 4)
                {
                    Offset.x = -3.5f;
                }
            }
            Display.Panel.SetPositionAndRotation(Structure.Position + Offset, Game.WorldCamera.transform.rotation);
            Display.Health.text = $"{Mathf.CeilToInt(Structure.Health)} / {Structure.Rules.Health:0}";
            float Fraction = Mathf.Clamp01(Structure.Health / Structure.Rules.Health);
            Display.Fill.anchorMax = new Vector2(Fraction, 1);
            Display.FillImage.color = new Color(0.39f, 0.84f, 0.6f);
            if (Fraction < 0.3f)
            {
                Display.FillImage.color = new Color(0.94f, 0.36f, 0.42f);
            }
            if (Structure.Rules.Kind == CombatStructureKind.Sawmill)
            {
                Display.Status.text = $"{Structure.StoredGold} золота";
            }
            else if (Structure.Rules.Kind == CombatStructureKind.Armory)
            {
                Display.UpgradePads[0].text = UpgradeCaption(false);
                Display.UpgradePads[1].text = UpgradeCaption(true);
                Display.Status.text = "Кузница готова";
                if (Structure.ForgeRemaining > 0)
                {
                    Display.Status.text = $"Ковка: {Structure.ForgeRemaining:0.0} с";
                    foreach (TMP_Text Pad in Display.UpgradePads)
                    {
                        Pad.text = $"Ковка\n{Structure.ForgeRemaining:0.0} с";
                    }
                }
            }
        }
        Removed.Clear();
        foreach (ActiveCombatGame.StructureState Structure in Displays.Keys)
        {
            if (!Game.Structures.Contains(Structure))
            {
                Removed.Add(Structure);
            }
        }
        foreach (ActiveCombatGame.StructureState Structure in Removed)
        {
            Displays.Remove(Structure);
        }
    }

    private string UpgradeCaption(bool bHealth)
    {
        int Level = Game.AttackUpgrades + 1;
        int Price = 40;
        string Description = "Атака +10";
        if (bHealth)
        {
            Level = Game.HealthUpgrades + 1;
            Price = 35;
            Description = "HP +25";
        }
        foreach (ActiveCombatGame.StructureState Structure in Game.Structures)
        {
            if (Structure.ForgeRemaining > 0 && Structure.bHealthUpgrade == bHealth)
            {
                Level++;
            }
        }
        foreach (UpgradeMilestone Milestone in Game.Rules.UpgradeMilestones)
        {
            if (Milestone.bHealth != bHealth || Milestone.Level != Level)
            {
                continue;
            }
            float Percent = Mathf.Abs(Milestone.Multiplier - 1) * 100;
            switch (Milestone.Bonus)
            {
                case ProgressionBonus.SpecialDamage:
                    Description += $"\nСпецурон +{Percent:0}%";
                    break;
                case ProgressionBonus.DashDistance:
                    Description += $"\nДальность дэша +{Percent:0}%";
                    break;
                case ProgressionBonus.DashDamage:
                    Description += $", урон +{Percent:0}%";
                    break;
                case ProgressionBonus.ChargeDuration:
                    Description += $"\nВремя заряда -{Percent:0}%";
                    break;
                case ProgressionBonus.SpecialRadius:
                    Description += $"\nРадиус спецатаки +{Percent:0}%";
                    break;
                case ProgressionBonus.MovementSpeed:
                    Description += $"\nСкорость +{Percent:0}%";
                    break;
                case ProgressionBonus.StaminaRecovery:
                    Description += $"\nВосстановление выносливости +{Percent:0}%";
                    break;
                case ProgressionBonus.IncomingDamage:
                    Description += $"\nВходящий урон -{Percent:0}%";
                    break;
                case ProgressionBonus.DefenseStrength:
                    Description += $"\nЗащитная спецатака +{Percent:0}%";
                    break;
            }
        }
        return $"Уровень {Level}: {Price} золота\n{Description}";
    }
}
