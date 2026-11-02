#if !UNITY_SERVER || UNITY_EDITOR
using System;
using System.Collections.Generic;
using Unity.Entities;
using Unity.NetCode;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using TMPro;
using VaticanDefendor.Combat;
using VaticanDefendor.Construction;

namespace VaticanDefendor.Client.Shell
{
    public sealed class ClientShellView : MonoBehaviour, IClientShellView
    {
        [SerializeField] private GameObject _heroPrefab;
        [SerializeField] private GameObject _targetPrefab;
        [SerializeField] private CombatBalance _savedBalance;
        [SerializeField] private GameObject _wallPrefab;
        [SerializeField] private GameObject _sawmillPrefab;
        [SerializeField] private GameObject _goldBagPrefab;
        [SerializeField] private GameObject _churchPrefab;

        private readonly List<(Button button, UnityEngine.Events.UnityAction action)> _buttons = new();
        private readonly PointerOwnership _pointers = new();
        private readonly Dictionary<ulong, EnemyVisual> _enemyVisuals = new();
        private readonly HashSet<ulong> _seenEnemies = new();
        private readonly List<ulong> _removedEnemies = new();
        private readonly Dictionary<int, StructureVisual> _structureVisuals = new();
        private readonly HashSet<int> _seenStructures = new();
        private readonly List<int> _removedStructures = new();
        private readonly List<GameObject> _previewBodies = new();
        private readonly List<GameObject> _previewMarkers = new();
        private ClientShellPresenter _presenter;
        private ClientShellViewModel _model;
        private Transform _menu;
        private Transform _split;
        private Transform _settings;
        private Transform _pause;
        private Button _startButton;
        private RectTransform _mapArea;
        private RectTransform _battleArea;
        private RectTransform _divider;
        private CombatRenderSurface _mapSurface;
        private CombatRenderSurface _battleSurface;
        private Camera _mapCamera;
        private Camera _battleCamera;
        private GameObject _heroVisual;
        private GameObject _mapMarker;
        private GameObject _targetVisual;
        private GameObject _targetMapMarker;
        private GameObject _churchVisual;
        private GameObject _churchMapMarker;
        private Button _attackButton;
        private Transform _buildingPalette;
        private Button _wallButton;
        private Button _sawmillButton;
        private StructureKind _selectedBuildKind;
        private int _buildPointer = int.MinValue;
        private GridCell _buildStart;
        private GridCell _buildEnd;
        private bool _hasBuildPreview;
        private uint _lastHitEventSequence;
        private float _hitFlashUntil;
        private Vector2 _touchMovement;
        private bool _touchRun;
        private int _width;
        private int _height;
        private bool _bound;
        private float _renderedMapFraction = -1f;
        private bool _gameVisible;
        private float _frameSeconds;
        private float _largestFrameSeconds;
        private int _frameCount;
        private float _nextFrameReport;

        private sealed class EnemyVisual
        {
            public GameObject Body;
            public GameObject MapMarker;
        }

        private sealed class StructureVisual
        {
            public GameObject Body;
            public GameObject MapMarker;
            public GameObject GoldBag;
        }

        public event Action StartRequested;
        public event Action MenuRequested;
        public event Action ResumeRequested;
        public event Action SettingsRequested;
        public event Action BackRequested;
        public event Action LeaveRequested;
        public event Action ExitRequested;
        public event Action<float> MapRatioRequested;

        public void Initialize(IClientSessionPort port)
        {
            if (_presenter != null)
            {
                throw new InvalidOperationException("Client Shell is already initialized.");
            }

            BindSavedLayout();
            float initialRatio = _savedBalance != null && _savedBalance.MobileView.Ratio == MobileRatioPreset.Third
                ? 1f / 3f : 0.25f;
            _presenter = new ClientShellPresenter(this, port, initialRatio);
            Debug.Log($"[ClientShell] Initialized {( _split != null ? "saved combat layout" : "UI stand" )}.", this);
        }

        public void Render(ClientShellViewModel model)
        {
            _model = model;
            if (model.Screen == ClientShellScreen.Launch ||
                model.Screen == ClientShellScreen.Connecting ||
                model.Screen == ClientShellScreen.Lost ||
                model.Screen == ClientShellScreen.Error)
            {
                _lastHitEventSequence = 0;
                _hitFlashUntil = 0f;
                _selectedBuildKind = 0;
                CancelBuildGesture();
            }
            if (Mathf.Abs(_renderedMapFraction - model.MapFraction) > 0.001f)
            {
                _renderedMapFraction = model.MapFraction;
                ApplyMapRatio();
            }
            if (_startButton != null)
            {
                _startButton.interactable = model.CanAct;
            }
            bool game = model.Screen == ClientShellScreen.Game || model.Screen == ClientShellScreen.Menu ||
                model.Screen == ClientShellScreen.Settings && model.HasHero;
            SetActive(_menu, !game && model.Screen != ClientShellScreen.Settings);
            SetActive(_split, game);
            SetActive(_settings, model.Screen == ClientShellScreen.Settings);
            SetActive(_pause, model.Screen == ClientShellScreen.Menu);
            if (game && !_gameVisible)
            {
                ApplyMapRatio();
                RefreshSurfaces();
            }
            _gameVisible = game;
            if (_heroVisual != null)
            {
                _heroVisual.SetActive(game && model.HasHero);
            }

            if (_mapMarker != null)
            {
                _mapMarker.SetActive(game && model.HasHero);
            }
            if (_targetVisual != null)
                _targetVisual.SetActive(game && model.Combat.HasTarget && !model.Combat.TargetDead);
            if (_targetMapMarker != null)
                _targetMapMarker.SetActive(game && model.Combat.HasTarget && !model.Combat.TargetDead);
            if (_churchVisual != null)
                _churchVisual.SetActive(game && model.Run.ChurchSeen != 0 && model.Run.ChurchDead == 0);
            if (_churchMapMarker != null)
                _churchMapMarker.SetActive(game && model.Run.ChurchSeen != 0 && model.Run.ChurchDead == 0);
            if (_attackButton != null)
                _attackButton.interactable = model.Screen == ClientShellScreen.Game && model.HasHero &&
                    model.Run.Phase == VaticanDefendor.RunRules.RunPhase.Active;
            SetActive(_buildingPalette, model.Screen == ClientShellScreen.Game &&
                model.Run.Phase == VaticanDefendor.RunRules.RunPhase.Active &&
                model.Construction.Offers != null && model.Construction.Offers.Length > 0);
            if (_wallButton != null) _wallButton.interactable = model.Screen == ClientShellScreen.Game;
            if (_sawmillButton != null) _sawmillButton.interactable = model.Screen == ClientShellScreen.Game;
        }

