using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
public static class MergedFlowVerification
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
        UnityEngine.Object.FindAnyObjectByType<ActiveCombatGame>().FlushCommands();
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
    private static void Gesture(ActiveCombatGame Game, Touchscreen Device, Vector2 Offset, float Hold, float Recovery = 1.4f)
    {
        Vector2 Position = ScreenPoint(Game.Hud.Mobile.BattlePane, new Vector2(0, 60));
        TouchIdentifier++;
        Touch(Device, UnityEngine.InputSystem.TouchPhase.Began, Position);
        Advance(Game, Hold);
        Touch(Device, UnityEngine.InputSystem.TouchPhase.Moved, Position + Offset);
        Touch(Device, UnityEngine.InputSystem.TouchPhase.Ended, Position + Offset);
        Advance(Game, Recovery);
    }
    public static object Run()
    {
        if (!Application.isPlaying)
        {
            throw new InvalidOperationException("Play Mode is required.");
        }
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
        InputSettings OriginalInput = InputSystem.settings;
        HideFlags OriginalInputFlags = OriginalInput.hideFlags;
        OriginalInput.hideFlags = HideFlags.DontSave;
        InputSettings TestInput = UnityEngine.Object.Instantiate(OriginalInput);
        TestInput.hideFlags = HideFlags.DontSave;
        TestInput.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
        TestInput.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
        InputSystem.settings = TestInput;
        Touchscreen Device = InputSystem.AddDevice<Touchscreen>();
        Passed = new List<string>();
        string Original = JsonUtility.ToJson(Game.Rules);
        try
        {
            Game.Root.MapPresenter.StopTransition();
            Game.Root.MapPresenter.bOverview = false;
            Game.Root.MapPresenter.bPanMode = false;
            Game.Root.MapPresenter.Center = Vector2.zero;
            Game.Root.MapPresenter.Zoom = 1;
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
            Require(Game.bSpecialReady && !Session.ReadyEffect.enabled, "Ready special uses the combo bar without a readiness edge effect.");
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
            Require(Tutorial.bCanAdvance && Game.PlayerPosition.x < -1, "Blue-zone movement completes the movement lesson in the correct direction. Position=" + Game.PlayerPosition + " Completed=" + Tutorial.bCanAdvance);
            TimeBefore = Game.SimulatedSeconds;
            Advance(Game, 1);
            Require(Game.SimulatedSeconds == TimeBefore, "Completed lessons pause until the player presses Next");
            Next(Game, Device);
            yield return null;
            Gesture(Game, Device, Vector2.up * 70, 0.05f);
            Require(Tutorial.Stage == 1 && Tutorial.bCanAdvance, "A real red-zone dash completes the dash lesson");
            Next(Game, Device);
            yield return null;
            Gesture(Game, Device, Vector2.zero, 0.05f, 0.25f);
            Require(Tutorial.Stage == 2 && Tutorial.bCanAdvance && Game.Enemies[0].Health < Game.CurrentWeapon.AutoDamage * 2, "A manual axe tap must hit an enemy to complete the strike lesson");
            Next(Game, Device);
            yield return null;
            Gesture(Game, Device, Vector2.zero, 0.05f, 0.25f);
            for (int Attempt = 0; Attempt < 40 && !Game.bSpecialReady; Attempt++)
            {
                Gesture(Game, Device, Vector2.zero, 0.05f, 0.25f);
            }
            Require(Tutorial.Stage == 3 && Tutorial.bCanAdvance && Game.bSpecialReady, "Personal training damage fills the combo bar. Points=" + Game.Combo.Points + " Remaining=" + Game.Enemies.Count);
            Require(Game.Combo.Fraction == 1, "The training readiness cue uses the filled combo bar.");
            Next(Game, Device);
            yield return null;
            Vector3 BeforeSpecial = Game.PlayerPosition;
            Gesture(Game, Device, Vector2.right * 70, 0.75f);
            Require(Tutorial.Stage == 4 && Tutorial.bCanAdvance && Game.SpecialUses == 1 && Vector3.Distance(BeforeSpecial, Game.PlayerPosition) > 3, "Charged directional release performs the offensive moving special");
            Next(Game, Device);
            yield return null;
            Gesture(Game, Device, Vector2.zero, 0.05f, 0.25f);
            for (int Attempt = 0; Attempt < 40 && !Game.bSpecialReady; Attempt++)
            {
                Gesture(Game, Device, Vector2.zero, 0.05f, 0.25f);
            }
            Gesture(Game, Device, Vector2.zero, 0.75f);
            Require(Tutorial.Stage == 5 && Tutorial.bCanAdvance && Game.SpecialUses == 2 && Game.Health > 45 && Game.Stamina >= 150, "Charged center release performs the defensive healing special. Uses=" + Game.SpecialUses + " Stage=" + Tutorial.Stage + " Ready=" + Game.bSpecialReady + " Points=" + Game.Combo.Points + " Health=" + Game.Health + " Stamina=" + Game.Stamina);
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
            Vector2 MapPoint = AvailableMapPoint(Game);
            TouchIdentifier++;
            Touch(Device, UnityEngine.InputSystem.TouchPhase.Began, MapPoint);
            Touch(Device, UnityEngine.InputSystem.TouchPhase.Ended, MapPoint);
            Advance(Game, 0.1f);
            Require(Tutorial.Stage == 8 && Tutorial.bCanAdvance && Game.Structures.Count == 1, "A map touch places a real tower and completes the construction lesson");
            Next(Game, Device);
            yield return null;
            Tap(Device, (RectTransform)Game.Hud.MapPanel.Find("MapTrebuchet"));
            TouchIdentifier++;
            Touch(Device, UnityEngine.InputSystem.TouchPhase.Began, MapPoint);
            Touch(Device, UnityEngine.InputSystem.TouchPhase.Ended, MapPoint);
            Advance(Game, 0.1f);
            Require(Game.bTrebuchet && Game.Bombs.Count == 1 && !Tutorial.bCanAdvance, "Trebuchet training waits for the delayed explosion. Mode=" + Game.bTrebuchet + " Bombs=" + Game.Bombs.Count + " Advance=" + Tutorial.bCanAdvance);
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
            InputSystem.settings = OriginalInput;
            OriginalInput.hideFlags = OriginalInputFlags;
            UnityEngine.Object.Destroy(TestInput);
            Tutorial.StopTutorial();
            JsonUtility.FromJsonOverwrite(Original, Game.Rules);
            Game.Hud.CloseSettings();
            Session.ShowMainMenu();
            Game.enabled = true;
        }
    }
    private static Vector2 AvailableMapPoint(ActiveCombatGame Game)
    {
        Rect Bounds = Game.Hud.MapRect.rect;
        for (int Horizontal = -6; Horizontal <= 6; Horizontal++)
        {
            for (int Vertical = -6; Vertical <= 6; Vertical++)
            {
                if (Game.PlacementError(new Vector2Int(Horizontal, Vertical), 0) != null)
                {
                    continue;
                }
                Vector3 Viewport = Game.Root.MapPresenter.Surface.OutputCamera.WorldToViewportPoint(new Vector3(Horizontal, 0, Vertical) * Game.Rules.CellSize);
                if (Viewport.x < 0.1f || Viewport.x > 0.9f || Viewport.y < 0.15f || Viewport.y > 0.85f)
                {
                    continue;
                }
                Vector2 Position = ScreenPoint(Game.Hud.MapRect, Bounds.min + new Vector2(Viewport.x, Viewport.y) * Bounds.size);
                List<RaycastResult> Hits = new List<RaycastResult>();
                EventSystem.current.RaycastAll(new PointerEventData(EventSystem.current) { position = Position }, Hits);
                if (Hits.Count > 0 && Hits[0].gameObject.GetComponent<TacticalMapInput>() != null)
                {
                    return Position;
                }
            }
        }
        throw new InvalidOperationException("No unobstructed map cell is available.");
    }
}
