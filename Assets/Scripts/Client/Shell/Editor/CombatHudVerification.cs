using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using VaticanDefendor;
using VaticanDefendor.Client.Shell;
using VaticanDefendor.Combat;
using VaticanDefendor.Maps;
using VaticanDefendor.Movement;

public static class CombatHudVerification
{
    private static string _state = "Not started";
    private static readonly List<string> _checks = new List<string>();
    private static ClientShellView _view;
    private static Touchscreen _touchscreen;
    private static World _server;
    private static Entity _hero;
    private static Vector2 _origin;
    private static Vector2 _walking;

    public static string Start()
    {
        _view = UnityEngine.Object.FindFirstObjectByType<ClientShellView>();
        Require(EditorApplication.isPlaying && _view != null, "A connected game view is required.");
        _server = (World)typeof(RiverNetworkPlayVerification).GetField("_server", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
        using EntityQuery Heroes = _server.EntityManager.CreateEntityQuery(typeof(HeroActionState), typeof(HeroMoveInput));
        _hero = Heroes.GetSingletonEntity();
        _checks.Clear();
        _state = "Running";
        _view.StartCoroutine(Run());
        return _state;
    }

    public static object Status()
    {
        return new { State = _state, Checks = _checks.ToArray(), Frame = Time.frameCount };
    }

    private static IEnumerator Run()
    {
        _touchscreen = InputSystem.AddDevice<Touchscreen>("CombatHudVerificationTouchscreen");
        try
        {
            Canvas.ForceUpdateCanvases();
            ClientJoystickGraphic Joystick = _view.GetComponentInChildren<ClientJoystickGraphic>();
            ClientPointerSurface Surface = Joystick.GetComponent<ClientPointerSurface>();
            RectTransform Area = Joystick.rectTransform;
            _origin = RectTransformUtility.WorldToScreenPoint(null, Area.TransformPoint(Area.rect.min + new Vector2(190, 300)));
            float Scale = _view.GetComponent<Canvas>().scaleFactor;
            _walking = _origin + Vector2.left * 36 * Scale;
            var Hits = new List<RaycastResult>();
            EventSystem.current.RaycastAll(new PointerEventData(EventSystem.current) { position = _origin }, Hits);
            Require(Hits.Count > 0 && Hits[0].gameObject == Joystick.gameObject, "The combat graphic does not receive pointer raycasts.");
            Check("Combat raycast reaches the joystick");
            RiverMapConfiguration Map = RiverMapAccess.Get(_server.EntityManager);
            _server.EntityManager.SetComponentData(_hero, LocalTransform.FromPosition(RiverMapAccess.Position(Map, new float2(-5, -7))));
            yield return new WaitForSecondsRealtime(0.3f);
            float3 StartPosition = _server.EntityManager.GetComponentData<LocalTransform>(_hero).Position;
            Touch(1, UnityEngine.InputSystem.TouchPhase.Began, _origin);
            yield return new WaitForSecondsRealtime(0.1f);
            Require(Surface.IsGestureActive, "The Input System touch did not start a joystick gesture.");
            Touch(1, UnityEngine.InputSystem.TouchPhase.Moved, _walking);
            yield return new WaitForSecondsRealtime(0.5f);
            HeroMoveInput WalkingInput = _server.EntityManager.GetComponentData<HeroMoveInput>(_hero);
            Require(WalkingInput.Direction.x < -0.2f && WalkingInput.Run == 0, "Walking input was not received by the server.");
            Require(math.distance(StartPosition, _server.EntityManager.GetComponentData<LocalTransform>(_hero).Position) > 0.2f,
                "The server hero did not move from the real joystick touch.");
            Check("Real touch drives server walking");
            Capture("Walking");
            Vector2 OriginalCenter = Surface.GestureOrigin;
            Touch(2, UnityEngine.InputSystem.TouchPhase.Began, _origin + Vector2.right * 180 * Scale);
            yield return new WaitForSecondsRealtime(0.1f);
            Require(Surface.GestureOrigin == OriginalCenter, "A second combat finger stole the first gesture.");
            Touch(2, UnityEngine.InputSystem.TouchPhase.Ended, _origin + Vector2.right * 180 * Scale);
            Check("Second combat finger preserves the active gesture");
            float BeforeSprint = _server.EntityManager.GetComponentData<HeroCombatStamina>(_hero).Current;
            Vector2 SprintPosition = _origin + Vector2.left * 95 * Scale;
            Touch(1, UnityEngine.InputSystem.TouchPhase.Moved, SprintPosition);
            yield return new WaitForSecondsRealtime(0.25f);
            Require(_server.EntityManager.GetComponentData<HeroMoveInput>(_hero).Run != 0, "The outer ring did not request running.");
            Require(_server.EntityManager.GetComponentData<HeroCombatStamina>(_hero).Current < BeforeSprint, "Running did not consume stamina.");
            Check("Outer ring drives running and stamina consumption");
            Capture("Running");
            Touch(1, UnityEngine.InputSystem.TouchPhase.Ended, SprintPosition);
            yield return new WaitForSecondsRealtime(0.2f);
            Require(!Surface.IsGestureActive && math.lengthsq(_server.EntityManager.GetComponentData<HeroMoveInput>(_hero).Direction) < 0.01f,
                "Releasing the joystick did not stop movement.");
            Check("Pointer release stops movement");
            HeroActionState Action = _server.EntityManager.GetComponentData<HeroActionState>(_hero);
            Action.Combo = Action.ComboRequired;
            Action.ComboIdle = 0;
            Action.SpecialRemaining = 0;
            _server.EntityManager.SetComponentData(_hero, Action);
            yield return new WaitForSecondsRealtime(0.25f);
            Touch(1, UnityEngine.InputSystem.TouchPhase.Began, _origin);
            yield return new WaitForSecondsRealtime(_view.ChargeSeconds + 0.35f);
            Require(_view.IsSpecialCharged, "Center hold did not charge the special ability.");
            Check("Center hold charges the special ability from server state");
            Capture("SpecialCharged");
            Touch(1, UnityEngine.InputSystem.TouchPhase.Moved, _origin + Vector2.right * 65 * Scale);
            yield return new WaitForSecondsRealtime(0.25f);
            Require(_server.EntityManager.GetComponentData<HeroActionState>(_hero).SpecialUses > Action.SpecialUses,
                "Dragging the charged center did not execute the special ability.");
            Touch(1, UnityEngine.InputSystem.TouchPhase.Ended, _origin + Vector2.right * 65 * Scale);
            Check("Charged drag executes the server special ability");
            yield return new WaitForSecondsRealtime(0.2f);
            Touch(1, UnityEngine.InputSystem.TouchPhase.Began, _origin);
            yield return new WaitForSecondsRealtime(0.1f);
            Touch(1, UnityEngine.InputSystem.TouchPhase.Moved, _walking);
            yield return new WaitForSecondsRealtime(0.1f);
            UnityEngine.UI.Button Menu = Array.Find(_view.GetComponentsInChildren<UnityEngine.UI.Button>(), Button => Button.name == "Menu");
            Menu.onClick.Invoke();
            yield return new WaitForSecondsRealtime(0.2f);
            Require(!Surface.IsGestureActive && !Surface.enabled, "Opening the menu did not cancel the joystick.");
            Check("Menu cancels movement and charge ownership");
            Touch(1, UnityEngine.InputSystem.TouchPhase.Ended, _walking);
            _state = "Passed";
            System.IO.File.WriteAllText("M:/Prototypes/VaticanDefendor/Builds/CombatHud/Checks.json", "[\"" + string.Join("\",\"", _checks) + "\"]");
            Debug.Log("[CombatHudVerification] PASS: real touch, walking, running, stamina, multi-touch, charge, special and cancellation.");
        }
        finally
        {
            _view.GetComponentInChildren<ClientJoystickGraphic>(true).GetComponent<ClientPointerSurface>().CancelInput();
            InputSystem.RemoveDevice(_touchscreen);
        }
    }

    private static void Touch(int Identifier, UnityEngine.InputSystem.TouchPhase Phase, Vector2 Position)
    {
        InputSystem.QueueStateEvent(_touchscreen, new TouchState { touchId = Identifier, phase = Phase, position = Position, pressure = 1 });
    }

    private static void Capture(string Name)
    {
        System.IO.Directory.CreateDirectory("M:/Prototypes/VaticanDefendor/Builds/CombatHud");
        Texture2D Picture = ScreenCapture.CaptureScreenshotAsTexture();
        System.IO.File.WriteAllBytes("M:/Prototypes/VaticanDefendor/Builds/CombatHud/" + Name + ".png", Picture.EncodeToPNG());
        UnityEngine.Object.Destroy(Picture);
    }

    private static void Require(bool Condition, string Message)
    {
        if (!Condition)
        {
            _state = "Failed: " + Message;
            throw new InvalidOperationException(Message);
        }
    }

    private static void Check(string Name)
    {
        _checks.Add(Name);
    }
}
