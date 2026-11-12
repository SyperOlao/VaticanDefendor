using System;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using VaticanDefendor.Combat;
using VaticanDefendor.Construction;
using VaticanDefendor.Content;
using VaticanDefendor.Navigation;
using VaticanDefendor.Production;
using VaticanDefendor.RunEconomy;
using VaticanDefendor.Session;

namespace VaticanDefendor.Gameplay
{
    public sealed class RunGameplayConfiguration : IComponentData
    {
        public GameplayConfiguration Configuration;
    }

    public static class SharedGameplayInitialization
    {
        public static void Initialize(World World, ContentRelease Release, bool bServer)
        {
            GameplayConfiguration Configuration = Release.GetGameplay();
            if (Configuration == null || Configuration.Buildings == null ||
                Configuration.Deposits == null || Configuration.GatheringRange <= 0 ||
                Configuration.GatheringCycle <= 0 || Configuration.GatheringAmount < 1 ||
                Configuration.DepositReservePerWave < 1 || !Configuration.InitialResources.IsValid ||
                Configuration.DashDuration <= 0 || Configuration.RespawnSeconds <= 0)
            {
                throw new InvalidOperationException("Gameplay release is incomplete.");
            }
            Configuration.Validate();
            EntityManager Entities = World.EntityManager;
            VaticanDefendor.Maps.RiverMapAccess.Install(Entities, Configuration.RiverMap);
            Entity Settings = Entities.CreateEntity(typeof(HeroActionTuning), typeof(GatheringTuning));
            Entities.AddComponentObject(Settings, new CombatFeelSettings { Configuration = Configuration.Combat });
            Entities.AddComponentObject(Settings, new RunGameplayConfiguration { Configuration = Configuration });
            Entities.AddComponentData(Settings, new ResourcePickupTuning
            {
                AttractionRadius = Configuration.PickupAttractionRadius,
                CollectionRadius = Configuration.PickupCollectionRadius,
                AttractionSpeed = Configuration.PickupAttractionSpeed,
                ScatterRadius = Configuration.PickupScatterRadius,
                ReleaseDelay = Configuration.PickupReleaseDelay,
                AmountPerPickup = Configuration.ResourceAmountPerPickup,
                MaximumPiecesPerDrop = Configuration.MaximumResourcePiecesPerDrop
            });
            Entities.SetComponentData(Settings, new HeroActionTuning
            {
                DashDistance = Configuration.DashDistance,
                DashDuration = Configuration.DashDuration,
                DashStaminaCost = Configuration.DashStaminaCost,
                ChargeSeconds = Configuration.SpecialChargeSeconds,
                RespawnSeconds = Configuration.RespawnSeconds,
                ChurchHealingPerSecond = Configuration.ChurchHealingPerSecond,
                HeroHealingPerSecond = Configuration.HeroHealingPerSecond,
                HealingRange = Configuration.ChurchHealingRange
            });
            Entities.SetComponentData(Settings, new GatheringTuning
            {
                Range = Configuration.GatheringRange,
                CycleSeconds = Configuration.GatheringCycle,
                Amount = Configuration.GatheringAmount,
                ProductionRange = Configuration.ProductionRange
            });
            Entities.AddBuffer<RunWeapon>(Settings);
            Entities.AddComponentData(Settings, new ConstructionEconomyTuning
            {
                FortificationCost = Configuration.FortificationCost,
                ArmamentCost = Configuration.ArmamentCost,
                ForgeWeaponCost = Configuration.ForgeWeaponCost,
                ForgeHealthCost = Configuration.ForgeHealthCost,
                ArtilleryCost = Configuration.ArtilleryCost,
                RefundFraction = Configuration.RefundFraction,
                HealthMultiplier = Configuration.StructureHealthMultiplier,
                DamageMultiplier = Configuration.StructureDamageMultiplier,
                WeaponDamageMultiplier = Configuration.WeaponDamageMultiplier,
                WeaponSpeedMultiplier = Configuration.WeaponSpeedMultiplier,
                HeroHealthIncrease = Configuration.HeroHealthIncrease,
                ForgeRange = Configuration.GatheringRange + 2,
                MaximumLevel = Configuration.MaximumUpgradeLevel,
                ArtilleryRadius = Configuration.ArtilleryRadius,
                ArtilleryDamage = Configuration.ArtilleryDamage,
                ArtilleryDelay = Configuration.ArtilleryDelay
            });
            if (bServer)
            {
                using EntityQuery Economy = Entities.CreateEntityQuery(typeof(RunEconomyState));
                RunEconomyState State = Economy.GetSingleton<RunEconomyState>();
                State.Resources = Configuration.InitialResources;
                Economy.SetSingleton(State);
                using EntityQuery Rewards = Entities.CreateEntityQuery(typeof(RunEconomyTuning));
                RunEconomyTuning Tuning = Rewards.GetSingleton<RunEconomyTuning>();
                Tuning.WoodPerKill = checked((int)Configuration.LootPerKill.Wood);
                Tuning.StonePerKill = checked((int)Configuration.LootPerKill.Stone);
                Tuning.IronPerKill = checked((int)Configuration.LootPerKill.Iron);
                Rewards.SetSingleton(Tuning);
                using EntityQuery Construction = Entities.CreateEntityQuery(typeof(ConstructionRunState));
                DynamicBuffer<ConstructionDefinitionRuntime> Definitions =
                    Entities.GetBuffer<ConstructionDefinitionRuntime>(Construction.GetSingletonEntity());
                Definitions.Clear();
                foreach (BuildingConfiguration Building in Configuration.Buildings)
                {
                    if (string.IsNullOrWhiteSpace(Building.Id) || !Building.Cost.IsValid ||
                        Building.Cost.IsEmpty || Building.Health <= 0 || Building.HalfExtent <= 0)
                    {
                        throw new InvalidOperationException("Invalid building definition: " + Building.Id);
                    }
                    Definitions.Add(new ConstructionDefinitionRuntime
                    {
                        StableId = Building.Id,
                        Kind = Building.Kind,
                        WoodPrice = checked((int)Building.Cost.Wood),
                        StonePrice = checked((int)Building.Cost.Stone),
                        IronPrice = checked((int)Building.Cost.Iron),
                        Health = Building.Health,
                        HalfExtentMeters = Building.HalfExtent,
                        ProductionKind = Building.ProductionKind,
                        ProductionAmountPerCycle = Building.ProductionAmount,
                        ProductionCycleSeconds = Building.ProductionCycle,
                        AttackRange = CombatRange(Building.Kind, Configuration),
                        AttackDamage = CombatDamage(Building.Kind, Configuration),
                        AttackInterval = CombatInterval(Building.Kind, Configuration)
                    });
                }
            }
            else
            {
                Entities.CreateEntity(typeof(RunSimulationTime), typeof(RunActivityState));
                CombatSession.CreateRun(Entities, new CombatRunTuning
                {
                    Damage = Release.HeroAttack.Damage,
                    IntervalSeconds = Release.HeroAttack.IntervalSeconds,
                    RangeMeters = Release.HeroAttack.WeaponRadiusMeters * Release.HeroAttack.RangeScale,
                    HitPaddingMeters = Release.HeroAttack.HitPaddingMeters,
                    HeroMaximumHealth = Release.HeroAttack.HeroMaximumHealth,
                    MaximumStamina = Release.HeroAttack.MaximumStamina,
                    StaminaRecoveryPerSecond = Release.HeroAttack.StaminaRecoveryPerSecond,
                    TargetMaximumHealth = Release.CombatTarget.MaximumHealth,
                    TargetSpawnForwardMeters = Release.CombatTarget.SpawnForwardMeters
                });
                Entity Concurrency = Entities.CreateEntity(typeof(EnemyCombatConcurrency));
                Entities.SetComponentData(Concurrency, new EnemyCombatConcurrency
                {
                    MaximumSimultaneousWindups = Release.EnemyAgent.MaximumSimultaneousWindups
                });
                NavigationSnapshot Navigation = Release.Navigation;
                NavigationObstacleDefinition[] Obstacles = Navigation.GetStaticObstacles();
                float3[] Positions = new float3[Obstacles.Length];
                for (int Index = 0; Index < Obstacles.Length; Index++)
                {
                    Positions[Index] = new float3(Obstacles[Index].XMeters, Obstacles[Index].ZMeters,
                        Obstacles[Index].RadiusMeters);
                }
                World.GetOrCreateSystemManaged<NavigationFlowSystem>().Initialize(new NavigationGridConfig
                {
                    Width = Navigation.WidthCells,
                    Height = Navigation.HeightCells,
                    CellSizeMeters = Navigation.CellSizeMeters,
                    FlowCellsPerTick = Navigation.FlowCellsPerTick,
                    GoalRefreshSeconds = Navigation.GoalRefreshSeconds,
                    GoalMoveThresholdMeters = Navigation.GoalMoveThresholdMeters,
                    MaximumDynamicObstacles = Navigation.MaximumDynamicObstacles
                }, Positions);
            }
        }

