using System;
using Unity.Collections;
using Unity.Core;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEditor;
using UnityEngine;
using VaticanDefendor.Content;
using VaticanDefendor.Movement;

namespace VaticanDefendor.Editor
{
    public static class MovementReleaseVerification
    {
        private const string CatalogPath = "Assets/ActiveCombat/Content/MovementCatalog.asset";

        [MenuItem("VaticanDefendor/Content/Verify Movement Release")]
        public static void Verify()
        {
            ContentCatalog catalog = AssetDatabase.LoadAssetAtPath<ContentCatalog>(CatalogPath);
            if (catalog == null)
            {
                throw new InvalidOperationException($"Missing catalog at {CatalogPath}");
            }

            ContentRelease oldRelease = ContentReleaseBuilder.Build(catalog);
            Require(ReferenceEquals(oldRelease, ContentReleaseBuilder.Build(catalog)),
                "Identical content must resolve to the same release asset.");
            Require(oldRelease.TryGetDefaultHeroMovement(out HeroMovementSnapshot oldMovement),
                "Built release must contain its default hero movement.");

            using (var world = new World("Movement release verification", WorldFlags.GameServer))
            {
                Entity oldHero = HeroMovementSession.Create(world.EntityManager, oldRelease, float3.zero);
                world.EntityManager.SetComponentData(oldHero,
                    new HeroMoveInput { Direction = new float2(1f, 0f) });
                world.SetTime(new TimeData(0, 1f));
                world.GetOrCreateSystemManaged<HeroMovementSystem>().Update();
                float oldDistance = world.EntityManager.GetComponentData<LocalTransform>(oldHero).Position.x;
                Require(math.abs(oldDistance - oldMovement.WalkSpeedMetersPerSecond) < 0.0001f,
                    "Server movement must use the pinned walk speed.");

                HeroMovementDefinition changedDefinition = ScriptableObject.CreateInstance<HeroMovementDefinition>();
                HeroMovementDefinition unrelatedDefinition = ScriptableObject.CreateInstance<HeroMovementDefinition>();
                ContentCatalog changedCatalog = ScriptableObject.CreateInstance<ContentCatalog>();
                ContentRelease changedRelease = ScriptableObject.CreateInstance<ContentRelease>();
                try
                {
                    SetFloat(changedDefinition, "_walkSpeedMetersPerSecond", oldMovement.WalkSpeedMetersPerSecond + 1f);
                    SetFloat(changedDefinition, "_runSpeedMetersPerSecond", oldMovement.RunSpeedMetersPerSecond + 1f);
                    ConfigureCatalog(changedCatalog, changedDefinition, catalog);
                    ContentReleaseBuilder.CompiledRelease changed = ContentReleaseBuilder.Compile(changedCatalog);
                    Require(changed.ReleaseId != oldRelease.ReleaseId,
                        "Changing authoring speed must change the release ID.");
                    changedRelease.SetCompiledData(changed.ReleaseId, ContentRelease.CurrentContentSchemaVersion,
                        changed.DefaultHeroMovementId, changed.MovementManifest,
                        changed.HeroAttack, changed.CombatTarget, changed.RunEconomy,
                        changed.EnemyAgent, changed.Navigation, changed.WaveSchedule,
                        changed.ConstructionRules, changed.Structures, changed.SawmillProduction);

                    Entity newHero = HeroMovementSession.Create(world.EntityManager, changedRelease,
                        new float3(0f, 0f, 10f));
                    world.EntityManager.SetComponentData(newHero,
                        new HeroMoveInput { Direction = new float2(1f, 0f) });
                    world.SetTime(new TimeData(1, 1f));
                    world.GetOrCreateSystemManaged<HeroMovementSystem>().Update();
                    float newDistance = world.EntityManager.GetComponentData<LocalTransform>(newHero).Position.x;
                    Require(math.abs(newDistance - (oldMovement.WalkSpeedMetersPerSecond + 1f)) < 0.0001f,
                        "New server session must use changed authoring speed.");
                    world.EntityManager.SetComponentData(newHero,
                        new HeroMoveInput { Direction = new float2(1f, 0f), Run = 1 });
                    world.SetTime(new TimeData(2, 1f));
                    world.GetOrCreateSystemManaged<HeroMovementSystem>().Update();
                    float runDistance = world.EntityManager.GetComponentData<LocalTransform>(newHero).Position.x - newDistance;
                    Require(math.abs(runDistance - (oldMovement.RunSpeedMetersPerSecond + 1f)) < 0.0001f,
                        "Run intent must use the pinned run speed.");
                    Require(world.EntityManager.GetComponentData<HeroMovementTuning>(oldHero)
                        .WalkSpeedMetersPerSecond == oldMovement.WalkSpeedMetersPerSecond,
                        "Existing session tuning must remain pinned.");
                    Require(world.EntityManager.GetComponentData<SessionContentRelease>(oldHero)
                        .ReleaseId.Equals(new FixedString128Bytes(oldRelease.ReleaseId)),
                        "Existing session release ID must remain pinned.");

                    SetFloat(changedDefinition, "_walkSpeedMetersPerSecond", 0f);
                    ExpectInvalid(changedCatalog, "_walkSpeedMetersPerSecond");
                    SetFloat(changedDefinition, "_walkSpeedMetersPerSecond", oldMovement.WalkSpeedMetersPerSecond);
                    SetFloat(changedDefinition, "_runSpeedMetersPerSecond", 0.5f);
                    ExpectInvalid(changedCatalog, "_runSpeedMetersPerSecond");
                    SetFloat(changedDefinition, "_runSpeedMetersPerSecond", oldMovement.RunSpeedMetersPerSecond);
                    SetObject(changedCatalog, "_defaultHeroMovement", null);
                    ExpectInvalid(changedCatalog, "_defaultHeroMovement");
                    SetObject(changedCatalog, "_defaultHeroMovement", changedDefinition);
                    SetCatalogDefinition(changedCatalog, 1, changedDefinition);
                    ExpectInvalid(changedCatalog, "_heroMovementDefinitions[1]");
                    SetCatalogDefinitionCount(changedCatalog, 1);
                    SetObject(changedCatalog, "_defaultHeroMovement", unrelatedDefinition);
                    ExpectInvalid(changedCatalog, "_defaultHeroMovement");
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(changedRelease);
                    UnityEngine.Object.DestroyImmediate(changedCatalog);
                    UnityEngine.Object.DestroyImmediate(changedDefinition);
                    UnityEngine.Object.DestroyImmediate(unrelatedDefinition);
                }
            }

            Debug.Log($"Movement release verified: {oldRelease.ReleaseId}; speed, invalid values and session pinning passed.");
        }

