using System;
using System.Collections;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

public static class MergedTouchVerification
{
    public static string Result;
    public static string Failure;
    private static int Checks;

    private static void Require(bool bCondition, string Message)
    {
        if (!bCondition)
        {
            Failure = Message;
            throw new InvalidOperationException(Message);
        }
        Checks++;
    }

    private static Vector2 Center(RectTransform Area)
    {
        return RectTransformUtility.WorldToScreenPoint(null, Area.TransformPoint(Area.rect.center));
    }

    private static void Touch(Touchscreen Device, int Identifier, UnityEngine.InputSystem.TouchPhase Phase, Vector2 Position)
    {
        InputSystem.QueueStateEvent(Device, new TouchState { touchId = Identifier, phase = Phase, position = Position, pressure = 1 });
        InputSystem.Update();
        EventSystem.current.currentInputModule.Process();
        UnityEngine.Object.FindAnyObjectByType<ActiveCombatGame>().FlushCommands();
    }

    public static string Run()
    {
        if (!Application.isPlaying)
        {
            throw new InvalidOperationException("Play Mode is required.");
        }
        Result = null;
        Failure = null;
        Checks = 0;
        UnityEngine.Object.FindFirstObjectByType<ActiveCombatGame>().StartCoroutine(Sequence());
        return "Touch verification started.";
    }

