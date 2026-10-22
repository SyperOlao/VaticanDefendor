using TMPro;
using UnityEngine;

public static class MergedHudInstaller
{
    public static void Install(ActiveCombatGame Game)
    {
        CombatUIFactory.RoundSprite = Game.Hud.RoundedPanelSprite;
        Game.Hud.Mobile.Handle.GetComponent<UnityEngine.UI.Image>().color = CombatUIFactory.Hex("#1b251b");
        ((RectTransform)Game.Hud.Mobile.Handle.Find("Grip")).sizeDelta = new Vector2(48, 3);
        InstallMapControls(Game);
        InstallReferenceLayout(Game);
        if (Game.GetComponent<MergedHudPresenter>() != null)
        {
            MergedHudPresenter Existing = Game.GetComponent<MergedHudPresenter>();
            InstallDamageEdges(Existing);
            InstallSpecialIcon(Existing);
            for (int Index = 0; Index < Existing.BuildingButtons.Length; Index++)
            {
                ((RectTransform)Existing.BuildingButtons[Index].transform).anchoredPosition = new Vector2(4 + Index * 112, 0);
            }
            return;
        }
        CombatHUD Hud = Game.Hud;
        MergedHudPresenter Presenter = Game.gameObject.AddComponent<MergedHudPresenter>();
        Presenter.Game = Game;
        Presenter.Values = new TMP_Text[3];
        Presenter.Fills = new UnityEngine.UI.Image[3];
        GameObject Vitals = new GameObject("BattleVitals", typeof(RectTransform));
        Vitals.transform.SetParent(Hud.Mobile.BattlePane, false);
        RectTransform VitalsArea = (RectTransform)Vitals.transform;
        VitalsArea.anchorMin = Vector2.up;
        VitalsArea.anchorMax = Vector2.one;
        VitalsArea.pivot = new Vector2(0.5f, 1);
        VitalsArea.offsetMin = new Vector2(10, -38);
        VitalsArea.offsetMax = new Vector2(-10, -8);
        Color[] Colors = { new Color(1, 0.32f, 0.44f), new Color(0.36f, 0.81f, 0.68f), new Color(1, 0.39f, 0.48f) };
        string[] Symbols = { "♥", "+", "✦" };
        for (int Index = 0; Index < 3; Index++)
        {
            GameObject Pill = CombatUIFactory.Panel(Vitals.transform, "Vital" + Index, new Color(0.08f, 0.13f, 0.09f, 0.9f), Vector2.zero, Vector2.zero, Vector2.zero);
            RectTransform Area = (RectTransform)Pill.transform;
            Area.anchorMin = new Vector2(Index / 3f, 0);
            Area.anchorMax = new Vector2((Index + 1) / 3f, 1);
            Area.offsetMin = new Vector2(2, 0);
            Area.offsetMax = new Vector2(-2, 0);
            Pill.GetComponent<UnityEngine.UI.Image>().raycastTarget = false;
            TMP_Text Icon = CombatUIFactory.Text(Pill.transform, "Icon", Symbols[Index], new Vector2(0, 0.5f), new Vector2(8, 0), new Vector2(14, 20), 13, Hud.Font);
            Icon.color = Colors[Index];
            Icon.raycastTarget = false;
            Presenter.Values[Index] = CombatUIFactory.Text(Pill.transform, "Value", "100", new Vector2(1, 0.5f), new Vector2(-7, 0), new Vector2(31, 20), 10, Hud.Font);
            Presenter.Values[Index].alignment = TextAlignmentOptions.Right;
            Presenter.Values[Index].raycastTarget = false;
            GameObject Track = CombatUIFactory.Panel(Pill.transform, "Track", new Color(1, 1, 1, 0.1f), Vector2.zero, Vector2.zero, Vector2.zero);
            RectTransform TrackArea = (RectTransform)Track.transform;
            TrackArea.anchorMin = new Vector2(0, 0.5f);
            TrackArea.anchorMax = new Vector2(1, 0.5f);
            TrackArea.offsetMin = new Vector2(27, -3);
            TrackArea.offsetMax = new Vector2(-40, 3);
            Track.GetComponent<UnityEngine.UI.Image>().raycastTarget = false;
            Presenter.Fills[Index] = Fill(Track.transform, Colors[Index]);
            if (Index == 2)
            {
                Presenter.ComboTimer = Fill(Track.transform, new Color(1, 0.8f, 0.4f));
                Presenter.ComboTimer.rectTransform.anchorMin = Vector2.zero;
                Presenter.ComboTimer.rectTransform.anchorMax = Vector2.right;
                Presenter.ComboTimer.rectTransform.offsetMin = new Vector2(0, -4);
                Presenter.ComboTimer.rectTransform.offsetMax = new Vector2(0, -2);
            }
        }
        Presenter.WaveStatus = CombatUIFactory.Text(Hud.Mobile.BattlePane, "WaveStatus", "Волна 1", Vector2.zero, new Vector2(10, 64), new Vector2(300, 24), 11, Hud.Font);
        Presenter.WaveStatus.raycastTarget = false;
        GameObject Palette = new GameObject("BuildingPalette", typeof(RectTransform), typeof(UnityEngine.UI.ScrollRect));
        Palette.transform.SetParent(Hud.MapPanel, false);
        RectTransform PaletteArea = (RectTransform)Palette.transform;
        PaletteArea.anchorMin = Vector2.zero;
        PaletteArea.anchorMax = Vector2.right;
        PaletteArea.offsetMin = Vector2.zero;
        PaletteArea.offsetMax = new Vector2(0, 44);
        GameObject Viewport = CombatUIFactory.Panel(Palette.transform, "Viewport", new Color(0.09f, 0.13f, 0.09f), Vector2.zero, Vector2.zero, Vector2.zero);
        CombatUIFactory.Stretch((RectTransform)Viewport.transform, Vector2.zero, Vector2.zero);
        Viewport.AddComponent<UnityEngine.UI.Mask>().showMaskGraphic = true;
        GameObject Content = new GameObject("Content", typeof(RectTransform));
        Content.transform.SetParent(Viewport.transform, false);
        RectTransform ContentArea = (RectTransform)Content.transform;
        ContentArea.anchorMin = Vector2.zero;
        ContentArea.anchorMax = Vector2.up;
        ContentArea.pivot = new Vector2(0, 0.5f);
        ContentArea.sizeDelta = new Vector2(6 * 112, 0);
        UnityEngine.UI.ScrollRect Scroll = Palette.GetComponent<UnityEngine.UI.ScrollRect>();
        Scroll.viewport = (RectTransform)Viewport.transform;
        Scroll.content = ContentArea;
        Scroll.horizontal = true;
        Scroll.vertical = false;
        Scroll.movementType = UnityEngine.UI.ScrollRect.MovementType.Clamped;
        Presenter.BuildingButtons = new UnityEngine.UI.Button[6];
        for (int Index = 0; Index < 6; Index++)
        {
            UnityEngine.UI.Button Button = CombatUIFactory.Button(Content.transform, "Building" + Index, Game.Balance.Structures[Index].Name, new Vector2(0, 0.5f), new Vector2(4 + Index * 112, 0), new Vector2(108, 40), Hud.Font);
            Button.GetComponentInChildren<TMP_Text>().fontSize = 11;
            BuildingPaletteAction Action = Button.gameObject.AddComponent<BuildingPaletteAction>();
            Action.Game = Game;
            Action.Index = Index;
            Presenter.BuildingButtons[Index] = Button;
        }
        Hud.MapRect.offsetMin = new Vector2(0, 44);
        Hud.Mobile.Split.offsetMax = Vector2.zero;
        Game.Balance.MobileView.SlowMotionDuration = 2;
        InstallDamageEdges(Presenter);
        InstallSpecialIcon(Presenter);
        InstallReferenceLayout(Game);
    }

