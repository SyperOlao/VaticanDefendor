using System;
using Unity.Core;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;
using UnityEditor;
using UnityEngine;
using VaticanDefendor.Combat;
using VaticanDefendor.Content;
using VaticanDefendor.RunEconomy;
using VaticanDefendor.Server;

namespace VaticanDefendor.Editor
{
    public static class CombatStageVerification
    {
        [MenuItem("VaticanDefendor/Combat/Verify Stage 5 Rules")]
        public static void Verify()
        {
            ContentCatalog catalog = AssetDatabase.LoadAssetAtPath<ContentCatalog>(
                "Assets/ActiveCombat/Content/MovementCatalog.asset");
            ContentRelease release = ContentReleaseBuilder.Build(catalog);
            Require(ReferenceEquals(release, ContentReleaseBuilder.Build(catalog)),
                "Identical authoring must resolve to the same release asset.");
            HeroAttackSnapshot attack = release.HeroAttack;
            Require(attack.Damage == 30f && attack.IntervalSeconds == 0.25f &&
                release.CombatTarget.MaximumHealth == 53f && release.RunEconomy.GoldPerKill == 8,
                "Saved sword, weak enemy and reward values were not transferred exactly.");

            using (var world = new World("Combat rules verification", WorldFlags.GameServer))
            {
                EntityManager entities = world.EntityManager;
                CombatRunTuning tuning = Tuning(release);
                Entity combatRun = CombatSession.CreateRun(entities, tuning);
                Entity economyEntity = RunEconomySession.Create(entities, release.RunEconomy.InitialGold,
                    release.RunEconomy.GoldPerKill, release.RunEconomy.ExperiencePerKill);
                Entity hero = Hero(entities, tuning);
                Entity target = entities.CreateEntity(typeof(CombatTargetTag), typeof(CombatHealth),
                    typeof(LocalTransform));
                entities.SetComponentData(target,
                    LocalTransform.FromPosition(new float3(0f, 0f, 2f)));
                entities.SetComponentData(target, new CombatHealth { Current = 53f, Maximum = 53f });
                CombatResolutionSystem system = world.GetOrCreateSystemManaged<CombatResolutionSystem>();

                CombatAttackResult first = Attack(world, system, hero, 0d, 1, new float2(0f, 1f)).Value;
                Require(first.Outcome == CombatAttackOutcome.Hit && first.DamageApplied == 30f &&
                    entities.GetComponentData<CombatHealth>(target).Current == 23f,
                    "Ordinary sword hit must apply the configured server damage.");
                Require(!Attack(world, system, hero, 0.05d, 1, new float2(0f, 1f)).HasValue,
                    "Duplicate command sequence must not produce another result.");
                Require(Attack(world, system, hero, 0.1d, 2, new float2(0f, 1f)).Value.Outcome ==
                    CombatAttackOutcome.Cooldown, "Spam must not bypass cooldown.");
                Require(Attack(world, system, hero, 0.3d, 3, new float2(0f, -1f)).Value.Outcome ==
                    CombatAttackOutcome.Miss, "Wrong facing must miss.");
                entities.SetComponentData(target,
                    LocalTransform.FromPosition(new float3(0f, 0f, 10f)));
                Require(Attack(world, system, hero, 0.6d, 4, new float2(0f, 1f)).Value.Outcome ==
                    CombatAttackOutcome.Miss, "Out-of-range attack must miss.");
                entities.SetComponentData(target,
                    LocalTransform.FromPosition(new float3(0f, 0f, 2f)));
                CombatAttackResult death = Attack(world, system, hero, 0.9d, 5, new float2(0f, 1f)).Value;
                CombatHealth health = entities.GetComponentData<CombatHealth>(target);
                RunEconomyState economy = entities.GetComponentData<RunEconomyState>(economyEntity);
                Require(death.Outcome == CombatAttackOutcome.Hit && death.TargetDied == 1 &&
                    health.Current == 0f && health.Dead == 1 && economy.Gold == 158 &&
                    economy.Experience == 18 && economy.Kills == 1,
                    "First death must set health and apply RunEconomy reward once.");
                Require(Attack(world, system, hero, 1.2d, 6, new float2(0f, 1f)).Value.Outcome ==
                    CombatAttackOutcome.Miss, "A dead target must not be hit again.");
                Require(!RunEconomySession.ApplyKillReward(ref economy,
                    entities.GetComponentData<RunEconomyTuning>(economyEntity), death.EventSequence) &&
                    economy.Gold == 158, "Repeated death reward must be ignored.");
                Require(Attack(world, system, hero, 1.5d, 7, new float2(float.NaN, 1f)).Value.Outcome ==
                    CombatAttackOutcome.InvalidAim, "Non-finite aim must be rejected.");
                tuning.StaminaCost = 5f;
                entities.SetComponentData(combatRun, tuning);
                entities.SetComponentData(hero, new HeroCombatStamina { Current = 0f, Maximum = 100f });
                Require(Attack(world, system, hero, 1.8d, 8, new float2(0f, 1f)).Value.Outcome ==
                    CombatAttackOutcome.NoStamina, "Configured action cost must be checked.");
                Require(entities.GetComponentData<CombatRunTuning>(combatRun).Damage == 30f,
                    "Existing session must keep its compiled damage.");
                VerifyOwnership(world, hero);
            }

            HeroAttackDefinition changedAttack = ScriptableObject.CreateInstance<HeroAttackDefinition>();
            ContentCatalog changedCatalog = ScriptableObject.CreateInstance<ContentCatalog>();
            try
            {
                SetFloat(changedAttack, "_damage", 31f);
                Configure(changedCatalog, catalog, changedAttack);
                ContentReleaseBuilder.CompiledRelease changed = ContentReleaseBuilder.Compile(changedCatalog);
                Require(changed.ReleaseId != release.ReleaseId && changed.HeroAttack.Damage == 31f,
                    "Damage authoring change must create a different compiled release.");
                Require(FirstHitHealth(changed) == 22f,
                    "A new session must apply changed damage to the same target.");
                SetFloat(changedAttack, "_damage", -1f);
                try
                {
                    ContentReleaseBuilder.Compile(changedCatalog);
                    throw new InvalidOperationException("Invalid damage was accepted.");
                }
                catch (InvalidOperationException error) when (error.Message.Contains("_damage")) { }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(changedCatalog);
                UnityEngine.Object.DestroyImmediate(changedAttack);
            }
            Debug.Log("[CombatStageVerification] hit, direction, range, cooldown, duplicate, ownership, death, reward, cost and release pinning passed.");
        }

