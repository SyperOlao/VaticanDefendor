using UnityEngine;
using UnityEngine.InputSystem.EnhancedTouch;

[DefaultExecutionOrder(-100)]
public sealed class GameRoot : MonoBehaviour
{
    public ActiveCombatGame Game;
    public CombatCameraPresenter CameraPresenter;
    public CombatRenderSurface BattleSurface;
    public CombatNavigation Navigation;
    public TacticalMapPresenter MapPresenter;
    public CombatRenderCatalog RenderCatalog;
    public GameObject AuthoringRoot;
    public readonly PointerOwnership Pointers = new PointerOwnership();
    public CombatDotsWorld Simulation { get; private set; }
    private bool bEnhancedTouchEnabled;

    private void Awake()
    {
        Simulation = new CombatDotsWorld();
    }

    private void Start()
    {
        Game.InitializeFacade();
        Simulation.Initialize(RenderCatalog, Game.Rules);
        Game.ConnectReadModel();
#if UNITY_EDITOR
        AuthoringRoot.SetActive(false);
        Destroy(AuthoringRoot);
#endif
        Game.Session.ShowMainMenu();
    }

    private void Update()
    {
        if (!Game.bInitialized || !Game.enabled)
        {
            return;
        }
        Game.ReadKeyboard();
        Game.SubmitKeyboardMovement();
        Game.SynchronizeSession();
        float Remaining = Mathf.Min(Time.unscaledDeltaTime, 0.1f);
        while (Remaining > 0)
        {
            float Step = Mathf.Min(Remaining, 1f / 60);
            Simulation.Step(Step, false);
            Remaining -= Step;
        }
        Simulation.Publish();
        Game.RefreshSnapshots();
        Navigation.EnsureCurrent();
        Game.RefreshMapIfDue(Mathf.Min(Time.unscaledDeltaTime, 0.1f) * Game.Match.TimeScale);
        Game.RefreshInterface(Time.unscaledDeltaTime);
    }

    private void LateUpdate()
    {
        if (Game.bInitialized)
        {
            Simulation.Present(Time.unscaledDeltaTime);
        }
    }

    private void OnDestroy()
    {
        Simulation.Dispose();
        if (bEnhancedTouchEnabled)
        {
            EnhancedTouchSupport.Disable();
        }
    }

    private void OnEnable()
    {
        if (!bEnhancedTouchEnabled)
        {
            EnhancedTouchSupport.Enable();
            bEnhancedTouchEnabled = true;
        }
    }

    private void OnDisable()
    {
        Pointers.Clear();
    }

    private void OnApplicationFocus(bool bFocused)
    {
        if (!bFocused)
        {
            Pointers.Clear();
            Game.CancelGesture();
        }
    }
}