        private static float CombatRange(StructureKind Kind, GameplayConfiguration Configuration)
        {
            if (Kind == StructureKind.Tower)
            {
                return Configuration.TowerRange;
            }
            if (Kind == StructureKind.HealingShrine)
            {
                return Configuration.ChurchHealingRange;
            }
            return 0;
        }

        private static float CombatDamage(StructureKind Kind, GameplayConfiguration Configuration)
        {
            if (Kind == StructureKind.Tower)
            {
                return Configuration.TowerDamage;
            }
            if (Kind == StructureKind.HealingShrine)
            {
                return Configuration.HeroHealingPerSecond;
            }
            return 0;
        }

        private static float CombatInterval(StructureKind Kind, GameplayConfiguration Configuration)
        {
            if (Kind == StructureKind.Tower)
            {
                return Configuration.TowerInterval;
            }
            if (Kind == StructureKind.HealingShrine)
            {
                return 1;
            }
            return 0;
        }

        public static void SetWeapons(EntityManager Entities, WeaponInventory Inventory)
        {
            if (Inventory == null || Inventory.Loadout == null || Inventory.Loadout.Length < 1 ||
                Inventory.Loadout.Length > 3 || Inventory.Weapons == null || Inventory.Configurations == null)
            {
                throw new InvalidOperationException("Run weapon loadout is invalid.");
            }
            using EntityQuery Query = Entities.CreateEntityQuery(typeof(RunWeapon));
            DynamicBuffer<RunWeapon> Weapons = Entities.GetBuffer<RunWeapon>(Query.GetSingletonEntity());
            Weapons.Clear();
            foreach (string InstanceId in Inventory.Loadout)
            {
                OwnedWeapon Owned = Array.Find(Inventory.Weapons, Entry => Entry.InstanceId == InstanceId);
                if (Owned == null)
                {
                    throw new InvalidOperationException("Selected weapon is not owned.");
                }
                WeaponConfiguration Configuration = Array.Find(Inventory.Configurations,
                    Entry => Entry.Id == Owned.ConfigurationId);
                if (Configuration == null || Owned.Level < 1 || Owned.Level > Configuration.MaximumLevel)
                {
                    throw new InvalidOperationException("Selected weapon configuration is invalid.");
                }
                Configuration.Validate();
                Weapons.Add(new RunWeapon
                {
                    Value = new EquippedWeapon
                    {
                        InstanceId = InstanceId,
                        ConfigurationId = Configuration.Id,
                        Rarity = Configuration.Rarity,
                        ModelIndex = Configuration.ModelIndex,
                        Level = Owned.Level,
                        Damage = Configuration.CalculateDamage(Owned.Level),
                        Interval = Configuration.CalculateAttackInterval(Owned.Level),
                        Range = Configuration.Range,
                        SpecialAbility = Configuration.SpecialAbility,
                        SpecialMultiplier = Configuration.SpecialDamageMultiplier,
                        SpecialRadius = Configuration.SpecialRadius,
                        SpecialDuration = Configuration.SpecialDuration,
                        SpecialCooldown = Configuration.SpecialCooldown,
                        HalfWidth = Configuration.HalfWidth,
                        TipWidthScale = Configuration.TipWidthScale,
                        DashDamageMultiplier = Configuration.DashDamageMultiplier,
                        DashDistance = Configuration.DashDistance,
                        DashRange = Configuration.DashRange,
                        DashDuration = Configuration.DashDuration,
                        SpecialDistance = Configuration.SpecialDistance,
                        FinishDamageMultiplier = Configuration.FinishDamageMultiplier,
                        FinishRadius = Configuration.FinishRadius,
                        SpecialPush = Configuration.SpecialPush,
                        LaunchHeight = Configuration.LaunchHeight,
                        JumpHeight = Configuration.JumpHeight,
                        DefenseAbility = Configuration.DefenseAbility,
                        DefenseRadius = Configuration.DefenseRadius,
                        DefensePush = Configuration.DefensePush,
                        DefenseDuration = Configuration.DefenseDuration,
                        DefenseShield = Configuration.DefenseShield,
                        DefenseHealing = Configuration.DefenseHealing,
                        DefenseStamina = Configuration.DefenseStamina,
                        DefenseDamageBonus = Configuration.DefenseDamageBonus,
                        DefenseDamageDuration = Configuration.DefenseDamageDuration,
                        DefensePulse = Configuration.DefensePulse
                    }
                });
            }
        }
    }
}
