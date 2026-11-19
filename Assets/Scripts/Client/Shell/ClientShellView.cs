#if !UNITY_SERVER || UNITY_EDITOR
using System;
using System.Collections.Generic;
using TMPro;
using Unity.Collections;
using Unity.Entities;
using Unity.Transforms;
using Unity.NetCode;
using UnityEngine;
using UnityEngine.InputSystem;
using Screen = UnityEngine.Device.Screen;
using VaticanDefendor.Combat;
using VaticanDefendor.Construction;
using VaticanDefendor.Content;

namespace VaticanDefendor.Client.Shell
{
    public sealed class ClientShellView : MonoBehaviour, IClientShellView
    {
        [SerializeField] private CombatRenderCatalog _renderCatalog;
        [SerializeField] private ClientPresentationDefinition _presentation;

        private readonly PointerOwnership _pointers = new PointerOwnership();
        private readonly List<(UnityEngine.UI.Button Button, UnityEngine.Events.UnityAction Action)> _bindings = new();
        private readonly Dictionary<StructureKind, UnityEngine.UI.Button> _buildingButtons = new();
        private readonly List<GameObject> _previews = new();
        private ClientShellPresenter _presenter;
        private ClientShellViewModel _model;
        private Transform _menu;
        private RectTransform _split;
        private Transform _settings;
        private Transform _pause;
        private RectTransform _mapArea;
        private RectTransform _battleArea;
        private RectTransform _divider;
        private CombatRenderSurface _mapSurface;
        private CombatRenderSurface _battleSurface;
        private TMP_FontAsset _font;
        private TMP_Text _status;
        private readonly TMP_Text[] ResourceCounts = new TMP_Text[3];
        private ClientCombatHud _combatHud;
        private ClientPointerSurface _combatPointer;
        private ClientJoystickGraphic _joystick;
        private SpecialReadyEdgeEffect _readyEdges;
        private SpecialReadyEdgeEffect _damageEdges;
        private readonly List<TMP_Text> _damageNumbers = new();
        private readonly List<TMP_Text> _structureLevels = new();
        private readonly List<(UnityEngine.UI.Image Frame, UnityEngine.UI.Image Stem, ResourceIconGraphic Icon, TMP_Text Infinity)> _depositMarkers = new();
        private readonly List<Rect> _depositMarkerBounds = new();
        private TacticalBuildingGraphic _buildingMarkers;
        private EcsWorldPresentationSystem _worldPresentation;
        private TMP_Text _buildStatus;
        private ResourceCostView BuildCost;
        private ResourceCostView UpgradeCost;
        private ResourceCostView ForgeWeaponCost;
        private ResourceCostView ForgeHealthCost;
        private UnityEngine.UI.Button _gatherButton;
        private ClientPointerSurface _weaponDock;
        private ClientWeaponDockGraphic _weaponDockGraphic;
        private ClientWeaponWheel _wheel;
        private RectTransform _palette;
        private UnityEngine.UI.ScrollRect BuildingScroll;
        private UnityEngine.UI.HorizontalLayoutGroup BuildingLayout;
        private UnityEngine.UI.Button PreviousBuildings;
        private UnityEngine.UI.Button NextBuildings;
        private float BuildingCardStride;
        private StructureKind _selectedKind;
        private int _buildPointer = int.MinValue;
        private Unity.Mathematics.float2 _buildStart;
        private Unity.Mathematics.float2 _buildEnd;
        private Vector2 _movement;
        private bool _run;
        private int _offerCount = -1;
        private uint _lastConstructionOperation;
        private int _width;
        private int _height;
        private Rect _safeArea;
        private float _mapFraction = -1;
        private bool _bound;
        private bool _artilleryArmed;
        private int _selectedStructure;
        private RectTransform _structureActions;
        private TMP_Text _structureLabel;
        private UnityEngine.UI.Button _sellButton;
        private UnityEngine.UI.Button _upgradeButton;
        private UnityEngine.UI.Button _forgeWeaponButton;
        private UnityEngine.UI.Button _forgeHealthButton;
        private UnityEngine.UI.Button _connectButton;
        private RectTransform _result;
        private TMP_Text _resultText;
        private UnityEngine.UI.Button _returnButton;

        public event Action StartRequested;
        public event Action MenuRequested;
        public event Action ResumeRequested;
        public event Action SettingsRequested;
        public event Action BackRequested;
        public event Action LeaveRequested;
        public event Action ExitRequested;
        public event Action<float> MapRatioRequested;

        public bool IsSpecialCharged => _model.Gameplay.ChargeSeconds > 0 && _model.Gameplay.Action.ComboRequired > 0 &&
            _model.Gameplay.Action.SpecialRemaining <= 0 && _model.Gameplay.Action.Combo >= _model.Gameplay.Action.ComboRequired &&
            _model.Gameplay.Action.ChargeProgress >= _model.Gameplay.ChargeSeconds;
        public float ChargeSeconds => _model.Gameplay.ChargeSeconds;
        public float CenterRadius => _presentation.CenterRadius * GetComponent<Canvas>().scaleFactor;
        public bool CanStartCombatGesture => !_wheel.gameObject.activeSelf;
        public ClientGameplaySnapshot Gameplay => _model.Gameplay;

        public void Initialize(IClientSessionPort Port)
        {
            if (_presenter != null)
            {
                throw new InvalidOperationException("Client shell is already initialized.");
            }
            BindLayout();
            World World = World.DefaultGameObjectInjectionWorld;
            _worldPresentation = World.GetOrCreateSystemManaged<EcsWorldPresentationSystem>();
            _worldPresentation.Initialize(_renderCatalog, _presentation);
            _worldPresentation.ConfigureOcclusionCameras(_battleSurface.OutputCamera, _mapSurface.OutputCamera);
            World.GetOrCreateSystemManaged<ResourcePickupPresentationSystem>().Initialize(_presentation);
            _presenter = new ClientShellPresenter(this, Port, 0.3f);
            StartRequested?.Invoke();
        }

        private void Start()
        {
            if (_presenter != null)
            {
                return;
            }
            World World = World.DefaultGameObjectInjectionWorld;
            if (World == null || !World.IsCreated || !World.IsClient())
            {
                Debug.LogError("[ClientShell] A client world is required.", this);
                enabled = false;
                return;
            }
            Initialize(new NetcodeClientSessionPort(World));
        }

        public void Render(ClientShellViewModel Model)
        {
            _model = Model;
            bool bFinished = Model.Run.Phase != VaticanDefendor.RunRules.RunPhase.Active && Model.Run.Revision > 0;
            bool bGame = (bFinished || Model.HasHero) && (Model.Screen == ClientShellScreen.Game || Model.Screen == ClientShellScreen.Menu || Model.Screen == ClientShellScreen.Settings);
            _menu.gameObject.SetActive(!bGame && Model.Screen != ClientShellScreen.Settings);
            _split.gameObject.SetActive(bGame);
            _settings.gameObject.SetActive(Model.Screen == ClientShellScreen.Settings);
            _pause.gameObject.SetActive(Model.Screen == ClientShellScreen.Menu);
            _status.text = Model.Message;
            _connectButton.interactable = Model.CanAct;
            _connectButton.GetComponentInChildren<TMP_Text>().text = "Повторить подключение";
            _returnButton.interactable = Model.Run.Phase == VaticanDefendor.RunRules.RunPhase.Completed;
            _result.gameObject.SetActive(bFinished && bGame);
            if (_result.gameObject.activeSelf)
            {
                string Outcome = "Поражение";
                if (Model.Run.Outcome == VaticanDefendor.RunRules.RunOutcome.Victory)
                {
                    Outcome = "Победа";
                }
                string Delivery = "Сохранение результата…";
                if (Model.Run.Phase == VaticanDefendor.RunRules.RunPhase.Completed)
                {
                    Delivery = "Оружие и прогресс сохранены. Престиж: +" + Model.Run.ConfirmedPermanentReward;
                }
                _resultText.text = Outcome + "\n" + Delivery;
            }
            ResourceAmounts Stock = Model.Gameplay.Resources;
            ResourceCounts[0].text = Stock.Wood.ToString();
            ResourceCounts[1].text = Stock.Stone.ToString();
            ResourceCounts[2].text = Stock.Iron.ToString();
            int DisplayWave = Model.Gameplay.WaveNumber;
            if (Model.Gameplay.WavePhase == 3)
            {
                DisplayWave++;
            }
            string WaveStatus = "Волна " + DisplayWave;
            if (Model.Gameplay.WavePhase == 0 || Model.Gameplay.WavePhase == 3)
            {
                WaveStatus += " через " + Mathf.CeilToInt(Model.Gameplay.WaveCountdown) + " с";
            }
            _readyEdges.SetReady(Model.Gameplay.Action.ComboRequired > 0 && Model.Gameplay.Action.Combo >= Model.Gameplay.Action.ComboRequired &&
                Model.Gameplay.Action.SpecialRemaining <= 0 && Model.Screen == ClientShellScreen.Game, 0.28f);
            _damageEdges.SetReady(Model.Gameplay.Impact.FlashRemaining > 0, Mathf.Clamp01(Model.Gameplay.Impact.FlashRemaining / 0.18f) * 0.6f);
            _combatHud.Render(Model, WaveStatus);
            bool bActive = Model.HasHero && Model.Screen == ClientShellScreen.Game &&
                Model.Run.Phase == VaticanDefendor.RunRules.RunPhase.Active && Model.Run.Paused == 0;
            _combatPointer.enabled = bActive;
            _joystick.enabled = bActive;
            _gatherButton.gameObject.SetActive(bActive && (Model.Gameplay.GatheringKind != ResourceKind.None || Model.Gameplay.Action.Gathering != 0));
            string Gathering = "Добывать";
            if (Model.Gameplay.Action.Gathering != 0)
            {
                Gathering = "Остановить";
            }
            if (Model.Gameplay.GatheringRemaining < 0)
            {
                Gathering += "\n∞";
            }
            else if (Model.Gameplay.GatheringKind != ResourceKind.None)
            {
                Gathering += "\n" + Model.Gameplay.GatheringRemaining;
            }
            _gatherButton.GetComponentInChildren<TMP_Text>().text = Gathering;
            EquippedWeapon[] Weapons = Model.Gameplay.Weapons;
            bool bHasWeapon = Weapons != null && Model.Gameplay.Action.ActiveSlot < Weapons.Length;
            _weaponDock.gameObject.SetActive(bActive && bHasWeapon);
            if (bHasWeapon)
            {
                _weaponDockGraphic.Render(Weapons[Model.Gameplay.Action.ActiveSlot]);
            }
            BuildPalette();
            if (Model.Construction.LastOperationId != _lastConstructionOperation)
            {
                _lastConstructionOperation = Model.Construction.LastOperationId;
                BuildCost.gameObject.SetActive(false);
                _buildStatus.text = ConstructionMessage(Model.Construction.LastOutcome);
            }
            RenderStructureActions();
            if (Mathf.Abs(_mapFraction - Model.MapFraction) > 0.001f || _width != Screen.width || _height != Screen.height || _safeArea != Screen.safeArea)
            {
                _mapFraction = Model.MapFraction;
                ApplyMapRatio();
            }
        }