        private static float FirstHitHealth(ContentReleaseBuilder.CompiledRelease compiled)
        {
            using var world = new World("Changed combat release", WorldFlags.GameServer);
            EntityManager entities = world.EntityManager;
            var tuning = new CombatRunTuning
            {
                Damage = compiled.HeroAttack.Damage,
                IntervalSeconds = compiled.HeroAttack.IntervalSeconds,
                RangeMeters = compiled.HeroAttack.WeaponRadiusMeters * compiled.HeroAttack.RangeScale,
                HitPaddingMeters = compiled.HeroAttack.HitPaddingMeters,
                StaminaCost = compiled.HeroAttack.StaminaCost,
                HeroMaximumHealth = compiled.HeroAttack.HeroMaximumHealth,
                MaximumStamina = compiled.HeroAttack.MaximumStamina,
                StaminaRecoveryPerSecond = compiled.HeroAttack.StaminaRecoveryPerSecond,
                TargetMaximumHealth = compiled.CombatTarget.MaximumHealth,
                TargetSpawnForwardMeters = compiled.CombatTarget.SpawnForwardMeters
            };
            CombatSession.CreateRun(entities, tuning);
            RunEconomySession.Create(entities, compiled.RunEconomy.InitialGold,
                compiled.RunEconomy.GoldPerKill, compiled.RunEconomy.ExperiencePerKill);
            Entity hero = Hero(entities, tuning);
            Entity target = entities.CreateEntity(typeof(CombatTargetTag), typeof(CombatHealth),
                typeof(LocalTransform));
            entities.SetComponentData(target, LocalTransform.FromPosition(new float3(0f, 0f, 2f)));
            entities.SetComponentData(target, new CombatHealth { Current = 53f, Maximum = 53f });
            Attack(world, world.GetOrCreateSystemManaged<CombatResolutionSystem>(), hero,
                0d, 1, new float2(0f, 1f));
            return entities.GetComponentData<CombatHealth>(target).Current;
        }

        private static Entity Hero(EntityManager entities, CombatRunTuning tuning)
        {
            Entity hero = entities.CreateEntity(typeof(CombatHealth), typeof(HeroCombatStamina),
                typeof(HeroCombatAttackState), typeof(LocalTransform));
            entities.AddBuffer<CombatAttackCommand>(hero);
            entities.AddBuffer<CombatAttackResult>(hero);
            entities.SetComponentData(hero, LocalTransform.FromPosition(float3.zero));
            CombatSession.InitializeHero(entities, hero, tuning);
            return hero;
        }

        private static CombatAttackResult? Attack(World world, CombatResolutionSystem system,
            Entity hero, double time, uint sequence, float2 aim)
        {
            EntityManager entities = world.EntityManager;
            entities.GetBuffer<CombatAttackCommand>(hero).Add(new CombatAttackCommand
            {
                Sequence = sequence,
                Aim = aim
            });
            world.SetTime(new TimeData(time, 0.016f));
            system.Update();
            DynamicBuffer<CombatAttackResult> results = entities.GetBuffer<CombatAttackResult>(hero);
            CombatAttackResult? result = results.Length == 0 ? null : results[results.Length - 1];
            results.Clear();
            return result;
        }