        public void SetTouchMovement(Vector2 direction, bool run)
        {
            _touchMovement = direction;
            _touchRun = run;
        }

        public void BeginBuildGesture(int pointerId, Vector2 screenPoint)
        {
            if (_model.Screen != ClientShellScreen.Game || _selectedBuildKind == 0 ||
                _buildPointer != int.MinValue || !TryMapCell(screenPoint, out GridCell cell)) return;
            _buildPointer = pointerId;
            _buildStart = cell;
            _buildEnd = cell;
            _hasBuildPreview = true;
        }

        public void UpdateBuildGesture(int pointerId, Vector2 screenPoint)
        {
            if (_buildPointer == pointerId && TryMapCell(screenPoint, out GridCell cell))
                _buildEnd = cell;
        }

        public void EndBuildGesture(int pointerId, Vector2 screenPoint)
        {
            if (_buildPointer != pointerId) return;
            UpdateBuildGesture(pointerId, screenPoint);
            if (_hasBuildPreview && TryGetOffer(_selectedBuildKind,
                out ClientConstructionOfferModel offer))
            {
                _presenter?.SubmitConstruction(offer.DefinitionId,
                    _buildStart.Horizontal, _buildStart.Vertical,
                    _selectedBuildKind == StructureKind.Wall ? _buildEnd.Horizontal : _buildStart.Horizontal,
                    _selectedBuildKind == StructureKind.Wall ? _buildEnd.Vertical : _buildStart.Vertical);
            }
            CancelBuildGesture();
        }

        public void CancelBuildGesture()
        {
            _buildPointer = int.MinValue;
            _hasBuildPreview = false;
            ClearBuildPreview();
        }

        private void Start()
        {
            if (_presenter != null)
            {
                return;
            }

            World world = World.DefaultGameObjectInjectionWorld;
            if (world == null || !world.IsCreated || (world.Flags & WorldFlags.GameClient) == 0)
            {
                Debug.LogError("[ClientShell] A client World is required. Run the client with -vdRole client.", this);
                enabled = false;
                return;
            }

            try
            {
                Initialize(new NetcodeClientSessionPort(world));
            }
            catch (Exception exception)
            {
                Debug.LogError($"[ClientShell] {exception}", this);
                enabled = false;
            }
        }

        private void Update()
        {
            if (_presenter == null)
            {
                return;
            }

            _presenter.Tick();
            Vector2 movement = _touchMovement;
            bool run = _touchRun;
            Keyboard keyboard = Keyboard.current;
            if (keyboard != null && _model.Screen == ClientShellScreen.Game)
            {
                Vector2 keys = new Vector2(
                    (keyboard.dKey.isPressed || keyboard.rightArrowKey.isPressed ? 1 : 0) -
                    (keyboard.aKey.isPressed || keyboard.leftArrowKey.isPressed ? 1 : 0),
                    (keyboard.wKey.isPressed || keyboard.upArrowKey.isPressed ? 1 : 0) -
                    (keyboard.sKey.isPressed || keyboard.downArrowKey.isPressed ? 1 : 0));
                if (keys.sqrMagnitude > 0)
                {
                    movement = keys.normalized;
                    run = keyboard.leftShiftKey.isPressed || keyboard.rightShiftKey.isPressed;
                }

                if (keyboard.escapeKey.wasPressedThisFrame)
                {
                    MenuRequested?.Invoke();
                }
                if (keyboard.spaceKey.wasPressedThisFrame)
                {
                    _presenter.SubmitAttack(movement.sqrMagnitude > 0.25f
                        ? movement.normalized : Vector2.up);
                }
            }

            _presenter.SubmitMovement(movement, run);
        }

