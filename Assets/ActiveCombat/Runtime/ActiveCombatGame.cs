using System;
using System.Collections.Generic;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.InputSystem;

public partial class ActiveCombatGame : MonoBehaviour
{
    public GameRoot Root;
    public CombatBalance Balance;
    public GameObject KnightPrefab;
    public GameObject[] WeaponPrefabs;
    public GameObject[] EnemyPrefabs;
    public GameObject[] ArmoredEnemyPrefabs;
    public GameObject[] StructurePrefabs;
    public GameObject PickupPrefab;
    public GameObject GoldBagPrefab;
    public Transform DynamicRoot;
    public Transform SceneryRoot;
    public Camera WorldCamera;
    public CombatHUD Hud;
    public CombatSession Session;
    public Material EffectMaterial;
    public CombatBalance Rules { get; private set; }
    public bool bTerrainPlacementArmed;
    public int StructureIndex;
    public bool bTrebuchet;
    public bool bSellBuildings;
    public CombatComboView Combo { get; private set; }
    public CombatSnapshotCollection<CombatBotState, EnemyState> Enemies { get; private set; }
    public CombatSnapshotCollection<CombatBuildingState, StructureState> Structures { get; private set; }
    public CombatSnapshotCollection<CombatPickupState, PickupState> Pickups { get; private set; }
    public CombatSnapshotCollection<CombatProjectileState, ArrowState> Arrows { get; private set; }
    public CombatSnapshotCollection<CombatBombState, BombState> Bombs { get; private set; }
    public CombatPlayerState Player => Root.Simulation.ReadModel.Player;
    public CombatMatchState Match => Root.Simulation.ReadModel.Match;
    public bool bInitialized => Rules != null && Root.Simulation.bAcceptsCommands;
    private int CommandIdentity;
    private string InterfaceError;
    private float InterfaceErrorRemaining;
    private float MapRemaining;
    public CombatWeaponView CurrentWeapon => new CombatWeaponView(this, WeaponIndex);
    public float MaximumHealth => Rules.MaximumHealth + HealthUpgrades * 25;
    public int Kills => Player.Kills;
    public int Rank => Player.Rank;
    public int Experience => Player.Experience;
    public int WeaponIndex => Player.WeaponIndex;
    public int AttackUpgrades => Player.AttackUpgrades;
    public int HealthUpgrades => Player.HealthUpgrades;
    public int WaveNumber => Match.WaveNumber;
    public float WaveCountdown => Match.WaveCountdown;
    public int NavigationRevision => Match.NavigationRevision;
    public float Charge => Player.Charge;
    public float DamageBonusRemaining => Player.DamageBonusRemaining;
    public float DamageBonusPercent => Player.DamageBonusPercent;
    public float Shield => Player.Shield;
    public float DefenseRemaining => Player.DefenseRemaining;
    public float SimulatedSeconds => Match.ElapsedSeconds;
    public float HitFeedback => Player.HitFlash / 0.18f;
    public float CameraImpactStrength => Player.CameraShake;
    public bool bGestureActive => Player.bGestureActive != 0;
    public bool bAttackSelected => Player.bAttackSelected != 0;
    public Vector2 GestureStart => Player.GestureStart;
    public Vector2 GestureOffset => Player.GestureOffset;
    public bool bDefeated => Match.bDefeated != 0;
    public bool bAttacking => Player.bMovementAttack != 0 || Player.CastRemaining > 0;
    public bool bSimulationPaused => Match.bMainMenu != 0 || Match.bTutorialBlocked != 0 || bPaused || bSettingsOpen || bDefeated;
    public int EnemyLimit => Rules.InitialEnemies + Mathf.Max(0, WaveNumber - 1) * Rules.EnemiesPerWave;
    public float ArenaBound => Rules.GridSize * Rules.CellSize / 2 + Rules.PlayableBorder - 0.8f;
    public int SpecialSacrificeCost => Combo.Cost;
    public bool bSpecialReady => Combo.bReady;
    public float SpecialChargeSeconds => Rules.ChargeSeconds * GetProgressionMultiplier(ProgressionBonus.ChargeDuration);
    public string Status
    {
        get
        {
            if (bAttacking)
            {
                return "Атака";
            }
            return "Ожидание";
        }
    }
    public string ErrorMessage
    {
        get
        {
            if (InterfaceErrorRemaining > 0)
            {
                return InterfaceError;
            }
            return ErrorCaption(Match.ErrorCode);
        }
    }
    public float Health
    {
        get { return Player.Health; }
        set { Dispatch(new CombatCommand { Kind = CombatCommandKind.SetPlayerValue, Index = 0, Value = value }, true); }
    }
    public float Stamina
    {
        get { return Player.Stamina; }
        set { Dispatch(new CombatCommand { Kind = CombatCommandKind.SetPlayerValue, Index = 1, Value = value }, true); }
    }
    public Vector3 PlayerPosition
    {
        get { return Player.Position; }
        set { Dispatch(new CombatCommand { Kind = CombatCommandKind.SetPlayerValue, Index = 2, Position = value }, true); }
    }
    public Vector3 Facing
    {
        get { return Player.Facing; }
        set { Dispatch(new CombatCommand { Kind = CombatCommandKind.SetPlayerValue, Index = 3, Direction = value }, true); }
    }
    public int Gold
    {
        get { return Player.Gold; }
        set { Dispatch(new CombatCommand { Kind = CombatCommandKind.SetPlayerValue, Index = 4, Value = value }, true); }
    }
    public bool bPaused
    {
        get { return Match.bPaused != 0; }
        set { SetSessionFlag(3, value); }
    }
    public bool bSettingsOpen
    {
        get { return Match.bSettings != 0; }
        set { SetSessionFlag(4, value); }
    }
    public bool bMapExpanded
    {
        get { return Match.bMapOpen != 0; }
        set { SetSessionFlag(5, value); }
    }
    public float SlowMotionRemaining
    {
        get { return Match.SlowMotionRemaining; }
        set
        {
            Dispatch(new CombatCommand
            {
                Kind = CombatCommandKind.SetMapTime, Value = Rules.MobileView.SlowMotionFactor,
                ExtraValue = Rules.MobileView.SlowMotionDuration, Parameters = new float4(value, 0, 0, 0),
                Index = (int)Rules.MobileView.TimeMode
            }, true);
        }
    }
    public int SpecialSacrifices
    {
        get { return Mathf.FloorToInt(Combo.Points); }
        set { Combo.Restore(value, Combo.Uses); }
    }
    public int SpecialUses
    {
        get { return Combo.Uses; }
        set { Combo.Restore(Combo.Points, value); }
    }

