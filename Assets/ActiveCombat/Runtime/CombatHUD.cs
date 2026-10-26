using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
public class CombatHUD : MonoBehaviour
{
    public ActiveCombatGame Game;
    public MobileCombatInterface Mobile;
    public Sprite RoundedPanelSprite;
    private void Awake()
    {
        CombatUIFactory.RoundSprite=RoundedPanelSprite;
    }
    public TMP_FontAsset Font;
    public RectTransform CanvasRoot;
    public TMP_Text HealthText;
    public TMP_Text StaminaText;
    public TMP_Text Counters;
    public TMP_Text ActionText;
    public TMP_Text HelpText;
    public TMP_Text ErrorText;
    public UnityEngine.UI.Image HealthFill;
    public UnityEngine.UI.Image StaminaFill;
    public UnityEngine.UI.Image ExhaustionOverlay;
    public UnityEngine.UI.Image HitOverlay;
    public UnityEngine.UI.RawImage MapImage;
    public RectTransform MapPanel;
    public RectTransform MapRect;
    public GameObject SettingsPanel;
    public RectTransform SettingsContent;
    public TMP_Text SettingsError;
    public CombatJoystick Joystick;
    public UnityEngine.UI.Button[] WeaponButtons;
    public UnityEngine.UI.Button[] BuildingButtons;
    public UnityEngine.UI.Button BuildingsModeButton;
    public UnityEngine.UI.Button TrebuchetModeButton;
    public GameObject PausePanel;
    private Texture2D MapTexture;
    private CombatBalance Pending;
    private List<SettingBinding> Bindings = new List<SettingBinding>();
    private List<FloatingLabel> Labels = new List<FloatingLabel>();
    private float Darkness;
    private readonly Vector3[] LabelCorners = new Vector3[4];
    private class SettingBinding
    {
        public object Target;
        public FieldInfo Field;
        public TMP_InputField Input;
        public UnityEngine.UI.Toggle Toggle;
    }
    private class FloatingLabel
    {
        public TMP_Text Text;
        public Vector3 Position;
        public float Age;
    }
    public void Refresh(ActiveCombatGame Current,float DeltaSeconds)
    {
        using var Measurement = CombatPerformanceMarkers.Hud.Auto();
        Joystick.SetVerticesDirty();
        float Low=Mathf.Clamp01(1-Current.Stamina/Current.Rules.MaximumStamina/Current.Rules.LowStaminaThreshold);
        Darkness=Mathf.Lerp(Darkness,Low*Low*Current.Rules.LowStaminaDarkness,1-Mathf.Exp(-DeltaSeconds*12));
        ExhaustionOverlay.color=new Color(0.01f,0.02f,0.04f,Darkness);
        if (Current.Root != null)
        {
            Mobile.Refresh();
            RefreshFloatingLabels(Current, DeltaSeconds);
            return;
        }
        HealthText.text=$"Здоровье {Mathf.CeilToInt(Current.Health)} / {Current.MaximumHealth:0}";
        StaminaText.text=$"Выносливость {Mathf.FloorToInt(Current.Stamina)} / {Current.Rules.MaximumStamina:0}";
        HealthFill.fillAmount=Current.Health/Current.MaximumHealth;
        StaminaFill.fillAmount=Mathf.Min(1,Current.Stamina/Current.Rules.MaximumStamina);
        StaminaFill.color=new Color(0.32f,0.8f,0.65f);
        if(Current.Stamina>Current.Rules.MaximumStamina)
        {
            StaminaFill.color=new Color(1,0.75f,0.2f);
        }
        Counters.text=$"Ранг {Current.Rank}     Золото {Current.Gold}     Враги {Current.Enemies.Count} / {Current.EnemyLimit}     Убито {Current.Kills}";
        string Action=$"Дэш: {Current.Rules.DashStaminaCost:0} выносливости";
        if(Current.Charge>0)
        {
            Action=$"Подготовка: {Current.Charge/Current.Rules.ChargeSeconds*100:0}%";
        }
        if(Current.Charge>=Current.Rules.ChargeSeconds)
        {
            Action="Отпустите: "+Current.CurrentWeapon.DefenseName;
            if(Current.bAttackSelected)
            {
                Action="Отпустите: "+Current.CurrentWeapon.AttackName;
            }
        }
        if(Current.bAttacking)
        {
            Action=Current.Status;
        }
        if(Current.Shield>0)
        {
            Action+=$"    Щит {Current.Shield:0} ({Current.DefenseRemaining:0.0} с)";
        }
        if(Current.DamageBonusRemaining>0)
        {
            Action+=$"    Урон +{Current.DamageBonusPercent:0}% ({Current.DamageBonusRemaining:0.0} с)";
        }
        string SpecialStatus=$"Спецатака: {Current.Combo.Fraction * 100:0}%";
        if(Current.bSpecialReady)
        {
            SpecialStatus="<color=#fb7185>Спецатака готова</color>";
        }
        ActionText.text=Action+"\n"+SpecialStatus;
        ErrorText.text=Current.ErrorMessage;
        PausePanel.SetActive(Current.bPaused);
        HitOverlay.color=new Color(0.7f,0.05f,0.03f,Current.HitFeedback*0.16f);
        Color Selected=new Color(0.38f,0.3f,0.15f,1);
        Color Normal=new Color(0.12f,0.16f,0.18f,1);
        for(int Index=0;Index<WeaponButtons.Length;Index++)
        {
            Color Color=Normal;
            if(Index==Current.WeaponIndex)
            {
                Color=Selected;
            }
            WeaponButtons[Index].GetComponent<UnityEngine.UI.Image>().color=Color;
        }
        for(int Index=0;Index<BuildingButtons.Length;Index++)
        {
            Color Color=Normal;
            if(!Current.bTrebuchet && Index==Current.StructureIndex)
            {
                Color=Selected;
            }
            BuildingButtons[Index].GetComponent<UnityEngine.UI.Image>().color=Color;
        }
        BuildingsModeButton.GetComponent<UnityEngine.UI.Image>().color=Selected;
        TrebuchetModeButton.GetComponent<UnityEngine.UI.Image>().color=Normal;
        if(Current.bTrebuchet)
        {
            BuildingsModeButton.GetComponent<UnityEngine.UI.Image>().color=Normal;
            TrebuchetModeButton.GetComponent<UnityEngine.UI.Image>().color=Selected;
        }
        if(Mobile!=null)
        {
            Mobile.Refresh();
        }
        RefreshFloatingLabels(Current, DeltaSeconds);
    }
    private void RefreshFloatingLabels(ActiveCombatGame Current, float DeltaSeconds)
    {
        if (Labels.Count == 0)
        {
            return;
        }
        Mobile.BattlePane.GetWorldCorners(LabelCorners);
        for (int Index = Labels.Count - 1; Index >= 0; Index--)
        {
            FloatingLabel Label = Labels[Index];
            Label.Age+=DeltaSeconds;
            Vector3 Screen=Current.WorldCamera.WorldToScreenPoint(Label.Position+Vector3.up*Label.Age);
            if (Current.Root != null)
            {
                Vector3 Viewport = Current.WorldCamera.WorldToViewportPoint(Label.Position + Vector3.up * Label.Age);
                Screen = new Vector3(Mathf.Lerp(LabelCorners[0].x, LabelCorners[2].x, Viewport.x), Mathf.Lerp(LabelCorners[0].y, LabelCorners[2].y, Viewport.y), Viewport.z);
            }
            Vector2 Local;
            RectTransformUtility.ScreenPointToLocalPointInRectangle(Mobile.BattlePane,Screen,null,out Local);
            Label.Text.rectTransform.anchoredPosition=Local;
            Label.Text.alpha=1-Label.Age/0.65f;
            if(Label.Age>=0.65f)
            {
                Destroy(Label.Text.gameObject);
                Labels.RemoveAt(Index);
            }
        }
    }
    public void SetMapExpanded(bool bExpanded)
    {
        if(Mobile!=null)
        {
            Mobile.SetCurtain(bExpanded);
            return;
        }
        MapPanel.sizeDelta=new Vector2(330,410);
        MapRect.sizeDelta=new Vector2(306,306);
        if(bExpanded)
        {
            MapPanel.sizeDelta=new Vector2(600,680);
            MapRect.sizeDelta=new Vector2(576,576);
        }
    }
    public void RefreshMap(ActiveCombatGame Current)
    {
        if (Current.Root != null && Current.Root.MapPresenter != null)
        {
            Current.Root.MapPresenter.Refresh();
            return;
        }
        int Width=Mathf.Max(1,Mathf.CeilToInt(MapRect.rect.width));
        int Height=Mathf.Max(1,Mathf.CeilToInt(MapRect.rect.height));
        if(MapTexture==null || MapTexture.width!=Width || MapTexture.height!=Height)
        {
            if(MapTexture!=null)
            {
                Destroy(MapTexture);
            }
            MapTexture=new Texture2D(Width,Height,TextureFormat.RGBA32,false);
            MapTexture.filterMode=FilterMode.Point;
            MapImage.texture=MapTexture;
        }
        Color32[] Pixels=new Color32[Width*Height];
        Color32 Background=CombatUIFactory.Hex("#182417");
        for(int Index=0;Index<Pixels.Length;Index++)
        {
            Pixels[Index]=Background;
        }
        MapTexture.SetPixels32(Pixels);
        float Cell=Mathf.Clamp(Mathf.Min(Width-76,Height)*0.86f/Current.Rules.GridSize,4,34);
        float CenterX=(Width-76)/2f;
        float CenterY=Height/2f;
        int Half=Current.Rules.GridSize/2;
        for(int CellX=-Half;CellX<=Half;CellX++)
        {
            for(int CellZ=-Half;CellZ<=Half;CellZ++)
            {
                MapRectFill(CenterX-CellX*Cell,CenterY-CellZ*Cell,Cell,Cell,CombatUIFactory.Hex("#354b2d"));
                MapRectFill(CenterX-CellX*Cell,CenterY-CellZ*Cell,Cell-1,Cell-1,CombatUIFactory.Hex("#20391e"));
            }
        }
        foreach(Transform Scenery in Current.SceneryRoot)
        {
            Color Color=CombatUIFactory.Hex("#14532d");
            if(Scenery.name.Contains("Rock"))
            {
                Color=CombatUIFactory.Hex("#6b7280");
            }
            MapCircle(CenterX-Scenery.position.x/Current.Rules.CellSize*Cell,CenterY-Scenery.position.z/Current.Rules.CellSize*Cell,Cell*0.34f,Color);
        }
        foreach(ActiveCombatGame.BombState Bomb in Current.Bombs)
        {
            for(int OffsetX=-Current.Rules.TrebuchetCellRadius;OffsetX<=Current.Rules.TrebuchetCellRadius;OffsetX++)
            {
                for(int OffsetZ=-Current.Rules.TrebuchetCellRadius;OffsetZ<=Current.Rules.TrebuchetCellRadius;OffsetZ++)
                {
                    int CellX=Bomb.Cell.x+OffsetX;
                    int CellZ=Bomb.Cell.y+OffsetZ;
                    if(Mathf.Abs(CellX)>Half || Mathf.Abs(CellZ)>Half)
                    {
                        continue;
                    }
                    MapRectFill(CenterX-CellX*Cell,CenterY-CellZ*Cell,Cell-1,Cell-1,new Color(0.7f+Mathf.Sin(Bomb.Remaining*20)*0.2f,0.28f,0.08f));
                }
            }
        }
        foreach(ActiveCombatGame.StructureState Structure in Current.Structures)
        {
            float Horizontal=CenterX-Structure.Position.x/Current.Rules.CellSize*Cell;
            float Vertical=CenterY-Structure.Position.z/Current.Rules.CellSize*Cell;
            MapRectFill(Horizontal,Vertical,Cell*0.84f+1.2f,Cell*0.84f+1.2f,Color.white);
            MapRectFill(Horizontal,Vertical,Cell*0.84f,Cell*0.84f,MobileCombatInterface.BuildingColor((int)Structure.Rules.Kind));
        }
        foreach(ActiveCombatGame.PickupState Pickup in Current.Pickups)
        {
            float Horizontal=CenterX-Pickup.Position.x/Current.Rules.CellSize*Cell;
            float Vertical=CenterY-Pickup.Position.z/Current.Rules.CellSize*Cell;
            MapRectFill(Horizontal,Vertical,10,10,CombatUIFactory.Hex("#158f60"));
            MapRectFill(Horizontal,Vertical,6,2,CombatUIFactory.Hex("#eefff6"));
            MapRectFill(Horizontal,Vertical,2,6,CombatUIFactory.Hex("#eefff6"));
        }
        foreach(ActiveCombatGame.EnemyState Enemy in Current.Enemies)
        {
            MapCircle(CenterX-Enemy.Position.x/Current.Rules.CellSize*Cell,CenterY-Enemy.Position.z/Current.Rules.CellSize*Cell,Mathf.Max(3.5f,Cell*0.24f),CombatUIFactory.Hex("#ef4444"));
        }
        float PlayerX=CenterX-Current.PlayerPosition.x/Current.Rules.CellSize*Cell;
        float PlayerY=CenterY-Current.PlayerPosition.z/Current.Rules.CellSize*Cell;
        MapCircle(PlayerX,PlayerY,5.5f,Color.white);
        MapCircle(PlayerX,PlayerY,4,CombatUIFactory.Hex("#38bdf8"));
        MapTexture.Apply(false);
    }
    private void MapRectFill(float CenterX,float CenterY,float Width,float Height,Color Color)
    {
        int FirstX=Mathf.Max(0,Mathf.FloorToInt(CenterX-Width/2));
        int LastX=Mathf.Min(MapTexture.width-1,Mathf.CeilToInt(CenterX+Width/2));
        int FirstY=Mathf.Max(0,Mathf.FloorToInt(CenterY-Height/2));
        int LastY=Mathf.Min(MapTexture.height-1,Mathf.CeilToInt(CenterY+Height/2));
        for(int Horizontal=FirstX;Horizontal<=LastX;Horizontal++)
        {
            for(int Vertical=FirstY;Vertical<=LastY;Vertical++)
            {
                MapTexture.SetPixel(Horizontal,Vertical,Color);
            }
        }
    }
    private void MapCircle(float CenterX,float CenterY,float Radius,Color Color)
    {
        for(int Horizontal=Mathf.FloorToInt(CenterX-Radius);Horizontal<=Mathf.CeilToInt(CenterX+Radius);Horizontal++)
        {
            for(int Vertical=Mathf.FloorToInt(CenterY-Radius);Vertical<=Mathf.CeilToInt(CenterY+Radius);Vertical++)
            {
                if(Horizontal<0 || Vertical<0 || Horizontal>=MapTexture.width || Vertical>=MapTexture.height || new Vector2(Horizontal-CenterX,Vertical-CenterY).sqrMagnitude>Radius*Radius)
                {
                    continue;
                }
                MapTexture.SetPixel(Horizontal,Vertical,Color);
            }
        }
    }
    public void ClearDamageLabels()
    {
        foreach(FloatingLabel Label in Labels)
        {
            Destroy(Label.Text.gameObject);
        }
        Labels.Clear();
    }
    public void RefreshChoices()
    {
        foreach(MobileInterfaceAction Choice in SettingsContent.GetComponentsInChildren<MobileInterfaceAction>())
        {
            bool bSelected=false;
            if(Choice.Action==MobileAction.Ratio)
            {
                bSelected=(int)Mobile.Settings.Ratio==Choice.Index;
            }
            else if(Choice.Action==MobileAction.TimeMode)
            {
                bSelected=(int)Mobile.Settings.TimeMode==Choice.Index;
            }
            else if(Choice.Action==MobileAction.SwipeZone)
            {
                bSelected=(int)Mobile.Settings.SwipeZone==Choice.Index;
            }
            Color Color=CombatUIFactory.Hex("#292524");
            if(bSelected)
            {
                Color=CombatUIFactory.Hex("#d97706");
            }
            Choice.GetComponent<UnityEngine.UI.Image>().color=Color;
        }
    }
    public void FloatText(Vector3 Position,string Text,Color Color)
    {
        TMP_Text Label=CombatUIFactory.Text(Mobile.BattlePane,"Damage",Text,new Vector2(0.5f,0.5f),Vector2.zero,new Vector2(160,26),13,Font);
        Label.color=Color;
        Label.alignment=TextAlignmentOptions.Center;
        Labels.Add(new FloatingLabel
        {
            Text=Label,Position=Position
        }
        );
    }
    public void OpenSettings()
    {
        Game.CancelGesture();
        if(Mobile!=null)
        {
            Mobile.CancelWheel();
            Mobile.bCurtainDragging=false;
        }
        Game.bSettingsOpen=true;
        SettingsPanel.SetActive(true);
        SettingsError.text="";
        if(Pending!=null)
        {
            Destroy(Pending);
        }
        Pending=Instantiate(Game.Rules);
        foreach(Transform Child in SettingsContent)
        {
            Destroy(Child.gameObject);
        }
        Bindings.Clear();
        BuildPresentationChoices();
        BuildSettingsGroup("Время и жесты",Pending.MobileView);
        BuildSettingsGroup("Параметры оружия и теста",Pending);
        foreach(WeaponBalance Weapon in Pending.Weapons)
        {
            BuildSettingsGroup(Weapon.Name,Weapon);
        }
        foreach(EnemyBalance Enemy in Pending.Enemies)
        {
            BuildSettingsGroup(Enemy.Name,Enemy);
        }
        foreach(StructureBalance Structure in Pending.Structures)
        {
            BuildSettingsGroup(Structure.Name,Structure);
        }
        RefreshChoices();
    }
    private void BuildPresentationChoices()
    {
        AddChoice("Разделение экрана",new[]
        {
            "1/4 и 3/4","1/3 и 2/3","Свободный"
        }
        ,MobileAction.Ratio);
        AddChoice("Время при опущенной карте",new[]
        {
            "Реалтайм","Пауза","Замедление"
        }
        ,MobileAction.TimeMode);
        AddChoice("Быстрый свайп на верхней карте",new[]
        {
            "Вся карта","Справа","Слева","Края","За шторку"
        }
        ,MobileAction.SwipeZone);
    }
    private void AddChoice(string Title,string[] Captions,MobileAction Action)
    {
        TMP_Text Header=CombatUIFactory.Text(SettingsContent,"Section",Title,Vector2.zero,Vector2.zero,new Vector2(330,28),12,Font);
        Header.color=CombatUIFactory.Hex("#fbbf24");
        Header.gameObject.AddComponent<UnityEngine.UI.LayoutElement>().preferredHeight=30;
        GameObject Row=new GameObject("Options",typeof(RectTransform),typeof(UnityEngine.UI.HorizontalLayoutGroup),typeof(UnityEngine.UI.LayoutElement));
        Row.transform.SetParent(SettingsContent,false);
        Row.GetComponent<UnityEngine.UI.LayoutElement>().preferredHeight=40;
        UnityEngine.UI.HorizontalLayoutGroup Layout=Row.GetComponent<UnityEngine.UI.HorizontalLayoutGroup>();
        Layout.spacing=4;
        Layout.childControlWidth=true;
        Layout.childControlHeight=true;
        Layout.childForceExpandWidth=true;
        Layout.childForceExpandHeight=true;
        for(int Index=0;Index<Captions.Length;Index++)
        {
            UnityEngine.UI.Button Button=CombatUIFactory.Button(Row.transform,"Option"+Index,Captions[Index],new Vector2(0.5f,0.5f),Vector2.zero,new Vector2(100,38),Font);
            Button.GetComponentInChildren<TMP_Text>().fontSize=10;
            MobileInterfaceFactory.MobileActionButton(Button,Mobile,Action,Index);
        }
    }
    private void BuildSettingsGroup(string Name,object Target)
    {
        TMP_Text Heading=CombatUIFactory.Text(SettingsContent,"Group",Name,Vector2.zero,Vector2.zero,new Vector2(330,28),12,Font);
        Heading.gameObject.AddComponent<UnityEngine.UI.LayoutElement>().preferredHeight=30;
        foreach(FieldInfo Field in Target.GetType().GetFields(BindingFlags.Public|BindingFlags.Instance))
        {
            if(Field.FieldType!=typeof(float) && Field.FieldType!=typeof(int) && Field.FieldType!=typeof(bool))
            {
                continue;
            }
            GameObject Row=CombatUIFactory.Panel(SettingsContent,"Setting",new Color(0.12f,0.15f,0.17f),Vector2.zero,Vector2.zero,new Vector2(330,38));
            Row.AddComponent<UnityEngine.UI.LayoutElement>().preferredHeight=38;
            string NameLabel=Field.Name;
            InspectorNameAttribute Attribute=Field.GetCustomAttribute<InspectorNameAttribute>();
            if(Attribute!=null)
            {
                NameLabel=Attribute.displayName;
            }
            TMP_Text Label=CombatUIFactory.Text(Row.transform,"Label",NameLabel,new Vector2(0,0.5f),new Vector2(12,0),new Vector2(225,34),11,Font);
            Label.alignment=TextAlignmentOptions.MidlineLeft;
            Label.rectTransform.anchorMin=new Vector2(0,0.5f);
            Label.rectTransform.anchorMax=new Vector2(1,0.5f);
            Label.rectTransform.offsetMin=new Vector2(8,-16);
            Label.rectTransform.offsetMax=new Vector2(-94,16);
            SettingBinding Binding=new SettingBinding
            {
                Target=Target,Field=Field
            }
            ;
            if(Field.FieldType==typeof(bool))
            {
                UnityEngine.UI.Button Button=CombatUIFactory.Button(Row.transform,"Boolean","",new Vector2(1,0.5f),new Vector2(-8,0),new Vector2(78,28),Font);
                TMP_Text ButtonText=Button.GetComponentInChildren<TMP_Text>();
                ButtonText.fontSize=10;
                bool Value=(bool)Field.GetValue(Target);
                ButtonText.text="Выключено";
                if(Value)
                {
                    ButtonText.text="Включено";
                }
                Button.onClick.AddListener(()=>
                {
                    bool NewValue=!(bool)Field.GetValue(Target);
                    Field.SetValue(Target,NewValue);
                    ButtonText.text="Выключено";
                    if(NewValue)
                    {
                        ButtonText.text="Включено";
                    }
                }
                );
            }
            else
            {
                Binding.Input=CombatUIFactory.Input(Row.transform,Font);
                Binding.Input.text=Convert.ToString(Field.GetValue(Target),CultureInfo.InvariantCulture);
                Bindings.Add(Binding);
            }
        }
    }
    public void ApplySettings()
    {
        foreach(SettingBinding Binding in Bindings)
        {
            float Value;
            if(!float.TryParse(Binding.Input.text.Replace(',','.'),NumberStyles.Float,CultureInfo.InvariantCulture,out Value) || float.IsNaN(Value) || float.IsInfinity(Value) || Value<0 || Value>10000)
            {
                SettingsError.text="Введите число от 0 до 10000.";
                return;
            }
            if(Binding.Field.FieldType==typeof(int))
            {
                Binding.Field.SetValue(Binding.Target,Mathf.RoundToInt(Value));
            }
            else
            {
                Binding.Field.SetValue(Binding.Target,Value);
            }
        }
        Pending.MobileView.Ratio=Game.Rules.MobileView.Ratio;
        Pending.MobileView.TimeMode=Game.Rules.MobileView.TimeMode;
        Pending.MobileView.SwipeZone=Game.Rules.MobileView.SwipeZone;
        string Error=Game.ValidateBalance(Pending);
        if(!string.IsNullOrEmpty(Error))
        {
            SettingsError.text=Error;
            return;
        }
        Game.ApplyBalance(Pending);
        CloseSettings();
    }
    public void CloseSettings()
    {
        SettingsPanel.SetActive(false);
        Game.bSettingsOpen=false;
        Game.CancelGesture();
        if (Game.Session != null)
        {
            Game.Session.SettingsClosed();
        }
    }
    public void ResetSettings()
    {
        if(Pending!=null)
        {
            Destroy(Pending);
        }
        Game.ApplyBalance(Game.Balance);
        OpenSettings();
    }
}