    private static void InstallSpecialIcon(MergedHudPresenter Presenter)
    {
        Transform Pill = Presenter.Values[2].transform.parent;
        Pill.Find("Icon").gameObject.SetActive(false);
        if (Pill.Find("SpecialIcon") != null)
        {
            return;
        }
        GameObject Model = new GameObject("SpecialIcon", typeof(RectTransform), typeof(PhoneGraphic));
        Model.transform.SetParent(Pill, false);
        RectTransform Area = (RectTransform)Model.transform;
        Area.anchorMin = new Vector2(0, 0.5f);
        Area.anchorMax = Area.anchorMin;
        Area.anchoredPosition = new Vector2(14, 0);
        Area.sizeDelta = new Vector2(14, 14);
        PhoneGraphic Icon = Model.GetComponent<PhoneGraphic>();
        Icon.Icon = PhoneIcon.Special;
        Icon.color = new Color(1, 0.39f, 0.48f);
        Icon.raycastTarget = false;
    }

    private static void InstallDamageEdges(MergedHudPresenter Presenter)
    {
        if (Presenter.DamageEdges != null)
        {
            if (Presenter.DamageEdges.GetComponent<CanvasRenderer>() == null)
            {
                Presenter.DamageEdges.gameObject.AddComponent<CanvasRenderer>();
            }
            return;
        }
        GameObject Model = new GameObject("DamageEdges", typeof(RectTransform), typeof(SpecialReadyEdgeEffect));
        Model.transform.SetParent(Presenter.Game.Hud.Mobile.BattlePane, false);
        CombatUIFactory.Stretch((RectTransform)Model.transform, Vector2.zero, Vector2.zero);
        Presenter.DamageEdges = Model.GetComponent<SpecialReadyEdgeEffect>();
        Presenter.DamageEdges.raycastTarget = false;
        Presenter.DamageEdges.bParticles = false;
    }

