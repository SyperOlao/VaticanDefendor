using System;
using TMPro;
using UnityEngine;
public class MobileCombatInterface : MonoBehaviour
{
    public ActiveCombatGame Game;
    public CombatHUD Hud;
    public RectTransform SafeFrame;
    public RectTransform Split;
    public RectTransform BattlePane;
    public RectTransform Handle;
    public RectTransform Dock;
    public RectTransform StaminaPanel;
    public TMP_Text RankText;
    public TMP_Text GoldEnemiesText;
    public TMP_Text WeaponText;
    public TMP_Text StateText;
    public TMP_Text TimeBadge;
    public TMP_Text RatioText;
    public TMP_Text SwipeText;
    public TMP_Text DockTag;
    public TMP_Text DockName;
    public PhoneGraphic DockIcon;
    public PhoneGraphic DockBackground;
    public TMP_Text BuildHint;
    public GameObject SlowMotionPanel;
    public TMP_Text SlowMotionLabel;
    public UnityEngine.UI.Image SlowMotionFill;
    public UnityEngine.UI.Image RankFill;
    public GameObject WheelLayer;
    public PhoneWheelGraphic Wheel;
    public UnityEngine.UI.RawImage WheelBlur;
    public UnityEngine.UI.Image WheelShade;
    public TMP_Text[] WheelLabels;
    public PhoneGraphic[] WheelIcons;
    public TMP_Text JoystickMovementLabel;
    public TMP_Text JoystickActionLabel;
    public UnityEngine.UI.Image[] RatioButtons;
    public TMP_Text[] BombCountdowns;
    public GameObject ModeControls;
    public float TopFraction=0.25f;
    public bool bCurtainDragging;
    public bool bMapFullscreen;
    public bool bWheelActive;
    public float WheelProgress;
    public int WheelSelection=-1;
    public Vector2 WheelStart;
    public Vector2 WheelPointer;
    public Vector2 WheelCenter;
    public Rect SafeAreaOverride;
    private float TargetFraction=0.25f;
    private float TransitionStart=0.25f;
    private float TransitionAge=1;
    private float CameraHeight=13.5f;
    private float CameraOffset=11.5f;
    private Vector3 CameraFocus;
    private Texture2D BlurSnapshot;
    private static readonly string[] WeaponZoneNames = { "Сектор 90°", "Прямой выпад", "Круговой удар" };
    private float ExpandedFraction = 0.75f;
    private float CurtainHeight
    {
        get
        {
            if (SafeFrame.rect.width <= 600)
            {
                return 44;
            }
            return 32;
        }
    }
    private void Awake()
    {
        ExpandedFraction = Mathf.Clamp(PlayerPrefs.GetFloat("MapExpandedFraction", 0.75f), 0.25f, 0.9f);
    }
    public float InputScale
    {
        get
        {
            return GetComponent<Canvas>().scaleFactor;
        }
    }
    public MobileViewSettings Settings
    {
        get
        {
            if(Game.Rules!=null)
            {
                return Game.Rules.MobileView;
            }
            return Game.Balance.MobileView;
        }
    }
    public void ApplyLayout(float DeltaSeconds)
    {
        Canvas Canvas=GetComponent<Canvas>();
        float Density=1;
        if (Application.isEditor)
        {
            Density = Mathf.Max(1, Screen.width / 460f);
        }
        if(!Application.isEditor && Application.isMobilePlatform)
        {
            Density=Mathf.Clamp(Mathf.Round(Screen.dpi/160),1,4);
        }
        Canvas.scaleFactor=Density;
        Rect Area=Screen.safeArea;
        if(SafeAreaOverride.width>0)
        {
            Area=SafeAreaOverride;
        }
        SafeFrame.anchorMin=new Vector2(Area.xMin/Screen.width,Area.yMin/Screen.height);
        SafeFrame.anchorMax=new Vector2(Area.xMax/Screen.width,Area.yMax/Screen.height);
        SafeFrame.offsetMin=Vector2.zero;
        SafeFrame.offsetMax=Vector2.zero;
        if(!bCurtainDragging && TransitionAge<0.32f)
        {
            TransitionAge+=DeltaSeconds;
            float Progress=Mathf.Clamp01(TransitionAge/0.32f);
            TopFraction=Mathf.Lerp(TransitionStart,TargetFraction,1-Mathf.Pow(1-Progress,3));
        }
        float Height=Split.rect.height;
        RectTransform Window=(RectTransform)Hud.SettingsContent.parent.parent.parent;
        Window.sizeDelta=new Vector2(Mathf.Min(384,SafeFrame.rect.width-32),0);
        float HeaderScale=Mathf.Min(1,SafeFrame.rect.width/460);
        RectTransform Header=(RectTransform)SafeFrame.GetChild(0);
        for(int Index=0;Index<Header.childCount;Index++)
        {
            RectTransform Child=Header.GetChild(Index) as RectTransform;
            if(Child!=null && Child.anchorMin.x==0)
            {
                if(Child.name=="Proportion")
                {
                    Child.anchoredPosition=new Vector2(10,0);
                    Child.sizeDelta=new Vector2(76*HeaderScale,30);
                    Child.GetComponent<TMP_Text>().fontSize=11*HeaderScale;
                }
                else if(Child.name.StartsWith("Ratio"))
                {
                    int ButtonIndex=int.Parse(Child.name.Substring(5));
                    float Width=66;
                    if(ButtonIndex==2)
                    {
                        Width=50;
                    }
                    Child.anchoredPosition=new Vector2((90+ButtonIndex*70)*HeaderScale,0);
                    Child.sizeDelta=new Vector2(Width*HeaderScale,28);
                    Child.GetComponentInChildren<TMP_Text>().fontSize=11*HeaderScale;
                }
            }
        }
        RectTransform Prestige=(RectTransform)RankText.transform.parent;
        float PrestigeWidth=Mathf.Min(132,SafeFrame.rect.width-196);
        Prestige.sizeDelta=new Vector2(PrestigeWidth,26);
        RankText.rectTransform.sizeDelta=new Vector2(PrestigeWidth-70,24);
        RankFill.rectTransform.sizeDelta=new Vector2(Mathf.Min(30,PrestigeWidth*0.28f),6);
        RectTransform Hint=(RectTransform)BuildHint.transform.parent;
        Hint.anchorMin=Vector2.zero;
        Hint.anchorMax=Vector2.right;
        Hint.pivot=new Vector2(0.5f,0);
        Hint.offsetMin=new Vector2(10,44);
        Hint.offsetMax=new Vector2(-82,72);
        BuildHint.rectTransform.sizeDelta=new Vector2(Mathf.Max(80,Hint.rect.width-24),27);
        float MapHeight=(Height-CurtainHeight)*TopFraction;
        if (bMapFullscreen)
        {
            MapHeight = Height;
        }
        Hud.MapPanel.anchorMin=new Vector2(0,1);
        Hud.MapPanel.anchorMax=Vector2.one;
        Hud.MapPanel.pivot=new Vector2(0.5f,1);
        Hud.MapPanel.anchoredPosition=Vector2.zero;
        Hud.MapPanel.sizeDelta=new Vector2(0,MapHeight);
        Handle.anchorMin=new Vector2(0,1);
        Handle.anchorMax=Vector2.one;
        Handle.pivot=new Vector2(0.5f,1);
        Handle.anchoredPosition=new Vector2(0,-MapHeight);
        Handle.sizeDelta=new Vector2(0,CurtainHeight);
        BattlePane.anchorMin=Vector2.zero;
        BattlePane.anchorMax=Vector2.one;
        BattlePane.offsetMin=Vector2.zero;
        BattlePane.offsetMax=new Vector2(0,-Mathf.Min(Height - 1, MapHeight+CurtainHeight));
        BattlePane.gameObject.SetActive(!bMapFullscreen);
        Handle.gameObject.SetActive(!bMapFullscreen);
        Vector3[] Corners=new Vector3[4];
        BattlePane.GetWorldCorners(Corners);
        if (Game.Root != null)
        {
            Game.Root.BattleSurface.Refresh();
        }
        else
        {
            Game.WorldCamera.rect=new Rect(Corners[0].x/Screen.width,Corners[0].y/Screen.height,(Corners[2].x-Corners[0].x)/Screen.width,(Corners[2].y-Corners[0].y)/Screen.height);
        }
        StaminaPanel.anchoredPosition=new Vector2(10,-44);
        if(Game.bMapExpanded)
        {
            StaminaPanel.anchoredPosition=new Vector2(10,-36);
        }
    }
    public void UpdateCamera(float DeltaSeconds)
    {
        if (Game.Root != null)
        {
            Game.Root.CameraPresenter.Track();
            return;
        }
        float BaseCollapsed=0.25f;
        float BaseExpanded=0.75f;
        if(Settings.Ratio==MobileRatioPreset.Third)
        {
            BaseCollapsed=0.3333f;
            BaseExpanded=0.6667f;
        }
        float Expansion=Mathf.Clamp01((TopFraction-BaseCollapsed)/Mathf.Max(0.1f,BaseExpanded-BaseCollapsed));
        CameraFocus=Vector3.Lerp(CameraFocus,Game.PlayerPosition,Mathf.Min(1,DeltaSeconds*12));
        float ShortPane=Mathf.Max(0,(BattlePane.rect.width/Mathf.Max(1,BattlePane.rect.height)-1.1f)*2.2f);
        CameraHeight=Mathf.Lerp(CameraHeight,13+Expansion*9.5f+ShortPane,Mathf.Min(1,DeltaSeconds*8));
        CameraOffset=Mathf.Lerp(CameraOffset,11.2f*(1-Expansion*0.7f)+ShortPane*0.35f,Mathf.Min(1,DeltaSeconds*8));
        Game.WorldCamera.transform.position=CameraFocus+new Vector3(0,CameraHeight,CameraOffset);
        Game.WorldCamera.transform.LookAt(CameraFocus+Vector3.up*0.45f);
    }
    public void SnapCamera()
    {
        if (Game.Root != null)
        {
            Game.Root.CameraPresenter.Snap();
            return;
        }
        CameraFocus=Game.PlayerPosition;
        Game.WorldCamera.transform.position=CameraFocus+new Vector3(0,CameraHeight,CameraOffset);
        Game.WorldCamera.transform.LookAt(CameraFocus+Vector3.up*0.45f);
    }
    public void SetRatio(int Index)
    {
        Settings.Ratio=(MobileRatioPreset)Index;
        if(Settings.Ratio!=MobileRatioPreset.Free)
        {
            SetCurtain(Game.bMapExpanded);
        }
    }
    public void SetCurtain(bool bOpen)
    {
        bMapFullscreen = false;
        Game.CancelGesture();
        CancelWheel();
        bool bWasOpen=Game.bMapExpanded;
        Game.bMapExpanded=bOpen;
        TransitionStart=TopFraction;
        TargetFraction=0.25f;
        if(bOpen)
        {
            TargetFraction=ExpandedFraction;
        }
        if(Settings.Ratio==MobileRatioPreset.Third)
        {
            TargetFraction=0.3333f;
            if(bOpen)
            {
                TargetFraction=0.6667f;
            }
        }
        if(Settings.Ratio==MobileRatioPreset.Free)
        {
            TargetFraction=Mathf.Clamp(TopFraction,0.25f,0.9f);
        }
        TransitionAge=0;
        if(!bWasOpen && bOpen)
        {
            Game.SlowMotionRemaining=Settings.SlowMotionDuration;
        }
    }
    public void DragCurtain(Vector2 ScreenPosition)
    {
        Vector2 Local;
        RectTransformUtility.ScreenPointToLocalPointInRectangle(Split,ScreenPosition,null,out Local);
        bool bWasOpen=Game.bMapExpanded;
        TopFraction=Mathf.Clamp((Split.rect.yMax-Local.y-CurtainHeight*0.5f)/(Split.rect.height-CurtainHeight),0.25f,0.9f);
        TargetFraction=TopFraction;
        Game.bMapExpanded=TopFraction>=0.48f;
        if(!bWasOpen && Game.bMapExpanded)
        {
            Game.SlowMotionRemaining=Settings.SlowMotionDuration;
        }
        ApplyLayout(0);
    }
    public void FinishCurtain()
    {
        bCurtainDragging=false;
        Game.bMapExpanded = TopFraction >= 0.48f;
        TargetFraction = TopFraction;
        TransitionAge = 1;
        if (Game.bMapExpanded)
        {
            ExpandedFraction = TopFraction;
            PlayerPrefs.SetFloat("MapExpandedFraction", ExpandedFraction);
        }
    }
    public float TimeScale(float RealDeltaSeconds)
    {
        if (Game.bGestureActive)
        {
            return 1;
        }
        if (bWheelActive && !Game.bMapExpanded)
        {
            return Settings.SlowMotionFactor;
        }
        if (Game.SlowMotionRemaining > 0)
        {
            return Settings.SlowMotionFactor;
        }
        return 1;
    }
    public bool InSwipeZone(Vector2 Position,RectTransform Rectangle)
    {
        if(Settings.SwipeZone==MobileSwipeZone.Handle)
        {
            return false;
        }
        if(Settings.SwipeZone==MobileSwipeZone.Map)
        {
            return true;
        }
        Vector2 Local;
        RectTransformUtility.ScreenPointToLocalPointInRectangle(Rectangle,Position,null,out Local);
        float Horizontal=(Local.x-Rectangle.rect.xMin)/Rectangle.rect.width;
        if(Settings.SwipeZone==MobileSwipeZone.Left)
        {
            return Horizontal<=Settings.SwipeEdgePercent/100;
        }
        if(Settings.SwipeZone==MobileSwipeZone.Right)
        {
            return Horizontal>=1-Settings.SwipeEdgePercent/100;
        }
        return Horizontal<=Settings.SwipeEdgePercent/100 || Horizontal>=1-Settings.SwipeEdgePercent/100;
    }
    public bool TryMapSwipe(Vector2 Start,Vector2 End,float Seconds)
    {
        if(!InSwipeZone(Start,Hud.MapRect))
        {
            return false;
        }
        float EdgeBonus=1;
        if(Settings.SwipeZone==MobileSwipeZone.Left || Settings.SwipeZone==MobileSwipeZone.Right || Settings.SwipeZone==MobileSwipeZone.Edges)
        {
            EdgeBonus=0.75f;
        }
        float Progress=(Settings.SwipeSensitivity-1)/9;
        Vector2 Offset=(End-Start)/InputScale;
        if(Mathf.Abs(Offset.y)<Mathf.Round((78-Progress*56)*EdgeBonus) || Seconds>(130+Progress*230)/EdgeBonus/1000+0.04f || Mathf.Abs(Offset.y)<Mathf.Abs(Offset.x)*Mathf.Max(1.05f,(2.1f-Progress*0.95f)*EdgeBonus))
        {
            return false;
        }
        bool bOpen=Offset.y<0;
        if(Settings.Ratio==MobileRatioPreset.Free)
        {
            TopFraction=Mathf.Clamp(TopFraction+Mathf.Sign(-Offset.y)*0.35f,0.22f,0.78f);
        }
        SetCurtain(bOpen);
        return true;
    }
    public void BeginWheel(Vector2 ScreenPosition)
    {
        Game.CancelGesture();
        bWheelActive=true;
        WheelProgress=0;
        WheelSelection=-1;
        RectTransformUtility.ScreenPointToLocalPointInRectangle(Split,ScreenPosition,null,out WheelStart);
        WheelPointer=WheelStart;
        WheelCenter=new Vector2(Split.rect.center.x,Split.rect.yMax-Split.rect.height*0.7f);
        if(Settings.bWheelBlur)
        {
            StartCoroutine(CaptureWheelBackground());
        }
    }
    private System.Collections.IEnumerator CaptureWheelBackground()
    {
        yield return new WaitForEndOfFrame();
        if(!bWheelActive)
        {
            yield break;
        }
        if(BlurSnapshot!=null)
        {
            Destroy(BlurSnapshot);
        }
        Texture2D Capture=ScreenCapture.CaptureScreenshotAsTexture();
        BlurSnapshot=new Texture2D(Capture.width,Capture.height,TextureFormat.RGBA32,false,false);
        BlurSnapshot.LoadRawTextureData(Capture.GetRawTextureData<byte>());
        BlurSnapshot.Apply();
        Destroy(Capture);
        BlurSnapshot.filterMode=FilterMode.Bilinear;
        WheelBlur.texture=BlurSnapshot;
    }
    public int WheelCount
    {
        get
        {
            if(!Game.bMapExpanded)
            {
                return 3;
            }
            return 8;
        }
    }
    public void MoveWheel(Vector2 ScreenPosition)
    {
        RectTransformUtility.ScreenPointToLocalPointInRectangle(Split,ScreenPosition,null,out WheelPointer);
        float Total=Mathf.Max(70,WheelCenter.y-WheelStart.y);
        WheelProgress=Mathf.Clamp01((WheelPointer.y-WheelStart.y)/(Total*0.65f));
        WheelSelection=-1;
        Vector2 Offset=WheelPointer-WheelCenter;
        if(WheelProgress>0.25f && Offset.magnitude>14)
        {
            float Angle=Mathf.Atan2(Offset.x,Offset.y);
            if(Angle<0)
            {
                Angle+=Mathf.PI*2;
            }
            WheelSelection=Mathf.FloorToInt(Angle/(Mathf.PI*2/WheelCount))%WheelCount;
        }
    }
    public void EndWheel()
    {
        if(WheelSelection>=0)
        {
            if(!Game.bMapExpanded)
            {
                Game.EquipWeapon(WheelSelection);
            }
            else
            {
                Game.bTrebuchet = WheelSelection == 6;
                Game.bSellBuildings = WheelSelection == 7;
                if (WheelSelection < 6)
                {
                    Game.StructureIndex=WheelSelection;
                }
            }
        }
        CancelWheel();
    }
    public void CancelWheel()
    {
        bWheelActive=false;
        WheelProgress=0;
        WheelSelection=-1;
    }
    public void Refresh()
    {
        ApplyLayout(Time.unscaledDeltaTime);
        RankText.text="Ранг "+Game.Rank;
        GoldEnemiesText.text=$"{Game.Gold}   |   Враги {Game.Enemies.Count} / {Game.EnemyLimit}";
        RankFill.fillAmount=Mathf.Clamp01(Game.Experience/(50*Mathf.Pow(Game.Rules.PrestigeGrowth,Game.Rank-1)));
        if (Game.Root == null)
        {
            Hud.HealthText.text=Mathf.CeilToInt(Game.Health).ToString();
            Hud.StaminaText.text=$"{Mathf.FloorToInt(Game.Stamina)} / {Game.Rules.MaximumStamina:0}";
            WeaponText.text=WeaponZoneNames[Game.WeaponIndex];
            StateText.text="Ожидание";
            if(Game.bGestureActive && Game.GestureOffset.magnitude>Game.Rules.JoystickCenterRadius)
            {
                StateText.text="Движение";
            }
            if(Game.bGestureActive && Game.GestureOffset.magnitude>=Game.Rules.JoystickMovementRadius)
            {
                StateText.text="Бег";
            }
            if(Game.Charge>0)
            {
                StateText.text="Подготовка";
            }
            if(Game.bAttacking)
            {
                StateText.text="Спецатака";
            }
            RatioText.text=$"{TopFraction*100:0}% / {(1-TopFraction)*100:0}%";
            string[] SwipeLabels=
            {
                "Карта","Справа","Слева","Края","Карта"
            }
            ;
            SwipeText.text="Свайп: "+SwipeLabels[(int)Settings.SwipeZone];
        }
        PhoneIcon PreviousDockIcon = DockIcon.Icon;
        DockTag.text="ОРУЖИЕ";
        DockName.text=Game.CurrentWeapon.Name;
        DockIcon.Icon=WeaponIcon(Game.WeaponIndex);
        DockIcon.color=Game.CurrentWeapon.Color;
        DockBackground.color=CombatUIFactory.Hex("#92400e");
        if(Game.bMapExpanded)
        {
            DockTag.text="ПОСТРОЙКИ";
            DockName.text=Game.Rules.Structures[Game.StructureIndex].Name;
            DockIcon.Icon=BuildingIcon(Game.StructureIndex);
            DockIcon.color=BuildingColor(Game.StructureIndex);
            DockBackground.color=CombatUIFactory.Hex("#075985");
            if(Game.bTrebuchet)
            {
                DockTag.text="ТРЕБУШЕТ";
                DockName.text="Требушет";
                DockIcon.Icon=PhoneIcon.Trebuchet;
                DockIcon.color=CombatUIFactory.Hex("#fb923c");
            }
        }
        if (PreviousDockIcon != DockIcon.Icon)
        {
            DockIcon.SetVerticesDirty();
        }
        if (Game.Root == null)
        {
            Hud.HelpText.text=$"Топор: короткий тап по центру наносит круговой удар. Автоатака включается в настройках.\nСпецатака требует {Game.SpecialSacrificeCost} жертв. Убийства накапливаются; при готовности центр джойстика краснеет.\nУдерживайте центр {Game.Rules.ChargeSeconds:0.0} с. После заряда сдвиньте в красную зону и отпустите для атаки с перемещением.\nОтпустите в центре для защиты. Спецатака не расходует выносливость. Следующее применение требует на {Game.Rules.SpecialSacrificeCostIncrease} жертву больше.";
            float HelpWidth=BattlePane.rect.width-20;
            Hud.HelpText.rectTransform.sizeDelta=new Vector2(HelpWidth,Mathf.Max(130,Hud.HelpText.preferredHeight));
            float ActionHeight=Mathf.Max(23,Hud.ActionText.preferredHeight);
            Hud.ActionText.rectTransform.sizeDelta=new Vector2(Mathf.Min(220,BattlePane.rect.width-40),ActionHeight);
            StaminaPanel.sizeDelta=new Vector2(Mathf.Min(240,BattlePane.rect.width-20),35+ActionHeight);
            BuildHint.transform.parent.gameObject.SetActive(Game.bMapExpanded);
            BuildHint.text=$"Построить: {Game.Rules.Structures[Game.StructureIndex].Name} ({Game.Rules.Structures[Game.StructureIndex].Price} золота)";
            if(Game.bTrebuchet)
            {
                BuildHint.text=$"Обстрел 3×3: {Game.Rules.TrebuchetPrice} золота, через {Game.Rules.TrebuchetDelay:0.0} с, {Game.Rules.TrebuchetDamage:0} урона";
            }
        }
        bool bSlow=Game.bMapExpanded && Settings.TimeMode==MobileMapTime.SlowMotion && (Settings.bInfiniteSlowMotion || Game.SlowMotionRemaining>0);
        SlowMotionPanel.SetActive(bSlow);
        SlowMotionFill.fillAmount=Game.SlowMotionRemaining/Mathf.Max(0.1f,Settings.SlowMotionDuration);
        SlowMotionLabel.text=$"Замедление времени                         {Game.SlowMotionRemaining:0.0}с";
        TimeBadge.text="100% Реалтайм";
        if(bSlow)
        {
            TimeBadge.text=$"{Settings.SlowMotionFactor*100:0}% ({Game.SlowMotionRemaining:0.0}с)";
        }
        if(Game.bMapExpanded && Settings.TimeMode==MobileMapTime.Pause)
        {
            TimeBadge.text="ПАУЗА (0%)";
        }
        if (Game.Root == null)
        {
            for(int Index=0;Index<RatioButtons.Length;Index++)
            {
                RatioButtons[Index].color=CombatUIFactory.Hex("#292524");
                if((int)Settings.Ratio==Index)
                {
                    RatioButtons[Index].color=CombatUIFactory.Hex("#d97706");
                }
            }
            Hud.HelpText.gameObject.SetActive(!Game.bGestureActive && !Game.bMapExpanded);
        }
        JoystickMovementLabel.gameObject.SetActive(Game.bGestureActive);
        JoystickActionLabel.gameObject.SetActive(Game.bGestureActive);
        if(Game.bGestureActive)
        {
            Vector2 Origin;
            RectTransformUtility.ScreenPointToLocalPointInRectangle(BattlePane,Game.GestureStart,null,out Origin);
            JoystickMovementLabel.rectTransform.anchoredPosition=Origin+new Vector2(0,-33);
            JoystickActionLabel.rectTransform.anchoredPosition=Origin+new Vector2(0,-Game.Rules.JoystickOuterRadius-25);
            JoystickMovementLabel.text="ДВИЖЕНИЕ";
            JoystickActionLabel.text=$"СПЕЦАТАКА {Game.Combo.Fraction * 100:0}%";
            if(Game.bSpecialReady)
            {
                JoystickActionLabel.text="СПЕЦАТАКА ГОТОВА";
            }
            JoystickActionLabel.color=CombatUIFactory.Hex("#e2e8f0");
            if(Game.bSpecialReady)
            {
                JoystickActionLabel.color=CombatUIFactory.Hex("#fb7185");
            }
            if(Game.Charge>0)
            {
                JoystickActionLabel.text=$"ЗАРЯД {Game.Charge/Game.Rules.ChargeSeconds*100:0}%";
            }
            if(Game.Charge>=Game.Rules.ChargeSeconds)
            {
                JoystickActionLabel.text="ОТПУСТИТЕ: ЗАЩИТА";
                if(Game.bAttackSelected)
                {
                    JoystickActionLabel.text="ОТПУСТИТЕ: АТАКА";
                }
            }
        }
        WheelLayer.SetActive(bWheelActive);
        if (!bWheelActive)
        {
            return;
        }
        WheelShade.color=new Color(0.03f,0.045f,0.07f,0);
        if(Settings.bWheelDim)
        {
            WheelShade.color=new Color(0.03f,0.045f,0.07f,0.58f*WheelProgress);
        }
        Vector3[] SplitCorners=new Vector3[4];
        Split.GetWorldCorners(SplitCorners);
        WheelBlur.uvRect=new Rect(SplitCorners[0].x/Screen.width,SplitCorners[0].y/Screen.height,(SplitCorners[2].x-SplitCorners[0].x)/Screen.width,(SplitCorners[2].y-SplitCorners[0].y)/Screen.height);
        WheelBlur.material.SetFloat("_BlurPixels",6*InputScale*WheelProgress);
        WheelBlur.color=new Color(1,1,1,0);
        if(Settings.bWheelBlur && BlurSnapshot!=null)
        {
            WheelBlur.color=new Color(1,1,1,WheelProgress);
        }
        Wheel.SetVerticesDirty();
        for(int Index=0;Index<WheelLabels.Length;Index++)
        {
            bool bVisible=bWheelActive && WheelProgress>0.3f && Index<WheelCount;
            WheelLabels[Index].gameObject.SetActive(bVisible);
            WheelIcons[Index].gameObject.SetActive(bVisible);
            if(!bVisible)
            {
                continue;
            }
            float Outer=36+78*WheelProgress;
            float Inner=22*WheelProgress;
            float Angle=(Index+0.5f)*Mathf.PI*2/WheelCount;
            Vector2 Center=Vector2.Lerp(WheelStart,WheelCenter,WheelProgress);
            Vector2 Position=Center+new Vector2(Mathf.Sin(Angle),Mathf.Cos(Angle))*(Inner+Outer)*0.56f;
            WheelLabels[Index].rectTransform.anchoredPosition=Position+new Vector2(0,-16);
            WheelIcons[Index].rectTransform.anchoredPosition=Position+new Vector2(0,9);
            WheelIcons[Index].Icon=WeaponIcon(Index);
            WheelIcons[Index].color=Game.CurrentWeapon.Color;
            WheelLabels[Index].text=Game.Rules.Weapons[Mathf.Min(Index,2)].Name+"\n"+WeaponZoneNames[Mathf.Min(Index,2)];
            if(Game.bMapExpanded)
            {
                int BuildingIndex = Mathf.Min(Index, 5);
                WheelLabels[Index].text=Game.Rules.Structures[BuildingIndex].Name+$"\n{Game.Rules.Structures[BuildingIndex].Price} золота";
                WheelIcons[Index].Icon=BuildingIcon(Index);
                WheelIcons[Index].color=BuildingColor(Index);
                if(Index == 6)
                {
                    WheelLabels[Index].text=$"Требушет\n{Game.Rules.TrebuchetPrice} золота";
                    WheelIcons[Index].Icon=PhoneIcon.Trebuchet;
                    WheelIcons[Index].color=CombatUIFactory.Hex("#fb923c");
                }
                if (Index == 7)
                {
                    WheelLabels[Index].text = "Продать";
                }
            }
            WheelIcons[Index].SetVerticesDirty();
        }
    }
    public static PhoneIcon WeaponIcon(int Index)
    {
        if(Index==0)
        {
            return PhoneIcon.Sword;
        }
        if(Index==1)
        {
            return PhoneIcon.Spear;
        }
        return PhoneIcon.Axe;
    }
    public static PhoneIcon BuildingIcon(int Index)
    {
        if(Index==0)
        {
            return PhoneIcon.Tower;
        }
        if(Index==1)
        {
            return PhoneIcon.Wall;
        }
        if(Index==2)
        {
            return PhoneIcon.Trap;
        }
        return PhoneIcon.Flag;
    }
    public static Color BuildingColor(int Index)
    {
        string[] Colors=
        {
            "#3b82f6","#9ca3af","#f97316","#22c55e"
        }
        ;
        return CombatUIFactory.Hex(Colors[Index % Colors.Length]);
    }
}
