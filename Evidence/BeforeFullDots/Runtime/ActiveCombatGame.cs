using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
public partial class ActiveCombatGame : MonoBehaviour
{
    public GameRoot Root;
    public bool bTerrainPlacementArmed;
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
    public CombatBalance Rules
    {
        get;
        private set;
    }
    public Vector3 PlayerPosition;
    public Vector3 Facing = Vector3.forward;
    public float Health;
    public float Stamina;
    public int Gold;
    public int Kills;
    public readonly SpecialCombo Combo = new SpecialCombo();
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
    public int SpecialSacrificeCost
    {
        get
        {
            return Combo.Cost;
        }
    }
    public bool bSpecialReady
    {
        get
        {
            return Combo.bReady;
        }
    }
    public int Rank = 1;
    public int Experience;
    public int WeaponIndex;
    public int StructureIndex;
    public bool bTrebuchet;
    public bool bPaused;
    public bool bMapExpanded;
    public bool bSettingsOpen;
    public bool bGestureActive;
    public bool bAttackSelected;
    public Vector2 GestureStart;
    public Vector2 GestureOffset;
    public float Charge;
    public float DamageBonusRemaining;
    public float DamageBonusPercent;
    public float Shield;
    public float DefenseRemaining;
    public float SimulatedSeconds;
    public float HitFeedback
    {
        get
        {
            return HitFlash / 0.18f;
        }
    }
    public float SlowMotionRemaining;
    public string Status;
    public string ErrorMessage;
    public WeaponBalance CurrentWeapon
    {
        get
        {
            return Rules.Weapons[WeaponIndex];
        }
    }
    public int EnemyLimit
    {
        get
        {
            return Rules.InitialEnemies + Mathf.Max(0, WaveNumber - 1) * Rules.EnemiesPerWave;
        }
    }
    public bool bAttacking
    {
        get
        {
            return MovementAttack != null || CastRemaining > 0;
        }
    }
    public bool bSimulationPaused
    {
        get
        {
            return Session != null && Session.bBlocksGameplay || bPaused || bSettingsOpen;
        }
    }
    public float ArenaBound
    {
        get
        {
            return Rules.GridSize * Rules.CellSize / 2 + Rules.PlayableBorder - 0.8f;
        }
    }
    [System.NonSerialized] public List<EnemyState> Enemies = new List<EnemyState>();
    [System.NonSerialized] public List<StructureState> Structures = new List<StructureState>();
    [System.NonSerialized] public List<PickupState> Pickups = new List<PickupState>();
    [System.NonSerialized] public List<ArrowState> Arrows = new List<ArrowState>();
    [System.NonSerialized] public List<BombState> Bombs = new List<BombState>();
    private GameObject Knight;
    public GameObject PlayerModel
    {
        get
        {
            return Knight;
        }
    }
    private Transform WeaponPivot;
    private GameObject EquippedWeapon;
    private float GestureAge;
    private float GestureStartedAt;
    private Vector3 DashMomentum;
    private float DashMomentumRemaining;
    private float AxeApproachRemaining;
    private Vector3 AxeApproachVelocity;
    private float MaximumGestureDistance;
    private bool bDashArmed = true;
    private Vector3 SpecialDirection = Vector3.forward;
    private float AutoRemaining;
    private float RecoilRemaining;
    private Vector3 RecoilDirection;
    private Vector3 PlayerVelocity;
    private float PlayerPushRemaining;
    private float CastRemaining;
    private float SwingRemaining;
    private WeaponBalance DefenseWeapon;
    private float DefensePulseRemaining;
    private GameObject DefenseVisual;
    private GameObject ChargeVisual;
    private float PickupRemaining;
    private float HitFlash;
    private float CameraShake;
    public float CameraImpactStrength => CameraShake;
    private float MapRemaining;
    private List<VisualEffect> Effects = new List<VisualEffect>();
    private List<Vector3> SceneryPositions = new List<Vector3>();
    private AttackState MovementAttack;
    public class AttackState
    {
        public WeaponBalance Weapon;
        public Vector3 Start;
        public Vector3 Target;
        public Vector3 Direction;
        public Vector3 InitialDirection;
        public float Elapsed;
        public float Duration;
        public bool bSpecial;
        public bool bFinished;
        public HashSet<EnemyState> Victims = new HashSet<EnemyState>();
        public GameObject Visual;
    }
    public class VisualEffect
    {
        public GameObject Model;
        public float Age;
        public float Duration;
        public float Expand;
        public Vector3 Velocity;
        public bool bCorpse;
    }
    private void Start()
    {
        Rules = Instantiate(Balance);
        Application.runInBackground = true;
        Application.targetFrameRate = 60;
        foreach (Transform Scenery in SceneryRoot)
        {
            SceneryPositions.Add(Scenery.position);
        }
        Knight = Instantiate(KnightPrefab, DynamicRoot);
        WeaponPivot = Knight.transform.Find("WeaponPivot");
        EquipWeapon(0);
        if (Session != null)
        {
            Session.ShowMainMenu();
        }
        else
        {
            ResetMatch();
        }
    }
    private void Update()
    {
        if (Rules == null)
        {
            return;
        }
        ReadKeyboard();
        float DeltaSeconds = Mathf.Min(Time.unscaledDeltaTime, 0.1f);
        if (bSimulationPaused)
        {
            DeltaSeconds = 0;
        }
        else if(Hud.Mobile!=null)
        {
            DeltaSeconds*=Hud.Mobile.TimeScale(DeltaSeconds);
        }
        float Remaining=DeltaSeconds;
        while(Remaining>0)
        {
            float Step=Mathf.Min(Remaining,1f/60f);
            Simulate(Step);
            Remaining-=Step;
        }
        if (Session != null)
        {
            Session.Tutorial.Tick();
        }
        Hud.Refresh(this, Time.unscaledDeltaTime);
        if (Session != null)
        {
            Session.RefreshUI();
        }
    }
    public void Simulate(float DeltaSeconds)
    {
        using var Measurement = CombatPerformanceMarkers.Simulation.Auto();
        if (DeltaSeconds <= 0 || Session != null && Session.bBlocksGameplay)
        {
            return;
        }
        SimulatedSeconds += DeltaSeconds;
        Combo.Advance(DeltaSeconds);
        if (!Combo.bReady)
        {
            Charge = 0;
        }
        ErrorRemaining = Mathf.Max(0, ErrorRemaining - DeltaSeconds);
        if (ErrorRemaining <= 0)
        {
            ErrorMessage = "";
        }
        DamageBonusRemaining = Mathf.Max(0, DamageBonusRemaining - DeltaSeconds);
        RecoilRemaining = Mathf.Max(0, RecoilRemaining - DeltaSeconds);
        HitFlash = Mathf.Max(0, HitFlash - DeltaSeconds);
        CameraShake = Mathf.Max(0, CameraShake - DeltaSeconds);
        SwingRemaining = Mathf.Max(0, SwingRemaining - DeltaSeconds);
        Vector3 PreviousPosition = PlayerPosition;
        bool bWasAttacking = bAttacking;
        UpdateDefense(DeltaSeconds);
        if (MovementAttack != null)
        {
            UpdateMovementAttack(DeltaSeconds);
        }
        else if (CastRemaining > 0)
        {
            CastRemaining = Mathf.Max(0, CastRemaining - DeltaSeconds);
        }
        else
        {
            RecoverStamina(DeltaSeconds);
            UpdatePlayerMovement(DeltaSeconds);
        }
        if (PlayerPushRemaining > 0 && !bAttacking)
        {
            PlayerPosition += PlayerVelocity * Mathf.Min(DeltaSeconds, PlayerPushRemaining) * PlayerPushRemaining / Rules.PlayerHitDuration;
            PlayerPushRemaining = Mathf.Max(0, PlayerPushRemaining - DeltaSeconds);
        }
        PlayerPosition = ClampToArena(PlayerPosition);
        if (AxeApproachRemaining > 0 && !bAttacking)
        {
            PlayerPosition = ClampToArena(PlayerPosition + AxeApproachVelocity * Mathf.Min(DeltaSeconds, AxeApproachRemaining));
            AxeApproachRemaining = Mathf.Max(0, AxeApproachRemaining - DeltaSeconds);
        }
        ResolvePlayerBuildings();
        if (!bWasAttacking && !bAttacking)
        {
            UpdateAutomaticAttack(DeltaSeconds);
        }
        UpdatePickups(DeltaSeconds, PreviousPosition);
        UpdateEnemies(DeltaSeconds);
        UpdateStructures(DeltaSeconds);
        UpdateArrows(DeltaSeconds);
        UpdateBombs(DeltaSeconds);
        UpdateWaves(DeltaSeconds);
        UpdateEffects(DeltaSeconds);
        UpdatePlayerVisual(DeltaSeconds);
        MapRemaining -= DeltaSeconds;
        if (MapRemaining <= 0)
        {
            MapRemaining = 0.08f;
            Hud.RefreshMap(this);
        }
    }
    private void LateUpdate()
    {
        if (Knight == null)
        {
            return;
        }
        if(Hud.Mobile!=null)
        {
            Hud.Mobile.UpdateCamera(Time.unscaledDeltaTime);
        }
        if(CameraShake>0 && Root == null)
        {
            WorldCamera.transform.position+=new Vector3(Mathf.Sin(SimulatedSeconds*107),0,Mathf.Cos(SimulatedSeconds*89))*CameraShake*0.4f;
        }
    }
    private void ReadKeyboard()
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
    public Vector3 ScreenDirectionToWorld(Vector2 ScreenDirection)
    {
        Vector3 CameraRight = Vector3.ProjectOnPlane(WorldCamera.transform.right, Vector3.up).normalized;
        Vector3 CameraForward = Vector3.ProjectOnPlane(WorldCamera.transform.forward, Vector3.up).normalized;
        return (CameraRight * ScreenDirection.x + CameraForward * ScreenDirection.y).normalized;
    }
    private void UpdatePlayerMovement(float DeltaSeconds)
    {
        Vector3 Movement = Vector3.zero;
        float Speed = Rules.WalkSpeed * GetProgressionMultiplier(ProgressionBonus.MovementSpeed);
        if (bGestureActive)
        {
            GestureAge += DeltaSeconds;
            float Distance = GestureOffset.magnitude;
            if (Distance <= Rules.JoystickCenterRadius)
            {
                FocusNearest();
                if (bSpecialReady)
                {
                    Charge = Mathf.Min(SpecialChargeSeconds, Charge + DeltaSeconds);
                }
                else
                {
                    Charge = 0;
                }
            }
            else if (Charge < SpecialChargeSeconds)
            {
                Charge = 0;
                Movement = ScreenDirectionToWorld(GestureOffset);
                Speed *= Mathf.Clamp01((Distance - Rules.JoystickCenterRadius) / (Rules.JoystickMovementRadius - Rules.JoystickCenterRadius));
                if (Distance >= Rules.JoystickMovementRadius)
                {
                    Speed = Rules.RunSpeed * GetProgressionMultiplier(ProgressionBonus.MovementSpeed);
                }
                Facing = Movement;
            }
        }
        else if (Keyboard.current != null && !bMapExpanded)
        {
            Keyboard Keys = Keyboard.current;
            Movement.x = Convert.ToInt32(Keys.dKey.isPressed) - Convert.ToInt32(Keys.aKey.isPressed);
            Movement.z = Convert.ToInt32(Keys.wKey.isPressed) - Convert.ToInt32(Keys.sKey.isPressed);
            Movement = ScreenDirectionToWorld(new Vector2(Movement.x, Movement.z));
            if (Movement.sqrMagnitude > 0)
            {
                Facing = Movement;
            }
        }
        Vector3 Translation = Movement * Speed;
        if (RecoilRemaining > 0)
        {
            float ForwardSpeed = Mathf.Max(0, Vector3.Dot(Translation, RecoilDirection));
            Translation -= RecoilDirection * ForwardSpeed * (1 - Rules.AutoForwardSpeed);
        }
        if (DashMomentumRemaining > 0)
        {
            float Weight = DashMomentumRemaining / 0.35f;
            Translation = Vector3.Lerp(Translation, DashMomentum, Weight * Weight);
            DashMomentumRemaining = Mathf.Max(0, DashMomentumRemaining - DeltaSeconds);
        }
        PlayerPosition += Translation * DeltaSeconds;
    }
    public void BeginGesture(Vector2 Position)
    {
        if (bSimulationPaused || Hud.Mobile.bWheelActive || Hud.Mobile.bCurtainDragging)
        {
            return;
        }
        bGestureActive = true;
        GestureStart = Position;
        GestureOffset = Vector2.zero;
        GestureAge = 0;
        GestureStartedAt = Time.unscaledTime;
        Charge = 0;
        MaximumGestureDistance = 0;
        bAttackSelected = false;
        bDashArmed = true;
        FocusNearest();
    }
    public void MoveGesture(Vector2 Position)
    {
        if (!bGestureActive)
        {
            return;
        }
        GestureOffset = (Position - GestureStart)/Hud.Mobile.InputScale;
        float Distance = GestureOffset.magnitude;
        MaximumGestureDistance = Mathf.Max(MaximumGestureDistance, Distance);
        if (Distance > Rules.JoystickCenterRadius && Charge < SpecialChargeSeconds)
        {
            Charge = 0;
        }
        if (Distance > Rules.JoystickCenterRadius && MovementAttack == null)
        {
            Facing = ScreenDirectionToWorld(GestureOffset);
        }
        if (Charge >= SpecialChargeSeconds)
        {
            if (Distance >= Rules.JoystickMovementRadius)
            {
                bAttackSelected = true;
                SpecialDirection = ScreenDirectionToWorld(GestureOffset);
                StartSpecial(true, SpecialDirection);
                bDashArmed = false;
                return;
            }
            else if (Distance <= Rules.JoystickCenterRadius)
            {
                bAttackSelected = false;
            }
        }
        if (Distance < Rules.JoystickMovementRadius)
        {
            bDashArmed = true;
        }
        else if (bDashArmed)
        {
            bDashArmed = false;
            if (Charge < SpecialChargeSeconds)
            {
                StartDash(Facing);
            }
        }
    }
    public void EndGesture(Vector2 Position)
    {
        if (!bGestureActive)
        {
            return;
        }
        Vector2 Offset = (Position - GestureStart)/Hud.Mobile.InputScale;
        bool bReady = Charge >= SpecialChargeSeconds;
        bool bOffensive = Offset.magnitude > Rules.JoystickCenterRadius && (bAttackSelected || Offset.magnitude >= Rules.JoystickMovementRadius);
        Vector3 Direction = SpecialDirection;
        if (Offset.magnitude >= Rules.JoystickMovementRadius)
        {
            Direction = ScreenDirectionToWorld(Offset);
        }
        bool bTap = Mathf.Max(MaximumGestureDistance, Offset.magnitude) <= Rules.JoystickCenterRadius && Time.unscaledTime - GestureStartedAt <= Rules.TapMaximumSeconds;
        CancelGesture();
        if (bTap && TryTerrainPlacement(Position))
        {
            return;
        }
        if (bReady)
        {
            StartSpecial(bOffensive, Direction);
        }
        else if (bTap && CurrentWeapon.Kind == CombatWeapon.Axe && !CurrentWeapon.bAutomaticAttack && !bAttacking)
        {
            PerformAutoAttack(true);
        }
    }
    public void CancelGesture()
    {
        bGestureActive = false;
        Charge = 0;
        bAttackSelected = false;
        if (ChargeVisual != null)
        {
            Destroy(ChargeVisual);
        }
    }
    private void OnApplicationFocus(bool bFocused)
    {
        if (!bFocused)
        {
            CancelGesture();
            Hud.Mobile.CancelWheel();
            Hud.Mobile.bCurtainDragging=false;
        }
    }
    public void EquipWeapon(int Index)
    {
        CancelGesture();
        WeaponIndex = Index;
        if (Rules == null || WeaponPivot == null)
        {
            return;
        }
        if (EquippedWeapon != null)
        {
            Destroy(EquippedWeapon);
        }
        EquippedWeapon = Instantiate(WeaponPrefabs[Index], WeaponPivot);
        EquippedWeapon.transform.localPosition = Vector3.zero;
        EquippedWeapon.transform.localRotation = Quaternion.identity;
    }
    private void FocusNearest()
    {
        EnemyState Nearest = null;
        float Distance = float.PositiveInfinity;
        foreach (EnemyState Enemy in Enemies)
        {
            float Candidate = (Enemy.Position - PlayerPosition).sqrMagnitude;
            if (Candidate < Distance)
            {
                Distance = Candidate;
                Nearest = Enemy;
            }
        }
        if (Nearest != null)
        {
            Facing = (Nearest.Position - PlayerPosition).normalized;
        }
    }
    private void RecoverStamina(float DeltaSeconds)
    {
        if (Stamina >= Rules.MaximumStamina)
        {
            return;
        }
        float Recovery = Rules.StaminaRecovery + Rules.LowStaminaRecovery * (1 - Stamina / Rules.MaximumStamina);
        if (Rules.LowStaminaRecovery > 0)
        {
            Stamina += Recovery * Rules.MaximumStamina / Rules.LowStaminaRecovery * (1 - Mathf.Exp(-Rules.LowStaminaRecovery * DeltaSeconds / Rules.MaximumStamina)) * GetProgressionMultiplier(ProgressionBonus.StaminaRecovery);
        }
        else
        {
            Stamina += Recovery * DeltaSeconds * GetProgressionMultiplier(ProgressionBonus.StaminaRecovery);
        }
        Stamina = Mathf.Min(Rules.MaximumStamina, Stamina);
    }
    public Vector3 ClampToArena(Vector3 Position)
    {
        return new Vector3(Mathf.Clamp(Position.x, -ArenaBound, ArenaBound), 0, Mathf.Clamp(Position.z, -ArenaBound, ArenaBound));
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
    private void UpdatePlayerVisual(float DeltaSeconds)
    {
        float Height = 0;
        Quaternion Rotation = Quaternion.LookRotation(Facing);
        if (MovementAttack != null)
        {
            AttackState Attack = MovementAttack;
            float Progress = Mathf.Clamp01(Attack.Elapsed / Attack.Duration);
            if (Attack.Weapon.Kind == CombatWeapon.Axe)
            {
                if (Attack.bSpecial)
                {
                    Height = Mathf.Sin(Progress * Mathf.PI) * Attack.Weapon.JumpHeight;
                }
                else
                {
                    Rotation *= Quaternion.Euler(0, Progress * 720, 0);
                }
            }
            else if (Attack.bSpecial && Attack.Weapon.Kind == CombatWeapon.Sword)
            {
                Rotation *= Quaternion.Euler(0, Progress * 1080, 0);
            }
        }
        Knight.transform.SetPositionAndRotation(PlayerPosition + Vector3.up * Height, Rotation);
        WeaponPivot.localRotation = Quaternion.Euler(0, Mathf.Sin(SwingRemaining * 16) * 70, 0);
        if (bGestureActive && Charge > 0)
        {
            WeaponPivot.localRotation = Quaternion.Euler(-100 * Charge / SpecialChargeSeconds, 0, 0);
            if (ChargeVisual == null)
            {
                ChargeVisual = CombatVisuals.Charge(DynamicRoot,CurrentWeapon.Color,EffectMaterial);
            }
            ChargeVisual.transform.position=PlayerPosition;
            float Progress=Mathf.Clamp01(Charge/SpecialChargeSeconds);
            ChargeVisual.transform.GetChild(0).localScale=Vector3.one*(1.6f-Progress*0.5f+Mathf.Sin(SimulatedSeconds*14)*0.04f);
            CombatVisuals.SetOpacity(ChargeVisual.transform.GetChild(0).gameObject,0.25f+Progress*0.5f);
            for(int Index=0;Index<8;Index++)
            {
                float Angle=Index*Mathf.PI/4+SimulatedSeconds*4;
                Transform Spark=ChargeVisual.transform.GetChild(Index+1);
                Spark.localPosition=new Vector3(Mathf.Sin(Angle)*(1.5f-Progress*0.8f),0.3f+Progress*1.1f+Mathf.Sin(Angle*2)*0.15f,Mathf.Cos(Angle)*(1.5f-Progress*0.8f));
                Spark.localScale=Vector3.one*(0.6f+Progress);
            }
        }
    }
    public void ShowError(string Message)
    {
        ErrorMessage = Message;
        ErrorRemaining = 2.5f;
    }
    private float ErrorRemaining;
}