        private void LateUpdate()
        {
            UpdateEnemyVisuals();
            UpdateStructureVisuals();
            UpdateBuildPreview();
            if (_model.HasHero || _model.Combat.HasTarget || _model.Run.ChurchSeen != 0 ||
                _model.Combat.Enemies != null && _model.Combat.Enemies.Length > 0)
            {
                EnsureVisuals();
                if (_heroVisual != null)
                {
                    _heroVisual.transform.position = _model.HeroPosition;
                }

                if (_mapMarker != null)
                {
                    _mapMarker.transform.position = _model.HeroPosition + Vector3.up * 0.15f;
                }

                if (_battleCamera != null)
                {
                    Vector3 position = _model.HeroPosition;
                    _battleCamera.transform.position = position + new Vector3(0, 13, -17);
                    _battleCamera.transform.LookAt(position);
                }

                if (_mapCamera != null)
                {
                    _mapCamera.transform.position = _model.HeroPosition + new Vector3(0, 45, 0);
                    _mapCamera.transform.rotation = Quaternion.Euler(90, 0, 0);
                }
                if (_targetVisual != null)
                {
                    _targetVisual.transform.position = _model.Combat.TargetPosition;
                    float pulse = Time.unscaledTime < _hitFlashUntil ? 1.2f : 1f;
                    _targetVisual.transform.localScale = Vector3.one * pulse;
                }
                if (_targetMapMarker != null)
                    _targetMapMarker.transform.position = _model.Combat.TargetPosition + Vector3.up * 0.15f;
                if (_churchVisual != null)
                    _churchVisual.transform.position = _model.Run.ChurchPosition;
                if (_churchMapMarker != null)
                    _churchMapMarker.transform.position = (Vector3)_model.Run.ChurchPosition +
                        new Vector3(0f, 0.15f, 0f);
                if (_model.Combat.LastHitEventSequence != 0 &&
                    _model.Combat.LastHitEventSequence != _lastHitEventSequence)
                {
                    _lastHitEventSequence = _model.Combat.LastHitEventSequence;
                    _hitFlashUntil = Time.unscaledTime + 0.15f;
                }
            }

            if (_width != Screen.width || _height != Screen.height)
            {
                _width = Screen.width;
                _height = Screen.height;
                ApplyMapRatio();
                RefreshSurfaces();
            }
            if (_model.Screen == ClientShellScreen.Game)
            {
                _frameSeconds += Time.unscaledDeltaTime;
                _largestFrameSeconds = Mathf.Max(_largestFrameSeconds, Time.unscaledDeltaTime);
                _frameCount++;
                if (Time.unscaledTime >= _nextFrameReport && _frameCount > 0)
                {
                    Debug.Log($"[ClientDisplay] frames={_frameCount} avgMs={_frameSeconds * 1000f / _frameCount:F2} " +
                        $"maxMs={_largestFrameSeconds * 1000f:F2} enemies={_enemyVisuals.Count}.");
                    _frameSeconds = 0f;
                    _largestFrameSeconds = 0f;
                    _frameCount = 0;
                    _nextFrameReport = Time.unscaledTime + 5f;
                }
            }
        }

        private void OnGUI()
        {
            if (_presenter == null)
            {
                return;
            }

            if (_model.Screen == ClientShellScreen.Game)
            {
                GUI.Label(new Rect(12, 8, 700, 72),
                    $"{_model.ProfileDisplayName} v{_model.ProfileVersion}  |  Run {_model.RunId}\nContent {_model.ContentReleaseId}  |  Position {_model.HeroPosition:F1}");
                string targetText = _model.Combat.HasTarget
                    ? _model.Combat.TargetDead ? "Цель побеждена" :
                        $"Цель: {_model.Combat.TargetHealth:0}/{_model.Combat.TargetMaximumHealth:0} HP"
                    : "Ожидание цели";
                string last = _model.Combat.LastEventSequence == 0 ? "" :
                    $" | {_model.Combat.LastOutcome} {_model.Combat.LastDamage:0}";
                string gold = _model.Construction.Offers == null ? "—" :
                    _model.Construction.Gold.ToString();
                string experience = _model.Combat.LastEventSequence == 0 ? "—" : _model.Combat.RunExperience.ToString();
                GUI.Label(new Rect(12, 79, 650, 36),
                    $"{targetText}{last} | Золото {gold} | Опыт {experience}");
                GUI.Label(new Rect(12, 104, 650, 28),
                    $"Герой: {_model.Combat.HeroHealth:0}/{_model.Combat.HeroMaximumHealth:0} HP" +
                    $" | Враги: {_model.Combat.Enemies?.Length ?? 0}");
                if (_model.Run.ChurchSeen != 0)
                    GUI.Label(new Rect(12, 130, 650, 28),
                        $"Церковь ур. {_model.Run.ChurchLevel}: " +
                        $"{_model.Run.ChurchHealth:0}/{_model.Run.ChurchMaximumHealth:0} HP");
                if (_model.Run.Phase != VaticanDefendor.RunRules.RunPhase.Active)
                    GUI.Box(new Rect(Screen.width / 2f - 180, 165, 360, 48), _model.Message);
                DrawConstructionReadModel();
                if (_attackButton == null &&
                    _model.Run.Phase == VaticanDefendor.RunRules.RunPhase.Active &&
                    GUI.Button(new Rect(Screen.width - 120, Screen.height - 100, 108, 72), "УДАР"))
                    _presenter.SubmitAttack(Vector2.up);
                if (_menu == null && GUI.Button(new Rect(Screen.width - 104, 8, 96, 36), "Menu")) MenuRequested?.Invoke();
                return;
            }

            if (_model.Screen == ClientShellScreen.Menu || _model.Screen == ClientShellScreen.Settings)
            {
                GUI.Label(new Rect(16, 10, Screen.width - 32, 45), _model.Message);
                if (_model.Screen == ClientShellScreen.Settings)
                {
                    GUI.Label(new Rect(16, 58, 240, 30), $"Map height: {_model.MapFraction:P0}");
                    if (GUI.Button(new Rect(16, 92, 105, 38), "Quarter")) MapRatioRequested?.Invoke(0.25f);
                    if (GUI.Button(new Rect(130, 92, 105, 38), "Third")) MapRatioRequested?.Invoke(1f / 3f);
                    if (_settings == null && GUI.Button(new Rect(16, 140, 105, 38), "Done")) BackRequested?.Invoke();
                }
                else if (_pause == null)
                {
                    if (GUI.Button(new Rect(16, 60, 100, 36), "Resume")) ResumeRequested?.Invoke();
                    if (GUI.Button(new Rect(124, 60, 100, 36), "Settings")) SettingsRequested?.Invoke();
                    if (GUI.Button(new Rect(232, 60, 100, 36), "Leave")) LeaveRequested?.Invoke();
                }
                return;
            }

            GUI.Box(new Rect(Screen.width / 2f - 190, 20, 380, 105), _model.Title);
            GUI.Label(new Rect(Screen.width / 2f - 175, 48, 350, 65), _model.Message);
            if (_menu == null && _model.Screen == ClientShellScreen.Launch &&
                GUI.Button(new Rect(Screen.width / 2f + 80, 132, 95, 42), "Settings"))
            {
                SettingsRequested?.Invoke();
            }
            if (_model.Screen == ClientShellScreen.Lost || _model.Screen == ClientShellScreen.Error ||
                _model.Screen == ClientShellScreen.Connecting || _menu == null)
            {
                GUI.enabled = _model.CanAct;
                if (GUI.Button(new Rect(Screen.width / 2f - 70, 132, 140, 42), _model.ActionLabel))
                {
                    StartRequested?.Invoke();
                }

                GUI.enabled = true;
            }
        }

