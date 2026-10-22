using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

public struct CombatMatchEntity : IComponentData
{
}

public struct CombatMatchState : IComponentData
{
    public float ElapsedSeconds;
    public float DeltaSeconds;
    public float UnscaledSeconds;
    public float SlowMotionRemaining;
    public float TimeScale;
    public float PickupRemaining;
    public float WaveCountdown;
    public int WaveNumber;
    public int NavigationRevision;
    public int NextIdentity;
    public int NextAttackIdentity;
    public int ResetSequence;
    public int bMainMenu;
    public int bTutorial;
    public int bTutorialBlocked;
    public int bPaused;
    public int bSettings;
    public int bDefeated;
    public int bMapOpen;
    public int bWeaponWheelOpen;
    public int MapTimeMode;
    public int bInfiniteSlowMotion;
    public float SlowMotionFactor;
    public float SlowMotionDuration;
    public uint RandomState;
    public float ErrorRemaining;
    public int ErrorCode;
    public int LastCommandIdentity;
    public int LastCommandSucceeded;
    public int NavigationReadyRevision;
}

public struct CombatPlayerState : IComponentData
{
    public float3 Position;
    public float3 Facing;
    public float3 PreviousPosition;
    public float Health;
    public float Stamina;
    public int Gold;
    public int Kills;
    public int Rank;
    public int Experience;
    public int WeaponIndex;
    public int AttackUpgrades;
    public int HealthUpgrades;
    public int3 WeaponUpgrades;
    public float Shield;
    public float DamageBonusRemaining;
    public float DamageBonusPercent;
    public float AutoRemaining;
    public float SwingRemaining;
    public float RecoilRemaining;
    public float3 RecoilDirection;
    public float HitFlash;
    public float CameraShake;
    public float3 PushVelocity;
    public float PushRemaining;
    public float3 DashMomentum;
    public float DashMomentumRemaining;
    public float AxeApproachRemaining;
    public float3 AxeApproachVelocity;
    public float CastRemaining;
    public float Charge;
    public int bGestureActive;
    public int bDashArmed;
    public int bAttackSelected;
    public float2 GestureStart;
    public float2 GestureOffset;
    public float MaximumGestureDistance;
    public float GestureStartedAt;
    public float GestureAge;
    public float3 CameraRight;
    public float3 CameraForward;
    public float2 KeyboardMovement;
    public int bMovementAttack;
    public int bSpecial;
    public int bAttackFinished;
    public int AttackIdentity;
    public float AttackElapsed;
    public float AttackDuration;
    public float3 AttackStart;
    public float3 AttackTarget;
    public float3 AttackDirection;
    public float3 InitialAttackDirection;
    public CombatWeaponDefinition AttackWeapon;
    public int bDefense;
    public float DefenseRemaining;
    public float DefensePulseRemaining;
    public CombatWeaponDefinition DefenseWeapon;
    public float ComboPoints;
    public int SpecialUses;
    public float ComboIdleSeconds;
    public float ComboPendingLoss;
    public int LessonEvents;
}

public struct CombatBuildingState : IComponentData
{
    public int Identity;
    public int DefinitionIndex;
    public int Kind;
    public float3 Position;
    public int2 Cell;
    public float2 HalfExtents;
    public float Health;
    public float Timer;
    public int PaidPrice;
    public int StoredGold;
    public Entity GoldBag;
    public float ForgeRemaining;
    public int ForgeWeapon;
    public int bHealthUpgrade;
    public int bForgeOccupied;
}

public struct CombatProjectileState : IComponentData
{
    public float3 Position;
    public float3 Direction;
    public float Speed;
    public float Damage;
    public float Remaining;
    public float Push;
    public int bFriendly;
}

public struct CombatPickupState : IComponentData
{
    public float3 Position;
    public int Kind;
    public Entity Owner;
}

public struct CombatBombState : IComponentData
{
    public int2 Cell;
    public float Remaining;
}

public struct CombatSceneryState : IComponentData
{
    public float3 Position;
    public int Identity;
    public int bRemoved;
    public int InitialModel;
}

public struct CombatCorpseState : IComponentData
{
    public float3 Velocity;
    public float Age;
}

public enum CombatEffectKind
{
    Area,
    Weapon,
    Special,
    Defense,
    Charge,
    Shard,
    Warning,
    Bomb
}

public struct CombatEffectState : IComponentData
{
    public CombatEffectKind Kind;
    public Entity Owner;
    public float3 Position;
    public float3 Direction;
    public float3 Velocity;
    public float4 Color;
    public float Radius;
    public float HalfWidth;
    public float TipWidthScale;
    public float Age;
    public float Duration;
    public float Expansion;
    public int WeaponKind;
}

public struct CombatEnemyIdentity : IComponentData
{
    public int SpawnIdentity;
    public float MaximumHealth;
    public int KindIndex;
    public int ArmorTier;
    public int LastPlayerAttack;
    public int LastPlayerFinish;
}

public struct CombatWaveGroup : IBufferElementData
{
    public float3 Position;
}

public enum CombatCommandKind
{
    ResetMatch,
    SetSession,
    BeginGesture,
    MoveGesture,
    EndGesture,
    CancelGesture,
    KeyboardMovement,
    EquipWeapon,
    Dash,
    OffensiveSpecial,
    DefensiveSpecial,
    ManualAttack,
    PlaceBuilding,
    PlaceWall,
    SellBuilding,
    Trebuchet,
    SpawnEnemy,
    ClearEnemies,
    DamageEnemy,
    DamagePlayer,
    SetPlayerValue,
    SetEnemyValue,
    RestoreCombo,
    ClearProgression,
    SetMapTime,
    SetBuildingValue,
    RecordPersonalDamage,
    AddExperience,
    Respawn,
    NavigationMeshReady
}

public struct CombatCommand : IBufferElementData
{
    public CombatCommandKind Kind;
    public Entity Target;
    public int Identity;
    public int Index;
    public int Auxiliary;
    public int Flags;
    public float Value;
    public float ExtraValue;
    public float Timestamp;
    public float3 Position;
    public float3 Direction;
    public float3 SecondaryDirection;
    public float4 Parameters;
    public int2 Cell;
    public float3 RayOrigin;
    public float3 RayDirection;
}

public struct CombatUiEvent : IBufferElementData
{
    public float3 Position;
    public float Amount;
    public int Kind;
}