        private void Update()
        {
            if (_presenter == null)
            {
                return;
            }
            _presenter.Tick();
            Keyboard Keyboard = Keyboard.current;
            if (_wheel.gameObject.activeSelf)
            {
                if (Keyboard != null && Keyboard.escapeKey.wasPressedThisFrame)
                {
                    _weaponDock.CancelInput();
                }
                _presenter.SubmitMovement(Vector2.zero, false);
                return;
            }
            Vector2 Direction = _movement;
            bool bRun = _run;
            if (Keyboard != null && _combatPointer.enabled && !_combatPointer.IsGestureActive)
            {
                Vector2 Keys = Vector2.zero;
                if (Keyboard.wKey.isPressed || Keyboard.upArrowKey.isPressed)
                {
                    Keys.y++;
                }
                if (Keyboard.sKey.isPressed || Keyboard.downArrowKey.isPressed)
                {
                    Keys.y--;
                }
                if (Keyboard.dKey.isPressed || Keyboard.rightArrowKey.isPressed)
                {
                    Keys.x++;
                }
                if (Keyboard.aKey.isPressed || Keyboard.leftArrowKey.isPressed)
                {
                    Keys.x--;
                }
                if (Keys.sqrMagnitude > 0)
                {
                    Direction = Keys.normalized;
                }
                bRun |= Keyboard.leftShiftKey.isPressed;
                if (Keyboard.spaceKey.wasPressedThisFrame)
                {
                    _presenter.SubmitDash();
                }
                if (Keyboard.eKey.wasPressedThisFrame)
                {
                    _presenter.SubmitGathering();
                }
                _presenter.SetCharging(Keyboard.qKey.isPressed || Keyboard.rKey.isPressed);
                if (IsSpecialCharged)
                {
                    if (Keyboard.rKey.wasReleasedThisFrame)
                    {
                        _presenter.SubmitDefense();
                    }
                    else if (Keyboard.qKey.wasReleasedThisFrame)
                    {
                        _presenter.SubmitAttack(Vector2.zero);
                    }
                }
                if (Keyboard.digit1Key.wasPressedThisFrame)
                {
                    _presenter.SelectWeapon(0);
                }
                if (Keyboard.digit2Key.wasPressedThisFrame)
                {
                    _presenter.SelectWeapon(1);
                }
                if (Keyboard.digit3Key.wasPressedThisFrame)
                {
                    _presenter.SelectWeapon(2);
                }
            }
            if (Keyboard != null && Keyboard.escapeKey.wasPressedThisFrame && _model.Screen == ClientShellScreen.Game)
            {
                MenuRequested?.Invoke();
            }
            _presenter.SubmitMovement(Direction, bRun);
        }

        private void LateUpdate()
        {
            if (_presenter == null)
            {
                return;
            }
            if (!_model.HasHero)
            {
                _worldPresentation.ClearOcclusionTarget();
                UpdateTacticalMarkers(_mapSurface.OutputCamera);
                return;
            }
            Camera Battle = _battleSurface.OutputCamera;
            Battle.transform.position = _model.HeroPosition + _presentation.BattleCameraOffset;
            Battle.transform.LookAt(_model.HeroPosition);
            if (_model.Run.Paused == 0 && _model.Run.Phase == VaticanDefendor.RunRules.RunPhase.Active)
            {
                float Impact = _model.Gameplay.Action.CameraImpactRemaining;
                float Phase = Time.unscaledTime * 65;
                Battle.transform.position += Battle.transform.right * Mathf.Sin(Phase) * Impact * _presentation.CameraImpactScale +
                    Battle.transform.up * Mathf.Cos(Phase * 1.37f) * Impact * _presentation.CameraImpactScale * 0.5f;
            }
            if (_model.Combat.HeroDead)
            {
                _worldPresentation.ClearOcclusionTarget();
            }
            else
            {
                _worldPresentation.SetOcclusionTarget(Battle, _model.HeroPosition);
            }
            Camera Map = _mapSurface.OutputCamera;
            Map.transform.position = (Vector3)_model.Run.ChurchPosition + Vector3.up * _presentation.MapHeight;
            Map.transform.rotation = Quaternion.Euler(90, 0, 0);
            Map.orthographic = true;
            Map.orthographicSize = _presentation.MapSize;
            Unity.Entities.World ClientWorld = Unity.Entities.World.DefaultGameObjectInjectionWorld;
            if (ClientWorld != null && ClientWorld.IsCreated)
            {
                RiverMapConfiguration Surface = VaticanDefendor.Maps.RiverMapAccess.Get(ClientWorld.EntityManager);
                _mapSurface.SetSurface(Surface);
                _battleSurface.SetSurface(Surface);
                if (Surface != null)
                {
                    Map.transform.position = new Vector3(0, 80, 0);
                    Map.orthographicSize = (Surface.WidthMeters * 0.5f + 2) / Mathf.Min(1, Map.aspect);
                }
            }
            UpdateTacticalMarkers(Map);
            UpdateDamageNumbers(Battle);
            UpdateStructureLevels(Battle);
            UpdatePreview();
        }

        private void UpdateTacticalMarkers(Camera Map)
        {
            UpdateDepositMarkers(Map);
            ClientStructureSnapshot[] Structures = null;
            if (_split.gameObject.activeSelf && _model.HasHero)
            {
                Structures = _model.Construction.Structures;
            }
            _buildingMarkers.Render(Map, Structures, _depositMarkerBounds);
        }

