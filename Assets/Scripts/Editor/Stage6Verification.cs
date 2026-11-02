using System;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEditor;
using UnityEngine;
using VaticanDefendor.Agents;
using VaticanDefendor.Combat;
using VaticanDefendor.Content;
using VaticanDefendor.Navigation;
using VaticanDefendor.Session;
using VaticanDefendor.Waves;

namespace VaticanDefendor.Editor
{
    public static class Stage6Verification
    {
        [MenuItem("VaticanDefendor/Stage 6/Verify Agents Navigation Waves")]
        public static void Verify()
        {
            ContentCatalog catalog = AssetDatabase.LoadAssetAtPath<ContentCatalog>(
                "Assets/ActiveCombat/Content/MovementCatalog.asset");
            ContentRelease release = ContentReleaseBuilder.Build(catalog);
            Require(ReferenceEquals(release, ContentReleaseBuilder.Build(catalog)),
                "Identical Stage 6 authoring changed release identity.");
            Require(release.Navigation.GetStaticObstacles().Length == 24 &&
                release.EnemyAgent.SpeedMetersPerSecond == 2.55f &&
                release.WaveSchedule.InitialEnemyCount == 7,
                "Saved arena and weak enemy values did not compile into release.");
            VerifyNavigation();
            VerifyWaves();
            VerifyEnemyCombat();
            Debug.Log("[Stage6Verification] static/dynamic obstacles, budgeted flow, enemy movement, wave cap/timing and Combat windup/recovery passed.");
        }

        private static void VerifyNavigation()
        {
            using var world = new World("Stage 6 navigation", WorldFlags.GameServer);
            EntityManager entities = world.EntityManager;
            Entity clock = RunSimulationSession.Create(entities, Guid.NewGuid().ToString("D"));
            NavigationFlowSystem navigation = world.GetOrCreateSystemManaged<NavigationFlowSystem>();
            navigation.Initialize(new NavigationGridConfig
            {
                Width = 9, Height = 9, CellSizeMeters = 1f,
                FlowCellsPerTick = 8, GoalRefreshSeconds = 0.5f,
                GoalMoveThresholdMeters = 1f, MaximumDynamicObstacles = 4
            }, new[] { new float3(0f, 2f, 0.45f) });
            Entity hero = entities.CreateEntity(typeof(HeroCombatStamina), typeof(CombatHealth),
                typeof(LocalTransform));
            entities.SetComponentData(hero, new CombatHealth { Current = 100f, Maximum = 100f });
            entities.SetComponentData(hero, LocalTransform.FromPosition(float3.zero));
            Entity enemy = entities.CreateEntity(typeof(EnemyAgentTag), typeof(CombatHealth),
                typeof(LocalTransform), typeof(NavigationGoal), typeof(NavigationSteering),
                typeof(EnemyAgentTuning), typeof(EnemyCombatTuning),
                typeof(EnemyCombatAttackState));
            entities.AddBuffer<EnemyAttackIntent>(enemy);
            entities.SetComponentData(enemy, new CombatHealth { Current = 53f, Maximum = 53f });
            entities.SetComponentData(enemy, LocalTransform.FromPosition(new float3(0f, 0f, 4f)));
            entities.SetComponentData(enemy, new NavigationGoal { StoppingDistanceMeters = 1f });
            entities.SetComponentData(enemy, new EnemyAgentTuning { SpeedMetersPerSecond = 2f,
                FormationDistanceMeters = 1f });
            entities.SetComponentData(enemy, new EnemyCombatTuning { AttackRadiusMeters = 1f });
            EnemyDecisionSystem decision = world.GetOrCreateSystemManaged<EnemyDecisionSystem>();
            EnemyMovementSystem movement = world.GetOrCreateSystemManaged<EnemyMovementSystem>();
            float largestSideStep = 0f;
            for (int tick = 1; tick <= 100; tick++)
            {
                entities.SetComponentData(clock, new RunSimulationTime
                {
                    Tick = (ulong)tick, ElapsedSeconds = tick * 0.1d, DeltaSeconds = 0.1f
                });
                decision.Update();
                navigation.Update();
                movement.Update();
                float3 position = entities.GetComponentData<LocalTransform>(enemy).Position;
                Require(navigation.IsWalkable(new float2(position.x, position.z)),
                    "Enemy entered a blocked cell.");
                largestSideStep = math.max(largestSideStep, math.abs(position.x));
            }
            float3 final = entities.GetComponentData<LocalTransform>(enemy).Position;
            Require(largestSideStep > 0.5f && math.length(new float2(final.x, final.z)) < 1.3f,
                $"Enemy did not route around the obstacle: {final}.");
            uint oldVersion = navigation.FlowVersion;
            Entity grid = entities.CreateEntityQuery(typeof(NavigationGridConfig)).GetSingletonEntity();
            entities.GetBuffer<NavigationObstacleRequest>(grid).Add(new NavigationObstacleRequest
            {
                StableId = 42, Center = new float2(1f, 1f), RadiusMeters = 0.45f
            });
            navigation.Update();
            Require(!navigation.IsWalkable(new float2(1f, 1f)) &&
                navigation.DynamicObstacleCount == 1,
                "Dynamic blocker upsert did not change occupancy.");
            for (int tick = 0; tick < 30; tick++) navigation.Update();
            Require(navigation.FlowVersion > oldVersion,
                "Dynamic blocker did not rebuild the flow field.");
            entities.GetBuffer<NavigationObstacleRequest>(grid).Add(new NavigationObstacleRequest
            {
                StableId = 42, Remove = 1
            });
            navigation.Update();
            Require(navigation.IsWalkable(new float2(1f, 1f)) &&
                navigation.DynamicObstacleCount == 0,
                "Dynamic blocker removal did not restore occupancy.");
        }