    public void InitializeFacade()
    {
        Rules = Instantiate(Balance);
        Combo = new CombatComboView(this);
        Application.runInBackground = true;
        Application.targetFrameRate = 60;
    }

    public void ConnectReadModel()
    {
        CombatReadModelSystem ReadModel = Root.Simulation.ReadModel;
        Enemies = new CombatSnapshotCollection<CombatBotState, EnemyState>(ReadModel.Enemies, Entity => new EnemyState(this, Entity), ClearEnemies, State => State.Health > 0);
        Structures = new CombatSnapshotCollection<CombatBuildingState, StructureState>(ReadModel.Buildings, Entity => new StructureState(this, Entity));
        Pickups = new CombatSnapshotCollection<CombatPickupState, PickupState>(ReadModel.Pickups, Entity => new PickupState(this, Entity), null, State => State.Kind == 0);
        Arrows = new CombatSnapshotCollection<CombatProjectileState, ArrowState>(ReadModel.Projectiles, Entity => new ArrowState(this, Entity));
        Bombs = new CombatSnapshotCollection<CombatBombState, BombState>(ReadModel.Bombs, Entity => new BombState(this, Entity));
    }

    public void RefreshSnapshots()
    {
        Enemies.Refresh();
        Structures.Refresh();
        Pickups.Refresh();
        Arrows.Refresh();
        Bombs.Refresh();
        foreach (CombatUiEvent Event in Root.Simulation.ReadModel.UiEvents)
        {
            Color Color = Color.white;
            if (Event.Kind == 1)
            {
                Color = new Color(1, 0.3f, 0.35f);
            }
            else if (Event.Kind == 2)
            {
                Color = new Color(0.3f, 1, 0.5f);
            }
            Hud.FloatText(Event.Position, Mathf.Abs(Event.Amount).ToString("0"), Color);
        }
    }