        private void UpdateDepositMarkers(Camera Map)
        {
            _depositMarkerBounds.Clear();
            int Visible = 0;
            RectTransform MapImage = _mapSurface.Image.rectTransform;
            Rect Bounds = MapImage.rect;
            if (_split.gameObject.activeSelf && _model.HasHero && _model.Construction.Structures != null)
            {
                foreach (ClientStructureSnapshot Structure in _model.Construction.Structures)
                {
                    if (!Structure.InfiniteResource || Structure.Health <= 0)
                    {
                        continue;
                    }
                    Vector3 Viewport = Map.WorldToViewportPoint(Structure.Position);
                    if (Viewport.z <= 0 || Viewport.x < 0 || Viewport.x > 1 || Viewport.y < 0 || Viewport.y > 1)
                    {
                        continue;
                    }
                    if (Visible == _depositMarkers.Count)
                    {
                        Vector2 Center = new Vector2(0.5f, 0.5f);
                        GameObject Frame = CombatUIFactory.Panel(MapImage, "InfiniteDeposit", Color.white, Center,
                            Vector2.zero, new Vector2(46, 25));
                        var FrameImage = Frame.GetComponent<UnityEngine.UI.Image>();
                        FrameImage.raycastTarget = false;
                        var StemObject = new GameObject("DepositStem", typeof(RectTransform), typeof(UnityEngine.UI.Image));
                        StemObject.transform.SetParent(MapImage, false);
                        StemObject.transform.SetAsFirstSibling();
                        var Stem = StemObject.GetComponent<UnityEngine.UI.Image>();
                        Stem.raycastTarget = false;
                        GameObject Background = CombatUIFactory.Panel(Frame.transform, "Background",
                            new Color(0.07f, 0.09f, 0.10f), Center, Vector2.zero, new Vector2(42, 21));
                        Background.GetComponent<UnityEngine.UI.Image>().raycastTarget = false;
                        var IconObject = new GameObject("ResourceIcon", typeof(RectTransform), typeof(CanvasRenderer), typeof(ResourceIconGraphic));
                        RectTransform IconRectangle = (RectTransform)IconObject.transform;
                        IconRectangle.SetParent(Background.transform, false);
                        IconRectangle.sizeDelta = new Vector2(16, 16);
                        IconRectangle.anchoredPosition = new Vector2(-10, 0);
                        TMP_Text Infinity = CombatUIFactory.Text(Background.transform, "Infinity", "∞", Center,
                            new Vector2(9, 0), new Vector2(20, 21), 19, _font);
                        Infinity.alignment = TextAlignmentOptions.Center;
                        _depositMarkers.Add((FrameImage, Stem, IconObject.GetComponent<ResourceIconGraphic>(), Infinity));
                    }
                    var Marker = _depositMarkers[Visible];
                    Color Tint = _presentation.InfiniteDepositColor(Structure.DepositKind);
                    Marker.Frame.color = Tint;
                    Marker.Infinity.color = Tint;
                    Marker.Icon.SetKind(Structure.DepositKind);
                    Vector2 Size = Marker.Frame.rectTransform.sizeDelta;
                    Vector2 Anchor = Bounds.min + Vector2.Scale(Viewport, Bounds.size);
                    Vector2 Position = FindDepositMarkerPosition(Anchor, Size, Bounds, Visible);
                    Marker.Frame.rectTransform.localPosition = Position;
                    _depositMarkerBounds.Add(new Rect(Position - Size * 0.5f, Size));
                    Vector2 Offset = Position - Anchor;
                    bool bShowStem = Mathf.Abs(Offset.x) > Size.x * 0.5f || Mathf.Abs(Offset.y) > Size.y * 0.5f;
                    Marker.Stem.gameObject.SetActive(bShowStem);
                    if (bShowStem)
                    {
                        Marker.Stem.color = Tint;
                        Marker.Stem.rectTransform.localPosition = Anchor + Offset * 0.5f;
                        Marker.Stem.rectTransform.sizeDelta = new Vector2(Offset.magnitude, 2);
                        Marker.Stem.rectTransform.localRotation = Quaternion.Euler(0, 0, Mathf.Atan2(Offset.y, Offset.x) * Mathf.Rad2Deg);
                    }
                    Marker.Frame.gameObject.SetActive(true);
                    Visible++;
                }
            }
            for (int Index = Visible; Index < _depositMarkers.Count; Index++)
            {
                _depositMarkers[Index].Frame.gameObject.SetActive(false);
                _depositMarkers[Index].Stem.gameObject.SetActive(false);
            }
        }

        private Vector2 FindDepositMarkerPosition(Vector2 Anchor, Vector2 Size, Rect Bounds, int Visible)
        {
            Vector2 Minimum = Bounds.min + Size * 0.5f;
            Vector2 Maximum = Bounds.max - Size * 0.5f;
            Vector2 Position = new Vector2(Mathf.Clamp(Anchor.x, Minimum.x, Maximum.x), Mathf.Clamp(Anchor.y, Minimum.y, Maximum.y));
            Vector2 Separation = Size + Vector2.one * 2;
            Vector2 Best = Position;
            float BestDistance = float.PositiveInfinity;
            for (int Axis = 0; Axis < 2; Axis++)
            {
                for (int Direction = -1; Direction <= 1; Direction += 2)
                {
                    Vector2 Candidate = Position;
                    for (int Index = 0; Index < Visible; Index++)
                    {
                        Vector2 Other = _depositMarkers[Index].Frame.rectTransform.localPosition;
                        if (Mathf.Abs(Other.x - Candidate.x) < Separation.x && Mathf.Abs(Other.y - Candidate.y) < Separation.y)
                        {
                            Candidate[Axis] = Other[Axis] + Direction * (Separation[Axis] + 0.01f);
                            Index = -1;
                        }
                    }
                    float Distance = (Candidate - Position).sqrMagnitude;
                    if (Candidate[Axis] >= Minimum[Axis] && Candidate[Axis] <= Maximum[Axis] && Distance < BestDistance)
                    {
                        Best = Candidate;
                        BestDistance = Distance;
                    }
                }
            }
            return Best;
        }

        private void UpdateStructureLevels(Camera Battle)
        {
            int Visible = 0;
            if (_model.Screen == ClientShellScreen.Game && _model.Construction.Structures != null)
            {
                foreach (ClientStructureSnapshot Structure in _model.Construction.Structures)
                {
                    if (Structure.StructureId == 0 || Structure.Health <= 0 ||
                        !Structure.InfiniteResource && !StructureActionRules.CanUpgrade(Structure.Kind))
                    {
                        continue;
                    }
                    float LabelHeight = Structure.HalfExtent * 2.7f + 0.8f;
                    if (!Structure.InfiniteResource)
                    {
                        LabelHeight = _worldPresentation.BuildingTop(Structure.Kind) + 0.3f;
                    }
                    Vector3 Viewport = Battle.WorldToViewportPoint(Structure.Position + Vector3.up * LabelHeight);
                    if (Viewport.z <= 0 || Viewport.x < 0 || Viewport.x > 1 || Viewport.y < 0 || Viewport.y > 1)
                    {
                        continue;
                    }
                    if (Visible == _structureLevels.Count)
                    {
                        TMP_Text NewLevel = CombatUIFactory.Text(_battleArea, "StructureLevel", "", Vector2.zero,
                            Vector2.zero, new Vector2(96, 30), 23, _font);
                        NewLevel.alignment = TextAlignmentOptions.Center;
                        NewLevel.outlineWidth = 0.2f;
                        NewLevel.outlineColor = Color.black;
                        NewLevel.rectTransform.pivot = new Vector2(0.5f, 0);
                        NewLevel.transform.SetSiblingIndex(_structureActions.GetSiblingIndex());
                        _structureLevels.Add(NewLevel);
                    }
                    TMP_Text Label = _structureLevels[Visible++];
                    Label.gameObject.SetActive(true);
                    Label.rectTransform.anchoredPosition = new Vector2(Viewport.x * _battleArea.rect.width,
                        Viewport.y * _battleArea.rect.height);
                    if (Structure.InfiniteResource)
                    {
                        Label.text = "∞";
                    }
                    else
                    {
                        Label.text = RomanLevel(Structure.Level);
                        if (Structure.UpgradeSecondsRemaining > 0)
                        {
                            Label.text += "  " + Structure.UpgradeSecondsRemaining + " с";
                        }
                    }
                }
            }
            for (int Index = Visible; Index < _structureLevels.Count; Index++)
            {
                _structureLevels[Index].gameObject.SetActive(false);
            }
        }

        private static string RomanLevel(int Level)
        {
            switch (Level)
            {
                case 1: return "I";
                case 2: return "II";
                case 3: return "III";
                case 4: return "IV";
                case 5: return "V";
                default: return Level.ToString();
            }
        }

        private void UpdateDamageNumbers(Camera Battle)
        {
            World World = World.DefaultGameObjectInjectionWorld;
            int Visible = 0;
            if (World != null && World.IsCreated)
            {
                EntityManager Entities = World.EntityManager;
                using EntityQuery Query = Entities.CreateEntityQuery(typeof(WorldDamageNumber), typeof(WorldCombatEffect), typeof(LocalTransform));
                using NativeArray<Entity> Numbers = Query.ToEntityArray(Allocator.Temp);
                foreach (Entity Number in Numbers)
                {
                    if (Visible >= _damageNumbers.Count)
                    {
                        break;
                    }
                    Vector3 Position = Entities.GetComponentData<LocalTransform>(Number).Position;
                    Vector3 Viewport = Battle.WorldToViewportPoint(Position);
                    if (Viewport.z <= 0 || Viewport.x < 0 || Viewport.x > 1 || Viewport.y < 0 || Viewport.y > 1)
                    {
                        continue;
                    }
                    WorldDamageNumber Damage = Entities.GetComponentData<WorldDamageNumber>(Number);
                    WorldCombatEffect Effect = Entities.GetComponentData<WorldCombatEffect>(Number);
                    TMP_Text Text = _damageNumbers[Visible++];
                    Text.gameObject.SetActive(true);
                    Text.text = Damage.Amount.ToString("0");
                    Color Tint = new Color(1, 0.85f, 0.3f, 1 - Effect.Age / Effect.Duration);
                    if (Damage.Hero != 0)
                    {
                        Tint = new Color(1, 0.3f, 0.15f, Tint.a);
                        Text.text = "−" + Text.text;
                    }
                    Text.color = Tint;
                    Text.rectTransform.anchoredPosition = new Vector2(Viewport.x * _battleArea.rect.width, Viewport.y * _battleArea.rect.height);
                }
            }
            for (int Index = Visible; Index < _damageNumbers.Count; Index++)
            {
                _damageNumbers[Index].gameObject.SetActive(false);
            }
        }