    private static void InstallMapControls(ActiveCombatGame Game)
    {
        if (Game.Hud.MapPanel.Find("MapPan") == null)
        {
            MapButton(Game, Game.Hud.MapPanel, "MapPan", "Сдвиг", TacticalMapCommand.Pan, new Vector2(-114, 132), new Vector2(64, 34));
        }
        if (Game.Hud.MapPanel.Find("MapOverview") != null || Game.Hud.Mobile.BattlePane.Find("MapOverview") != null)
        {
            return;
        }
        MapButton(Game, Game.Hud.MapPanel, "MapOverview", "Обзор", TacticalMapCommand.Overview, new Vector2(-10, 94), new Vector2(64, 34));
        MapButton(Game, Game.Hud.MapPanel, "MapPlayer", "Игрок", TacticalMapCommand.Player, new Vector2(-78, 94), new Vector2(64, 34));
        MapButton(Game, Game.Hud.MapPanel, "MapFullscreen", "Развернуть", TacticalMapCommand.Fullscreen, new Vector2(-10, 132), new Vector2(100, 34));
        MapButton(Game, Game.Hud.MapPanel, "MapSell", "Продать", TacticalMapCommand.Sell, new Vector2(-10, 48), new Vector2(64, 42));
        MapButton(Game, Game.Hud.MapPanel, "MapTrebuchet", "Требушет", TacticalMapCommand.Trebuchet, new Vector2(-78, 48), new Vector2(72, 42));
        MapButton(Game, Game.Hud.Mobile.BattlePane, "ExpandMap", "Открыть\nкарту", TacticalMapCommand.ToggleMap, new Vector2(-10, 10), new Vector2(82, 48));
        MapButton(Game, Game.Hud.MapPanel, "CollapseMap", "Закрыть\nкарту", TacticalMapCommand.ToggleMap, new Vector2(-10, 10), new Vector2(82, 44));
    }

    private static void InstallReferenceLayout(ActiveCombatGame Game)
    {
        RectTransform Economy = (RectTransform)Game.Hud.Mobile.GoldEnemiesText.transform.parent;
        Economy.sizeDelta = new Vector2(172, 30);
        RectTransform Coin = (RectTransform)Economy.Find("Coin");
        Coin.sizeDelta = new Vector2(26, 26);
        Coin.anchoredPosition = new Vector2(17, 0);
        Game.Hud.Mobile.GoldEnemiesText.rectTransform.anchoredPosition = new Vector2(34, 0);
        Game.Hud.Mobile.GoldEnemiesText.rectTransform.sizeDelta = new Vector2(134, 24);
        RectTransform Menu = (RectTransform)Game.Hud.Mobile.BattlePane.Find("Menu");
        Menu.anchorMin = Vector2.zero;
        Menu.anchorMax = Vector2.zero;
        Menu.pivot = Vector2.zero;
        Menu.anchoredPosition = new Vector2(10, 12);
        Menu.sizeDelta = new Vector2(64, 44);
        SetMapActionIcon(Game.Hud.MapPanel.Find("MapSell"), PhoneIcon.Delete, new Vector2(-8, 110));
        SetMapActionIcon(Game.Hud.MapPanel.Find("MapTrebuchet"), PhoneIcon.Trebuchet, new Vector2(-8, 60));
        Game.Hud.MapPanel.Find("MapPlayer").gameObject.SetActive(false);
        Game.Hud.MapPanel.Find("MapPan").gameObject.SetActive(false);
        Transform Overview = Game.Hud.MapPanel.Find("MapOverview");
        if (Overview == null)
        {
            Overview = Game.Hud.Mobile.BattlePane.Find("MapOverview");
        }
        Overview.SetParent(Game.Hud.Mobile.BattlePane, false);
        SetMapActionIcon(Overview, PhoneIcon.Overview, new Vector2(-10, 68));
        MergedHudPresenter Presenter = Game.GetComponent<MergedHudPresenter>();
        if (Presenter == null)
        {
            return;
        }
        PhoneIcon[] Icons = { PhoneIcon.Tower, PhoneIcon.Wall, PhoneIcon.Trap, PhoneIcon.Flag, PhoneIcon.Armory, PhoneIcon.Sawmill };
        for (int Index = 0; Index < Presenter.BuildingButtons.Length; Index++)
        {
            Transform Button = Presenter.BuildingButtons[Index].transform;
            if (Button.Find("Icon") == null)
            {
                GameObject Symbol = new GameObject("Icon", typeof(RectTransform), typeof(CanvasRenderer), typeof(PhoneGraphic));
                Symbol.transform.SetParent(Button, false);
                PhoneGraphic Graphic = Symbol.GetComponent<PhoneGraphic>();
                Graphic.Icon = Icons[Index];
                Graphic.color = new Color(0.91f, 0.92f, 0.81f);
                Graphic.raycastTarget = false;
                Graphic.rectTransform.anchorMin = new Vector2(0, 0.5f);
                Graphic.rectTransform.anchorMax = Graphic.rectTransform.anchorMin;
                Graphic.rectTransform.anchoredPosition = new Vector2(18, 0);
                Graphic.rectTransform.sizeDelta = new Vector2(22, 22);
            }
            TMP_Text Label = Button.GetComponentInChildren<TMP_Text>();
            Label.rectTransform.anchorMin = new Vector2(0, 0.5f);
            Label.rectTransform.anchorMax = Label.rectTransform.anchorMin;
            Label.rectTransform.pivot = Label.rectTransform.anchorMin;
            Label.rectTransform.anchoredPosition = new Vector2(36, 0);
            Label.rectTransform.sizeDelta = new Vector2(70, 38);
            Label.fontSize = 10;
            Label.alignment = TextAlignmentOptions.MidlineLeft;
        }
    }

