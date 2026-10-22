using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

public struct CombatWeaponDefinition
{
    public FixedString128Bytes Name;
    public int Kind;
    public float4 Color;
    public float AutoDamage;
    public float AutoInterval;
    public float AutoRangeScale;
    public bool bAutomaticAttack;
    public float Radius;
    public float HalfWidth;
    public float TipWidthScale;
    public float DashDamage;
    public float DashDistance;
    public FixedString128Bytes AttackName;
    public float SpecialDamage;
    public float SpecialRadius;
    public float SpecialDistance;
    public float SpecialDuration;
    public float FinishDamage;
    public float FinishRadius;
    public float SpecialPush;
    public float LaunchHeight;
    public float JumpHeight;
    public FixedString128Bytes DefenseName;
    public float DefenseRadius;
    public float DefensePush;
    public float DefenseDuration;
    public float DefensePulse;
    public float Shield;
    public float Healing;
    public float StaminaTarget;
    public float DamageBonusPercent;
    public float DamageBonusDuration;
}

public struct CombatEnemyDefinition
{
    public FixedString128Bytes Name;
    public int Kind;
    public float4 Color;
    public float Health;
    public float Speed;
    public float SpeedVariation;
    public float Damage;
    public float AttackRadius;
    public float HalfWidth;
    public float Windup;
    public float Recovery;
    public float FormationDistance;
    public float MinimumRange;
    public float DashDistance;
    public float DashDuration;
    public float ThrustLength;
    public float ProjectileSpeed;
    public float SpawnWeight;
}

public struct CombatBuildingDefinition
{
    public FixedString128Bytes Name;
    public int Kind;
    public int Price;
    public float Health;
    public float Range;
    public float Interval;
    public float Damage;
    public float Push;
    public float Healing;
}

public struct CombatRulesDefinition
{
    public float MaximumHealth;
    public float WalkSpeed;
    public float RunSpeed;
    public float JoystickCenterRadius;
    public float JoystickMovementRadius;
    public float JoystickOuterRadius;
    public float ChargeSeconds;
    public int SpecialSacrificeBaseCost;
    public int SpecialSacrificeCostIncrease;
    public float TapMaximumSeconds;
    public bool bSpecialReadyEdgeEffect;
    public float SpecialReadyEdgeOpacity;
    public float MaximumStamina;
    public float DashStaminaCost;
    public float StaminaRecovery;
    public float LowStaminaRecovery;
    public float DashDuration;
    public float DashDamageVariationPercent;
    public float DashPush;
    public float DashPushDuration;
    public float SidePushStrength;
    public float AutoPush;
    public float AutoPushDuration;
    public float AutoRecoilDuration;
    public float AutoForwardSpeed;
    public float PlayerHitPush;
    public float PlayerHitDuration;
    public float ArrowPushScale;
    public float LowStaminaThreshold;
    public float LowStaminaDarkness;
    public int GridSize;
    public float CellSize;
    public int InitialEnemies;
    public int EnemiesPerWave;
    public float WaveDelay;
    public float EnemyHealthGrowth;
    public float EnemyDamageGrowth;
    public float EnemyDamageMultiplier;
    public float PlayableBorder;
    public int KillsPerIncrease;
    public int MaximumSimultaneousAttacks;
    public float SeparationStrength;
    public float FormationAngularSpeed;
    public int InitialPickups;
    public int MaximumPickups;
    public float PickupHealing;
    public float PickupRadius;
    public float PickupMinimumInterval;
    public float PickupMaximumInterval;
    public int InitialGold;
    public int KillGold;
    public int KillExperience;
    public int BuildingExperience;
    public float PrestigeGrowth;
    public float RefundFraction;
    public int TrebuchetPrice;
    public float TrebuchetDelay;
    public float TrebuchetDamage;
    public int TrebuchetCellRadius;
    public float TowerArrowSpeed;
}

public struct CombatMilestoneDefinition
{
    public int Level;
    public int Bonus;
    public bool bHealth;
    public float Multiplier;
}

public struct CombatConfiguration
{
    public CombatRulesDefinition Rules;
    public BlobArray<CombatWeaponDefinition> Weapons;
    public BlobArray<CombatEnemyDefinition> Enemies;
    public BlobArray<CombatBuildingDefinition> Buildings;
    public BlobArray<CombatMilestoneDefinition> Milestones;
}

public struct CombatConfigurationData : IComponentData
{
    public BlobAssetReference<CombatConfiguration> Value;
}
