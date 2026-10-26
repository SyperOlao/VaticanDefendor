using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
public static class MobileInterfaceFactory
{
    private static Color Color(string Hex)
    {
        return CombatUIFactory.Hex(Hex);
    }
    private static GameObject Panel(Transform Parent,string Name,string Hex,Vector2 Anchor,Vector2 Position,Vector2 Size)
    {
        return CombatUIFactory.Panel(Parent,Name,Color(Hex),Anchor,Position,Size);
    }
    private static TMP_Text Text(Transform Parent,string Name,string Caption,Vector2 Anchor,Vector2 Position,Vector2 Size,int FontSize,TMP_FontAsset Font)
    {
        return CombatUIFactory.Text(Parent,Name,Caption,Anchor,Position,Size,FontSize,Font);
    }
    private static UnityEngine.UI.Button Button(Transform Parent,string Name,string Caption,Vector2 Anchor,Vector2 Position,Vector2 Size,TMP_FontAsset Font,int FontSize=11)
    {
        UnityEngine.UI.Button Result=CombatUIFactory.Button(Parent,Name,Caption,Anchor,Position,Size,Font);
        Result.GetComponentInChildren<TMP_Text>().fontSize=FontSize;
        Result.GetComponent<UnityEngine.UI.Image>().color=Color("#292524");
        return Result;
    }
    private static void Action(UnityEngine.UI.Button Button,ActiveCombatGame Game,CombatAction Action)
    {
        CombatUIAction Component=Button.gameObject.AddComponent<CombatUIAction>();
        Component.Game=Game;
        Component.Action=Action;
    }
    public static PhoneGraphic Icon(Transform Parent,string Name,PhoneIcon Icon,Vector2 Anchor,Vector2 Position,Vector2 Size,Color Color)
    {
        GameObject Model=new GameObject(Name,typeof(RectTransform),typeof(CanvasRenderer),typeof(PhoneGraphic));
        Model.transform.SetParent(Parent,false);
        RectTransform Rectangle=Model.GetComponent<RectTransform>();
        Rectangle.anchorMin=Anchor;
        Rectangle.anchorMax=Anchor;
        Rectangle.pivot=new Vector2(0.5f,0.5f);
        Rectangle.anchoredPosition=Position;
        Rectangle.sizeDelta=Size;
        PhoneGraphic Graphic=Model.GetComponent<PhoneGraphic>();
        Graphic.Icon=Icon;
        Graphic.color=Color;
        Graphic.raycastTarget=false;
        return Graphic;
    }
    public static void MobileActionButton(UnityEngine.UI.Button Button,MobileCombatInterface Mobile,MobileAction Action,int Index)
    {
        MobileInterfaceAction Component=Button.gameObject.AddComponent<MobileInterfaceAction>();
        Component.Mobile=Mobile;
        Component.Action=Action;
        Component.Index=Index;
    }
    private static UnityEngine.UI.Image Fill(Transform Parent,string Name,string Hex,Vector2 Anchor,Vector2 Position,Vector2 Size)
    {
        GameObject Track=Panel(Parent,Name+"Track","#292524",Anchor,Position,Size);
        Track.GetComponent<UnityEngine.UI.Image>().raycastTarget=false;
        UnityEngine.UI.Image Image=Panel(Track.transform,Name,Hex,new Vector2(0.5f,0.5f),Vector2.zero,Size).GetComponent<UnityEngine.UI.Image>();
        Image.type=UnityEngine.UI.Image.Type.Filled;
        Image.fillMethod=UnityEngine.UI.Image.FillMethod.Horizontal;
        Image.raycastTarget=false;
        return Image;
    }
    public static CombatHUD Build(ActiveCombatGame Game,TMP_FontAsset Font)
    {
        GameObject CanvasObject=new GameObject("MobileCombatCanvas",typeof(RectTransform),typeof(Canvas),typeof(UnityEngine.UI.CanvasScaler),typeof(UnityEngine.UI.GraphicRaycaster));
        CanvasObject.transform.SetParent(Game.transform,false);
        CanvasObject.GetComponent<Canvas>().renderMode=RenderMode.ScreenSpaceOverlay;
        UnityEngine.UI.CanvasScaler Scaler=CanvasObject.GetComponent<UnityEngine.UI.CanvasScaler>();
        Scaler.uiScaleMode=UnityEngine.UI.CanvasScaler.ScaleMode.ConstantPixelSize;
        Scaler.referenceResolution=new Vector2(460,940);
        CombatHUD Hud=CanvasObject.AddComponent<CombatHUD>();
        Hud.Game=Game;
        Hud.Font=Font;
        Hud.RoundedPanelSprite=CombatUIFactory.RoundSprite;
        Hud.CanvasRoot=CanvasObject.GetComponent<RectTransform>();
        MobileCombatInterface Mobile=CanvasObject.AddComponent<MobileCombatInterface>();
        Hud.Mobile=Mobile;
        Mobile.Game=Game;
        Mobile.Hud=Hud;
        Hud.WeaponButtons=new UnityEngine.UI.Button[0];
        Hud.BuildingButtons=new UnityEngine.UI.Button[0];
        GameObject Safe=new GameObject("PhoneSafeArea",typeof(RectTransform),typeof(UnityEngine.UI.RectMask2D));
        Safe.transform.SetParent(CanvasObject.transform,false);
        Mobile.SafeFrame=Safe.GetComponent<RectTransform>();
        CombatUIFactory.Stretch(Mobile.SafeFrame,Vector2.zero,Vector2.zero);
        GameObject Header=Panel(Safe.transform,"QuickTestBar","#1c1917",new Vector2(0.5f,1),Vector2.zero,new Vector2(0,40));
        Header.GetComponent<UnityEngine.UI.Image>().sprite=null;
        RectTransform HeaderRectangle=Header.GetComponent<RectTransform>();
        HeaderRectangle.anchorMin=new Vector2(0,1);
        HeaderRectangle.anchorMax=Vector2.one;
        Text(Header.transform,"Proportion","ПРОПОРЦИЯ:",new Vector2(0,0.5f),new Vector2(10,0),new Vector2(76,30),11,Font).color=Color("#fbbf24");
        Mobile.RatioButtons=new UnityEngine.UI.Image[3];
        string[] RatioCaptions=
        {
            "1/4 • 3/4","1/3 • 2/3","Своб."
        }
        ;
        for(int Index=0;Index<3;Index++)
        {
            float Width=66;
            if(Index==2)
            {
                Width=50;
            }
            UnityEngine.UI.Button Ratio=Button(Header.transform,"Ratio"+Index,RatioCaptions[Index],new Vector2(0,0.5f),new Vector2(84+Index*70,0),new Vector2(Width,28),Font,11);
            MobileActionButton(Ratio,Mobile,MobileAction.Ratio,Index);
            Mobile.RatioButtons[Index]=Ratio.GetComponent<UnityEngine.UI.Image>();
        }
        GameObject Badge=Panel(Header.transform,"TimeBadge","#064e3b",new Vector2(1,0.5f),new Vector2(-45,0),new Vector2(100,22));
        Mobile.TimeBadge=Text(Badge.transform,"Time","100% Реалтайм",new Vector2(0.5f,0.5f),Vector2.zero,new Vector2(96,22),10,Font);
        Mobile.TimeBadge.alignment=TextAlignmentOptions.Center;
        Mobile.TimeBadge.color=Color("#6ee7b7");
        UnityEngine.UI.Button Settings=Button(Header.transform,"Settings","",new Vector2(1,0.5f),new Vector2(-10,0),new Vector2(28,28),Font);
        Action(Settings,Game,CombatAction.Settings);
        Icon(Settings.transform,"SettingsIcon",PhoneIcon.Settings,new Vector2(0.5f,0.5f),Vector2.zero,new Vector2(18,18),Color("#fcd34d"));
        GameObject Split=new GameObject("SplitContainer",typeof(RectTransform));
        Split.transform.SetParent(Safe.transform,false);
        Mobile.Split=Split.GetComponent<RectTransform>();
        CombatUIFactory.Stretch(Mobile.Split,Vector2.zero,new Vector2(0,-40));
        Hud.MapPanel=Panel(Split.transform,"TacticalMap","#182417",new Vector2(0.5f,1),Vector2.zero,new Vector2(0,225)).GetComponent<RectTransform>();
        Hud.MapPanel.GetComponent<UnityEngine.UI.Image>().sprite=null;
        Hud.MapPanel.gameObject.AddComponent<UnityEngine.UI.RectMask2D>();
        GameObject Map=new GameObject("TacticalMapCanvas",typeof(RectTransform),typeof(UnityEngine.UI.RawImage),typeof(MobileGestureSurface));
        Map.transform.SetParent(Hud.MapPanel,false);
        Hud.MapRect=Map.GetComponent<RectTransform>();
        CombatUIFactory.Stretch(Hud.MapRect,Vector2.zero,Vector2.zero);
        Hud.MapImage=Map.GetComponent<UnityEngine.UI.RawImage>();
        MobileGestureSurface MapGesture=Map.GetComponent<MobileGestureSurface>();
        MapGesture.Mobile=Mobile;
        MapGesture.Kind=MobileGestureKind.Map;
        GameObject Prestige=Panel(Hud.MapPanel,"PrestigeBadge","#12120edb",new Vector2(0,1),new Vector2(10,-8),new Vector2(192,26));
        Prestige.GetComponent<UnityEngine.UI.Image>().raycastTarget=false;
        Icon(Prestige.transform,"Crown",PhoneIcon.Crown,new Vector2(0,0.5f),new Vector2(14,0),new Vector2(11,11),Color("#fbbf24"));
        Mobile.RankText=Text(Prestige.transform,"Rank","Ранг престижа 1",new Vector2(0,0.5f),new Vector2(27,0),new Vector2(112,24),10,Font);
        Mobile.RankFill=Fill(Prestige.transform,"PrestigeProgress","#fbbf24",new Vector2(1,0.5f),new Vector2(-10,0),new Vector2(54,6));
        GameObject Economy=Panel(Hud.MapPanel,"GoldEnemies","#12120edb",new Vector2(1,1),new Vector2(-10,-8),new Vector2(162,26));
        Economy.GetComponent<UnityEngine.UI.Image>().raycastTarget=false;
        Icon(Economy.transform,"Coin",PhoneIcon.Coin,new Vector2(0,0.5f),new Vector2(13,0),new Vector2(12,12),Color("#fbbf24"));
        Mobile.GoldEnemiesText=Text(Economy.transform,"Value","150   |   Враги 7 / 7",new Vector2(0,0.5f),new Vector2(24,0),new Vector2(134,24),11,Font);
        GameObject Modes=new GameObject("MapModeControls",typeof(RectTransform));
        Modes.transform.SetParent(Hud.MapPanel,false);
        Mobile.ModeControls=Modes;
        RectTransform ModesRectangle=Modes.GetComponent<RectTransform>();
        ModesRectangle.anchorMin=Vector2.one;
        ModesRectangle.anchorMax=Vector2.one;
        ModesRectangle.pivot=Vector2.one;
        ModesRectangle.anchoredPosition=new Vector2(-9,-44);
        ModesRectangle.sizeDelta=new Vector2(60,98);
        Hud.BuildingsModeButton=Button(Modes.transform,"BuildingsMode","",new Vector2(0.5f,1),Vector2.zero,new Vector2(60,46),Font);
        Action(Hud.BuildingsModeButton,Game,CombatAction.BuildingsMode);
        Icon(Hud.BuildingsModeButton.transform,"TowerIcon",PhoneIcon.Tower,new Vector2(0.5f,1),new Vector2(0,-16),new Vector2(22,22),Color("#ffe3a2"));
        Text(Hud.BuildingsModeButton.transform,"Caption","Постройки",new Vector2(0.5f,0),new Vector2(0,2),new Vector2(58,14),9,Font).alignment=TextAlignmentOptions.Center;
        Hud.TrebuchetModeButton=Button(Modes.transform,"TrebuchetMode","",new Vector2(0.5f,1),new Vector2(0,-52),new Vector2(60,46),Font);
        Action(Hud.TrebuchetModeButton,Game,CombatAction.TrebuchetMode);
        Icon(Hud.TrebuchetModeButton.transform,"TrebuchetIcon",PhoneIcon.Trebuchet,new Vector2(0.5f,1),new Vector2(0,-16),new Vector2(22,22),Color("#ffd6ba"));
        Text(Hud.TrebuchetModeButton.transform,"Caption","Требушет",new Vector2(0.5f,0),new Vector2(0,2),new Vector2(58,14),9,Font).alignment=TextAlignmentOptions.Center;
        GameObject Timer=Panel(Hud.MapPanel,"SlowMotionTimer","#0c0a09d9",new Vector2(0.5f,0),new Vector2(0,8),new Vector2(0,32));
        RectTransform TimerRect=Timer.GetComponent<RectTransform>();
        TimerRect.anchorMin=Vector2.zero;
        TimerRect.anchorMax=Vector2.right;
        TimerRect.offsetMin=new Vector2(12,8);
        TimerRect.offsetMax=new Vector2(-12,40);
        Timer.GetComponent<UnityEngine.UI.Image>().raycastTarget=false;
        Mobile.SlowMotionPanel=Timer;
        Mobile.SlowMotionLabel=Text(Timer.transform,"Countdown","Замедление времени",new Vector2(0,1),new Vector2(8,-3),new Vector2(380,16),10,Font);
        Mobile.SlowMotionFill=Fill(Timer.transform,"Remaining","#38bdf8",new Vector2(0,0),new Vector2(8,5),new Vector2(0,6));
        RectTransform FillTrack=(RectTransform)Mobile.SlowMotionFill.transform.parent;
        FillTrack.anchorMax=Vector2.right;
        FillTrack.offsetMin=new Vector2(8,5);
        FillTrack.offsetMax=new Vector2(-8,11);
        CombatUIFactory.Stretch(Mobile.SlowMotionFill.rectTransform,Vector2.zero,Vector2.zero);
        GameObject Hint=Panel(Hud.MapPanel,"BuildHint","#451a03e6",new Vector2(0.5f,0),new Vector2(-32,44),new Vector2(350,28));
        Hint.GetComponent<UnityEngine.UI.Image>().raycastTarget=false;
        Mobile.BuildHint=Text(Hint.transform,"SelectedBuilding","Построить: Башня лучников",new Vector2(0.5f,0.5f),Vector2.zero,new Vector2(334,27),10,Font);
        Mobile.BuildHint.alignment=TextAlignmentOptions.Center;
        GameObject Curtain=Panel(Split.transform,"CurtainHandle","#3b2d22",new Vector2(0.5f,1),Vector2.zero,new Vector2(0,28));
        Curtain.GetComponent<UnityEngine.UI.Image>().sprite=null;
        Mobile.Handle=Curtain.GetComponent<RectTransform>();
        MobileGestureSurface CurtainGesture=Curtain.AddComponent<MobileGestureSurface>();
        CurtainGesture.Mobile=Mobile;
        CurtainGesture.Kind=MobileGestureKind.Curtain;
        GameObject Grip=Panel(Curtain.transform,"Grip","#d6b37c",new Vector2(0.5f,0.5f),Vector2.zero,new Vector2(54,6));
        Grip.GetComponent<UnityEngine.UI.Image>().raycastTarget=false;
        Mobile.RatioText=Text(Curtain.transform,"RatioReadout","25% / 75%",new Vector2(0,0.5f),new Vector2(14,0),new Vector2(100,20),10,Font);
        Mobile.RatioText.color=Color("#fde68a");
        UnityEngine.UI.Button Swipe=Button(Curtain.transform,"CycleSwipeZone","",new Vector2(1,0.5f),new Vector2(-10,0),new Vector2(98,20),Font,10);
        Mobile.SwipeText=Swipe.GetComponentInChildren<TMP_Text>();
        MobileQuickSwipe Quick=Swipe.gameObject.AddComponent<MobileQuickSwipe>();
        Quick.Mobile=Mobile;
        GameObject Battle=new GameObject("BattleViewport",typeof(RectTransform),typeof(UnityEngine.UI.RectMask2D));
        Battle.transform.SetParent(Split.transform,false);
        Mobile.BattlePane=Battle.GetComponent<RectTransform>();
        CombatUIFactory.Stretch(Mobile.BattlePane,Vector2.zero,new Vector2(0,-253));
        GameObject Input=new GameObject("CombatInput",typeof(RectTransform),typeof(CanvasRenderer),typeof(CombatJoystick));
        Input.transform.SetParent(Battle.transform,false);
        CombatUIFactory.Stretch(Input.GetComponent<RectTransform>(),Vector2.zero,Vector2.zero);
        Hud.Joystick=Input.GetComponent<CombatJoystick>();
        Hud.Joystick.Game=Game;
        Hud.ExhaustionOverlay=Panel(Battle.transform,"Exhaustion","#00000000",new Vector2(0.5f,0.5f),Vector2.zero,Vector2.zero).GetComponent<UnityEngine.UI.Image>();
        CombatUIFactory.Stretch(Hud.ExhaustionOverlay.rectTransform,Vector2.zero,Vector2.zero);
        Hud.ExhaustionOverlay.raycastTarget=false;
        Hud.HitOverlay=Panel(Battle.transform,"HitFlash","#00000000",new Vector2(0.5f,0.5f),Vector2.zero,Vector2.zero).GetComponent<UnityEngine.UI.Image>();
        CombatUIFactory.Stretch(Hud.HitOverlay.rectTransform,Vector2.zero,Vector2.zero);
        Hud.HitOverlay.raycastTarget=false;
        GameObject Health=Panel(Battle.transform,"HealthState","#0c0a098c",new Vector2(0,1),new Vector2(10,-8),new Vector2(208,26));
        Health.GetComponent<UnityEngine.UI.Image>().raycastTarget=false;
        Icon(Health.transform,"Heart",PhoneIcon.Heart,new Vector2(0,0.5f),new Vector2(14,0),new Vector2(10,10),Color("#f43f5e"));
        Hud.HealthFill=Fill(Health.transform,"Health","#f43f5e",new Vector2(0,0.5f),new Vector2(27,0),new Vector2(64,6));
        Hud.HealthText=Text(Health.transform,"HealthValue","100",new Vector2(0,0.5f),new Vector2(99,0),new Vector2(28,18),10,Font);
        Mobile.StateText=Text(Health.transform,"PlayerState","Ожидание",new Vector2(1,0.5f),new Vector2(-8,0),new Vector2(72,20),10,Font);
        Mobile.StateText.color=Color("#6ee7b7");
        GameObject Equipment=Panel(Battle.transform,"EquippedWeapon","#0c0a098c",new Vector2(1,1),new Vector2(-10,-8),new Vector2(126,26));
        Equipment.GetComponent<UnityEngine.UI.Image>().raycastTarget=false;
        Mobile.WeaponText=Text(Equipment.transform,"WeaponZone","Сектор 90°",new Vector2(0.5f,0.5f),Vector2.zero,new Vector2(120,24),10,Font);
        Mobile.WeaponText.color=Color("#fcd34d");
        Mobile.WeaponText.alignment=TextAlignmentOptions.Center;
        GameObject Stamina=Panel(Battle.transform,"CombatHud","#0c1116db",new Vector2(0,1),new Vector2(10,-44),new Vector2(240,63));
        Mobile.StaminaPanel=Stamina.GetComponent<RectTransform>();
        Stamina.GetComponent<UnityEngine.UI.Image>().raycastTarget=false;
        Text(Stamina.transform,"Label","Выносливость",new Vector2(0,1),new Vector2(10,-6),new Vector2(130,18),11,Font);
        Hud.StaminaText=Text(Stamina.transform,"StaminaValue","100 / 100",new Vector2(1,1),new Vector2(-10,-6),new Vector2(70,18),11,Font);
        Hud.StaminaText.alignment=TextAlignmentOptions.MidlineRight;
        Hud.StaminaFill=Fill(Stamina.transform,"Stamina","#5bceae",new Vector2(0,1),new Vector2(10,-27),new Vector2(220,4));
        Hud.ActionText=Text(Stamina.transform,"DashStatus","Рывок: 33 выносливости",new Vector2(0,1),new Vector2(10,-35),new Vector2(220,23),10,Font);
        Hud.HelpText=Text(Battle.transform,"CombatHelp","Топор: каждый короткий тап по центру сразу наносит круговой удар без перерыва между тапами. Автоатака включается в настройках.\nУдерживайте центр 0,7 с. После заряда сдвиньте в красную зону и отпустите — атакующая спецатака с перемещением.\nОтпустите в центре — защита. Возврат в центр отменяет выбор атакующей спецатаки.\nСпецатаки расходуют жертвы за убийства и не расходуют выносливость. Первая стоит 12 жертв, каждое применение увеличивает цену на 1. Красный центр означает готовность.",new Vector2(0.5f,0),new Vector2(0,70),new Vector2(430,130),10,Font);
        Hud.HelpText.alignment=TextAlignmentOptions.Center;
        Hud.HelpText.lineSpacing=4;
        Hud.ErrorText=Text(Battle.transform,"CommandError","",new Vector2(0.5f,0.5f),new Vector2(0,-40),new Vector2(350,40),13,Font);
        Hud.ErrorText.alignment=TextAlignmentOptions.Center;
        Hud.ErrorText.color=Color("#f87171");
        Mobile.JoystickMovementLabel=Text(Battle.transform,"JoystickMovement","ДВИЖЕНИЕ",new Vector2(0.5f,0.5f),Vector2.zero,new Vector2(120,20),10,Font);
        Mobile.JoystickMovementLabel.alignment=TextAlignmentOptions.Center;
        Mobile.JoystickActionLabel=Text(Battle.transform,"JoystickAction","РЫВОК / БЕГ",new Vector2(0.5f,0.5f),Vector2.zero,new Vector2(240,20),10,Font);
        Mobile.JoystickActionLabel.alignment=TextAlignmentOptions.Center;
        Input.transform.SetAsLastSibling();
        Mobile.JoystickMovementLabel.transform.SetAsLastSibling();
        Mobile.JoystickActionLabel.transform.SetAsLastSibling();
        GameObject WheelLayer=new GameObject("WheelOverlay",typeof(RectTransform));
        WheelLayer.transform.SetParent(Split.transform,false);
        CombatUIFactory.Stretch(WheelLayer.GetComponent<RectTransform>(),Vector2.zero,Vector2.zero);
        Mobile.WheelLayer=WheelLayer;
        GameObject Blur=new GameObject("WheelBlur",typeof(RectTransform),typeof(UnityEngine.UI.RawImage));
        Blur.transform.SetParent(WheelLayer.transform,false);
        CombatUIFactory.Stretch(Blur.GetComponent<RectTransform>(),Vector2.zero,Vector2.zero);
        Mobile.WheelBlur=Blur.GetComponent<UnityEngine.UI.RawImage>();
        Mobile.WheelBlur.raycastTarget=false;
        Mobile.WheelShade=Panel(WheelLayer.transform,"Shade","#00000000",new Vector2(0.5f,0.5f),Vector2.zero,Vector2.zero).GetComponent<UnityEngine.UI.Image>();
        CombatUIFactory.Stretch(Mobile.WheelShade.rectTransform,Vector2.zero,Vector2.zero);
        Mobile.WheelShade.raycastTarget=false;
        GameObject Wheel=new GameObject("Wheel",typeof(RectTransform),typeof(CanvasRenderer),typeof(PhoneWheelGraphic));
        Wheel.transform.SetParent(WheelLayer.transform,false);
        CombatUIFactory.Stretch(Wheel.GetComponent<RectTransform>(),Vector2.zero,Vector2.zero);
        Mobile.Wheel=Wheel.GetComponent<PhoneWheelGraphic>();
        Mobile.Wheel.Mobile=Mobile;
        Mobile.Wheel.raycastTarget=false;
        Mobile.WheelLabels=new TMP_Text[4];
        Mobile.WheelIcons=new PhoneGraphic[4];
        for(int Index=0;Index<4;Index++)
        {
            Mobile.WheelLabels[Index]=Text(WheelLayer.transform,"Item"+Index,"",new Vector2(0.5f,0.5f),Vector2.zero,new Vector2(110,42),11,Font);
            Mobile.WheelLabels[Index].alignment=TextAlignmentOptions.Center;
            Mobile.WheelIcons[Index]=Icon(WheelLayer.transform,"Icon"+Index,PhoneIcon.Sword,new Vector2(0.5f,0.5f),Vector2.zero,new Vector2(24,24),Color("#f59e0b"));
        }
        WheelLayer.SetActive(false);
        GameObject Dock=Panel(Split.transform,"WheelDock","#1c1917",new Vector2(0.5f,0),new Vector2(0,-34),new Vector2(112,96));
        Dock.GetComponent<UnityEngine.UI.Image>().color=UnityEngine.Color.clear;
        Mobile.Dock=Dock.GetComponent<RectTransform>();
        MobileGestureSurface DockGesture=Dock.AddComponent<MobileGestureSurface>();
        DockGesture.Mobile=Mobile;
        DockGesture.Kind=MobileGestureKind.Wheel;
        Mobile.DockBackground=Icon(Dock.transform,"DockCircle",PhoneIcon.None,new Vector2(0.5f,0.5f),Vector2.zero,new Vector2(112,96),Color("#92400e"));
        Mobile.DockBackground.bCircle=true;
        Mobile.DockTag=Text(Dock.transform,"ModeTag","ОРУЖИЕ",new Vector2(0.5f,1),new Vector2(0,-7),new Vector2(108,12),8,Font);
        Mobile.DockTag.alignment=TextAlignmentOptions.Center;
        Mobile.DockTag.color=Color("#fcd34d");
        Mobile.DockIcon=Icon(Dock.transform,"CurrentIcon",PhoneIcon.Sword,new Vector2(0.5f,1),new Vector2(-22,-34),new Vector2(22,22),Color("#fbbf24"));
        Mobile.DockName=Text(Dock.transform,"CurrentName","Меч",new Vector2(0.5f,1),new Vector2(12,-22),new Vector2(65,24),11,Font);
        Mobile.DockName.alignment=TextAlignmentOptions.Center;
        BuildSettings(Game,Hud,Mobile,Font,Safe.transform);
        Hud.PausePanel=Panel(Safe.transform,"Paused","#000000bf",new Vector2(0.5f,0.5f),Vector2.zero,new Vector2(260,130));
        Text(Hud.PausePanel.transform,"PauseCaption","Пауза",new Vector2(0.5f,1),new Vector2(0,-15),new Vector2(200,30),18,Font).alignment=TextAlignmentOptions.Center;
        Action(Button(Hud.PausePanel.transform,"Resume","Продолжить",new Vector2(0.5f,0),new Vector2(0,15),new Vector2(210,38),Font),Game,CombatAction.Pause);
        Hud.PausePanel.SetActive(false);
        Hud.Counters=Mobile.GoldEnemiesText;
        GameObject Events=new GameObject("EventSystem",typeof(EventSystem),typeof(InputSystemUIInputModule));
        Events.transform.SetParent(Game.transform,false);
        CombatFlowInterface.Install(Game, Hud);
        return Hud;
    }
    private static void BuildSettings(ActiveCombatGame Game,CombatHUD Hud,MobileCombatInterface Mobile,TMP_FontAsset Font,Transform Parent)
    {
        Hud.SettingsPanel=Panel(Parent,"SettingsModal","#000000cc",Vector2.zero,Vector2.zero,Vector2.zero);
        CombatUIFactory.Stretch(Hud.SettingsPanel.GetComponent<RectTransform>(),Vector2.zero,Vector2.zero);
        GameObject Window=Panel(Hud.SettingsPanel.transform,"SettingsWindow","#1c1917",new Vector2(0.5f,0.5f),Vector2.zero,new Vector2(384,0));
        RectTransform WindowRect=Window.GetComponent<RectTransform>();
        WindowRect.anchorMin=new Vector2(0.5f,0.06f);
        WindowRect.anchorMax=new Vector2(0.5f,0.94f);
        WindowRect.sizeDelta=new Vector2(384,0);
        WindowRect.pivot=new Vector2(0.5f,0.5f);
        Text(Window.transform,"Title","Настройки UX-прототипа",new Vector2(0,1),new Vector2(16,-10),new Vector2(310,30),16,Font).color=Color("#fcd34d");
        Action(Button(Window.transform,"Close","×",new Vector2(1,1),new Vector2(-10,-10),new Vector2(28,28),Font,20),Game,CombatAction.CloseSettings);
        GameObject Scroll=new GameObject("ScrollView",typeof(RectTransform),typeof(UnityEngine.UI.ScrollRect));
        Scroll.transform.SetParent(Window.transform,false);
        CombatUIFactory.Stretch(Scroll.GetComponent<RectTransform>(),new Vector2(12,98),new Vector2(-12,-54));
        GameObject Viewport=Panel(Scroll.transform,"Viewport","#1c1917",Vector2.zero,Vector2.zero,Vector2.zero);
        CombatUIFactory.Stretch(Viewport.GetComponent<RectTransform>(),Vector2.zero,Vector2.zero);
        Viewport.AddComponent<UnityEngine.UI.Mask>().showMaskGraphic=false;
        GameObject Content=new GameObject("Content",typeof(RectTransform),typeof(UnityEngine.UI.VerticalLayoutGroup),typeof(UnityEngine.UI.ContentSizeFitter));
        Content.transform.SetParent(Viewport.transform,false);
        Hud.SettingsContent=Content.GetComponent<RectTransform>();
        Hud.SettingsContent.anchorMin=new Vector2(0,1);
        Hud.SettingsContent.anchorMax=Vector2.one;
        Hud.SettingsContent.pivot=new Vector2(0.5f,1);
        Hud.SettingsContent.sizeDelta=Vector2.zero;
        UnityEngine.UI.VerticalLayoutGroup Layout=Content.GetComponent<UnityEngine.UI.VerticalLayoutGroup>();
        Layout.childControlWidth=true;
        Layout.childControlHeight=true;
        Layout.childForceExpandWidth=true;
        Layout.childForceExpandHeight=false;
        Layout.spacing=6;
        Layout.padding=new RectOffset(4,4,4,4);
        Content.GetComponent<UnityEngine.UI.ContentSizeFitter>().verticalFit=UnityEngine.UI.ContentSizeFitter.FitMode.PreferredSize;
        UnityEngine.UI.ScrollRect ScrollComponent=Scroll.GetComponent<UnityEngine.UI.ScrollRect>();
        ScrollComponent.viewport=Viewport.GetComponent<RectTransform>();
        ScrollComponent.content=Hud.SettingsContent;
        ScrollComponent.horizontal=false;
        Hud.SettingsError=Text(Window.transform,"ValidationError","",new Vector2(0.5f,0),new Vector2(0,72),new Vector2(352,24),11,Font);
        Hud.SettingsError.color=Color("#f87171");
        Action(Button(Window.transform,"Defaults","Исходные значения",new Vector2(0,0),new Vector2(16,44),new Vector2(170,25),Font,11),Game,CombatAction.DefaultSettings);
        UnityEngine.UI.Button Reset=Button(Window.transform,"ResetMap","Пересоздать карту",new Vector2(1,0),new Vector2(-16,44),new Vector2(170,25),Font,11);
        MobileActionButton(Reset,Mobile,MobileAction.ResetBattle,0);
        UnityEngine.UI.Button Done=Button(Window.transform,"Done","Готово",new Vector2(0.5f,0),new Vector2(0,8),new Vector2(352,30),Font,14);
        Done.GetComponent<UnityEngine.UI.Image>().color=Color("#d97706");
        Action(Done,Game,CombatAction.ApplySettings);
        Hud.SettingsPanel.SetActive(false);
    }
}