        private void OnDisable()
        {
            DisposeShell();
        }

        private void OnDestroy()
        {
            DisposeShell();
        }

        private void DisposeShell()
        {
            _presenter?.Dispose();
            _presenter = null;
            foreach ((Button button, UnityEngine.Events.UnityAction action) entry in _buttons)
            {
                if (entry.button != null)
                {
                    entry.button.onClick.RemoveListener(entry.action);
                }
            }

            _buttons.Clear();
            _pointers.Clear();
            if (_heroVisual != null) Destroy(_heroVisual);
            if (_mapMarker != null) Destroy(_mapMarker);
            if (_targetVisual != null) Destroy(_targetVisual);
            if (_targetMapMarker != null) Destroy(_targetMapMarker);
            if (_churchVisual != null) Destroy(_churchVisual);
            if (_churchMapMarker != null) Destroy(_churchMapMarker);
            foreach (EnemyVisual visual in _enemyVisuals.Values)
            {
                if (visual.Body != null) Destroy(visual.Body);
                if (visual.MapMarker != null) Destroy(visual.MapMarker);
            }
            _enemyVisuals.Clear();
            _seenEnemies.Clear();
            _removedEnemies.Clear();
            foreach (StructureVisual visual in _structureVisuals.Values)
            {
                if (visual.Body != null) Destroy(visual.Body);
                if (visual.MapMarker != null) Destroy(visual.MapMarker);
                if (visual.GoldBag != null) Destroy(visual.GoldBag);
            }
            _structureVisuals.Clear();
            _seenStructures.Clear();
            _removedStructures.Clear();
            ClearBuildPreview();
            if (_attackButton != null) Destroy(_attackButton.gameObject);
            _bound = false;
        }

        private void BindSavedLayout()
        {
            if (_bound) return;
            _bound = true;
            _menu = transform.Find("PhoneSafeArea/MainMenu");
            _split = transform.Find("PhoneSafeArea/SplitContainer");
            _settings = transform.Find("PhoneSafeArea/SettingsModal");
            _pause = transform.Find("PhoneSafeArea/PauseOverlay");
            _buildingPalette = transform.Find("PhoneSafeArea/SplitContainer/TacticalMap/BuildingPalette");
            _mapArea = transform.Find("PhoneSafeArea/SplitContainer/TacticalMap") as RectTransform;
            _battleArea = transform.Find("PhoneSafeArea/SplitContainer/BattleViewport") as RectTransform;
            _divider = transform.Find("PhoneSafeArea/SplitContainer/CurtainHandle") as RectTransform;
            Bind("PhoneSafeArea/MainMenu/MenuActions/NormalGame", () => StartRequested?.Invoke());
            _startButton = transform.Find("PhoneSafeArea/MainMenu/MenuActions/NormalGame")?.GetComponent<Button>();
            Bind("PhoneSafeArea/MainMenu/MenuActions/Settings", () => SettingsRequested?.Invoke());
            Bind("PhoneSafeArea/MainMenu/MenuActions/Exit", () => ExitRequested?.Invoke());
            Bind("PhoneSafeArea/SplitContainer/BattleViewport/Menu", () => MenuRequested?.Invoke());
            Bind("PhoneSafeArea/PauseOverlay/Paused/Resume", () => ResumeRequested?.Invoke());
            Bind("PhoneSafeArea/PauseOverlay/Paused/MainMenu", () => LeaveRequested?.Invoke());
            Bind("PhoneSafeArea/PauseOverlay/Paused/Settings", () => SettingsRequested?.Invoke());
            Bind("PhoneSafeArea/SettingsModal/SettingsWindow/Done", () => BackRequested?.Invoke());
            Bind("PhoneSafeArea/SettingsModal/SettingsWindow/Close", () => BackRequested?.Invoke());

            float radius = _savedBalance != null ? _savedBalance.JoystickMovementRadius : 52f;
            float runRadius = _savedBalance != null ? _savedBalance.JoystickOuterRadius : 64f;
            float canvasScale = GetComponent<Canvas>() != null ? GetComponent<Canvas>().scaleFactor : 1f;
            radius *= canvasScale;
            runRadius *= canvasScale;
            Surface("PhoneSafeArea/SplitContainer/BattleViewport/CombatInput", PointerRegion.Combat, radius, runRadius);
            Surface("PhoneSafeArea/SplitContainer/BattleViewport/JoystickMovement", PointerRegion.Combat, radius, runRadius);
            Surface("PhoneSafeArea/SplitContainer/BattleViewport/BattleRenderSurface", PointerRegion.Combat, radius, runRadius);
            Surface("PhoneSafeArea/SplitContainer/TacticalMap/TacticalMapCanvas", PointerRegion.Map, radius, runRadius);
            Surface("PhoneSafeArea/SplitContainer/CurtainHandle", PointerRegion.Divider, radius, runRadius);
            CreateAttackButton();
            BindBuildingPalette();
            _mapSurface = transform.Find("PhoneSafeArea/SplitContainer/TacticalMap/TacticalMapCanvas")?.GetComponent<CombatRenderSurface>();
            _battleSurface = transform.Find("PhoneSafeArea/SplitContainer/BattleViewport/BattleRenderSurface")?.GetComponent<CombatRenderSurface>();
            _mapCamera = _mapSurface != null ? _mapSurface.OutputCamera : null;
            _battleCamera = _battleSurface != null ? _battleSurface.OutputCamera : null;
            if (gameObject.name == "MobileCombatCanvas" &&
                (_menu == null || _split == null || _mapCamera == null || _battleCamera == null ||
                 _heroPrefab == null || _targetPrefab == null || _savedBalance == null ||
                 _wallPrefab == null || _sawmillPrefab == null))
            {
                throw new InvalidOperationException("Saved client layout is missing menu, split surfaces, cameras, hero/target prefab or presentation settings.");
            }
            HideUnsupported("PhoneSafeArea/SplitContainer/TacticalMap/GoldEnemies");
            HideUnsupported("PhoneSafeArea/SplitContainer/TacticalMap/PrestigeBadge");
            HideUnsupported("PhoneSafeArea/SplitContainer/TacticalMap/MapModeControls");
            HideUnsupported("PhoneSafeArea/SplitContainer/TacticalMap/MapSell");
            HideUnsupported("PhoneSafeArea/SplitContainer/TacticalMap/MapTrebuchet");
            HideUnsupported("PhoneSafeArea/SplitContainer/TacticalMap/MapFullscreen");
            HideUnsupported("PhoneSafeArea/SplitContainer/TacticalMap/CollapseMap");
            HideUnsupported("PhoneSafeArea/SplitContainer/TacticalMap/BuildHint");
            HideUnsupported("PhoneSafeArea/SplitContainer/BattleViewport/WaveStatus");
            HideUnsupported("PhoneSafeArea/SplitContainer/BattleViewport/CombatHelp");
            HideUnsupported("PhoneSafeArea/SplitContainer/BattleViewport/JoystickAction");
            HideUnsupported("PhoneSafeArea/MainMenu/MenuActions/Training");
            HideUnsupported("PhoneSafeArea/SettingsModal/SettingsWindow/ScrollView");
            HideUnsupported("PhoneSafeArea/SettingsModal/SettingsWindow/Defaults");
            HideUnsupported("PhoneSafeArea/SettingsModal/SettingsWindow/ResetMap");
            HideUnsupported("PhoneSafeArea/SettingsModal/SettingsWindow/ValidationError");
            RefreshSurfaces();
        }