        public void SetTouchMovement(Vector2 Direction, bool bRun)
        {
            _movement = Direction;
            _run = bRun;
        }

        public void SetCharging(bool bCharging)
        {
            _presenter?.SetCharging(bCharging);
        }

        public void ReleaseChargedAttack(Vector2 Direction)
        {
            SetCharging(false);
            _presenter?.SubmitAttack(Direction);
        }

        public void ReleaseChargedDefense()
        {
            SetCharging(false);
            _presenter?.SubmitDefense();
        }

        public void ReleaseDash()
        {
            _presenter?.SubmitDash();
        }

        public bool BeginWeaponSelection(Vector2 ScreenPoint)
        {
            EquippedWeapon[] Weapons = _model.Gameplay.Weapons;
            if (!_weaponDock.gameObject.activeInHierarchy || Weapons == null || Weapons.Length == 0)
            {
                return false;
            }
            _combatPointer.CancelInput();
            CancelBuildGesture();
            SetCharging(false);
            _presenter.SubmitMovement(Vector2.zero, false);
            _weaponDockGraphic.SetPressed(true);
            _wheel.Open(ScreenPoint, Weapons, _model.Gameplay.Action.ActiveSlot, _presentation.WheelRadius);
            return true;
        }

        public void UpdateWeaponSelection(Vector2 ScreenPoint)
        {
            _wheel.Move(ScreenPoint);
        }

        public void EndWeaponSelection(Vector2 ScreenPoint)
        {
            int Slot = _wheel.Move(ScreenPoint);
            CancelWeaponSelection();
            if (Slot >= 0 && Slot < _model.Gameplay.Weapons.Length)
            {
                _presenter.SelectWeapon(Slot);
            }
        }

        public void CancelWeaponSelection()
        {
            _weaponDockGraphic.SetPressed(false);
            _wheel.gameObject.SetActive(false);
        }

        public void BeginBuildGesture(int PointerId, Vector2 ScreenPoint)
        {
            if (_model.Screen != ClientShellScreen.Game)
            {
                return;
            }
            if (_selectedKind == 0 && _mapSurface.TryGetWorldPoint(ScreenPoint, out Vector3 Pick))
            {
                if (_artilleryArmed)
                {
                    ClientConstructionOfferModel[] Offers = _model.Construction.Offers;
                    if (Offers != null && Offers.Length > 0)
                    {
                        _presenter.SubmitStructureAction(ConstructionOperation.Artillery, 0,
                            Pick.x, Pick.z);
                    }
                    _artilleryArmed = false;
                    return;
                }
                return;
            }
            if (!TryPosition(ScreenPoint, out Unity.Mathematics.float2 Position))
            {
                return;
            }
            _buildPointer = PointerId;
            _buildStart = _buildEnd = Position;
        }

        public void UpdateBuildGesture(int PointerId, Vector2 ScreenPoint)
        {
            if (_buildPointer == PointerId && TryPosition(ScreenPoint, out Unity.Mathematics.float2 Position))
            {
                _buildEnd = Position;
            }
        }

        public void EndBuildGesture(int PointerId, Vector2 ScreenPoint)
        {
            if (_buildPointer != PointerId)
            {
                return;
            }
            UpdateBuildGesture(PointerId, ScreenPoint);
            if (TryOffer(out ClientConstructionOfferModel Offer))
            {
                _presenter.SubmitConstruction(Offer.DefinitionId, _buildStart.x, _buildStart.y, _buildEnd.x, _buildEnd.y);
            }
            CancelBuildGesture();
        }

        public void CancelBuildGesture()
        {
            _buildPointer = int.MinValue;
            if (BuildCost != null)
            {
                BuildCost.gameObject.SetActive(false);
            }
            foreach (GameObject Preview in _previews)
            {
                Preview.SetActive(false);
            }
        }

        private bool TryPosition(Vector2 ScreenPoint, out Unity.Mathematics.float2 Position)
        {
            Position = default;
            if (!TryOffer(out ClientConstructionOfferModel Offer) || !_mapSurface.TryGetWorldPoint(ScreenPoint, out Vector3 WorldPosition))
            {
                return false;
            }
            Position = new Unity.Mathematics.float2(WorldPosition.x, WorldPosition.z);
            return true;
        }

        private bool TryOffer(out ClientConstructionOfferModel Selected)
        {
            foreach (ClientConstructionOfferModel Offer in _model.Construction.Offers ?? Array.Empty<ClientConstructionOfferModel>())
            {
                if (Offer.Kind == _selectedKind)
                {
                    Selected = Offer;
                    return true;
                }
            }
            Selected = default;
            return false;
        }