    public void RefreshInterface(float DeltaSeconds)
    {
        InterfaceErrorRemaining = Mathf.Max(0, InterfaceErrorRemaining - DeltaSeconds);
        Session.Tutorial.Tick();
        Hud.Refresh(this, DeltaSeconds);
        Session.RefreshUI();
        Hud.Mobile.UpdateCamera(DeltaSeconds);
    }

    public void Dispatch(CombatCommand Command, bool bFlush = false)
    {
        if (Root.Simulation == null || !Root.Simulation.bAcceptsCommands)
        {
            return;
        }
        Command.Identity = ++CommandIdentity;
        Root.Simulation.Enqueue(Command);
        if (bFlush)
        {
            FlushCommands();
        }
    }

    public void FlushCommands()
    {
        Root.Simulation.FlushCommands();
        RefreshSnapshots();
    }

    private int SessionFlags()
    {
        return Match.bMainMenu | Match.bTutorial << 1 | Match.bTutorialBlocked << 2 | Match.bPaused << 3
            | Match.bSettings << 4 | Match.bMapOpen << 5 | Match.bWeaponWheelOpen << 6;
    }

    private void SetSessionFlag(int Bit, bool bValue)
    {
        if (!bInitialized)
        {
            return;
        }
        int Flags = SessionFlags() & ~(1 << Bit);
        if (bValue)
        {
            Flags |= 1 << Bit;
        }
        if (Flags != SessionFlags())
        {
            Dispatch(new CombatCommand { Kind = CombatCommandKind.SetSession, Flags = Flags }, true);
        }
    }

    public void SynchronizeSession()
    {
        SetSessionFlag(0, Session.bMainMenuOpen);
        SetSessionFlag(1, Session.Tutorial.bActive);
        SetSessionFlag(2, Session.Tutorial.bActive && (Session.Tutorial.bCanAdvance || Session.Tutorial.bCompleted));
        SetSessionFlag(6, Hud.Mobile.bWheelActive && !bMapExpanded);
    }

    public void ResetMatch()
    {
        Root.Pointers.Clear();
        Root.Simulation.ReplaceConfiguration(Rules);
        Root.Simulation.Reset(Session.bMainMenuOpen, Session.Tutorial.bActive, (uint)UnityEngine.Random.Range(1, int.MaxValue), Rules);
        RefreshSnapshots();
        Root.Navigation.Rebuild();
        Root.Simulation.Simulation.InitializeNavigation();
        Hud.ClearDamageLabels();
        Root.CameraPresenter.Snap();
        Hud.RefreshChoices();
        Hud.RefreshMap(this);
        MapRemaining = 0.08f;
    }

    public void Simulate(float DeltaSeconds)
    {
        using var Measurement = CombatPerformanceMarkers.Simulation.Auto();
        SynchronizeSession();
        Root.Simulation.Step(DeltaSeconds, true);
        Root.Simulation.Publish();
        RefreshSnapshots();
        Root.Navigation.EnsureCurrent();
        RefreshMapIfDue(Match.DeltaSeconds);
    }

    public void RefreshMapIfDue(float DeltaSeconds)
    {
        MapRemaining -= DeltaSeconds;
        if (MapRemaining <= 0)
        {
            MapRemaining = 0.08f;
            Root.MapPresenter.Refresh();
        }
    }

    public void BeginGesture(Vector2 Position)
    {
        if (bSimulationPaused || Hud.Mobile.bWheelActive || Hud.Mobile.bCurtainDragging)
        {
            return;
        }
        Dispatch(GestureCommand(CombatCommandKind.BeginGesture, Position));
    }

    public void MoveGesture(Vector2 Position)
    {
        Dispatch(GestureCommand(CombatCommandKind.MoveGesture, Position));
    }

