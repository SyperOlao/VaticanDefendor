using System;
using Unity.Core;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;
using UnityEditor;
using UnityEngine;
using VaticanDefendor.Combat;
using VaticanDefendor.Construction;
using VaticanDefendor.Content;
using VaticanDefendor.Gameplay;
using VaticanDefendor.Movement;
using VaticanDefendor.Navigation;
using VaticanDefendor.Production;
using VaticanDefendor.RunEconomy;
using VaticanDefendor.Session;

namespace VaticanDefendor.Editor
{
    public static class GameplayVerification
    {
        [MenuItem("VaticanDefendor/Gameplay/Verify Shared Gameplay")]
        public static string Run()
        {
            ContentRelease Release = Resources.Load<ContentSelection>("NetworkContent").Release;
            ContentReleaseHash.Validate(Release);
            var SavedAttack = new VaticanDefendor.Server.CheckpointActor
            {
                CombatTuning = new EnemyCombatTuning { Damage = 17, AttackRadiusMeters = 2, WindupSeconds = 0.4f, RecoverySeconds = 0.8f }
            };
            var RestoredAttack = JsonUtility.FromJson<VaticanDefendor.Server.CheckpointActor>(JsonUtility.ToJson(SavedAttack));
            Require(RestoredAttack.CombatTuning.Damage == 17, "Checkpoint serialization discarded enemy combat tuning.");
            using var World = new World("Gameplay verification", WorldFlags.GameServer);
            EntityManager Entities = World.EntityManager;
            World.SetTime(new TimeData(1, 0.1f));
            Entity Clock = RunSimulationSession.Create(Entities, Guid.NewGuid().ToString());
            Entities.SetComponentData(Clock, new RunSimulationTime { DeltaSeconds = 0.1f, ElapsedSeconds = 1 });
            CombatSession.CreateRun(Entities, new CombatRunTuning
            {
                Damage = 18, IntervalSeconds = 0.5f, RangeMeters = 2.5f,
                HeroMaximumHealth = 100, MaximumStamina = 100, StaminaRecoveryPerSecond = 10,
                TargetMaximumHealth = 100, TargetSpawnForwardMeters = 3
            });
            Entity EconomyEntity = RunEconomySession.Create(Entities, 100, 2, 10);
            Entity Construction = ConstructionSession.Create(Entities, new ConstructionRunRules
            {
                GridWidth = 31, GridHeight = 31, CellSizeMeters = 1.2f,
                MaximumWallCells = 64, MinimumCommandIntervalSeconds = 0.05f, HeroClearanceMeters = 0.5f
            }, new[] { new ConstructionDefinitionRuntime { StableId = "initial", WoodPrice = 1, HalfExtentMeters = 0.5f } });
            NavigationFlowSystem Navigation = World.GetOrCreateSystemManaged<NavigationFlowSystem>();
            Navigation.Initialize(new NavigationGridConfig
            {
                Width = 31, Height = 31, CellSizeMeters = 1.2f, FlowCellsPerTick = 1024,
                GoalRefreshSeconds = 0.1f, GoalMoveThresholdMeters = 0.5f, MaximumDynamicObstacles = 100
            }, Array.Empty<float3>());
            SharedGameplayInitialization.Initialize(World, Release, true);
            SharedGameplayInitialization.SetWeapons(Entities, JsonUtility.FromJson<WeaponInventory>(Resources.Load<TextAsset>("DevelopmentWeapons").text));
            Entity Hero = Entities.CreateEntity(typeof(LocalTransform), typeof(CombatHealth), typeof(HeroCombatStamina),
                typeof(HeroActionState), typeof(HeroMoveInput), typeof(HeroMovementTuning), typeof(Simulate));
            Entities.AddBuffer<EquippedWeapon>(Hero);
            Entities.SetComponentData(Hero, LocalTransform.FromPosition(new float3(-5, 0, 0)));
            Entities.SetComponentData(Hero, new HeroMovementTuning { WalkSpeedMetersPerSecond = 4, RunSpeedMetersPerSecond = 6 });
            CombatSession.InitializeHero(Entities, Hero, Get<CombatRunTuning>(Entities));
            RunEconomyState Economy = Entities.GetComponentData<RunEconomyState>(EconomyEntity);
            ResourceAmounts Before = Economy.Resources;
            Require(!RunEconomySession.TrySpend(ref Economy, new ResourceAmounts(1, 100000, 0)) && Economy.Resources.Wood == Before.Wood,
                "A failed multi-resource transaction consumed a partial price.");
            Entity Node = Entities.CreateEntity(typeof(ResourceNode), typeof(StructureInstance), typeof(LocalTransform));
            Entities.SetComponentData(Node, new ResourceNode { Kind = ResourceKind.Wood, Remaining = 20, Capacity = 20, WaveNumber = 1 });
            Entities.SetComponentData(Node, new StructureInstance { StructureId = 100, Kind = StructureKind.ResourceDeposit });
            Entities.SetComponentData(Node, LocalTransform.FromPosition(new float3(-6, 0, 0)));
            HeroMoveInput Input = default;
            Input.Gathering.Set();
            Entities.SetComponentData(Hero, Input);
            CombatResolutionSystem Combat = World.GetOrCreateSystemManaged<CombatResolutionSystem>();
            GatheringSystem Gathering = World.GetOrCreateSystemManaged<GatheringSystem>();
            Combat.Update();
            Input.Gathering = default;
            Entities.SetComponentData(Hero, Input);
            Entities.AddComponent<Simulate>(Node);
            Entities.SetComponentEnabled<Simulate>(Node, false);
            for (int Step = 0; Step < 10; Step++)
            {
                Gathering.Update();
            }
            Require(Get<RunEconomyState>(Entities).Wood == Before.Wood &&
                Entities.GetComponentData<ResourceNode>(Node).Remaining == 20 &&
                Entities.GetComponentData<HeroActionState>(Hero).GatheringProgress == 0,
                "Gathering changed a deposit outside the current prediction tick.");
            Entities.SetComponentEnabled<Simulate>(Node, true);
            for (int Step = 0; Step < 8; Step++)
            {
                Combat.Update();
                Gathering.Update();
            }
            Economy = Entities.GetComponentData<RunEconomyState>(EconomyEntity);
            Require(Economy.Wood == Before.Wood + Release.GetGameplay().GatheringAmount &&
                Entities.GetComponentData<ResourceNode>(Node).Remaining == 20 - Release.GetGameplay().GatheringAmount,
                "Manual gathering failed to debit the same deposit that credited the bank.");
            Input.Direction = new float2(1, 0);
            Entities.SetComponentData(Hero, Input);
            Combat.Update();
            Require(Entities.GetComponentData<HeroActionState>(Hero).Gathering == 0, "Movement did not cancel gathering.");
            HeroActionState Action = Entities.GetComponentData<HeroActionState>(Hero);
            Action.Gathering = 1;
            Entities.SetComponentData(Hero, Action);
            CombatDamage.Apply(Entities, Hero, 1);
            Require(Entities.GetComponentData<HeroActionState>(Hero).Gathering == 0, "Damage did not cancel gathering.");
            Entity Producer = Entities.CreateEntity(typeof(ProductionSource), typeof(LocalTransform), typeof(CombatHealth), typeof(Simulate));
            Entities.SetComponentData(Producer, LocalTransform.FromPosition(new float3(-7, 0, 0)));
            Entities.SetComponentData(Producer, new CombatHealth { Current = 100, Maximum = 100 });
            Entities.SetComponentData(Producer, new ProductionSource { Kind = ResourceKind.Wood, CycleSeconds = 0.1f, AmountPerCycle = 100 });
            ProductionSystem Production = World.GetOrCreateSystemManaged<ProductionSystem>();
            Entities.SetComponentEnabled<Simulate>(Node, false);
            int RemainingBeforeProduction = Entities.GetComponentData<ResourceNode>(Node).Remaining;
            long BalanceBeforeProduction = Get<RunEconomyState>(Entities).Wood;
            Production.Update();
            Require(Entities.GetComponentData<ResourceNode>(Node).Remaining == RemainingBeforeProduction &&
                Get<RunEconomyState>(Entities).Wood == BalanceBeforeProduction,
                "Production changed a deposit outside the current prediction tick.");
            Entities.SetComponentEnabled<Simulate>(Node, true);
            Production.Update();
            long ExhaustedBalance = Get<RunEconomyState>(Entities).Wood;
            Require(Entities.GetComponentData<ResourceNode>(Node).Remaining == 0, "Production ignored the finite deposit.");
            for (int Step = 0; Step < 20; Step++)
            {
                Production.Update();
            }
            Require(Get<RunEconomyState>(Entities).Wood == ExhaustedBalance, "An exhausted deposit produced resources.");
            Input = default;
            Entities.SetComponentData(Hero, Input);
            Entity Enemy = Entities.CreateEntity(typeof(CombatTargetTag), typeof(CombatHealth), typeof(LocalTransform));
            Entities.SetComponentData(Enemy, LocalTransform.FromPosition(new float3(-4, 0, 0)));
            Entities.SetComponentData(Enemy, new CombatHealth { Current = 1000, Maximum = 1000 });
            Action = Entities.GetComponentData<HeroActionState>(Hero);
            Action.AttackRemaining = 0;
            Action.Facing = new float2(1, 0);
            Entities.SetComponentData(Hero, Action);
            Combat.Update();
            Require(Entities.GetComponentData<CombatHealth>(Enemy).Current < 1000, "Automatic weapon attack did not run.");
            DynamicBuffer<EquippedWeapon> Weapons = Entities.GetBuffer<EquippedWeapon>(Hero);
            EquippedWeapon Other = Weapons[0];
            Other.InstanceId = "00000000-0000-0000-0000-000000000002";
            Other.ModelIndex = 1;
            Other.Damage = 9;
            Weapons.Add(Other);
            Input.SelectedSlot = 2;
            Entities.SetComponentData(Hero, Input);
            Combat.Update();
            Require(Entities.GetComponentData<HeroActionState>(Hero).ActiveSlot == 1, "Active weapon selection was not simulated.");
            Input.SelectedSlot = 0;
            Entities.SetComponentData(Hero, Input);
            Combat.Update();
            Require(Entities.GetComponentData<HeroActionState>(Hero).ActiveSlot == 1, "Missing input changed the restored active weapon.");
            Entities.SetComponentData(Hero, LocalTransform.FromPosition(new float3(18, 0, 0)));
            Input.Direction = new float2(1, 0);
            Entities.SetComponentData(Hero, Input);
            for (int Step = 0; Step < 20; Step++)
            {
                World.GetOrCreateSystemManaged<HeroMovementSystem>().Update();
            }
            Require(Navigation.IsWalkable(Entities.GetComponentData<LocalTransform>(Hero).Position.xz), "Hero left the navigation grid.");
            Entities.SetComponentData(Hero, LocalTransform.FromPosition(new float3(-10, 0, 0)));
            HeroActionTuning DashTuning = Get<HeroActionTuning>(Entities);
            Action = Entities.GetComponentData<HeroActionState>(Hero);
            Action.Facing = new float2(1, 0);
            Action.DashRemaining = DashTuning.DashDuration;
            Action.MotionKind = 1;
            Action.MotionDuration = DashTuning.DashDuration;
            Action.MotionDistance = DashTuning.DashDistance;
            Action.AttackDirection = Action.Facing;
            Action.MotionElapsed = 0;
            Action.PreviousPosition = new float2(-10, 0);
            Entities.SetComponentData(Hero, Action);
            Entities.SetComponentData(Hero, new HeroMoveInput());
            for (int Step = 0; Step < 3; Step++)
            {
                World.GetOrCreateSystemManaged<HeroMovementSystem>().Update();
                Combat.Update();
            }
            Require(math.abs(Entities.GetComponentData<LocalTransform>(Hero).Position.x + 10 - DashTuning.DashDistance) < 0.001f,
                "Dash distance depended on a partial final simulation tick.");
            Entities.SetComponentData(Hero, LocalTransform.FromPosition(new float3(15, 0, 0)));
            VerifyConstruction(World, Construction, Hero);
            return "Shared gameplay passed: placement, construction replay, upgrades, sales, atomic resources, manual gathering, production, interruption, weapon selection, combat, navigation bounds.";
        }