        private void BindLayout()
        {
            if (_bound)
            {
                return;
            }
            _bound = true;
            if (_renderCatalog == null || _presentation == null)
            {
                throw new InvalidOperationException("Client presentation assets are required.");
            }
            _menu = transform.Find("PhoneSafeArea/MainMenu");
            _split = (RectTransform)transform.Find("PhoneSafeArea/SplitContainer");
            _settings = transform.Find("PhoneSafeArea/SettingsModal");
            _pause = transform.Find("PhoneSafeArea/PauseOverlay");
            _mapArea = (RectTransform)_split.Find("TacticalMap");
            _battleArea = (RectTransform)_split.Find("BattleViewport");
            _divider = (RectTransform)_split.Find("CurtainHandle");
            _mapSurface = _mapArea.Find("TacticalMapCanvas").GetComponent<CombatRenderSurface>();
            _battleSurface = _battleArea.Find("BattleRenderSurface").GetComponent<CombatRenderSurface>();
            var BuildingMarkers = new GameObject("BuildingMarkers", typeof(RectTransform), typeof(CanvasRenderer), typeof(TacticalBuildingGraphic));
            RectTransform MapImage = _mapSurface.Image.rectTransform;
            var BuildingMarkerBounds = (RectTransform)BuildingMarkers.transform;
            BuildingMarkerBounds.SetParent(MapImage, false);
            BuildingMarkerBounds.pivot = MapImage.pivot;
            CombatUIFactory.Stretch(BuildingMarkerBounds, Vector2.zero, Vector2.zero);
            _buildingMarkers = BuildingMarkers.GetComponent<TacticalBuildingGraphic>();
            _buildingMarkers.raycastTarget = false;
            _font = GetComponentInChildren<TMP_Text>(true).font;
            Sprite PanelSprite = null;
            foreach (UnityEngine.UI.Image Image in GetComponentsInChildren<UnityEngine.UI.Image>(true))
            {
                if (Image.sprite != null && Image.sprite.name == "PhoneRounded")
                {
                    PanelSprite = Image.sprite;
                    break;
                }
            }
            CombatUIFactory.RoundSprite = PanelSprite;
            GameObject Background = CombatUIFactory.Panel(transform, "ScreenBackground", new Color(0.02f, 0.03f, 0.04f), Vector2.zero, Vector2.zero, Vector2.zero);
            CombatUIFactory.Stretch((RectTransform)Background.transform, Vector2.zero, Vector2.zero);
            Background.GetComponent<UnityEngine.UI.Image>().raycastTarget = false;
            Background.transform.SetAsFirstSibling();
            Bind("PhoneSafeArea/MainMenu/MenuActions/NormalGame", () => StartRequested?.Invoke());
            _connectButton = transform.Find("PhoneSafeArea/MainMenu/MenuActions/NormalGame").GetComponent<UnityEngine.UI.Button>();
            Bind("PhoneSafeArea/MainMenu/MenuActions/Settings", () => SettingsRequested?.Invoke());
            Bind("PhoneSafeArea/MainMenu/MenuActions/Exit", () => ExitRequested?.Invoke());
            RectTransform MenuActions = (RectTransform)_menu.Find("MenuActions");
            MenuActions.sizeDelta = new Vector2(MenuActions.sizeDelta.x, 390);
            MakeButton(MenuActions, "WeaponShop", "Магазин оружия", new Vector2(0.5f, 1), new Vector2(0, -318), new Vector2(320, 48),
                () => UnityEngine.Object.FindAnyObjectByType<VaticanDefendor.Bootstrap.OnlineStartup>().OpenWeaponShop());
            RectTransform PauseActions = (RectTransform)_pause.Find("Paused");
            PauseActions.sizeDelta = new Vector2(PauseActions.sizeDelta.x, 300);
            MakeButton(PauseActions, "WeaponShop", "Магазин оружия", new Vector2(0.5f, 1), new Vector2(0, -230), new Vector2(280, 48),
                () => UnityEngine.Object.FindAnyObjectByType<VaticanDefendor.Bootstrap.OnlineStartup>().OpenWeaponShop());
            Bind("PhoneSafeArea/SplitContainer/BattleViewport/Menu", () => MenuRequested?.Invoke());
            Bind("PhoneSafeArea/PauseOverlay/Paused/Resume", () => ResumeRequested?.Invoke());
            Bind("PhoneSafeArea/PauseOverlay/Paused/MainMenu", () =>
            {
                LeaveRequested?.Invoke();
                UnityEngine.Object.FindAnyObjectByType<VaticanDefendor.Bootstrap.OnlineStartup>().ReturnToLobby();
            });
            Bind("PhoneSafeArea/PauseOverlay/Paused/Settings", () => SettingsRequested?.Invoke());
            Bind("PhoneSafeArea/SettingsModal/SettingsWindow/Done", () => BackRequested?.Invoke());
            Bind("PhoneSafeArea/SettingsModal/SettingsWindow/Close", () => BackRequested?.Invoke());
            Transform CombatInput = _battleArea.Find("CombatInput");
            ConfigureSurface(CombatInput, PointerRegion.Combat);
            _combatPointer = CombatInput.GetComponent<ClientPointerSurface>();
            _joystick = CombatInput.gameObject.AddComponent<ClientJoystickGraphic>();
            _joystick.Configure(_combatPointer, this, _presentation);
            ConfigureSurface(_mapSurface.transform, PointerRegion.Map);
            ConfigureSurface(_divider, PointerRegion.Divider);
            foreach (string Name in new[] { "SpecialReadyEdges", "CombatDamageEdges" })
            {
                GameObject Edges = new GameObject(Name, typeof(RectTransform), typeof(CanvasRenderer), typeof(SpecialReadyEdgeEffect));
                Edges.transform.SetParent(_battleArea, false);
                CombatUIFactory.Stretch((RectTransform)Edges.transform, Vector2.zero, Vector2.zero);
                SpecialReadyEdgeEffect Graphic = Edges.GetComponent<SpecialReadyEdgeEffect>();
                Graphic.raycastTarget = false;
                Graphic.SetReady(false, 0);
                if (Name == "SpecialReadyEdges")
                {
                    _readyEdges = Graphic;
                }
                else
                {
                    _damageEdges = Graphic;
                    Graphic.bParticles = false;
                }
            }
            for (int Index = 0; Index < 32; Index++)
            {
                TMP_Text Number = CombatUIFactory.Text(_battleArea, "DamageNumber" + Index, "", Vector2.zero, Vector2.zero, new Vector2(130, 45), 26, _font);
                Number.alignment = TextAlignmentOptions.Center;
                Number.raycastTarget = false;
                Number.outlineWidth = 0.2f;
                Number.outlineColor = Color.black;
                Number.gameObject.SetActive(false);
                _damageNumbers.Add(Number);
            }
            _status = CombatUIFactory.Text(_menu, "ConnectionStatus", "", new Vector2(0.5f, 0), new Vector2(0, 30), new Vector2(600, 80), 22, _font);
            for (int ResourceIndex = 0; ResourceIndex < ResourceCounts.Length; ResourceIndex++)
            {
                var Container = new GameObject(((ResourceKind)(ResourceIndex + 1)).ToString() + "Resource", typeof(RectTransform));
                Container.transform.SetParent(_mapArea, false);
                RectTransform Bounds = (RectTransform)Container.transform;
                Bounds.anchorMin = Bounds.anchorMax = new Vector2(0.5f, 1);
                Bounds.anchoredPosition = new Vector2((ResourceIndex - 1) * 195, -28);
                Bounds.sizeDelta = new Vector2(185, 44);
                var IconObject = new GameObject("Icon", typeof(RectTransform), typeof(CanvasRenderer), typeof(ResourceIconGraphic));
                IconObject.transform.SetParent(Container.transform, false);
                var Icon = IconObject.GetComponent<ResourceIconGraphic>();
                Icon.rectTransform.anchoredPosition = new Vector2(-62, 0);
                Icon.rectTransform.sizeDelta = new Vector2(34, 34);
                Icon.SetKind((ResourceKind)(ResourceIndex + 1));
                ResourceCounts[ResourceIndex] = CombatUIFactory.Text(Bounds, "Amount", "0", new Vector2(0.5f, 0.5f), new Vector2(15, 0), new Vector2(115, 40), 25, _font);
                ResourceCounts[ResourceIndex].alignment = TextAlignmentOptions.MidlineLeft;
            }
            GameObject Vitals = new GameObject("BattleVitals", typeof(RectTransform), typeof(ClientCombatHud));
            Vitals.transform.SetParent(_battleArea, false);
            _combatHud = Vitals.GetComponent<ClientCombatHud>();
            _combatHud.Initialize(_battleArea, _font, PanelSprite);
            _buildStatus = CombatUIFactory.Text(_mapArea, "BuildStatus", "", new Vector2(0.5f, 0), new Vector2(0, 90), new Vector2(620, 50), 20, _font);
            BuildCost = ResourceCostView.Create(_buildStatus.transform, _font, Vector2.zero, new Vector2(400, 32));
            BuildCost.gameObject.SetActive(false);
            _gatherButton = MakeButton(_battleArea, "Gather", "Добывать", new Vector2(1, 0), new Vector2(-22, 125), new Vector2(120, _presentation.ButtonSize), () => _presenter.SubmitGathering());
            var Dock = new GameObject("WeaponDock", typeof(RectTransform), typeof(ClientWeaponDockGraphic));
            Dock.transform.SetParent(_split, false);
            _weaponDockGraphic = Dock.GetComponent<ClientWeaponDockGraphic>();
            _weaponDockGraphic.Initialize(_font, _presentation.MaximumViewportAspect);
            ConfigureSurface(Dock.transform, PointerRegion.Wheel);
            _weaponDock = Dock.GetComponent<ClientPointerSurface>();
            GameObject Selection = new GameObject("WeaponSelection", typeof(RectTransform), typeof(ClientWeaponWheel));
            Selection.transform.SetParent(_split, false);
            CombatUIFactory.Stretch((RectTransform)Selection.transform, Vector2.zero, Vector2.zero);
            _wheel = Selection.GetComponent<ClientWeaponWheel>();
            _wheel.Initialize(_font);
            Dock.transform.SetAsLastSibling();
            Transform Palette = _mapArea.Find("BuildingPalette");
            UnityEngine.UI.ScrollRect Scroll = Palette.GetComponent<UnityEngine.UI.ScrollRect>();
            _palette = Scroll.content;
            BuildingScroll = Scroll;
            RectTransform PaletteBounds = (RectTransform)Palette;
            PaletteBounds.anchorMin = Vector2.zero;
            PaletteBounds.anchorMax = Vector2.right;
            PaletteBounds.pivot = new Vector2(0.5f, 0);
            PaletteBounds.offsetMin = new Vector2(10, 8);
            PaletteBounds.offsetMax = new Vector2(-10, 8 + _presentation.BuildingPaletteHeight);
            var PaletteBackground = Palette.gameObject.AddComponent<UnityEngine.UI.Image>();
            PaletteBackground.color = new Color(0.05f, 0.08f, 0.07f, 0.97f);
            PaletteBackground.sprite = PanelSprite;
            PaletteBackground.type = UnityEngine.UI.Image.Type.Sliced;
            CombatUIFactory.Stretch(Scroll.viewport, new Vector2(48, 0), new Vector2(-48, 0));
            _palette.anchorMin = Vector2.zero;
            _palette.anchorMax = Vector2.up;
            _palette.pivot = Vector2.zero;
            _palette.anchoredPosition = Vector2.zero;
            _palette.sizeDelta = new Vector2(0, 0);
            BuildingLayout = _palette.gameObject.AddComponent<UnityEngine.UI.HorizontalLayoutGroup>();
            BuildingLayout.childAlignment = TextAnchor.LowerLeft;
            BuildingLayout.padding = new RectOffset(0, 0, 8, 8);
            BuildingLayout.spacing = _presentation.BuildingCardSpacing;
            BuildingLayout.childControlWidth = false;
            BuildingLayout.childControlHeight = false;
            BuildingLayout.childForceExpandWidth = false;
            BuildingLayout.childForceExpandHeight = false;
            Scroll.horizontal = true;
            Scroll.vertical = false;
            Scroll.inertia = false;
            Scroll.movementType = UnityEngine.UI.ScrollRect.MovementType.Clamped;
            PreviousBuildings = MakeButton(Palette, "PreviousBuildings", "‹", new Vector2(0, 0.5f), new Vector2(4, 0), new Vector2(40, _presentation.BuildingPaletteHeight - 16), () => MoveBuildingPalette(-1));
            NextBuildings = MakeButton(Palette, "NextBuildings", "›", new Vector2(1, 0.5f), new Vector2(-4, 0), new Vector2(40, _presentation.BuildingPaletteHeight - 16), () => MoveBuildingPalette(1));
            var PaletteEvents = Palette.gameObject.AddComponent<UnityEngine.EventSystems.EventTrigger>();
            var EndDrag = new UnityEngine.EventSystems.EventTrigger.Entry { eventID = UnityEngine.EventSystems.EventTriggerType.EndDrag };
            EndDrag.callback.AddListener(Event => SnapBuildingPalette());
            PaletteEvents.triggers.Add(EndDrag);
            Scroll.onValueChanged.AddListener(Position => UpdateBuildingPaletteArrows());
            CreateStructureActions();
            RectTransform SettingsContent = _settings.Find("SettingsWindow/ScrollView").GetComponent<UnityEngine.UI.ScrollRect>().content;
            SettingsContent.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, 260);
            CombatUIFactory.Text(SettingsContent, "MapSizeTitle", "Доля экрана для карты", new Vector2(0.5f, 1), new Vector2(0, -20), new Vector2(500, 55), 24, _font);
            MakeButton(SettingsContent, "SmallMap", "20%", new Vector2(0.5f, 1), new Vector2(-170, -85), new Vector2(150, 70), () => MapRatioRequested?.Invoke(0.2f));
            MakeButton(SettingsContent, "MediumMap", "30%", new Vector2(0.5f, 1), new Vector2(0, -85), new Vector2(150, 70), () => MapRatioRequested?.Invoke(0.3f));
            MakeButton(SettingsContent, "LargeMap", "50%", new Vector2(0.5f, 1), new Vector2(170, -85), new Vector2(150, 70), () => MapRatioRequested?.Invoke(0.5f));
            CombatUIFactory.Text(SettingsContent, "MapSizeHint", "Размер карты также меняется перетаскиванием границы экранов.", new Vector2(0.5f, 1), new Vector2(0, -175), new Vector2(500, 80), 22, _font);
            _result = (RectTransform)CombatUIFactory.Panel(_battleArea, "MatchResult", new Color(0.05f, 0.07f, 0.08f, 0.98f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(580, 250)).transform;
            _resultText = CombatUIFactory.Text(_result, "ResultText", "", new Vector2(0.5f, 1), new Vector2(0, -20), new Vector2(540, 120), 24, _font);
            _resultText.alignment = TextAlignmentOptions.Center;
            _returnButton = MakeButton(_result, "ReturnToLobby", "В профиль", new Vector2(0.5f, 0), new Vector2(0, 20), new Vector2(250, 65), () => UnityEngine.Object.FindAnyObjectByType<VaticanDefendor.Bootstrap.OnlineStartup>().ReturnToLobby());
#if UNITY_EDITOR
            var Startup = UnityEngine.Object.FindAnyObjectByType<VaticanDefendor.Bootstrap.OnlineStartup>();
            if (Startup != null && Startup.IsEditorLocal)
            {
                _returnButton.GetComponentInChildren<TMP_Text>().text = "Новый матч";
                transform.Find("PhoneSafeArea/PauseOverlay/Paused/MainMenu").GetComponentInChildren<TMP_Text>().text = "Новый матч";
            }
#endif
            _result.gameObject.SetActive(false);
            _width = Screen.width;
            _height = Screen.height;
        }