    public void EndGesture(Vector2 Position)
    {
        CombatCommand Command = GestureCommand(CombatCommandKind.EndGesture, Position);
        ConfigureTerrainCommand(Position, ref Command);
        Dispatch(Command);
    }

    private CombatCommand GestureCommand(CombatCommandKind Kind, Vector2 Position)
    {
        return new CombatCommand
        {
            Kind = Kind, Position = new float3(Position.x, Position.y, 0), Timestamp = Time.unscaledTime,
            Value = Hud.Mobile.InputScale, Direction = Vector3.ProjectOnPlane(WorldCamera.transform.right, Vector3.up).normalized,
            SecondaryDirection = Vector3.ProjectOnPlane(WorldCamera.transform.forward, Vector3.up).normalized
        };
    }

    public void CancelGesture()
    {
        Dispatch(new CombatCommand { Kind = CombatCommandKind.CancelGesture });
    }

    public void EquipWeapon(int Index)
    {
        Dispatch(new CombatCommand { Kind = CombatCommandKind.EquipWeapon, Index = Index });
    }

    public void StartDash(Vector3 Direction)
    {
        Dispatch(new CombatCommand { Kind = CombatCommandKind.Dash, Direction = Direction });
    }

    public void StartSpecial(bool bOffensive, Vector3 Direction)
    {
        CombatCommandKind Kind = CombatCommandKind.DefensiveSpecial;
        if (bOffensive)
        {
            Kind = CombatCommandKind.OffensiveSpecial;
        }
        Dispatch(new CombatCommand { Kind = Kind, Direction = Direction });
    }

    public void PerformAutoAttack(bool bManual)
    {
        int Flags = 0;
        if (bManual)
        {
            Flags = 1;
        }
        Dispatch(new CombatCommand { Kind = CombatCommandKind.ManualAttack, Flags = Flags });
    }

    public void ClearEnemies()
    {
        Dispatch(new CombatCommand { Kind = CombatCommandKind.ClearEnemies }, true);
    }

    public EnemyState SpawnEnemy(int KindIndex, Vector3 Position)
    {
        Dispatch(new CombatCommand { Kind = CombatCommandKind.SpawnEnemy, Index = KindIndex, Position = Position }, true);
        EnemyState Newest = null;
        int NewestIdentity = -1;
        foreach (EnemyState Enemy in Enemies)
        {
            int Identity = Root.Simulation.ReadModel.EnemyIdentities.Values[Enemy.Entity].SpawnIdentity;
            if (Identity > NewestIdentity)
            {
                Newest = Enemy;
                NewestIdentity = Identity;
            }
        }
        return Newest;
    }

    public void DamageEnemy(EnemyState Enemy, float Damage, Vector3 Direction, float Push, float PushDuration, float LaunchHeight, bool bKeepStronger, bool bPlayerDamage = false)
    {
        int Flags = 0;
        if (bKeepStronger)
        {
            Flags |= 1;
        }
        if (bPlayerDamage)
        {
            Flags |= 2;
        }
        Dispatch(new CombatCommand
        {
            Kind = CombatCommandKind.DamageEnemy, Target = Enemy.Entity, Value = Damage, Direction = Direction,
            Parameters = new float4(Push, PushDuration, LaunchHeight, 0), Flags = Flags
        }, true);
    }

    public void DamagePlayer(float Damage, Vector3 Direction, float PushScale)
    {
        Dispatch(new CombatCommand { Kind = CombatCommandKind.DamagePlayer, Value = Damage, Direction = Direction, ExtraValue = PushScale }, true);
    }

    public void SetEnemyValue(Entity Entity, int Index, float Value, Vector3 Position)
    {
        Dispatch(new CombatCommand { Kind = CombatCommandKind.SetEnemyValue, Target = Entity, Index = Index, Value = Value, Position = Position }, true);
    }

    public void SetBuildingValue(Entity Entity, int Index, float Value)
    {
        Dispatch(new CombatCommand { Kind = CombatCommandKind.SetBuildingValue, Target = Entity, Index = Index, Value = Value }, true);
    }