    private static IEnumerator Sequence()
    {
        ActiveCombatGame Game = UnityEngine.Object.FindFirstObjectByType<ActiveCombatGame>();
        while (Game.Rules == null)
        {
            yield return null;
        }
        InputSettings OriginalInput = InputSystem.settings;
        HideFlags OriginalInputFlags = OriginalInput.hideFlags;
        OriginalInput.hideFlags = HideFlags.DontSave;
        InputSettings TestInput = UnityEngine.Object.Instantiate(OriginalInput);
        TestInput.hideFlags = HideFlags.DontSave;
        TestInput.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
        TestInput.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
        InputSystem.settings = TestInput;
        Touchscreen Device = InputSystem.AddDevice<Touchscreen>();
        Game.enabled = false;
        try
        {
            Game.Session.StartGame();
            Game.Hud.Mobile.SetCurtain(false);
            Game.Hud.Mobile.ApplyLayout(0.4f);
            Canvas.ForceUpdateCanvases();
            yield return null;
            Vector2 Button = Center((RectTransform)Game.Hud.Mobile.BattlePane.Find("ExpandMap"));
            Touch(Device, 1, UnityEngine.InputSystem.TouchPhase.Began, Button);
            yield return null;
            Touch(Device, 1, UnityEngine.InputSystem.TouchPhase.Ended, Button);
            yield return null;
            Require(Game.bMapExpanded && !Game.bGestureActive, "Map button touch must expand the map without starting combat.");
            Game.Hud.Mobile.ApplyLayout(0.4f);
            Canvas.ForceUpdateCanvases();
            yield return null;
            Button = Center((RectTransform)Game.Hud.MapPanel.Find("MapFullscreen"));
            Touch(Device, 2, UnityEngine.InputSystem.TouchPhase.Began, Button);
            yield return null;
            Touch(Device, 2, UnityEngine.InputSystem.TouchPhase.Ended, Button);
            yield return null;
            Require(Game.Hud.Mobile.bMapFullscreen, "Fullscreen map button must respond through Input System.");
            Game.Hud.Mobile.ApplyLayout(0.4f);
            Canvas.ForceUpdateCanvases();
            yield return null;
            Require(!Game.Hud.Mobile.BattlePane.gameObject.activeSelf, "Fullscreen map must hide the combat viewport.");
            TacticalMapPresenter Map = Game.Root.MapPresenter;
            Map.bOverview = false;
            Map.bPanMode = true;
            Map.Zoom = 2;
            Map.Center = Vector2.zero;
            Map.Refresh();
            Vector2 MapPosition = Center(Game.Hud.MapRect);
            int BuildingCount = Game.Structures.Count;
            Touch(Device, 10, UnityEngine.InputSystem.TouchPhase.Began, MapPosition);
            yield return null;
            Touch(Device, 10, UnityEngine.InputSystem.TouchPhase.Moved, MapPosition + Vector2.right * 60 * Game.Hud.Mobile.InputScale);
            yield return null;
            Touch(Device, 10, UnityEngine.InputSystem.TouchPhase.Ended, MapPosition + Vector2.right * 60 * Game.Hud.Mobile.InputScale);
            yield return null;
            Require(Map.Center.x > 0 && Game.Structures.Count == BuildingCount && !Game.bGestureActive, "Map pan must move the camera without building or acquiring the combat stick.");
            Map.Surface.Image.GetComponent<TacticalMapInput>().OnCancel(null);
            Map.Center = Vector2.one * 1000;
            Map.Refresh();
            Require(Map.Center.magnitude < 30, "Map camera must remain within the construction map bounds.");
            Map.Center = Vector2.zero;
            Map.Refresh();
            float BeforeZoom = Map.Zoom;
            Touch(Device, 11, UnityEngine.InputSystem.TouchPhase.Began, MapPosition - Vector2.right * 40);
            yield return null;
            Touch(Device, 12, UnityEngine.InputSystem.TouchPhase.Began, MapPosition + Vector2.right * 40);
            yield return null;
            Touch(Device, 12, UnityEngine.InputSystem.TouchPhase.Moved, MapPosition + Vector2.right * 120);
            yield return null;
            Touch(Device, 12, UnityEngine.InputSystem.TouchPhase.Ended, MapPosition + Vector2.right * 120);
            Touch(Device, 11, UnityEngine.InputSystem.TouchPhase.Ended, MapPosition - Vector2.right * 40);
            yield return null;
            Require(Map.Zoom > BeforeZoom && Game.Structures.Count == BuildingCount, "Pinch zoom must not commit a construction tap.");
            Map.ShowOverview();
            yield return new WaitForSecondsRealtime(0.65f);
            Require(Map.bOverview && !Map.bTransitioning && Map.Center == Vector2.zero, "Overview must finish its camera transition at the map center.");
            Touch(Device, 13, UnityEngine.InputSystem.TouchPhase.Began, MapPosition);
            yield return null;
            Touch(Device, 13, UnityEngine.InputSystem.TouchPhase.Moved, MapPosition + Vector2.right * 70);
            yield return null;
            Touch(Device, 13, UnityEngine.InputSystem.TouchPhase.Ended, MapPosition + Vector2.right * 70);
            yield return null;
            Require(Map.bOverview && Map.Center == Vector2.zero, "Dragging an overview must keep the entire construction map visible.");
            BeforeZoom = Map.Zoom;
            Touch(Device, 14, UnityEngine.InputSystem.TouchPhase.Began, MapPosition);
            Touch(Device, 14, UnityEngine.InputSystem.TouchPhase.Ended, MapPosition);
            yield return null;
            Touch(Device, 15, UnityEngine.InputSystem.TouchPhase.Began, MapPosition);
            yield return null;
            Touch(Device, 15, UnityEngine.InputSystem.TouchPhase.Moved, MapPosition - Vector2.up * 80);
            yield return null;
            Touch(Device, 15, UnityEngine.InputSystem.TouchPhase.Ended, MapPosition - Vector2.up * 80);
            yield return null;
            Require(Map.Zoom > BeforeZoom && !Map.bOverview && Game.Structures.Count == BuildingCount, "Double-tap drag must zoom the map without placing a building.");
            Map.bPanMode = false;
            Game.Hud.Mobile.SetCurtain(false);
            Game.Hud.Mobile.ApplyLayout(0.4f);
            Canvas.ForceUpdateCanvases();
            yield return null;
            Vector2 Position = Center(Game.Hud.Mobile.BattlePane);
            Game.Combo.RecordDamage(1000);
            Touch(Device, 3, UnityEngine.InputSystem.TouchPhase.Began, Position);
            yield return null;
            Require(Game.bGestureActive, "Combat touch must acquire the stick.");
            Game.Simulate(Game.SpecialChargeSeconds);
            Touch(Device, 3, UnityEngine.InputSystem.TouchPhase.Moved, Position + Vector2.right * 70 * Game.Hud.Mobile.InputScale);
            yield return null;
            Require(Game.bAttacking && Game.bGestureActive && Game.SpecialUses == 1, "Special must start from the outer ring without releasing the pointer.");
            Touch(Device, 4, UnityEngine.InputSystem.TouchPhase.Began, Position + Vector2.up * 100);
            yield return null;
            Touch(Device, 4, UnityEngine.InputSystem.TouchPhase.Ended, Position + Vector2.up * 100);
            yield return null;
            Require(Game.bGestureActive && Game.SpecialUses == 1, "A second combat finger must not release or duplicate the first action.");
            Touch(Device, 3, UnityEngine.InputSystem.TouchPhase.Canceled, Position);
            yield return null;
            Require(!Game.bGestureActive, "Touch cancellation must release the stick.");
            Result = $"Passed {Checks} touch checks.";
        }
        finally
        {
            InputSystem.RemoveDevice(Device);
            InputSystem.settings = OriginalInput;
            OriginalInput.hideFlags = OriginalInputFlags;
            UnityEngine.Object.Destroy(TestInput);
            Game.CancelGesture();
            Game.Session.ShowMainMenu();
            Game.enabled = true;
        }
    }
}