        private void BuildPalette()
        {
            ClientConstructionOfferModel[] Offers = _model.Construction.Offers;
            if (Offers == null || Offers.Length == _offerCount)
            {
                return;
            }
            _offerCount = Offers.Length;
            foreach (UnityEngine.UI.Button Button in _buildingButtons.Values)
            {
                Destroy(Button.gameObject);
            }
            _buildingButtons.Clear();
            for (int Index = 0; Index < Offers.Length; Index++)
            {
                ClientConstructionOfferModel Offer = Offers[Index];
                UnityEngine.UI.Button Button = MakeButton(_palette, Offer.Kind.ToString(), BuildingName(Offer.Kind), new Vector2(0, 0.5f), new Vector2(Index * 150, 0), new Vector2(140, 72), () =>
                {
                    if (_selectedKind == Offer.Kind)
                    {
                        _selectedKind = 0;
                    }
                    else
                    {
                        _selectedKind = Offer.Kind;
                    }
                    CancelBuildGesture();
                    RefreshBuildingSelection();
                });
                TMP_Text Caption = Button.transform.Find("Label").GetComponent<TMP_Text>();
                Caption.rectTransform.anchoredPosition = new Vector2(0, 15);
                Caption.rectTransform.sizeDelta = new Vector2(136, 30);
                Caption.enableAutoSizing = true;
                Caption.fontSizeMin = 15;
                Caption.fontSizeMax = 20;
                Caption.textWrappingMode = TextWrappingModes.NoWrap;
                Caption.overflowMode = TextOverflowModes.Ellipsis;
                ResourceCostView Cost = ResourceCostView.Create(Button.transform, _font, new Vector2(0, -17), new Vector2(130, 26));
                Cost.SetAmounts(new ResourceAmounts(Offer.WoodPrice, Offer.StonePrice, Offer.IronPrice));
                _buildingButtons.Add(Offer.Kind, Button);
            }
            LayoutBuildingPalette();
            RefreshBuildingSelection();
        }

        private void LayoutBuildingPalette()
        {
            float ViewportWidth = BuildingScroll.viewport.rect.width;
            if (ViewportWidth <= 0)
            {
                return;
            }
            int VisibleCards = Mathf.Min(_presentation.BuildingCardsVisible, Mathf.Max(1, _offerCount));
            float Spacing = _presentation.BuildingCardSpacing;
            float CardWidth = (ViewportWidth - Spacing * (VisibleCards - 1)) / VisibleCards;
            float FirstCard = 0;
            if (BuildingCardStride > 0)
            {
                FirstCard = Mathf.Round(-_palette.anchoredPosition.x / BuildingCardStride);
            }
            BuildingCardStride = CardWidth + Spacing;
            float ContentWidth = Mathf.Max(ViewportWidth, _offerCount * BuildingCardStride - Spacing);
            _palette.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, ContentWidth);
            foreach (ClientConstructionOfferModel Offer in _model.Construction.Offers ?? Array.Empty<ClientConstructionOfferModel>())
            {
                UnityEngine.UI.Button Button = _buildingButtons[Offer.Kind];
                RectTransform Bounds = (RectTransform)Button.transform;
                Bounds.sizeDelta = new Vector2(CardWidth, _presentation.BuildingPaletteHeight - 16);
                TMP_Text Caption = Button.transform.Find("Label").GetComponent<TMP_Text>();
                Caption.rectTransform.sizeDelta = new Vector2(CardWidth - 12, 30);
                ResourceCostView Cost = Button.GetComponentInChildren<ResourceCostView>();
                ((RectTransform)Cost.transform).sizeDelta = new Vector2(CardWidth - 16, 26);
                Cost.RefreshLayout();
            }
            UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(_palette);
            _palette.anchoredPosition = new Vector2(-Mathf.Clamp(FirstCard * BuildingCardStride, 0, ContentWidth - ViewportWidth), 0);
            UpdateBuildingPaletteArrows();
        }

        private void MoveBuildingPalette(int Direction)
        {
            BuildingScroll.StopMovement();
            float Maximum = Mathf.Max(0, _palette.rect.width - BuildingScroll.viewport.rect.width);
            float Position = -_palette.anchoredPosition.x + Direction * BuildingCardStride * _presentation.BuildingCardsVisible;
            _palette.anchoredPosition = new Vector2(-Mathf.Clamp(Position, 0, Maximum), 0);
            UpdateBuildingPaletteArrows();
        }

        private void SnapBuildingPalette()
        {
            if (BuildingCardStride <= 0)
            {
                return;
            }
            BuildingScroll.StopMovement();
            float Maximum = Mathf.Max(0, _palette.rect.width - BuildingScroll.viewport.rect.width);
            float Position = Mathf.Round(-_palette.anchoredPosition.x / BuildingCardStride) * BuildingCardStride;
            _palette.anchoredPosition = new Vector2(-Mathf.Clamp(Position, 0, Maximum), 0);
            UpdateBuildingPaletteArrows();
        }