        private static void VerifyWaves()
        {
            using var world = new World("Stage 6 waves", WorldFlags.GameServer);
            EntityManager entities = world.EntityManager;
            Entity clock = RunSimulationSession.Create(entities, Guid.NewGuid().ToString("D"));
            Entity schedule = WaveSession.Create(entities, new WaveScheduleConfig
            {
                InitialDelaySeconds = 0f, InitialEnemyCount = 2, EnemiesAddedPerWave = 2,
                SpawnIntervalSeconds = 0.1f, MaximumSpawnsPerTick = 4,
                WaveDelaySeconds = 1f, MaximumSimultaneousEnemies = 1,
                MaximumWaves = 2, SpawnRadiusMeters = 4f
            });
            WaveScheduleSystem waves = world.GetOrCreateSystemManaged<WaveScheduleSystem>();
            SetTime(entities, clock, 0d);
            waves.Update();
            Require(entities.GetBuffer<WaveSpawnRequest>(schedule).Length == 1,
                "Wave cap must schedule one enemy at a time.");
            entities.GetBuffer<WaveSpawnRequest>(schedule).Clear();
            Entity first = entities.CreateEntity(typeof(EnemyAgentTag), typeof(CombatHealth));
            entities.SetComponentData(first, new CombatHealth { Current = 53f });
            SetTime(entities, clock, 0.5d);
            waves.Update();
            Require(entities.GetBuffer<WaveSpawnRequest>(schedule).Length == 0,
                "Wave exceeded simultaneous enemy cap.");
            entities.SetComponentData(first, new CombatHealth { Dead = 1 });
            waves.Update();
            Require(entities.GetBuffer<WaveSpawnRequest>(schedule).Length == 1,
                "Wave did not resume after an enemy death.");
            entities.GetBuffer<WaveSpawnRequest>(schedule).Clear();
            SetTime(entities, clock, 0.6d);
            waves.Update();
            Require(entities.GetComponentData<WaveState>(schedule).Phase == 3,
                "Cleared wave did not enter configured delay.");
            SetTime(entities, clock, 1.7d);
            waves.Update();
            Require(entities.GetComponentData<WaveState>(schedule).WaveNumber == 2,
                "Second wave did not start after delay.");
        }