    public void AddExperience(int Amount)
    {
        Dispatch(new CombatCommand { Kind = CombatCommandKind.AddExperience, Value = Amount }, true);
    }

    public void Respawn()
    {
        Dispatch(new CombatCommand { Kind = CombatCommandKind.Respawn }, true);
    }

    public void ResetProgression()
    {
        Dispatch(new CombatCommand { Kind = CombatCommandKind.ClearProgression }, true);
    }

    public float GetProgressionMultiplier(ProgressionBonus Bonus)
    {
        float Multiplier = 1;
        foreach (UpgradeMilestone Milestone in Rules.UpgradeMilestones)
        {
            int Count = AttackUpgrades;
            if (Milestone.bHealth)
            {
                Count = HealthUpgrades;
            }
            if (Milestone.Bonus == Bonus && Count >= Milestone.Level)
            {
                Multiplier *= Milestone.Multiplier;
            }
        }
        return Multiplier;
    }

    public Vector3 ScreenDirectionToWorld(Vector2 Direction)
    {
        return (Vector3.ProjectOnPlane(WorldCamera.transform.right, Vector3.up).normalized * Direction.x
            + Vector3.ProjectOnPlane(WorldCamera.transform.forward, Vector3.up).normalized * Direction.y).normalized;
    }

    public Vector3 ClampToArena(Vector3 Position)
    {
        return CombatBotGeometry.ClampToArena(Position, ArenaBound);
    }

    public void ToggleMap()
    {
        CancelGesture();
        bMapExpanded = !bMapExpanded;
        if (bMapExpanded)
        {
            SlowMotionRemaining = Rules.MobileView.SlowMotionDuration;
        }
        Hud.SetMapExpanded(bMapExpanded);
    }

    public void ShowError(string Message)
    {
        InterfaceError = Message;
        InterfaceErrorRemaining = 2.5f;
    }

    public static string ErrorCaption(int Code)
    {
        switch (Code)
        {
            case 1: return "Недостаточно выносливости";
            case 2: return "Спецатака не готова";
            case 3: return "Недостаточно золота";
            case 4: return "За пределами зоны строительства";
            case 5: return "Здесь находится игрок";
            case 6: return "Место занято";
            case 7: return "Церковь разрушена";
            case 8: return "Церковь нельзя продать";
            case 9: return "Нельзя построить всю стену в выбранном месте";
        }
        return "";
    }
    public void ReadKeyboard()
    {
        Keyboard Keys = Keyboard.current;
        if (Keys == null)
        {
            return;
        }
        if (Keys.escapeKey.wasPressedThisFrame)
        {
            CancelGesture();
            if (bSettingsOpen)
            {
                Hud.CloseSettings();
            }
            else
            {
                if (Session != null)
                {
                    Session.TogglePause();
                }
                else
                {
                    bPaused = !bPaused;
                }
            }
        }
        if (bSettingsOpen || bPaused || Session != null && Session.bBlocksGameplay)
        {
            return;
        }
        if (Keys.digit1Key.wasPressedThisFrame)
        {
            EquipWeapon(0);
        }
        if (Keys.digit2Key.wasPressedThisFrame)
        {
            EquipWeapon(1);
        }
        if (Keys.digit3Key.wasPressedThisFrame)
        {
            EquipWeapon(2);
        }
        if (Keys.tabKey.wasPressedThisFrame)
        {
            ToggleMap();
        }
        if (!bPaused && Keys.spaceKey.wasPressedThisFrame && !bGestureActive)
        {
            StartDash(Facing);
        }
    }
    public void SubmitKeyboardMovement()
    {
        Vector2 Direction = Vector2.zero;
        Keyboard Keys = Keyboard.current;
        if (Keys != null && !bMapExpanded)
        {
            Direction.x = Convert.ToInt32(Keys.dKey.isPressed) - Convert.ToInt32(Keys.aKey.isPressed);
            Direction.y = Convert.ToInt32(Keys.wKey.isPressed) - Convert.ToInt32(Keys.sKey.isPressed);
        }
        CombatCommand Command = GestureCommand(CombatCommandKind.KeyboardMovement, Direction);
        Dispatch(Command);
    }
}