    private static void SetMapActionIcon(Transform Button, PhoneIcon Icon, Vector2 Position)
    {
        RectTransform Area = (RectTransform)Button;
        Area.anchorMin = Vector2.right;
        Area.anchorMax = Vector2.right;
        Area.pivot = Vector2.right;
        Area.anchoredPosition = Position;
        Area.sizeDelta = new Vector2(44, 44);
        Button.GetComponentInChildren<TMP_Text>(true).gameObject.SetActive(false);
        Transform Symbol = Button.Find("ActionIcon");
        if (Symbol == null)
        {
            GameObject Model = new GameObject("ActionIcon", typeof(RectTransform), typeof(CanvasRenderer), typeof(PhoneGraphic));
            Model.transform.SetParent(Button, false);
            Symbol = Model.transform;
        }
        PhoneGraphic Graphic = Symbol.GetComponent<PhoneGraphic>();
        Graphic.Icon = Icon;
        Graphic.color = new Color(0.9f, 0.91f, 0.81f);
        Graphic.raycastTarget = false;
        Graphic.rectTransform.anchorMin = new Vector2(0.5f, 0.5f);
        Graphic.rectTransform.anchorMax = Graphic.rectTransform.anchorMin;
        Graphic.rectTransform.anchoredPosition = Vector2.zero;
        Graphic.rectTransform.sizeDelta = new Vector2(22, 22);
    }

    private static void MapButton(ActiveCombatGame Game, Transform Parent, string Name, string Text, TacticalMapCommand Command, Vector2 Position, Vector2 Size)
    {
        UnityEngine.UI.Button Button = CombatUIFactory.Button(Parent, Name, Text, Vector2.right, Position, Size, Game.Hud.Font);
        Button.GetComponentInChildren<TMP_Text>().fontSize = 11;
        TacticalMapAction Action = Button.gameObject.AddComponent<TacticalMapAction>();
        Action.Game = Game;
        Action.Command = Command;
    }

    private static UnityEngine.UI.Image Fill(Transform Parent, Color Color)
    {
        GameObject Model = new GameObject("Fill", typeof(RectTransform), typeof(UnityEngine.UI.Image));
        Model.transform.SetParent(Parent, false);
        UnityEngine.UI.Image Image = Model.GetComponent<UnityEngine.UI.Image>();
        Image.sprite = CombatUIFactory.RoundSprite;
        Image.type = UnityEngine.UI.Image.Type.Filled;
        Image.fillMethod = UnityEngine.UI.Image.FillMethod.Horizontal;
        Image.color = Color;
        Image.raycastTarget = false;
        CombatUIFactory.Stretch(Image.rectTransform, Vector2.zero, Vector2.zero);
        return Image;
    }
}