        private void UpdateBuildingPaletteArrows()
        {
            float Maximum = Mathf.Max(0, _palette.rect.width - BuildingScroll.viewport.rect.width);
            PreviousBuildings.interactable = -_palette.anchoredPosition.x > 0.5f;
            NextBuildings.interactable = -_palette.anchoredPosition.x < Maximum - 0.5f;
        }

        private void RefreshBuildingSelection()
        {
            foreach (var Pair in _buildingButtons)
            {
                Color Tint = new Color(0.12f, 0.16f, 0.18f);
                if (Pair.Key == _selectedKind)
                {
                    Tint = new Color(0.24f, 0.39f, 0.31f);
                }
                Pair.Value.image.color = Tint;
            }
        }

        private void CreateStructureActions()
        {
            _structureActions = (RectTransform)CombatUIFactory.Panel(_battleArea, "StructureActions",
                new Color(0.06f, 0.08f, 0.1f, 0.93f), new Vector2(1, 0.58f), new Vector2(-14, 0), new Vector2(176, 240)).transform;
            _structureActions.GetComponent<UnityEngine.UI.Image>().raycastTarget = false;
            _structureLabel = CombatUIFactory.Text(_structureActions, "Structure", "", new Vector2(0.5f, 1),
                new Vector2(0, -6), new Vector2(154, 44), 17, _font);
            _structureLabel.alignment = TextAlignmentOptions.Center;
            _upgradeButton = MakeStructureAction(ConstructionOperation.Upgrade, "↑", "Улучшить");
            _forgeWeaponButton = MakeStructureAction(ConstructionOperation.ForgeWeapon, "↑", "Оружие");
            _forgeHealthButton = MakeStructureAction(ConstructionOperation.ForgeHealth, "+", "Здоровье");
            _sellButton = MakeStructureAction(ConstructionOperation.Sell, "−", "Продать");
            UpgradeCost = AddActionCost(_upgradeButton);
            ForgeWeaponCost = AddActionCost(_forgeWeaponButton);
            ForgeHealthCost = AddActionCost(_forgeHealthButton);
            _structureActions.gameObject.SetActive(false);
            MakeButton(_mapArea, "Artillery", "Требушет", new Vector2(1, 1), new Vector2(-12, -65), new Vector2(130, 55), () => { _artilleryArmed = true; _selectedKind = 0; });
        }

        private UnityEngine.UI.Button MakeStructureAction(ConstructionOperation Operation, string Icon, string Caption)
        {
            UnityEngine.UI.Button Button = MakeButton(_structureActions, Operation.ToString(), Caption,
                new Vector2(0.5f, 1), Vector2.zero, new Vector2(162, 76),
                () => _presenter.SubmitStructureAction(Operation, _selectedStructure));
            TMP_Text Symbol = CombatUIFactory.Text(Button.transform, "Icon", Icon, new Vector2(0, 0.5f),
                new Vector2(7, 0), new Vector2(21, 34), 23, _font);
            Symbol.alignment = TextAlignmentOptions.Center;
            Symbol.color = new Color(0.96f, 0.77f, 0.41f);
            TMP_Text Label = Button.transform.Find("Label").GetComponent<TMP_Text>();
            Label.rectTransform.anchoredPosition = new Vector2(10, 0);
            Label.rectTransform.sizeDelta = new Vector2(120, 28);
            return Button;
        }

        private void RenderStructureActions()
        {
            ClientStructureSnapshot Selected = default;
            ConstructionEconomyTuning Tuning = _model.Gameplay.Construction;
            if (_model.Screen == ClientShellScreen.Game && _model.HasHero && _model.Combat.HeroHealth > 0 &&
                _model.Run.Paused == 0 && _model.Run.Phase == VaticanDefendor.RunRules.RunPhase.Active)
            {
                float Best = float.PositiveInfinity;
                var Hero = new Unity.Mathematics.float2(_model.HeroPosition.x, _model.HeroPosition.z);
                foreach (ClientStructureSnapshot Structure in _model.Construction.Structures ?? Array.Empty<ClientStructureSnapshot>())
                {
                    if (Structure.StructureId == 0 || Structure.Health <= 0 || !StructureActionRules.CanInteract(Structure.Kind))
                    {
                        continue;
                    }
                    var Position = new Unity.Mathematics.float2(Structure.Position.x, Structure.Position.z);
                    if (!StructureActionRules.InInteractionRange(Hero, Position, Structure.HalfExtent, Tuning))
                    {
                        continue;
                    }
                    float Distance = Unity.Mathematics.math.length(Position - Hero) - Structure.HalfExtent;
                    if (Distance < Best || Distance == Best && Structure.StructureId < Selected.StructureId)
                    {
                        Best = Distance;
                        Selected = Structure;
                    }
                }
            }
            bool bChangedStructure = _selectedStructure != Selected.StructureId;
            _selectedStructure = Selected.StructureId;
            _structureActions.gameObject.SetActive(_selectedStructure != 0);
            if (_selectedStructure == 0)
            {
                return;
            }
            bool bUpgrade = StructureActionRules.CanUpgrade(Selected.Kind) &&
                Selected.Level < StructureActionRules.MaximumLevel(Selected.Kind, Tuning) && Selected.UpgradeSecondsRemaining == 0;
            bool bForge = Selected.Kind == StructureKind.Forge;
            bool bSell = Selected.Kind != StructureKind.Church;
            ResourceAmounts Upgrade = StructureActionRules.Cost(ConstructionOperation.Upgrade, Selected.Kind, Tuning);
            _structureLabel.text = BuildingName(Selected.Kind);
            if (StructureActionRules.CanUpgrade(Selected.Kind))
            {
                _structureLabel.text += " " + RomanLevel(Selected.Level);
            }
            if (Selected.UpgradeSecondsRemaining > 0)
            {
                _structureLabel.text += "\n" + Selected.UpgradeSecondsRemaining + " с";
            }
            ResourceAmounts Weapon = StructureActionRules.Cost(ConstructionOperation.ForgeWeapon, Selected.Kind, Tuning);
            ResourceAmounts Health = StructureActionRules.Cost(ConstructionOperation.ForgeHealth, Selected.Kind, Tuning);
            UpgradeCost.SetAmounts(Upgrade);
            ForgeWeaponCost.SetAmounts(Weapon);
            ForgeHealthCost.SetAmounts(Health);
            ResourceAmounts Resources = _model.Gameplay.Resources;
            bool bLayoutChanged = bChangedStructure || _upgradeButton.gameObject.activeSelf != bUpgrade ||
                _forgeWeaponButton.gameObject.activeSelf != bForge || _sellButton.gameObject.activeSelf != bSell;
            _upgradeButton.interactable = Resources.Covers(Upgrade);
            _forgeWeaponButton.interactable = Resources.Covers(Weapon);
            _forgeHealthButton.interactable = Resources.Covers(Health);
            if (!bLayoutChanged)
            {
                return;
            }
            float Top = 50;
            PositionStructureAction(_upgradeButton, bUpgrade, ref Top);
            PositionStructureAction(_forgeWeaponButton, bForge, ref Top);
            PositionStructureAction(_forgeHealthButton, bForge, ref Top);
            PositionStructureAction(_sellButton, bSell, ref Top);
            _structureActions.sizeDelta = new Vector2(_structureActions.sizeDelta.x, Top + 3);
        }

        private static void PositionStructureAction(UnityEngine.UI.Button Button, bool bVisible, ref float Top)
        {
            Button.gameObject.SetActive(bVisible);
            if (bVisible)
            {
                ((RectTransform)Button.transform).anchoredPosition = new Vector2(0, -Top);
                Top += ((RectTransform)Button.transform).sizeDelta.y + 4;
            }
        }

        public void DragDivider(Vector2 ScreenPoint)
        {
            if (RectTransformUtility.ScreenPointToLocalPointInRectangle(_split, ScreenPoint, null, out Vector2 Point))
            {
                float Fraction = 1 - Mathf.InverseLerp(_split.rect.yMin, _split.rect.yMax, Point.y);
                MapRatioRequested?.Invoke(Mathf.Clamp(Fraction, 0.2f, 0.5f));
            }
        }