        private void Bind(string path, UnityEngine.Events.UnityAction action)
        {
            Transform child = transform.Find(path);
            if (child == null) return;
            Button button = child.GetComponent<Button>();
            if (button == null) return;
            button.onClick.AddListener(action);
            _buttons.Add((button, action));
            ClientPointerSurface surface = child.GetComponent<ClientPointerSurface>() ?? child.gameObject.AddComponent<ClientPointerSurface>();
            surface.Configure(_pointers, PointerRegion.Interface, this, 1, 1);
        }

        private void BindBuildingPalette()
        {
            if (_buildingPalette == null) return;
            foreach (Button button in _buildingPalette.GetComponentsInChildren<Button>(true))
            {
                StructureKind kind;
                if (button.name == "Building1") kind = StructureKind.Wall;
                else if (button.name == "Building5") kind = StructureKind.Sawmill;
                else
                {
                    if (button.name.StartsWith("Building", StringComparison.Ordinal))
                        button.gameObject.SetActive(false);
                    continue;
                }
                if (kind == StructureKind.Wall) _wallButton = button;
                else _sawmillButton = button;
                UnityEngine.Events.UnityAction action = () => SelectBuild(kind);
                button.onClick.AddListener(action);
                _buttons.Add((button, action));
                ClientPointerSurface surface = button.GetComponent<ClientPointerSurface>() ??
                    button.gameObject.AddComponent<ClientPointerSurface>();
                surface.Configure(_pointers, PointerRegion.Interface, this, 1f, 1f);
            }
            if (_wallButton != null && _sawmillButton != null &&
                _wallButton.transform is RectTransform wall &&
                _sawmillButton.transform is RectTransform sawmill)
            {
                // The saved palette has gaps for now unsupported buildings.
                // Compact the two supported buttons at runtime without rewriting scene data.
                wall.anchoredPosition = new Vector2(40f, wall.anchoredPosition.y);
                sawmill.anchoredPosition = new Vector2(156f, sawmill.anchoredPosition.y);
                ScrollRect scroll = _buildingPalette.GetComponent<ScrollRect>();
                if (scroll != null && scroll.content is RectTransform content)
                    content.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, 280f);
            }
        }

        private void SelectBuild(StructureKind kind)
        {
            _selectedBuildKind = _selectedBuildKind == kind ? 0 : kind;
            CancelBuildGesture();
        }

        private bool TryGetOffer(StructureKind kind, out ClientConstructionOfferModel selected)
        {
            if (_model.Construction.Offers != null)
                foreach (ClientConstructionOfferModel offer in _model.Construction.Offers)
                    if (offer.Kind == kind) { selected = offer; return true; }
            selected = default;
            return false;
        }

        private bool TryMapCell(Vector2 screenPoint, out GridCell cell)
        {
            cell = default;
            if (_mapSurface == null || !TryGetOffer(_selectedBuildKind,
                out ClientConstructionOfferModel offer) ||
                !_mapSurface.TryGetWorldPoint(screenPoint, out Vector3 world)) return false;
            cell = new GridCell(Mathf.RoundToInt(world.x / offer.CellSizeMeters),
                Mathf.RoundToInt(world.z / offer.CellSizeMeters));
            return true;
        }

        private void Surface(string path, PointerRegion region, float radius, float runRadius)
        {
            Transform child = transform.Find(path);
            if (child == null) return;
            ClientPointerSurface surface = child.GetComponent<ClientPointerSurface>() ?? child.gameObject.AddComponent<ClientPointerSurface>();
            surface.Configure(_pointers, region, this, radius, runRadius);
        }