        private static void VerifyEnemyCombat()
        {
            using var world = new World("Stage 6 enemy Combat", WorldFlags.GameServer);
            EntityManager entities = world.EntityManager;
            Entity clock = RunSimulationSession.Create(entities, Guid.NewGuid().ToString("D"));
            Entity run = entities.CreateEntity(typeof(CombatRunState));
            entities.SetComponentData(run, new CombatRunState { NextEventSequence = 1 });
            Entity concurrency = entities.CreateEntity(typeof(EnemyCombatConcurrency));
            entities.SetComponentData(concurrency, new EnemyCombatConcurrency
            {
                MaximumSimultaneousWindups = 1
            });
            Entity hero = entities.CreateEntity(typeof(HeroCombatStamina), typeof(CombatHealth),
                typeof(LocalTransform));
            entities.SetComponentData(hero, new CombatHealth { Current = 100f, Maximum = 100f });
            entities.SetComponentData(hero, LocalTransform.FromPosition(float3.zero));
            Entity enemy = entities.CreateEntity(typeof(CombatHealth), typeof(LocalTransform),
                typeof(EnemyCombatTuning), typeof(EnemyCombatAttackState));
            entities.SetComponentData(enemy, new CombatHealth { Current = 53f });
            entities.SetComponentData(enemy, LocalTransform.FromPosition(new float3(0f, 0f, 2f)));
            entities.SetComponentData(enemy, new EnemyCombatTuning
            {
                Damage = 7f, AttackRadiusMeters = 3.2f,
                WindupSeconds = 0.7f, RecoverySeconds = 1.3f
            });
            DynamicBuffer<EnemyAttackIntent> intents = entities.AddBuffer<EnemyAttackIntent>(enemy);
            EnemyCombatResolutionSystem combat = world.GetOrCreateSystemManaged<EnemyCombatResolutionSystem>();
            intents.Add(new EnemyAttackIntent { Target = hero });
            SetTime(entities, clock, 0d);
            combat.Update();
            Entity secondEnemy = entities.CreateEntity(typeof(CombatHealth), typeof(LocalTransform),
                typeof(EnemyCombatTuning), typeof(EnemyCombatAttackState));
            entities.SetComponentData(secondEnemy, new CombatHealth { Current = 53f });
            entities.SetComponentData(secondEnemy, LocalTransform.FromPosition(new float3(0f, 0f, 2f)));
            entities.SetComponentData(secondEnemy, entities.GetComponentData<EnemyCombatTuning>(enemy));
            entities.AddBuffer<EnemyAttackIntent>(secondEnemy).Add(new EnemyAttackIntent { Target = hero });
            SetTime(entities, clock, 0.6d);
            combat.Update();
            Require(entities.GetComponentData<EnemyCombatAttackState>(secondEnemy).Phase == 0,
                "Enemy exceeded maximum simultaneous windups.");
            Require(entities.GetComponentData<CombatHealth>(hero).Current == 100f,
                "Enemy damage occurred before configured windup.");
            SetTime(entities, clock, 0.7d);
            combat.Update();
            Require(entities.GetComponentData<CombatHealth>(hero).Current == 93f,
                "Combat did not apply configured enemy damage after windup.");
            entities.GetBuffer<EnemyAttackIntent>(enemy).Add(new EnemyAttackIntent { Target = hero });
            SetTime(entities, clock, 1d);
            combat.Update();
            Require(entities.GetComponentData<CombatHealth>(hero).Current == 93f,
                "Enemy intent bypassed recovery.");
            entities.SetComponentData(enemy, new CombatHealth { Dead = 1 });
            SetTime(entities, clock, 3d);
            combat.Update();
            Require(entities.GetComponentData<CombatHealth>(hero).Current == 93f,
                "Dead enemy applied damage.");
        }

        private static void SetTime(EntityManager entities, Entity clock, double seconds) =>
            entities.SetComponentData(clock, new RunSimulationTime
            {
                ElapsedSeconds = seconds, DeltaSeconds = 0.1f
            });

        private static void Require(bool value, string message)
        {
            if (!value) throw new InvalidOperationException(message);
        }
    }
}