        private static void VerifyConstruction(World World, Entity Construction, Entity Hero)
        {
            EntityManager Entities = World.EntityManager;
            Entity Prefab = Entities.CreateEntity(typeof(Prefab), typeof(StructureInstance), typeof(LocalTransform),
                typeof(ProductionSource), typeof(CombatHealth), typeof(StructureCombatState), typeof(ResourceNode));
            Entity Church = Entities.CreateEntity(typeof(ChurchTag), typeof(ChurchRunState), typeof(StructureInstance),
                typeof(CombatHealth), typeof(LocalTransform), typeof(StructureCombatState));
            Entities.SetComponentData(Church, new StructureInstance { StructureId = int.MaxValue - 1, Kind = StructureKind.Church, HalfExtent = 1.5f, Level = 1 });
            Entities.SetComponentData(Church, new CombatHealth { Current = 500, Maximum = 500 });
            Entities.SetComponentData(Church, LocalTransform.Identity);
            NavigationFlowSystem Navigation = World.GetExistingSystemManaged<NavigationFlowSystem>();
            using EntityQuery NavigationQuery = Entities.CreateEntityQuery(typeof(NavigationGridConfig));
            Entity NavigationEntity = NavigationQuery.GetSingletonEntity();
            RunEconomyState Economy = Get<RunEconomyState>(Entities);
            Economy.Resources = new ResourceAmounts(1000, 1000, 1000);
            ConstructionRequest Request = new ConstructionRequest
            {
                OperationId = 1, DefinitionId = "structure.tower.default", StartX = 0, StartZ = 0, EndX = 0, EndZ = 0
            };
            RunSimulationTime Time = new RunSimulationTime { ElapsedSeconds = 10 };
            ConstructionResultRpc Result = ConstructionSession.Apply(Entities, Construction, Prefab, NavigationEntity,
                Navigation, Hero, default, Time, ref Economy, Request);
            Require(Result.Outcome == ConstructionOutcome.Occupied, "Construction overlapped the church.");
            Request.OperationId++;
            Request.StartX = Request.EndX = 5;
            Request.StartZ = Request.EndZ = 5;
            Time.ElapsedSeconds++;
            ResourceAmounts Before = Economy.Resources;
            Result = ConstructionSession.Apply(Entities, Construction, Prefab, NavigationEntity, Navigation, Hero, default, Time, ref Economy, Request);
            Require(Result.Outcome == ConstructionOutcome.Built && Result.BuiltCount == 1, "Tower construction failed.");
            ResourceAmounts After = Economy.Resources;
            Require(After.Wood < Before.Wood, "Construction did not consume resources.");
            ConstructionSession.Apply(Entities, Construction, Prefab, NavigationEntity, Navigation, Hero, default, Time, ref Economy, Request);
            Require(Economy.Resources.Wood == After.Wood, "A repeated construction command spent resources twice.");
            Request.OperationId++;
            Request.Operation = ConstructionOperation.Upgrade;
            Request.TargetStructureId = Entities.GetComponentData<ConstructionRunState>(Construction).NextStructureId - 1;
            Time.ElapsedSeconds++;
            Result = ConstructionSession.Apply(Entities, Construction, Prefab, NavigationEntity, Navigation, Hero, default, Time, ref Economy, Request);
            Require(Result.Outcome == ConstructionOutcome.Built, "Structure upgrade failed.");
            Request.OperationId++;
            Request.Operation = ConstructionOperation.Sell;
            Time.ElapsedSeconds++;
            Before = Economy.Resources;
            Result = ConstructionSession.Apply(Entities, Construction, Prefab, NavigationEntity, Navigation, Hero, default, Time, ref Economy, Request);
            Require(Result.Outcome == ConstructionOutcome.Built && Economy.Wood > Before.Wood, "Structure sale did not refund resources.");
        }

        private static T Get<T>(EntityManager Entities) where T : unmanaged, IComponentData
        {
            using EntityQuery Query = Entities.CreateEntityQuery(typeof(T));
            return Query.GetSingleton<T>();
        }

        private static void Require(bool Condition, string Message)
        {
            if (!Condition)
            {
                throw new InvalidOperationException(Message);
            }
        }
    }
}