        private void CreateAttackButton()
        {
            if (_battleArea == null) return;
            var root = new GameObject("BasicAttackButton", typeof(RectTransform), typeof(Image), typeof(Button));
            root.transform.SetParent(_battleArea, false);
            RectTransform rect = (RectTransform)root.transform;
            rect.anchorMin = rect.anchorMax = new Vector2(1f, 0f);
            rect.pivot = new Vector2(1f, 0f);
            rect.anchoredPosition = new Vector2(-24f, 24f);
            rect.sizeDelta = new Vector2(108f, 82f);
            root.GetComponent<Image>().color = new Color(0.72f, 0.23f, 0.15f, 0.85f);
            _attackButton = root.GetComponent<Button>();
            UnityEngine.Events.UnityAction action = () => _presenter?.SubmitAttack(
                _touchMovement.sqrMagnitude > 0.25f ? _touchMovement.normalized : Vector2.up);
            _attackButton.onClick.AddListener(action);
            _buttons.Add((_attackButton, action));
            ClientPointerSurface surface = root.AddComponent<ClientPointerSurface>();
            surface.Configure(_pointers, PointerRegion.Interface, this, 1f, 1f);

            Transform savedLabel = transform.Find("PhoneSafeArea/SplitContainer/BattleViewport/JoystickAction");
            if (savedLabel != null)
            {
                GameObject label = Instantiate(savedLabel.gameObject, root.transform, false);
                label.SetActive(true);
                TextMeshProUGUI text = label.GetComponent<TextMeshProUGUI>();
                if (text != null) text.text = "УДАР";
                RectTransform labelRect = label.GetComponent<RectTransform>();
                labelRect.anchorMin = Vector2.zero;
                labelRect.anchorMax = Vector2.one;
                labelRect.offsetMin = labelRect.offsetMax = Vector2.zero;
            }
        }

        private void HideUnsupported(string path)
        {
            Transform child = transform.Find(path);
            if (child != null) child.gameObject.SetActive(false);
        }

        private void EnsureVisuals()
        {
            if (_churchVisual == null && _churchPrefab != null && _model.Run.ChurchSeen != 0)
            {
                _churchVisual = Instantiate(_churchPrefab);
                _churchVisual.name = "Replicated Church View";
            }
            if (_churchMapMarker == null && _model.Run.ChurchSeen != 0)
            {
                _churchMapMarker = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                _churchMapMarker.name = "Replicated Church Map Marker";
                _churchMapMarker.layer = 31;
                _churchMapMarker.transform.localScale = new Vector3(1.2f, 0.04f, 1.2f);
                Destroy(_churchMapMarker.GetComponent<Collider>());
            }
            if (_heroVisual == null && _heroPrefab != null)
            {
                _heroVisual = Instantiate(_heroPrefab);
                _heroVisual.name = "Replicated Hero View";
            }

            if (_mapMarker == null)
            {
                _mapMarker = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                _mapMarker.name = "Replicated Hero Map Marker";
                _mapMarker.layer = 31;
                _mapMarker.transform.localScale = new Vector3(0.7f, 0.04f, 0.7f);
                Destroy(_mapMarker.GetComponent<Collider>());
            }
            if (_targetVisual == null && _targetPrefab != null && _model.Combat.HasTarget &&
                !_model.Combat.TargetDead)
            {
                _targetVisual = Instantiate(_targetPrefab);
                _targetVisual.name = "Replicated Combat Target View";
            }
            if (_targetMapMarker == null && _model.Combat.HasTarget && !_model.Combat.TargetDead)
            {
                _targetMapMarker = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                _targetMapMarker.name = "Replicated Target Map Marker";
                _targetMapMarker.layer = 31;
                _targetMapMarker.transform.localScale = new Vector3(0.7f, 0.04f, 0.7f);
                Destroy(_targetMapMarker.GetComponent<Collider>());
            }
        }

        private void UpdateEnemyVisuals()
        {
            _seenEnemies.Clear();
            ClientEnemySnapshot[] enemies = _model.Combat.Enemies;
            if (enemies != null)
            {
                foreach (ClientEnemySnapshot enemy in enemies)
                {
                    _seenEnemies.Add(enemy.GhostKey);
                    if (!_enemyVisuals.TryGetValue(enemy.GhostKey, out EnemyVisual visual))
                    {
                        visual = new EnemyVisual();
                        if (_targetPrefab != null)
                        {
                            visual.Body = Instantiate(_targetPrefab);
                            visual.Body.name = $"Replicated Enemy {enemy.GhostKey}";
                        }
                        visual.MapMarker = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                        visual.MapMarker.name = $"Replicated Enemy Map Marker {enemy.GhostKey}";
                        visual.MapMarker.layer = 31;
                        visual.MapMarker.transform.localScale = new Vector3(0.5f, 0.04f, 0.5f);
                        Destroy(visual.MapMarker.GetComponent<Collider>());
                        _enemyVisuals.Add(enemy.GhostKey, visual);
                    }
                    bool visible = _model.Screen == ClientShellScreen.Game && !enemy.Dead;
                    if (visual.Body != null)
                    {
                        visual.Body.SetActive(visible);
                        visual.Body.transform.position = enemy.Position;
                    }
                    visual.MapMarker.SetActive(visible);
                    visual.MapMarker.transform.position = enemy.Position + Vector3.up * 0.15f;
                }
            }
            _removedEnemies.Clear();
            foreach (ulong key in _enemyVisuals.Keys)
                if (!_seenEnemies.Contains(key)) _removedEnemies.Add(key);
            foreach (ulong key in _removedEnemies)
            {
                EnemyVisual visual = _enemyVisuals[key];
                if (visual.Body != null) Destroy(visual.Body);
                if (visual.MapMarker != null) Destroy(visual.MapMarker);
                _enemyVisuals.Remove(key);
            }
        }

