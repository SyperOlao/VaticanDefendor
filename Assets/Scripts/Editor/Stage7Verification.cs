using System;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEditor;
using UnityEngine;
using VaticanDefendor.Combat;
using VaticanDefendor.Construction;
using VaticanDefendor.Content;
using VaticanDefendor.Navigation;
using VaticanDefendor.Production;
using VaticanDefendor.RunEconomy;
using VaticanDefendor.Session;

namespace VaticanDefendor.Editor
{
    public static class Stage7Verification
    {
        [MenuItem("VaticanDefendor/Stage 7/Verify Construction Economy Production")]
        public static void Verify()
        {
            VerifyContent();
            using var world = new World("Stage 7 authoritative modules", WorldFlags.GameServer);
            EntityManager entities = world.EntityManager;
            Entity clock = RunSimulationSession.Create(entities, Guid.NewGuid().ToString("D"));
            Entity economyEntity = RunEconomySession.Create(entities, 70, 5, 2);
            NavigationFlowSystem navigation = world.GetOrCreateSystemManaged<NavigationFlowSystem>();
            navigation.Initialize(new NavigationGridConfig
            {
                Width = 15, Height = 15, CellSizeMeters = 2.4f,
                FlowCellsPerTick = 32, GoalRefreshSeconds = 0.5f,
                GoalMoveThresholdMeters = 1f, MaximumDynamicObstacles = 32
            }, new[] { new float3(0f, 4.8f, 0.5f) });
            Entity grid = entities.CreateEntityQuery(typeof(NavigationGridConfig)).GetSingletonEntity();
            Entity hero = entities.CreateEntity(typeof(HeroCombatStamina), typeof(CombatHealth),
                typeof(LocalTransform));
            entities.SetComponentData(hero, new CombatHealth { Current = 100f, Maximum = 100f });
            entities.SetComponentData(hero, LocalTransform.FromPosition(float3.zero));
            Entity prefab = entities.CreateEntity(typeof(Prefab), typeof(LocalTransform),
                typeof(StructureInstance), typeof(ProductionSource));
            Entity run = ConstructionSession.Create(entities, new ConstructionRunRules
            {
                GridWidth = 15, GridHeight = 15, CellSizeMeters = 2.4f,
                MaximumWallCells = 12, MinimumCommandIntervalSeconds = 0.25f,
                HeroClearanceMeters = 1.4f
            }, new[]
            {
                new ConstructionDefinitionRuntime
                {
                    StableId = "structure.wall", Kind = StructureKind.Wall,
                    GoldPrice = 10, HalfExtentMeters = 1.2f
                },
                new ConstructionDefinitionRuntime
                {
                    StableId = "structure.sawmill", Kind = StructureKind.Sawmill,
                    GoldPrice = 50, HalfExtentMeters = 1.6f,
                    ProductionCycleSeconds = 10f, ProductionGoldPerCycle = 25,
                    PickupOffsetZMeters = 2.6f, PickupRadiusMeters = 1.1f
                }
            });
            RunEconomyState economy = entities.GetComponentData<RunEconomyState>(economyEntity);
            SetTime(entities, clock, 1d, 0f);
            ConstructionResultRpc insufficient = Apply(entities, run, prefab, grid, navigation,
                hero, ref economy, 1, "structure.wall", 2, 0, 9, 0);
            Require(insufficient.Outcome == ConstructionOutcome.OutOfBounds && economy.Gold == 70 &&
                entities.GetBuffer<OccupiedCell>(run).Length == 0,
                "Out-of-bounds wall changed the authoritative state.");
            SetTime(entities, clock, 1.3d, 0f);
            Require(Apply(entities, run, prefab, grid, navigation, hero, ref economy,
                2, "structure.wall", 0, 2, 0, 2).Outcome == ConstructionOutcome.Blocked,
                "Static obstacle was not rejected.");
            SetTime(entities, clock, 1.6d, 0f);
            Require(Apply(entities, run, prefab, grid, navigation, hero, ref economy,
                3, "structure.wall", 0, 0, 0, 0).Outcome == ConstructionOutcome.HeroTooClose,
                "Hero clearance was not enforced.");
            SetTime(entities, clock, 1.9d, 0f);
            ConstructionResultRpc built = Apply(entities, run, prefab, grid, navigation,
                hero, ref economy, 4, "structure.wall", 2, 0, 4, 0);
            Require(built.Outcome == ConstructionOutcome.Built && built.BuiltCount == 3 &&
                economy.Gold == 40 && entities.GetBuffer<OccupiedCell>(run).Length == 3,
                "Atomic wall did not debit and occupy all three cells.");
            ConstructionResultRpc duplicate = Apply(entities, run, prefab, grid, navigation,
                hero, ref economy, 4, "structure.wall", 2, 0, 4, 0);
            Require(duplicate.Outcome == ConstructionOutcome.Built && duplicate.GoldAfter == 40 &&
                economy.Gold == 40 && entities.GetBuffer<OccupiedCell>(run).Length == 3,
                "Repeated operation charged or placed twice.");
            Require(Apply(entities, run, prefab, grid, navigation, hero, ref economy,
                4, "structure.wall", 2, 1, 4, 1).Outcome ==
                ConstructionOutcome.ConflictingOperation && economy.Gold == 40 &&
                entities.GetBuffer<OccupiedCell>(run).Length == 3,
                "Conflicting operation payload reused a prior success.");
            SetTime(entities, clock, 2.2d, 0f);
            Require(Apply(entities, run, prefab, grid, navigation, hero, ref economy,
                5, "structure.wall", 3, 0, 3, 0).Outcome == ConstructionOutcome.Occupied,
                "Competing placement occupied an existing cell.");
            SetTime(entities, clock, 2.5d, 0f);
            Require(Apply(entities, run, prefab, grid, navigation, hero, ref economy,
                6, "structure.wall", -3, 0, -3, 0).Outcome == ConstructionOutcome.Built &&
                economy.Gold == 30, "Independent subsequent command did not use current balance.");
            navigation.Update();
            Require(!navigation.IsWalkable(new float2(4.8f, 0f)) &&
                navigation.DynamicObstacleCount == 4,
                "Construction did not update Navigation via obstacle requests.");
            SetTime(entities, clock, 2.8d, 0f);
            Require(Apply(entities, run, prefab, grid, navigation, hero, ref economy,
                7, "structure.sawmill", -2, 2, -2, 2).Outcome ==
                ConstructionOutcome.InsufficientGold && economy.Gold == 30,
                "Insufficient funds changed the run balance.");
            economy.Gold = 80;
            SetTime(entities, clock, 3.1d, 0f);
            Require(Apply(entities, run, prefab, grid, navigation, hero, ref economy,
                8, "structure.sawmill", -2, 2, -2, 2).Outcome == ConstructionOutcome.Built &&
                economy.Gold == 30, "Sawmill did not use the same RunEconomy balance.");
            Entity target = entities.CreateEntity(typeof(CombatTargetRole), typeof(CombatHealth),
                typeof(LocalTransform));
            entities.SetComponentData(target, new CombatHealth { Current = 10f, Maximum = 10f });
            entities.SetComponentData(target, LocalTransform.FromPosition(new float3(0f, 0f, 2.4f)));
            SetTime(entities, clock, 3.4d, 0f);
            Require(Apply(entities, run, prefab, grid, navigation, hero, ref economy,
                9, "structure.wall", 0, 1, 0, 1).Outcome == ConstructionOutcome.Occupied &&
                economy.Gold == 30, "Placement overlapped a living combat target.");
            Require(Apply(entities, run, prefab, grid, navigation, hero, ref economy,
                10, "structure.wall", -4, 0, -4, 0).Outcome == ConstructionOutcome.RateLimited &&
                economy.Gold == 30, "Build command rate limit was bypassed.");
            SetTime(entities, clock, 3.7d, 0f);
            Require(Apply(entities, run, prefab, grid, navigation, hero, ref economy,
                11, "structure.wall", -7, -7, 7, 7).Outcome == ConstructionOutcome.InvalidPath &&
                economy.Gold == 30, "Overlong wall command was accepted.");
            entities.SetComponentData(economyEntity, economy);
            Entity sawmill = Entity.Null;
            using (EntityQuery structures = entities.CreateEntityQuery(typeof(StructureInstance),
                typeof(ProductionSource)))
            using (var array = structures.ToEntityArray(Unity.Collections.Allocator.Temp))
                foreach (Entity candidate in array)
                    if (entities.GetComponentData<StructureInstance>(candidate).Kind ==
                        StructureKind.Sawmill) sawmill = candidate;
            Require(sawmill != Entity.Null, "Sawmill ghost was not instantiated.");
            ProductionSystem production = world.GetOrCreateSystemManaged<ProductionSystem>();
            SetTime(entities, clock, 13.1d, 10f);
            production.Update();
            Require(entities.GetComponentData<ProductionSource>(sawmill).StoredGold == 25 &&
                entities.GetComponentData<RunEconomyState>(economyEntity).Gold == 30,
                "Configured production cycle did not store its output.");
            entities.SetComponentData(clock, new RunActivityState { Paused = 1 });
            SetTime(entities, clock, 23.1d, 10f);
            production.Update();
            Require(entities.GetComponentData<ProductionSource>(sawmill).StoredGold == 25,
                "Production advanced while paused.");
            entities.SetComponentData(clock, new RunActivityState { Terminal = 1 });
            production.Update();
            Require(entities.GetComponentData<ProductionSource>(sawmill).StoredGold == 25,
                "Production advanced in terminal state.");
            entities.SetComponentData(clock, default(RunActivityState));
            entities.SetComponentData(hero, LocalTransform.FromPosition(new float3(-4.8f, 0f, 7.4f)));
            SetTime(entities, clock, 23.2d, 0.1f);
            production.Update();
            Require(entities.GetComponentData<ProductionSource>(sawmill).StoredGold == 0 &&
                entities.GetComponentData<RunEconomyState>(economyEntity).Gold == 55,
                "Stored production did not credit the single RunEconomy wallet on pickup.");
            Debug.Log("[Stage7Verification] authority, atomic wall, dedupe, rate/path limits, occupancy, navigation, production, pause and pickup passed.");
        }

