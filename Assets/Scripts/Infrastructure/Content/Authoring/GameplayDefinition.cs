using System;
using UnityEngine;

namespace VaticanDefendor.Content
{
    [Serializable]
    public sealed class GameplayConfiguration
    {
        public RiverMapConfiguration RiverMap;
        public int CombatRevision;
        public float EnemyDamageMultiplier = 2;
        public CombatFeelConfiguration Combat = new CombatFeelConfiguration();
        public EnemyCombatConfiguration[] Enemies = EnemyCombatConfiguration.CreateDefaults();
        public ResourceAmounts InitialResources;
        public ResourceAmounts FortificationCost;
        public ResourceAmounts ArmamentCost;
        public ResourceAmounts ForgeWeaponCost;
        public ResourceAmounts ForgeHealthCost;
        public ResourceAmounts ArtilleryCost;
        public float StructureHealthMultiplier;
        public float StructureDamageMultiplier;
        public float WeaponDamageMultiplier;
        public float WeaponSpeedMultiplier;
        public float HeroHealthIncrease;
        public int MaximumUpgradeLevel;
        public float ArtilleryRadius;
        public float ArtilleryDamage;
        public float ArtilleryDelay;
        public ResourceAmounts LootPerKill;
        public float GatheringRange;
        public float GatheringCycle;
        public int GatheringAmount;
        public int DepositReservePerWave;
        public float PickupAttractionRadius = 3;
        public float PickupCollectionRadius = 0.2f;
        public float PickupAttractionSpeed = 9;
        public float PickupScatterRadius = 0.8f;
        public float PickupReleaseDelay = 0.3f;
        public int ResourceAmountPerPickup = 2;
        public int MaximumResourcePiecesPerDrop = 8;
        public float ProductionRange;
        public float SpecialChargeSeconds;
        public float DashDistance;
        public float DashDuration;
        public float DashStaminaCost;
        public float RespawnSeconds;
        public float ChurchHealingPerSecond;
        public float HeroHealingPerSecond;
        public float ChurchHealingRange;
        public float TowerRange;
        public float TowerDamage;
        public float TowerInterval;
        public float StructureHealth;
        public float RefundFraction;
        public float EnemyHealthGrowth;
        public float EnemyDamageGrowth;
        public ResourceDepositDefinition[] Deposits;
        public BuildingConfiguration[] Buildings;

        public void Validate()
        {
            if (Combat == null)
            {
                throw new InvalidOperationException("Combat feel configuration is required.");
            }
            Combat.Validate();
            if (Enemies == null || Enemies.Length != 4)
            {
                throw new InvalidOperationException("Four enemy combat profiles are required.");
            }
            var EnemyKinds = new System.Collections.Generic.HashSet<int>();
            foreach (EnemyCombatConfiguration Enemy in Enemies)
            {
                if (Enemy == null || !EnemyKinds.Add(Enemy.Kind))
                {
                    throw new InvalidOperationException("Enemy combat profiles must have unique kinds.");
                }
                Enemy.Validate();
            }
            foreach (float Value in new[] { GatheringRange, GatheringCycle, ProductionRange, SpecialChargeSeconds,
                DashDistance, DashDuration, RespawnSeconds, ChurchHealingRange, TowerRange, TowerDamage, TowerInterval,
                StructureHealthMultiplier, StructureDamageMultiplier, WeaponDamageMultiplier, WeaponSpeedMultiplier,
                HeroHealthIncrease, ArtilleryRadius, ArtilleryDamage, ArtilleryDelay,
                PickupAttractionRadius, PickupCollectionRadius, PickupAttractionSpeed, PickupScatterRadius, PickupReleaseDelay })
            {
                if (!float.IsFinite(Value) || Value <= 0)
                {
                    throw new InvalidOperationException("Gameplay contains an invalid positive parameter.");
                }
            }
            foreach (float Value in new[] { DashStaminaCost, ChurchHealingPerSecond, HeroHealingPerSecond,
                EnemyHealthGrowth, EnemyDamageGrowth, EnemyDamageMultiplier })
            {
                if (!float.IsFinite(Value) || Value < 0)
                {
                    throw new InvalidOperationException("Gameplay contains an invalid nonnegative parameter.");
                }
            }
            if (!InitialResources.IsValid || !LootPerKill.IsValid || !FortificationCost.IsValid ||
                !ArmamentCost.IsValid || !ForgeWeaponCost.IsValid || !ForgeHealthCost.IsValid || !ArtilleryCost.IsValid ||
                !float.IsFinite(RefundFraction) || RefundFraction < 0 || RefundFraction > 1 ||
                MaximumUpgradeLevel < 1 || GatheringAmount < 1 || DepositReservePerWave < 1 ||
                ResourceAmountPerPickup < 1 || MaximumResourcePiecesPerDrop < 1 ||
                Buildings == null || Buildings.Length == 0 || Deposits == null || Deposits.Length == 0)
            {
                throw new InvalidOperationException("Gameplay economy or content is invalid.");
            }
            var Identifiers = new System.Collections.Generic.HashSet<string>();
            foreach (BuildingConfiguration Building in Buildings)
            {
                if (Building == null || string.IsNullOrWhiteSpace(Building.Id) || !Identifiers.Add(Building.Id) ||
                    (int)Building.Kind < 1 || (int)Building.Kind > 7 || !Building.Cost.IsValid || Building.Cost.IsEmpty ||
                    !float.IsFinite(Building.Health) || Building.Health <= 0 || !float.IsFinite(Building.HalfExtent) || Building.HalfExtent <= 0 ||
                    !Enum.IsDefined(typeof(ResourceKind), Building.ProductionKind) ||
                    Building.ProductionKind != ResourceKind.None && (Building.ProductionAmount < 1 ||
                    !float.IsFinite(Building.ProductionCycle) || Building.ProductionCycle <= 0))
                {
                    throw new InvalidOperationException("Gameplay building is invalid.");
                }
            }
            foreach (ResourceDepositDefinition Deposit in Deposits)
            {
                if (Deposit == null || Deposit.Kind == ResourceKind.None || !Enum.IsDefined(typeof(ResourceKind), Deposit.Kind) ||
                    !float.IsFinite(Deposit.Horizontal) || !float.IsFinite(Deposit.Vertical))
                {
                    throw new InvalidOperationException("Gameplay deposit is invalid.");
                }
            }
        }

    }

    [Serializable]
    public sealed class ResourceDepositDefinition
    {
        public ResourceKind Kind;
        public float Horizontal;
        public float Vertical;
    }

    [Serializable]
    public sealed class BuildingConfiguration
    {
        public string Id;
        public VaticanDefendor.Construction.StructureKind Kind;
        public ResourceAmounts Cost;
        public float Health;
        public float HalfExtent;
        public ResourceKind ProductionKind;
        public int ProductionAmount;
        public float ProductionCycle;
    }

    [CreateAssetMenu(menuName = "VaticanDefendor/Content/Gameplay")]
    public sealed class GameplayDefinition : ScriptableObject
    {
        [SerializeField] private GameplayConfiguration _configuration;

        public GameplayConfiguration Configuration => _configuration;
    }
}