        private void UpdateStructureVisuals()
        {
            _seenStructures.Clear();
            ClientStructureSnapshot[] structures = _model.Construction.Structures;
            if (structures != null)
            {
                foreach (ClientStructureSnapshot structure in structures)
                {
                    _seenStructures.Add(structure.StructureId);
                    if (!_structureVisuals.TryGetValue(structure.StructureId, out StructureVisual visual))
                    {
                        visual = new StructureVisual();
                        GameObject prefab = structure.Kind == StructureKind.Wall
                            ? _wallPrefab : _sawmillPrefab;
                        if (prefab != null)
                        {
                            visual.Body = Instantiate(prefab);
                            visual.Body.name = $"Replicated {structure.Kind} {structure.StructureId}";
                            if (structure.Kind == StructureKind.Wall &&
                                TryGetOffer(StructureKind.Wall, out ClientConstructionOfferModel wall) &&
                                visual.Body.transform.childCount > 0)
                                visual.Body.transform.GetChild(0).localScale =
                                    new Vector3(wall.CellSizeMeters, 1.35f, wall.CellSizeMeters);
                        }
                        visual.MapMarker = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                        visual.MapMarker.name = $"Structure Map Marker {structure.StructureId}";
                        visual.MapMarker.layer = 31;
                        visual.MapMarker.transform.localScale = new Vector3(0.65f, 0.04f, 0.65f);
                        Destroy(visual.MapMarker.GetComponent<Collider>());
                        _structureVisuals.Add(structure.StructureId, visual);
                    }
                    bool visible = _model.Screen == ClientShellScreen.Game ||
                        _model.Screen == ClientShellScreen.Menu;
                    if (visual.Body != null)
                    {
                        visual.Body.SetActive(visible);
                        visual.Body.transform.position = structure.Position;
                    }
                    visual.MapMarker.SetActive(visible);
                    visual.MapMarker.transform.position = structure.Position + Vector3.up * 0.15f;
                    if (structure.Kind == StructureKind.Sawmill && structure.StoredGold > 0 &&
                        _goldBagPrefab != null &&
                        TryGetOffer(StructureKind.Sawmill, out ClientConstructionOfferModel offer))
                    {
                        if (visual.GoldBag == null) visual.GoldBag = Instantiate(_goldBagPrefab);
                        visual.GoldBag.SetActive(visible);
                        visual.GoldBag.transform.position = structure.Position +
                            Vector3.forward * offer.PickupOffsetZMeters;
                    }
                    else if (visual.GoldBag != null) visual.GoldBag.SetActive(false);
                }
            }
            _removedStructures.Clear();
            foreach (int id in _structureVisuals.Keys)
                if (!_seenStructures.Contains(id)) _removedStructures.Add(id);
            foreach (int id in _removedStructures)
            {
                StructureVisual visual = _structureVisuals[id];
                if (visual.Body != null) Destroy(visual.Body);
                if (visual.MapMarker != null) Destroy(visual.MapMarker);
                if (visual.GoldBag != null) Destroy(visual.GoldBag);
                _structureVisuals.Remove(id);
            }
        }

        private void UpdateBuildPreview()
        {
            if (!_hasBuildPreview || _model.Screen != ClientShellScreen.Game ||
                !TryGetOffer(_selectedBuildKind, out ClientConstructionOfferModel offer))
            {
                ClearBuildPreview();
                return;
            }
            List<GridCell> cells = _selectedBuildKind == StructureKind.Wall
                ? GridCell.Trace(_buildStart, _buildEnd)
                : new List<GridCell> { _buildStart };
            bool available = PreviewAvailable(offer, cells, out _);
            while (_previewBodies.Count < cells.Count)
            {
                GameObject body = GameObject.CreatePrimitive(PrimitiveType.Cube);
                body.name = "Construction Preview";
                Destroy(body.GetComponent<Collider>());
                _previewBodies.Add(body);
                GameObject marker = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                marker.name = "Construction Preview Map Marker";
                marker.layer = 31;
                Destroy(marker.GetComponent<Collider>());
                _previewMarkers.Add(marker);
            }
            for (int index = 0; index < _previewBodies.Count; index++)
            {
                bool active = index < cells.Count;
                _previewBodies[index].SetActive(active);
                _previewMarkers[index].SetActive(active);
                if (!active) continue;
                Vector3 position = new Vector3(cells[index].Horizontal * offer.CellSizeMeters,
                    0f, cells[index].Vertical * offer.CellSizeMeters);
                GameObject body = _previewBodies[index];
                body.transform.position = position + Vector3.up * 0.3f;
                body.transform.localScale = new Vector3(offer.CellSizeMeters * 0.85f,
                    0.6f, offer.CellSizeMeters * 0.85f);
                body.GetComponent<Renderer>().material.color = available
                    ? new Color(0.18f, 0.8f, 0.35f) : new Color(0.9f, 0.2f, 0.18f);
                GameObject marker = _previewMarkers[index];
                marker.transform.position = position + Vector3.up * 0.17f;
                marker.transform.localScale = new Vector3(0.85f, 0.04f, 0.85f);
                marker.GetComponent<Renderer>().material.color = body.GetComponent<Renderer>().material.color;
            }
        }

        private bool PreviewAvailable(ClientConstructionOfferModel offer, List<GridCell> cells,
            out string reason)
        {
            if (cells.Count > (offer.Kind == StructureKind.Wall ? offer.MaximumWallCells : 1))
            {
                reason = "Слишком длинная стена";
                return false;
            }
            foreach (GridCell cell in cells)
            {
                if (Mathf.Abs(cell.Horizontal) > offer.GridWidth / 2 ||
                    Mathf.Abs(cell.Vertical) > offer.GridHeight / 2)
                {
                    reason = "За пределами карты";
                    return false;
                }
                if (_model.Construction.Structures == null) continue;
                foreach (ClientStructureSnapshot existing in _model.Construction.Structures)
                {
                    if (!TryGetOffer(existing.Kind, out ClientConstructionOfferModel other)) continue;
                    Vector3 position = new Vector3(cell.Horizontal * offer.CellSizeMeters,
                        0f, cell.Vertical * offer.CellSizeMeters);
                    float extent = offer.HalfExtentMeters + other.HalfExtentMeters - 0.01f;
                    if (Mathf.Abs(position.x - existing.Position.x) < extent &&
                        Mathf.Abs(position.z - existing.Position.z) < extent)
                    {
                        reason = "Клетка занята";
                        return false;
                    }
                }
            }
            long cost = (long)offer.GoldPrice * cells.Count;
            if (_model.Construction.Gold < cost)
            {
                reason = "Недостаточно золота";
                return false;
            }
            reason = "Сервер проверит место";
            return true;
        }