        private void UpdatePreview()
        {
            if (_buildPointer == int.MinValue || !TryOffer(out ClientConstructionOfferModel Offer))
            {
                return;
            }
            List<Unity.Mathematics.float2> Cells = ConstructionGeometry.Positions(_buildStart, _buildEnd, Offer.HalfExtentMeters, _selectedKind == StructureKind.Wall);
            float Rotation = ConstructionGeometry.Rotation(_buildStart, _buildEnd);
            Unity.Mathematics.float2 HalfExtents = ConstructionGeometry.HalfExtents(_selectedKind, Offer.HalfExtentMeters);
            RiverMapConfiguration Surface = _mapSurface.Surface;
            ResourceAmounts Cost = new ResourceAmounts(Offer.WoodPrice, Offer.StonePrice, Offer.IronPrice) * Cells.Count;
            bool bAvailable = _model.Gameplay.Resources.Covers(Cost);
            _buildStatus.text = string.Empty;
            BuildCost.gameObject.SetActive(true);
            BuildCost.SetAmounts(Cost);
            while (_previews.Count < Cells.Count)
            {
                GameObject Preview = GameObject.CreatePrimitive(PrimitiveType.Cube);
                Preview.name = "PlacementPreview";
                Destroy(Preview.GetComponent<Collider>());
                _previews.Add(Preview);
            }
            for (int Index = 0; Index < _previews.Count; Index++)
            {
                GameObject Preview = _previews[Index];
                Preview.SetActive(Index < Cells.Count);
                if (Index >= Cells.Count)
                {
                    continue;
                }
                float Height = 0;
                bool bSupported = true;
                if (Surface != null)
                {
                    Height = Surface.Height(Cells[Index].x, Cells[Index].y);
                    bSupported = Surface.CanSupport(Cells[Index].x, Cells[Index].y, HalfExtents.x, HalfExtents.y, Rotation);
                }
                foreach (ClientStructureSnapshot Structure in _model.Construction.Structures ?? Array.Empty<ClientStructureSnapshot>())
                {
                    if (Structure.Health > 0 && Structure.Kind != StructureKind.Artillery &&
                        ConstructionGeometry.Overlaps(Cells[Index], HalfExtents, Rotation,
                            new Unity.Mathematics.float2(Structure.Position.x, Structure.Position.z),
                            ConstructionGeometry.HalfExtents(Structure.Kind, Structure.HalfExtent), Structure.RotationRadians))
                    {
                        bSupported = false;
                    }
                }
                Preview.transform.position = new Vector3(Cells[Index].x, Height + 0.2f, Cells[Index].y);
                Preview.transform.rotation = Quaternion.Euler(0, Rotation * Mathf.Rad2Deg, 0);
                Preview.transform.localScale = new Vector3(HalfExtents.x * 2, 0.4f, HalfExtents.y * 2);
                Color Tint = Color.green;
                if (!bAvailable || !bSupported)
                {
                    Tint = Color.red;
                }
                Preview.GetComponent<Renderer>().material.color = Tint;
            }
        }

        private void ApplyMapRatio()
        {
            _safeArea = Screen.safeArea;
            Rect Bounds = _safeArea;
            float MaximumWidth = Bounds.height * _presentation.MaximumViewportAspect;
            if (Bounds.width > MaximumWidth)
            {
                Bounds.x += (Bounds.width - MaximumWidth) * 0.5f;
                Bounds.width = MaximumWidth;
            }
            RectTransform SafeArea = (RectTransform)_split.parent;
            SafeArea.anchorMin = new Vector2(Bounds.xMin / Screen.width, Bounds.yMin / Screen.height);
            SafeArea.anchorMax = new Vector2(Bounds.xMax / Screen.width, Bounds.yMax / Screen.height);
            SafeArea.offsetMin = SafeArea.offsetMax = Vector2.zero;
            _mapArea.anchorMin = new Vector2(0, 1 - _model.MapFraction);
            _mapArea.anchorMax = Vector2.one;
            _battleArea.anchorMin = Vector2.zero;
            _battleArea.anchorMax = new Vector2(1, 1 - _model.MapFraction);
            _mapArea.offsetMin = _mapArea.offsetMax = Vector2.zero;
            _battleArea.offsetMin = _battleArea.offsetMax = Vector2.zero;
            _divider.anchorMin = new Vector2(0, 1 - _model.MapFraction);
            _divider.anchorMax = new Vector2(1, 1 - _model.MapFraction);
            _width = Screen.width;
            _height = Screen.height;
            _combatPointer.CancelInput();
            _weaponDock.CancelInput();
            ConfigureSurface(_combatPointer.transform, PointerRegion.Combat);
            Canvas.ForceUpdateCanvases();
            LayoutBuildingPalette();
            _mapSurface.Refresh();
            _battleSurface.Refresh();
        }

        private void ConfigureSurface(Transform Target, PointerRegion Region)
        {
            ClientPointerSurface Surface = Target.GetComponent<ClientPointerSurface>();
            if (Surface == null)
            {
                Surface = Target.gameObject.AddComponent<ClientPointerSurface>();
            }
            float Scale = GetComponent<Canvas>().scaleFactor;
            Surface.Configure(_pointers, Region, this, _presentation.JoystickRadius * Scale);
        }

        private void Bind(string Path, UnityEngine.Events.UnityAction Action)
        {
            Transform Target = transform.Find(Path);
            if (Target == null)
            {
                return;
            }
            UnityEngine.UI.Button Button = Target.GetComponent<UnityEngine.UI.Button>();
            Button.onClick.AddListener(Action);
            _bindings.Add((Button, Action));
        }

        private UnityEngine.UI.Button MakeButton(Transform Parent, string Name, string Caption, Vector2 Anchor, Vector2 Position, Vector2 Size, UnityEngine.Events.UnityAction Action)
        {
            UnityEngine.UI.Button Button = CombatUIFactory.Button(Parent, Name, Caption, Anchor, Position, Size, _font);
            Button.onClick.AddListener(Action);
            _bindings.Add((Button, Action));
            return Button;
        }

        private static string ConstructionMessage(ConstructionOutcome Outcome)
        {
            switch (Outcome)
            {
                case ConstructionOutcome.Built: return "Выполнено";
                case ConstructionOutcome.InsufficientResources: return "Недостаточно ресурсов";
                case ConstructionOutcome.HeroTooClose: return "Здесь находится герой";
                case ConstructionOutcome.HeroTooFar: return "Вне зоны действия";
                case ConstructionOutcome.UpgradeInProgress: return "Улучшение уже идёт";
                case ConstructionOutcome.Occupied: return "Место занято";
                case ConstructionOutcome.Blocked: return "Место недоступно";
                case ConstructionOutcome.OutOfBounds: return "Выберите место внутри карты";
                case ConstructionOutcome.CapacityReached: return "Достигнут предел улучшений или построек";
                case ConstructionOutcome.RateLimited: return "Повторите действие чуть позже";
                default: return "Действие недоступно";
            }
        }

        private ResourceCostView AddActionCost(UnityEngine.UI.Button Button)
        {
            TMP_Text Caption = Button.transform.Find("Label").GetComponent<TMP_Text>();
            Caption.rectTransform.anchoredPosition = new Vector2(10, 12);
            return ResourceCostView.Create(Button.transform, _font, new Vector2(10, -15), new Vector2(115, 24));
        }

        internal static string WeaponName(int Model)
        {
            switch (Model) { case 1: return "Копьё"; case 2: return "Топор"; default: return "Меч"; }
        }

        internal static Color RarityColor(WeaponRarity Rarity)
        {
            switch (Rarity)
            {
                case WeaponRarity.Rare: return new Color(0.12f, 0.3f, 0.55f);
                case WeaponRarity.Epic: return new Color(0.35f, 0.15f, 0.5f);
                case WeaponRarity.Legendary: return new Color(0.6f, 0.35f, 0.1f);
                default: return new Color(0.2f, 0.25f, 0.3f);
            }
        }

        private static string BuildingName(StructureKind Kind)
        {
            switch (Kind)
            {
                case StructureKind.Wall: return "Стена";
                case StructureKind.Tower: return "Башня";
                case StructureKind.Sawmill: return "Лесопилка";
                case StructureKind.Quarry: return "Каменоломня";
                case StructureKind.IronMine: return "Шахта";
                case StructureKind.Forge: return "Кузница";
                case StructureKind.HealingShrine: return "Лечение";
                case StructureKind.Church: return "Церковь";
                default: return Kind.ToString();
            }
        }

        private void OnApplicationPause(bool suspended)
        {
            if (_presenter == null) return;
            if (_combatPointer != null) _combatPointer.CancelInput();
            if (_weaponDock != null) _weaponDock.CancelInput();
            CancelBuildGesture();
            _presenter.SetSuspended(suspended);
        }

        private void OnApplicationFocus(bool bFocused)
        {
            if (!bFocused && _presenter != null)
            {
                _combatPointer.CancelInput();
                _weaponDock.CancelInput();
                SetCharging(false);
            }
        }

        private void OnDestroy()
        {
            _worldPresentation?.ClearOcclusionTarget();
            _presenter?.Dispose();
            _presenter = null;
            foreach (var Binding in _bindings)
            {
                if (Binding.Button != null)
                {
                    Binding.Button.onClick.RemoveListener(Binding.Action);
                }
            }
            foreach (GameObject Preview in _previews)
            {
                if (Preview != null)
                {
                    Material Material = Preview.GetComponent<Renderer>().sharedMaterial;
                    Destroy(Material);
                    Destroy(Preview);
                }
            }
            _pointers.Clear();
        }
    }
}
#endif
