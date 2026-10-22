using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
public static class CombatFlowVerification
{
    public static object Result;
    public static string Failure;
    private static List<string> Passed;
    private static int TouchIdentifier;
    private static void Require(bool bCondition, string Message)
    {
        if (!bCondition)
        {
            Failure = Message;
            throw new Exception("Flow verification failed: " + Message);
        }
        Passed.Add(Message);
    }
    private static Vector2 ScreenPoint(RectTransform Rectangle, Vector2 Position)
    {
        return RectTransformUtility.WorldToScreenPoint(null, Rectangle.TransformPoint(Position));
    }
    private static void Layout(ActiveCombatGame Game)
    {
        Game.Hud.Mobile.ApplyLayout(0.4f);
        Canvas.ForceUpdateCanvases();
        Game.Hud.Refresh(Game, 0);
        Game.Session.RefreshUI();
        Canvas.ForceUpdateCanvases();
        foreach (UnityEngine.UI.RectMask2D Mask in UnityEngine.Object.FindObjectsByType<UnityEngine.UI.RectMask2D>())
        {
            Mask.PerformClipping();
        }
    }
    private static void Touch(Touchscreen Device, UnityEngine.InputSystem.TouchPhase Phase, Vector2 Position)
    {
        InputSystem.QueueStateEvent(Device, new TouchState { touchId = TouchIdentifier, phase = Phase, position = Position, pressure = 1 });
        InputSystem.Update();
        EventSystem.current.currentInputModule.Process();
    }
    private static void Tap(Touchscreen Device, RectTransform Target)
    {
        Vector2 Position = ScreenPoint(Target, Target.rect.center);
        TouchIdentifier++;
        Touch(Device, UnityEngine.InputSystem.TouchPhase.Began, Position);
        Touch(Device, UnityEngine.InputSystem.TouchPhase.Ended, Position);
    }
    private static void Advance(ActiveCombatGame Game, float Seconds)
    {
        for (float Elapsed = 0; Elapsed < Seconds; Elapsed += 0.01f)
        {
            Game.Simulate(0.01f);
            Game.Session.Tutorial.Tick();
        }
        Layout(Game);
    }
    private static void Next(ActiveCombatGame Game, Touchscreen Device)
    {
        Layout(Game);
        Tap(Device, (RectTransform)Game.Session.NextButton.transform);
        Layout(Game);
    }
    private static void Gesture(ActiveCombatGame Game, Touchscreen Device, Vector2 Offset, float Hold)
    {
        Vector2 Position = ScreenPoint(Game.Hud.Mobile.BattlePane, new Vector2(0, 60));
        TouchIdentifier++;
        Touch(Device, UnityEngine.InputSystem.TouchPhase.Began, Position);
        Advance(Game, Hold);
        Touch(Device, UnityEngine.InputSystem.TouchPhase.Moved, Position + Offset);
        Touch(Device, UnityEngine.InputSystem.TouchPhase.Ended, Position + Offset);
        Advance(Game, 1.4f);
    }
    public static object Run()
    {
        Result = null;
        Failure = null;
        UnityEngine.Object.FindAnyObjectByType<ActiveCombatGame>().StartCoroutine(RunSequence());
        return new { Started = true };
    }
    private static System.Collections.IEnumerator RunSequence()
    {
        ActiveCombatGame Game = UnityEngine.Object.FindAnyObjectByType<ActiveCombatGame>();
        CombatSession Session = Game.Session;
        CombatTutorial Tutorial = Session.Tutorial;
        Game.enabled = false;
        Touchscreen Device = InputSystem.AddDevice<Touchscreen>();
        Passed = new List<string>();
        string Original = JsonUtility.ToJson(Game.Rules);
        try
        {
            Session.ShowMainMenu();
            Layout(Game);
            yield return null;
            Require(Session.MainPanel.activeInHierarchy && Session.bBlocksGameplay, "Startup shows a blocking main menu");
            float TimeBefore = Game.SimulatedSeconds;
            Game.BeginGesture(Vector2.one * 200);
            Game.StartDash(Vector3.forward);
            Advance(Game, 1);
            Require(!Game.bGestureActive && !Game.bAttacking && Game.SimulatedSeconds == TimeBefore && Game.Enemies.Count == 0, "Main menu blocks input, simulation and enemy population");
            Tap(Device, (RectTransform)Session.MainContent.Find("Settings"));
            Layout(Game);
            yield return null;
            Require(Game.bSettingsOpen && !Session.MainPanel.activeSelf, "Touchscreen opens settings from the menu");
            Game.Hud.CloseSettings();
            yield return null;
            Require(Session.MainPanel.activeSelf && !Game.bPaused, "Closing menu settings returns to the menu");
            Tap(Device, (RectTransform)Session.MainContent.Find("NormalGame"));
            Layout(Game);
            yield return null;
            Require(!Session.bMainMenuOpen && Game.Enemies.Count == Game.EnemyLimit && Game.bSimulationPaused == false, "Touchscreen starts a populated normal match");
            Game.SpecialSacrifices = 12;
            Layout(Game);
            yield return null;
            Require(Session.ReadyEffect.enabled && !Session.ReadyEffect.raycastTarget, "Ready special enables a nonblocking edge effect");
            Game.StartSpecial(false, Vector3.forward);
            Layout(Game);
            yield return null;
            Require(!Session.ReadyEffect.enabled, "Spending the last ready special removes the edge effect");
            Advance(Game, 1);
            Game.SpecialSacrifices = 100;
            Game.Rules.bSpecialReadyEdgeEffect = false;
            Layout(Game);
            yield return null;
            Require(!Session.ReadyEffect.enabled, "The readiness effect can be disabled in balance settings");
            Game.Rules.bSpecialReadyEdgeEffect = true;
            Session.TogglePause();
            Layout(Game);
            yield return null;
            Require(Game.Hud.PausePanel.activeSelf && !Session.ReadyEffect.enabled, "Pause shows a blocking menu and hides the readiness effect");
            TimeBefore = Game.SimulatedSeconds;
            Vector3 PositionBefore = Game.PlayerPosition;
            TouchIdentifier++;
            Touch(Device, UnityEngine.InputSystem.TouchPhase.Began, new Vector2(4, Screen.height * 0.5f));
            Touch(Device, UnityEngine.InputSystem.TouchPhase.Ended, new Vector2(4, Screen.height * 0.5f));
            Require(!Game.bGestureActive && Game.PlayerPosition == PositionBefore, "Pause overlay intercepts combat touches");
            Session.OpenSettings();
            Require(Game.bSettingsOpen && !Game.Hud.PausePanel.activeSelf, "Pause settings replace the pause overlay");
            Game.Hud.CloseSettings();
            yield return null;
            Require(Game.bPaused && Game.Hud.PausePanel.activeSelf, "Closing pause settings restores pause");
            Session.TogglePause();
            Game.Rules.WalkSpeed = 5.73f;
            string BeforeTutorial = JsonUtility.ToJson(Game.Rules);
            Session.ShowMainMenu();
            Layout(Game);
            yield return null;
            Tap(Device, (RectTransform)Session.MainContent.Find("Training"));
            Layout(Game);
            yield return null;
            Require(Tutorial.bActive && Tutorial.Stage == 0 && Game.Enemies.Count == 0 && Game.Rules.MaximumSimultaneousAttacks == 0, "Training starts an isolated safe arena through a real touch");
            Next(Game, Device);
            yield return null;
            Require(Tutorial.Stage == 0, "Next cannot skip an unfinished lesson");
            Vector2 Origin = ScreenPoint(Game.Hud.Mobile.BattlePane, new Vector2(0, 60));
            TouchIdentifier++;
            Touch(Device, UnityEngine.InputSystem.TouchPhase.Began, Origin);
            Touch(Device, UnityEngine.InputSystem.TouchPhase.Moved, Origin + Vector2.right * 40);
            Advance(Game, 1.2f);
            Touch(Device, UnityEngine.InputSystem.TouchPhase.Ended, Origin + Vector2.right * 40);
            Require(Tutorial.bCanAdvance && Game.PlayerPosition.x < -1, "Blue-zone movement completes the movement lesson in the correct direction");
            TimeBefore = Game.SimulatedSeconds;
            Advance(Game, 1);
            Require(Game.SimulatedSeconds == TimeBefore, "Completed lessons pause until the player presses Next");
            Next(Game, Device);
            yield return null;
            Gesture(Game, Device, Vector2.up * 60, 0.05f);
            Require(Tutorial.Stage == 1 && Tutorial.bCanAdvance, "A real red-zone dash completes the dash lesson");
            Next(Game, Device);
            yield return null;
            Gesture(Game, Device, Vector2.zero, 0.05f);
            Require(Tutorial.Stage == 2 && Tutorial.bCanAdvance && Game.Enemies[0].Health < Game.CurrentWeapon.AutoDamage * 2, "A manual axe tap must hit an enemy to complete the strike lesson");
            Next(Game, Device);
            yield return null;
            Gesture(Game, Device, Vector2.zero, 0.05f);
            Require(Tutorial.Stage == 3 && Tutorial.bCanAdvance && Game.SpecialSacrifices >= 12, "Killing training targets earns the twelve required sacrifices");
            Require(Session.ReadyEffect.enabled, "The training readiness cue includes the screen edges");
            Next(Game, Device);
            yield return null;
            Vector3 BeforeSpecial = Game.PlayerPosition;
            Gesture(Game, Device, Vector2.right * 60, 0.75f);
            Require(Tutorial.Stage == 4 && Tutorial.bCanAdvance && Game.SpecialUses == 1 && Vector3.Distance(BeforeSpecial, Game.PlayerPosition) > 3, "Charged directional release performs the offensive moving special");
            Next(Game, Device);
            yield return null;
            Gesture(Game, Device, Vector2.zero, 0.05f);
            Gesture(Game, Device, Vector2.zero, 0.75f);
            Require(Tutorial.Stage == 5 && Tutorial.bCanAdvance && Game.SpecialUses == 2 && Game.Health > 45 && Game.Stamina >= 150, "Charged center release performs the defensive healing special");
            Next(Game, Device);
            yield return null;
            Vector2 Dock = ScreenPoint(Game.Hud.Mobile.Dock, new Vector2(0, 35));
            TouchIdentifier++;
            Touch(Device, UnityEngine.InputSystem.TouchPhase.Began, Dock);
            Vector2 Selection = ScreenPoint(Game.Hud.Mobile.Split, Game.Hud.Mobile.WheelCenter + new Vector2(65, 40));
            Touch(Device, UnityEngine.InputSystem.TouchPhase.Moved, Selection);
            Touch(Device, UnityEngine.InputSystem.TouchPhase.Ended, Selection);
            Advance(Game, 0.1f);
            Require(Tutorial.Stage == 6 && Tutorial.bCanAdvance && Game.WeaponIndex != 2, "Touch wheel selection completes the weapon lesson");
            Next(Game, Device);
            yield return null;
            Vector2 Curtain = ScreenPoint(Game.Hud.Mobile.Handle, Vector2.zero);
            TouchIdentifier++;
            Touch(Device, UnityEngine.InputSystem.TouchPhase.Began, Curtain);
            Touch(Device, UnityEngine.InputSystem.TouchPhase.Moved, Curtain + Vector2.down * 430);
            Touch(Device, UnityEngine.InputSystem.TouchPhase.Ended, Curtain + Vector2.down * 430);
            Advance(Game, 0.1f);
            Require(Tutorial.Stage == 7 && Tutorial.bCanAdvance && Game.bMapExpanded, "Dragging the curtain completes the map lesson");
            Next(Game, Device);
            yield return null;
            Rect Bounds = Game.Hud.MapRect.rect;
            Vector2 MapPoint = ScreenPoint(Game.Hud.MapRect, new Vector2(Bounds.xMin + (Bounds.width - 76) / 2, Bounds.center.y + 60));
            TouchIdentifier++;
            Touch(Device, UnityEngine.InputSystem.TouchPhase.Began, MapPoint);
            Touch(Device, UnityEngine.InputSystem.TouchPhase.Ended, MapPoint);
            Advance(Game, 0.1f);
            Require(Tutorial.Stage == 8 && Tutorial.bCanAdvance && Game.Structures.Count == 1, "A map touch places a real tower and completes the construction lesson");
            Next(Game, Device);
            yield return null;
            Tap(Device, (RectTransform)Game.Hud.TrebuchetModeButton.transform);
            TouchIdentifier++;
            Touch(Device, UnityEngine.InputSystem.TouchPhase.Began, MapPoint + Vector2.right * 25);
            Touch(Device, UnityEngine.InputSystem.TouchPhase.Ended, MapPoint + Vector2.right * 25);
            Advance(Game, 0.1f);
            Require(Game.bTrebuchet && Game.Bombs.Count == 1 && !Tutorial.bCanAdvance, "Trebuchet training waits for the delayed explosion");
            Advance(Game, 1.6f);
            Require(Tutorial.Stage == 9 && Tutorial.bCanAdvance && Game.Bombs.Count == 0, "The explosion completes the final lesson");
            Next(Game, Device);
            yield return null;
            Require(Tutorial.bCompleted && Session.CompletionPanel.activeSelf && Game.bSimulationPaused, "Tutorial completion freezes the arena and offers a normal match");
            Session.StartGame();
            Require(!Tutorial.bActive && JsonUtility.ToJson(Game.Rules) == BeforeTutorial && Game.SpecialUses == 0 && Game.Kills == 0 && Game.Enemies.Count == Game.EnemyLimit, "Starting a match restores all player settings and resets training progress");
            Session.StartTutorial();
            Session.ShowMainMenu();
            Require(!Tutorial.bActive && Session.bMainMenuOpen && JsonUtility.ToJson(Game.Rules) == BeforeTutorial && Game.Enemies.Count == 0, "Leaving training early restores settings and returns to a clean menu");
            Result = new { Success = true, Count = Passed.Count, Checks = Passed.ToArray(), Width = Screen.width, Height = Screen.height };
        }
        finally
        {
            InputSystem.RemoveDevice(Device);
            Tutorial.StopTutorial();
            JsonUtility.FromJsonOverwrite(Original, Game.Rules);
            Game.Hud.CloseSettings();
            Session.ShowMainMenu();
            Game.enabled = true;
        }
    }
}