        private static void ConfigureCatalog(ContentCatalog catalog, HeroMovementDefinition definition,
            ContentCatalog source)
        {
            var serialized = new SerializedObject(catalog);
            SerializedProperty list = serialized.FindProperty("_heroMovementDefinitions");
            list.arraySize = 1;
            list.GetArrayElementAtIndex(0).objectReferenceValue = definition;
            serialized.FindProperty("_defaultHeroMovement").objectReferenceValue = definition;
            serialized.FindProperty("_heroAttack").objectReferenceValue = source.HeroAttack;
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

        private static void SetObject(UnityEngine.Object target, string field, UnityEngine.Object value)
        {
            var serialized = new SerializedObject(target);
            serialized.FindProperty(field).objectReferenceValue = value;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void SetCatalogDefinition(ContentCatalog catalog, int index, HeroMovementDefinition definition)
        {
            var serialized = new SerializedObject(catalog);
            SerializedProperty list = serialized.FindProperty("_heroMovementDefinitions");
            list.arraySize = index + 1;
            list.GetArrayElementAtIndex(index).objectReferenceValue = definition;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void SetCatalogDefinitionCount(ContentCatalog catalog, int count)
        {
            var serialized = new SerializedObject(catalog);
            serialized.FindProperty("_heroMovementDefinitions").arraySize = count;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void ExpectInvalid(ContentCatalog catalog, string field)
        {
            try
            {
                ContentReleaseBuilder.Compile(catalog);
            }
            catch (InvalidOperationException exception)
            {
                Require(exception.Message.Contains(field),
                    $"Validation error must name {field}: {exception.Message}");
                return;
            }

            throw new InvalidOperationException($"Invalid {field} was accepted.");
        }

        private static void Require(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
    }
}