        private static void VerifyOwnership(World world, Entity hero)
        {
            EntityManager entities = world.EntityManager;
            Entity connection = entities.CreateEntity(typeof(NetworkId), typeof(NetworkStreamInGame));
            entities.SetComponentData(connection, new NetworkId { Value = 1 });
            Entity stranger = entities.CreateEntity(typeof(NetworkId), typeof(NetworkStreamInGame));
            entities.SetComponentData(stranger, new NetworkId { Value = 2 });
            entities.AddComponentData(hero, new GhostOwner { NetworkId = 1 });
            Entity run = entities.CreateEntity(typeof(RunSession));
            entities.SetComponentData(run, new RunSession
            {
                Hero = hero,
                Connection = connection,
                Occupied = 1
            });
            Entity unauthorized = entities.CreateEntity(typeof(CombatAttackRequest),
                typeof(ReceiveRpcCommandRequest));
            entities.SetComponentData(unauthorized, new CombatAttackRequest
            {
                Sequence = 9,
                Aim = new float2(0f, 1f)
            });
            entities.SetComponentData(unauthorized, new ReceiveRpcCommandRequest
            {
                SourceConnection = stranger
            });
            ServerCombatCommandBridgeSystem bridge = world.GetOrCreateSystemManaged<ServerCombatCommandBridgeSystem>();
            bridge.Update();
            Require(entities.GetBuffer<CombatAttackCommand>(hero).Length == 0,
                "A foreign connection must not enqueue an attack.");
            Entity authorized = entities.CreateEntity(typeof(CombatAttackRequest),
                typeof(ReceiveRpcCommandRequest));
            entities.SetComponentData(authorized, new CombatAttackRequest
            {
                Sequence = 9,
                Aim = new float2(0f, 1f)
            });
            entities.SetComponentData(authorized, new ReceiveRpcCommandRequest
            {
                SourceConnection = connection
            });
            bridge.Update();
            Require(entities.GetBuffer<CombatAttackCommand>(hero).Length == 1,
                "The owning connection must enqueue exactly one attack.");
        }

        private static CombatRunTuning Tuning(ContentRelease release)
        {
            HeroAttackSnapshot attack = release.HeroAttack;
            return new CombatRunTuning
            {
                Damage = attack.Damage,
                IntervalSeconds = attack.IntervalSeconds,
                RangeMeters = attack.WeaponRadiusMeters * attack.RangeScale,
                HitPaddingMeters = attack.HitPaddingMeters,
                StaminaCost = attack.StaminaCost,
                StaminaRecoveryPerSecond = attack.StaminaRecoveryPerSecond,
                HeroMaximumHealth = attack.HeroMaximumHealth,
                MaximumStamina = attack.MaximumStamina,
                TargetMaximumHealth = release.CombatTarget.MaximumHealth,
                TargetSpawnForwardMeters = release.CombatTarget.SpawnForwardMeters
            };
        }

        private static void Configure(ContentCatalog destination, ContentCatalog source,
            HeroAttackDefinition attack)
        {
            var serialized = new SerializedObject(destination);
            HeroMovementDefinition[] definitions = source.GetHeroMovementDefinitions();
            SerializedProperty list = serialized.FindProperty("_heroMovementDefinitions");
            list.arraySize = definitions.Length;
            for (int index = 0; index < definitions.Length; index++)
                list.GetArrayElementAtIndex(index).objectReferenceValue = definitions[index];
            serialized.FindProperty("_defaultHeroMovement").objectReferenceValue = source.DefaultHeroMovement;
            serialized.FindProperty("_heroAttack").objectReferenceValue = attack;
            serialized.FindProperty("_combatTarget").objectReferenceValue = source.CombatTarget;
            serialized.FindProperty("_runEconomy").objectReferenceValue = source.RunEconomy;
            serialized.FindProperty("_enemyAgent").objectReferenceValue = source.EnemyAgent;
            serialized.FindProperty("_navigation").objectReferenceValue = source.Navigation;
            serialized.FindProperty("_waveSchedule").objectReferenceValue = source.WaveSchedule;
            serialized.FindProperty("_constructionRules").objectReferenceValue = source.ConstructionRules;
            serialized.FindProperty("_sawmillProduction").objectReferenceValue = source.SawmillProduction;
            StructureDefinition[] structures = source.GetStructures();
            SerializedProperty structureList = serialized.FindProperty("_structures");
            structureList.arraySize = structures.Length;
            for (int index = 0; index < structures.Length; index++)
                structureList.GetArrayElementAtIndex(index).objectReferenceValue = structures[index];
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void SetFloat(UnityEngine.Object target, string field, float value)
        {
            var serialized = new SerializedObject(target);
            serialized.FindProperty(field).floatValue = value;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void Require(bool value, string message)
        {
            if (!value) throw new InvalidOperationException(message);
        }
    }
}