        private void ClearBuildPreview()
        {
            foreach (GameObject body in _previewBodies)
                if (body != null) Destroy(body);
            foreach (GameObject marker in _previewMarkers)
                if (marker != null) Destroy(marker);
            _previewBodies.Clear();
            _previewMarkers.Clear();
        }

        private void DrawConstructionReadModel()
        {
            ClientConstructionSnapshot construction = _model.Construction;
            if (construction.Offers == null || construction.Offers.Length == 0) return;
            string wall = TryGetOffer(StructureKind.Wall, out ClientConstructionOfferModel wallOffer)
                ? $"Стена {wallOffer.GoldPrice}" : "Стена —";
            string sawmill = TryGetOffer(StructureKind.Sawmill, out ClientConstructionOfferModel sawmillOffer)
                ? $"Пилорама {sawmillOffer.GoldPrice}" : "Пилорама —";
            string production = sawmillOffer.ProductionCycleSeconds > 0f
                ? $"+{sawmillOffer.ProductionGoldPerCycle} / {sawmillOffer.ProductionCycleSeconds:0} с" : "";
            long storedGold = 0;
            if (construction.Structures != null)
                foreach (ClientStructureSnapshot structure in construction.Structures)
                    storedGold += structure.StoredGold;
            string selected = _selectedBuildKind == 0 ? "Выберите постройку" :
                _selectedBuildKind == StructureKind.Wall ? "Стена: проведите по карте" :
                    "Пилорама: коснитесь карты";
            if (_hasBuildPreview && TryGetOffer(_selectedBuildKind, out ClientConstructionOfferModel offer))
            {
                List<GridCell> cells = _selectedBuildKind == StructureKind.Wall
                    ? GridCell.Trace(_buildStart, _buildEnd) : new List<GridCell> { _buildStart };
                PreviewAvailable(offer, cells, out string reason);
                selected = $"{cells.Count} клеток, {(long)offer.GoldPrice * cells.Count} золота — {reason}";
            }
            string result = construction.LastOperationId == 0 ? "" :
                $" | #{construction.LastOperationId}: {ConstructionReason(construction.LastOutcome)}";
            Rect panel = new Rect(8, Screen.height - 188, Screen.width - 16, 74);
            GUI.Box(panel, GUIContent.none);
            var style = new GUIStyle(GUI.skin.label)
            {
                fontSize = 12,
                wordWrap = true,
                alignment = TextAnchor.UpperLeft
            };
            style.normal.textColor = Color.white;
            GUI.Label(new Rect(panel.x + 6, panel.y + 4, panel.width - 12, panel.height - 8),
                $"Золото {construction.Gold} | {wall} | {sawmill} {production} | Выпуск {storedGold}\n{selected}{result}", style);
            if (_buildingPalette == null)
            {
                if (GUI.Button(new Rect(12, Screen.height - 267, 100, 32), wall))
                    SelectBuild(StructureKind.Wall);
                if (GUI.Button(new Rect(116, Screen.height - 267, 120, 32), sawmill))
                    SelectBuild(StructureKind.Sawmill);
                if (_selectedBuildKind != 0 &&
                    GUI.Button(new Rect(12, Screen.height - 232, 150, 32), "Sample build intent") &&
                    TryGetOffer(_selectedBuildKind, out ClientConstructionOfferModel sample))
                    _presenter?.SubmitConstruction(sample.DefinitionId, 3, 3, 3, 3);
            }
        }

        private static string ConstructionReason(ConstructionOutcome outcome)
        {
            switch (outcome)
            {
                case ConstructionOutcome.Built: return "Построено";
                case ConstructionOutcome.Duplicate: return "Команда уже обработана";
                case ConstructionOutcome.Unauthorized: return "Нет права строить";
                case ConstructionOutcome.InactiveRun: return "Забег неактивен";
                case ConstructionOutcome.InvalidDefinition: return "Неизвестная постройка";
                case ConstructionOutcome.InvalidPath: return "Недопустимая стена";
                case ConstructionOutcome.OutOfBounds: return "Вне карты";
                case ConstructionOutcome.Occupied: return "Клетка занята";
                case ConstructionOutcome.Blocked: return "Препятствие";
                case ConstructionOutcome.HeroTooClose: return "Слишком близко к герою";
                case ConstructionOutcome.InsufficientGold: return "Недостаточно золота";
                case ConstructionOutcome.RateLimited: return "Слишком часто";
                case ConstructionOutcome.CapacityReached: return "Лимит построек";
                default: return outcome.ToString();
            }
        }

        private void RefreshSurfaces()
        {
            Canvas.ForceUpdateCanvases();
            if (_mapSurface != null && _mapSurface.Image != null && _mapCamera != null) _mapSurface.Refresh();
            if (_battleSurface != null && _battleSurface.Image != null && _battleCamera != null) _battleSurface.Refresh();
        }

        private void ApplyMapRatio()
        {
            if (_split is not RectTransform split || _mapArea == null || _battleArea == null) return;
            float height = split.rect.height * _renderedMapFraction;
            _mapArea.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, height);
            _battleArea.offsetMax = new Vector2(_battleArea.offsetMax.x, -(height + 28f));
            if (_divider != null)
            {
                _divider.anchoredPosition = new Vector2(_divider.anchoredPosition.x, -height);
            }
            RefreshSurfaces();
        }

        private static void SetActive(Transform item, bool active)
        {
            if (item != null && item.gameObject.activeSelf != active) item.gameObject.SetActive(active);
        }
    }
}
#endif
