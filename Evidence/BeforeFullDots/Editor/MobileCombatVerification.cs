using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
public static class MobileCombatVerification
{
    private static List<string> Passed;
    private static void Require(bool bCondition,string Message)
    {
        if(!bCondition)
        {
            throw new Exception("Mobile verification failed: "+Message);
        }
        Passed.Add(Message);
    }
    private static void Touch(Touchscreen Device,int Identifier,UnityEngine.InputSystem.TouchPhase Phase,Vector2 Position)
    {
        Canvas.ForceUpdateCanvases();
        foreach(UnityEngine.UI.RectMask2D Mask in UnityEngine.Object.FindObjectsByType<UnityEngine.UI.RectMask2D>())
        {
            Mask.PerformClipping();
        }
        InputSystem.QueueStateEvent(Device,new TouchState
        {
            touchId=Identifier,phase=Phase,position=Position,pressure=1
        }
        );
        InputSystem.Update();
        EventSystem.current.currentInputModule.Process();
    }
    private static void Advance(ActiveCombatGame Game,float Seconds)
    {
        for(float Elapsed=0;Elapsed<Seconds;Elapsed+=0.01f)
        {
            Game.Simulate(0.01f);
        }
    }
    private static Vector2 ScreenPoint(RectTransform Rectangle,Vector2 Local)
    {
        return RectTransformUtility.WorldToScreenPoint(null,Rectangle.TransformPoint(Local));
    }
    public static object Run()
    {
        ActiveCombatGame Game=UnityEngine.Object.FindAnyObjectByType<ActiveCombatGame>();
        MobileCombatInterface Mobile=Game.Hud.Mobile;
        Game.Session.StartGame();
        string Original=JsonUtility.ToJson(Game.Rules);
        Game.enabled=false;
        Touchscreen Device=InputSystem.AddDevice<Touchscreen>();
        Passed=new List<string>();
        try
        {
            Game.bPaused=false;
            Game.Hud.CloseSettings();
            Game.ResetMatch();
            Mobile.SetRatio(0);
            Mobile.SetCurtain(false);
            Mobile.ApplyLayout(0.4f);
            Canvas.ForceUpdateCanvases();
            Mobile.ApplyLayout(0);
            Require(Mathf.Abs(Mobile.TopFraction-0.25f)<0.001f,"Collapsed map uses one quarter of the phone split");
            Vector3[] Corners=new Vector3[4];
            Mobile.BattlePane.GetWorldCorners(Corners);
            Require(Mathf.Abs(Game.WorldCamera.rect.yMax-Corners[2].y/Screen.height)<0.001f,"Battle camera renders only inside its native viewport");
            Mobile.SetCurtain(true);
            Mobile.ApplyLayout(0.4f);
            Require(Mathf.Abs(Mobile.TopFraction-0.75f)<0.001f && Mobile.WheelCount==4,"Expanded map uses three quarters and offers four buildings");
            Mobile.SetRatio(1);
            Mobile.ApplyLayout(0.4f);
            Require(Mathf.Abs(Mobile.TopFraction-0.6667f)<0.001f,"Alternative expanded ratio uses two thirds");
            Mobile.SetCurtain(false);
            Mobile.ApplyLayout(0.4f);
            Require(Mathf.Abs(Mobile.TopFraction-0.3333f)<0.001f,"Alternative collapsed ratio uses one third");
            Mobile.SetRatio(2);
            Mobile.DragCurtain(ScreenPoint(Mobile.Split,new Vector2(0,Mobile.Split.rect.yMax+100)));
            Require(Mathf.Abs(Mobile.TopFraction-0.15f)<0.001f,"Free curtain clamps the minimum map size");
            Mobile.DragCurtain(ScreenPoint(Mobile.Split,new Vector2(0,Mobile.Split.rect.yMin-100)));
            Require(Mathf.Abs(Mobile.TopFraction-0.82f)<0.001f,"Free curtain clamps the maximum map size");
            Mobile.FinishCurtain();
            Mobile.SafeAreaOverride=new Rect(0,34,Screen.width,Screen.height-80);
            Mobile.ApplyLayout(0);
            Canvas.ForceUpdateCanvases();
            Mobile.ApplyLayout(0);
            Canvas.ForceUpdateCanvases();
            Mobile.SafeFrame.GetWorldCorners(Corners);
            Require(Mathf.Abs(Corners[0].y-34)<1 && Mathf.Abs(Corners[2].y-(Screen.height-46))<1,"Safe area keeps UI away from notch and home indicator");
            Mobile.SafeAreaOverride=default(Rect);
            Mobile.SetRatio(0);
            Mobile.SetCurtain(false);
            Mobile.ApplyLayout(0.4f);
            Canvas.ForceUpdateCanvases();
            Mobile.ApplyLayout(0);
            Mobile.Settings.SwipeZone=MobileSwipeZone.Handle;
            Vector2 MapCenter=ScreenPoint(Game.Hud.MapRect,Vector2.zero);
            Require(!Mobile.TryMapSwipe(MapCenter,MapCenter+Vector2.down*100,0.1f),"Default map swipe is restricted to the curtain handle");
            Mobile.Settings.SwipeZone=MobileSwipeZone.Map;
            Require(Mobile.TryMapSwipe(MapCenter,MapCenter+Vector2.down*100,0.1f) && Game.bMapExpanded,"Optional map swipe opens the tactical map");
            Mobile.Settings.SwipeZone=MobileSwipeZone.Handle;
            Mobile.Settings.TimeMode=MobileMapTime.SlowMotion;
            Mobile.Settings.SlowMotionFactor=0.2f;
            Mobile.Settings.SlowMotionDuration=4;
            Mobile.Settings.bInfiniteSlowMotion=false;
            Game.SlowMotionRemaining=4;
            Require(Mathf.Abs(Mobile.TimeScale(1)-0.2f)<0.001f && Mathf.Abs(Game.SlowMotionRemaining-3)<0.001f,"Map slowdown uses real time and the reference 20 percent speed");
            Require(Mobile.TimeScale(4)==1,"Finite slowdown returns to realtime");
            Mobile.Settings.bInfiniteSlowMotion=true;
            Require(Mathf.Abs(Mobile.TimeScale(100)-0.2f)<0.001f,"Infinite slowdown remains active after the timer expires");
            Mobile.Settings.TimeMode=MobileMapTime.Pause;
            float Stamina=Game.Stamina;
            Game.StartDash(Vector3.forward);
            Require(Mobile.TimeScale(1)==0 && Game.bSimulationPaused && Game.Stamina==Stamina && !Game.bAttacking,"Map pause blocks combat input and stamina spending");
            Mobile.ApplyLayout(0.4f);
            Canvas.ForceUpdateCanvases();
            Mobile.ApplyLayout(0);
            Game.Gold=1000;
            Game.bTrebuchet=false;
            Game.StructureIndex=0;
            Rect Bounds=Game.Hud.MapRect.rect;
            Vector2 CellCenter=new Vector2(Bounds.xMin+(Bounds.width-76)/2,Bounds.center.y);
            Vector2 Placement=ScreenPoint(Game.Hud.MapRect,CellCenter);
            Touch(Device,1,UnityEngine.InputSystem.TouchPhase.Began,Placement);
            Touch(Device,1,UnityEngine.InputSystem.TouchPhase.Ended,Placement);
            Require(Game.Structures.Count==1 && Game.Gold==975,"Real touchscreen tap builds a tower while the map is paused");
            Touch(Device,2,UnityEngine.InputSystem.TouchPhase.Began,Placement);
            Touch(Device,2,UnityEngine.InputSystem.TouchPhase.Ended,Placement);
            Require(Game.Structures.Count==0 && Game.Gold==987,"Second touchscreen tap removes the building and refunds half its price");
            float MapCellSize=Mathf.Clamp(Mathf.Min(Bounds.width-76,Bounds.height)*0.86f/Game.Rules.GridSize,4,34);
            Vector2 RightPlacement=Placement+Vector2.right*MapCellSize*2;
            Touch(Device,14,UnityEngine.InputSystem.TouchPhase.Began,RightPlacement);
            Touch(Device,14,UnityEngine.InputSystem.TouchPhase.Ended,RightPlacement);
            Require(Game.Structures.Count==1 && Game.Structures[0].Cell.x==-2,"Right-side map placement matches the battle camera orientation");
            Touch(Device,15,UnityEngine.InputSystem.TouchPhase.Began,RightPlacement);
            Touch(Device,15,UnityEngine.InputSystem.TouchPhase.Ended,RightPlacement);
            Require(Game.Structures.Count==0,"A second tap removes an off-center building from its displayed cell");
            Game.bTrebuchet=true;
            Require(Mobile.WheelCount==1,"Trebuchet mode has its own single-item wheel");
            Game.bTrebuchet=false;
            Mobile.Settings.TimeMode=MobileMapTime.Realtime;
            Mobile.SetCurtain(false);
            Mobile.ApplyLayout(0.4f);
            Canvas.ForceUpdateCanvases();
            Mobile.ApplyLayout(0);
            Require(Mobile.WheelCount==3,"Collapsed battle wheel contains exactly three weapons");
            Vector2 DockPoint=ScreenPoint(Mobile.Dock,new Vector2(0,35));
            Touch(Device,3,UnityEngine.InputSystem.TouchPhase.Began,DockPoint);
            Require(Mobile.bWheelActive && !Game.bGestureActive,"Dock touch starts wheel selection without combat input");
            Vector2 Selection=ScreenPoint(Mobile.Split,Mobile.WheelCenter+new Vector2(-65,40));
            Touch(Device,3,UnityEngine.InputSystem.TouchPhase.Moved,Selection);
            Require(Mobile.WheelSelection==2,"Touch drag selects the axe sector");
            Touch(Device,3,UnityEngine.InputSystem.TouchPhase.Ended,Selection);
            Require(Game.WeaponIndex==2 && !Mobile.bWheelActive,"Releasing the touch equips the selected weapon");
            foreach(ActiveCombatGame.EnemyState Enemy in Game.Enemies.ToArray())
            {
                UnityEngine.Object.Destroy(Enemy.Model);
            }
            Game.Enemies.Clear();
            Game.Rules.InitialEnemies=0;
            Game.Rules.MaximumSimultaneousAttacks=0;
            foreach(EnemyBalance Enemy in Game.Rules.Enemies)
            {
                Enemy.Speed=0;
                Enemy.SpeedVariation=0;
            }
            Vector2 CombatPoint=ScreenPoint(Mobile.BattlePane,new Vector2(-90,-20));
            Vector2[] Directions={Vector2.right,Vector2.left,Vector2.up,Vector2.down};
            for(int DirectionIndex=0;DirectionIndex<Directions.Length;DirectionIndex++)
            {
                Game.ResetMatch();
                Mobile.SnapCamera();
                Vector2 Direction=Directions[DirectionIndex];
                Vector3 ScreenStart=Game.WorldCamera.WorldToScreenPoint(Game.PlayerPosition);
                Touch(Device,20+DirectionIndex,UnityEngine.InputSystem.TouchPhase.Began,CombatPoint);
                Touch(Device,20+DirectionIndex,UnityEngine.InputSystem.TouchPhase.Moved,CombatPoint+Direction*36);
                Advance(Game,0.1f);
                Touch(Device,20+DirectionIndex,UnityEngine.InputSystem.TouchPhase.Ended,CombatPoint+Direction*36);
                Vector2 ScreenMovement=Game.WorldCamera.WorldToScreenPoint(Game.PlayerPosition)-ScreenStart;
                Require(Vector2.Dot(ScreenMovement.normalized,Direction)>0.95f,"Blue-zone movement follows screen direction "+DirectionIndex);
                Game.ResetMatch();
                Mobile.SnapCamera();
                ScreenStart=Game.WorldCamera.WorldToScreenPoint(Game.PlayerPosition);
                Touch(Device,30+DirectionIndex,UnityEngine.InputSystem.TouchPhase.Began,CombatPoint);
                Touch(Device,30+DirectionIndex,UnityEngine.InputSystem.TouchPhase.Moved,CombatPoint+Direction*60);
                Touch(Device,30+DirectionIndex,UnityEngine.InputSystem.TouchPhase.Ended,CombatPoint+Direction*60);
                Advance(Game,Game.Rules.DashDuration);
                ScreenMovement=Game.WorldCamera.WorldToScreenPoint(Game.PlayerPosition)-ScreenStart;
                Require(Vector2.Dot(ScreenMovement.normalized,Direction)>0.95f,"Dash follows screen direction "+DirectionIndex);
            }
            Game.ResetMatch();
            ActiveCombatGame.EnemyState Victim=Game.SpawnEnemy(0,Game.PlayerPosition+Vector3.forward);
            Victim.Health=1000;
            float Before=Victim.Health;
            Touch(Device,4,UnityEngine.InputSystem.TouchPhase.Began,CombatPoint);
            Touch(Device,5,UnityEngine.InputSystem.TouchPhase.Began,CombatPoint+Vector2.right*90);
            Touch(Device,5,UnityEngine.InputSystem.TouchPhase.Ended,CombatPoint+Vector2.right*90);
            Require(Game.bGestureActive,"A second finger cannot release the active combat gesture");
            Touch(Device,4,UnityEngine.InputSystem.TouchPhase.Ended,CombatPoint);
            Touch(Device,6,UnityEngine.InputSystem.TouchPhase.Began,CombatPoint);
            Touch(Device,6,UnityEngine.InputSystem.TouchPhase.Ended,CombatPoint);
            Require(Mathf.Abs(Before-Victim.Health-60)<0.01f,"Two immediate axe taps each deal 30 damage without cooldown");
            Game.SpecialSacrifices=Game.SpecialSacrificeCost;
            Touch(Device,7,UnityEngine.InputSystem.TouchPhase.Began,CombatPoint);
            Advance(Game,0.72f);
            Require(Game.Charge>=Game.Rules.ChargeSeconds,"Holding the center charges the special with touchscreen input");
            Touch(Device,7,UnityEngine.InputSystem.TouchPhase.Ended,CombatPoint);
            Require(Game.Stamina==150 && Game.DamageBonusRemaining>0,"Charged center release activates axe defense instead of a manual tap");
            Advance(Game,0.5f);
            Game.Stamina=100;
            Touch(Device,8,UnityEngine.InputSystem.TouchPhase.Began,CombatPoint);
            Touch(Device,8,UnityEngine.InputSystem.TouchPhase.Moved,CombatPoint+Vector2.right*60);
            Require(Game.bAttacking && Game.Stamina==67,"Crossing the red zone triggers a dash for exactly 33 stamina");
            Touch(Device,8,UnityEngine.InputSystem.TouchPhase.Ended,CombatPoint+Vector2.right*60);
            Advance(Game,0.7f);
            Game.SpecialSacrifices=Game.SpecialSacrificeCost;
            Touch(Device,9,UnityEngine.InputSystem.TouchPhase.Began,CombatPoint);
            Advance(Game,0.72f);
            float SpecialStamina=Game.Stamina;
            Touch(Device,9,UnityEngine.InputSystem.TouchPhase.Moved,CombatPoint+Vector2.up*80);
            Touch(Device,9,UnityEngine.InputSystem.TouchPhase.Ended,CombatPoint+Vector2.up*80);
            Vector3 Start=Game.PlayerPosition;
            Advance(Game,0.8f);
            Require(Game.PlayerPosition.z<Start.z-4 && Mathf.Approximately(Game.Stamina,SpecialStamina),"Charged outward release moves the special in screen-up direction");
            Game.Hud.OpenSettings();
            Require(Game.bSettingsOpen && !Game.bGestureActive && !Mobile.bWheelActive,"Settings cancel active gestures and pause gameplay");
            foreach(TMPro.TMP_InputField Input in Game.Hud.SettingsContent.GetComponentsInChildren<TMPro.TMP_InputField>())
            {
                TMPro.TMP_Text Label=Input.transform.parent.Find("Label").GetComponent<TMPro.TMP_Text>();
                if(Label.text=="Скорость времени при открытой карте")
                {
                    Input.text="0.35";
                }
            }
            Game.Hud.ApplySettings();
            Require(!Game.bSettingsOpen && Mathf.Abs(Game.Rules.MobileView.SlowMotionFactor-0.35f)<0.001f,"Mobile numeric settings apply to the runtime balance");
            Require(Mathf.Abs(Game.Balance.MobileView.SlowMotionFactor-0.2f)<0.001f,"Applying settings does not change the source asset");
            return new
            {
                Success=true,Count=Passed.Count,Checks=Passed.ToArray(),ScreenWidth=Screen.width,ScreenHeight=Screen.height
            }
            ;
        }
        finally
        {
            InputSystem.RemoveDevice(Device);
            JsonUtility.FromJsonOverwrite(Original,Game.Rules);
            Game.Hud.CloseSettings();
            Mobile.SafeAreaOverride=default(Rect);
            Mobile.CancelWheel();
            Mobile.bCurtainDragging=false;
            Mobile.SetRatio(0);
            Mobile.SetCurtain(false);
            Mobile.ApplyLayout(0.4f);
            Game.ResetMatch();
            Game.EquipWeapon(0);
            Game.enabled=true;
        }
    }
}