        private static void VerifyContent()
        {
            ContentCatalog catalog = AssetDatabase.LoadAssetAtPath<ContentCatalog>(
                "Assets/ActiveCombat/Content/MovementCatalog.asset");
            ContentRelease release = ContentReleaseBuilder.Build(catalog);
            Require(ReferenceEquals(release, ContentReleaseBuilder.Build(catalog)) &&
                release.ContentSchemaVersion == ContentRelease.CurrentContentSchemaVersion &&
                release.GetStructures().Length == 2,
                "Stage 7 release was not reproducible or complete.");
            StructureDefinition wall = AssetDatabase.LoadAssetAtPath<StructureDefinition>(
                "Assets/ActiveCombat/Content/WallStructure.asset");
            StructureDefinition changedWall = UnityEngine.Object.Instantiate(wall);
            ContentCatalog changedCatalog = UnityEngine.Object.Instantiate(catalog);
            try
            {
                var changedStructure = new SerializedObject(changedWall);
                changedStructure.FindProperty("_goldPrice").intValue = wall.GoldPrice + 1;
                changedStructure.ApplyModifiedPropertiesWithoutUndo();
                var changedList = new SerializedObject(changedCatalog);
                changedList.FindProperty("_structures").GetArrayElementAtIndex(0)
                    .objectReferenceValue = changedWall;
                changedList.ApplyModifiedPropertiesWithoutUndo();
                ContentReleaseBuilder.CompiledRelease changed = ContentReleaseBuilder.Compile(changedCatalog);
                int oldWallPrice = -1;
                int newWallPrice = -1;
                foreach (StructureSnapshot definition in release.GetStructures())
                    if (definition.Kind == StructureKind.Wall) oldWallPrice = definition.GoldPrice;
                foreach (StructureSnapshot definition in changed.Structures)
                    if (definition.Kind == StructureKind.Wall) newWallPrice = definition.GoldPrice;
                Require(changed.ReleaseId != release.ReleaseId &&
                    oldWallPrice == wall.GoldPrice && newWallPrice == wall.GoldPrice + 1,
                    "Price authoring did not create a new release while preserving the old one.");
                changedStructure.FindProperty("_goldPrice").intValue = -1;
                changedStructure.ApplyModifiedPropertiesWithoutUndo();
                try
                {
                    ContentReleaseBuilder.Compile(changedCatalog);
                    throw new InvalidOperationException("Invalid construction price was accepted.");
                }
                catch (InvalidOperationException error) when (error.Message.Contains("_goldPrice")) { }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(changedWall);
                UnityEngine.Object.DestroyImmediate(changedCatalog);
            }
        }

        private static ConstructionResultRpc Apply(EntityManager entities, Entity run, Entity prefab,
            Entity grid, NavigationFlowSystem navigation, Entity hero, ref RunEconomyState economy,
            uint id, string definition, int startX, int startZ, int endX, int endZ) =>
            ConstructionSession.Apply(entities, run, prefab, grid, navigation, hero,
                entities.CreateEntityQuery(typeof(RunActivityState)).GetSingleton<RunActivityState>(),
                entities.CreateEntityQuery(typeof(RunSimulationTime)).GetSingleton<RunSimulationTime>(),
                ref economy, new ConstructionRequest
                {
                    OperationId = id, DefinitionId = definition,
                    StartX = startX, StartZ = startZ, EndX = endX, EndZ = endZ
                });

        private static void SetTime(EntityManager entities, Entity clock, double elapsed, float delta) =>
            entities.SetComponentData(clock, new RunSimulationTime
            {
                ElapsedSeconds = elapsed, DeltaSeconds = delta
            });

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
