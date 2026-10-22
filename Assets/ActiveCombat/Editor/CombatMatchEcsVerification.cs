using System;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEditor;

public static class CombatMatchEcsVerification
{
    public static string RunContentChecks()
    {
        CombatRenderCatalog Catalog = CombatRenderCatalogBuilder.Build();
        ActiveCombatGame Game = UnityEngine.Object.FindAnyObjectByType<ActiveCombatGame>();
        using CombatDotsWorld Preview = new CombatDotsWorld();
        Preview.Initialize(Catalog, Game.Balance);
        Preview.Reset(false, false, 12345, Game.Balance);
        for (int Step = 0; Step < 10; Step++)
        {
            Preview.Step(1f / 60, true);
            Preview.Publish();
            Preview.Present(1f / 60);
        }
        Require(Preview.ReadModel.Enemies.Entries.Length == Game.Balance.InitialEnemies, "Render world lost ECS actors.");
        EntityQuery Parts = Preview.Manager.CreateEntityQuery(typeof(CombatRenderPart), typeof(Unity.Rendering.MaterialMeshInfo));
        int PartCount = Parts.CalculateEntityCount();
        Require(PartCount > Catalog.StaticObjects.Length, $"Render catalog did not instantiate entity parts: {PartCount}.");
        Parts.Dispose();
        return $"Passed ECS content initialization, animation, snapshots and presentation: {PartCount} visible render parts.";
    }

    public static string RunModelChecks()
    {
        CombatBalance Balance = AssetDatabase.LoadAssetAtPath<CombatBalance>("Assets/ActiveCombat/Settings/CombatBalance.asset");
        if (Balance == null)
        {
            string[] Matches = AssetDatabase.FindAssets("t:CombatBalance");
            Balance = AssetDatabase.LoadAssetAtPath<CombatBalance>(AssetDatabase.GUIDToAssetPath(Matches[0]));
        }
        using World World = new World("ActiveCombatEcsVerification");
        EntityManager Manager = World.EntityManager;
        NativeArray<Entity> Prefabs = new NativeArray<Entity>(45, Allocator.Persistent);
        for (int Index = 0; Index < Prefabs.Length; Index++)
        {
            Entity Prefab = Manager.CreateEntity(typeof(Prefab), typeof(LocalTransform), typeof(CombatModelState));
            Manager.SetComponentData(Prefab, LocalTransform.Identity);
            Manager.SetComponentData(Prefab, new CombatModelState { ModelIndex = Index, Scale = new float3(1), bVisible = 1 });
            Prefabs[Index] = Prefab;
        }
        BlobAssetReference<CombatConfiguration> Configuration = CombatConfigurationBuilder.Build(Balance);
        CombatMatchSimulationSystem Simulation = World.GetOrCreateSystemManaged<CombatMatchSimulationSystem>();
        Simulation.Configuration = Configuration;
        Simulation.Prefabs = Prefabs;
        Simulation.PlayerEntity = Manager.CreateEntity(typeof(CombatPlayerState), typeof(CombatModelState));
        Simulation.MatchEntity = Manager.CreateEntity(typeof(CombatMatchState), typeof(CombatWaveGroup), typeof(CombatUiEvent));
        int Checks = 0;
        try
        {
            Simulation.ResetMatch(false, false, 12345, 1, 0.2f, 3);
            Require(Simulation.Bots.CalculateEntityCount() == Balance.InitialEnemies, "Initial ECS wave differs from balance.");
            Checks++;
            EntityQuery Buildings = Manager.CreateEntityQuery(typeof(CombatBuildingState));
            Require(Buildings.CalculateEntityCount() == 6, "Initial ECS base is incomplete.");
            Checks++;
            for (int Step = 0; Step < 120; Step++)
            {
                Simulation.ScheduleStep(1f / 60, true);
                Simulation.CompleteSimulation();
            }
            CombatPlayerState Player = Manager.GetComponentData<CombatPlayerState>(Simulation.PlayerEntity);
            Require(math.all(math.isfinite(Player.Position)) && Player.Health > 0, "ECS player state is invalid.");
            Checks++;
            using (NativeArray<CombatBotState> Bots = Simulation.Bots.ToComponentDataArray<CombatBotState>(Allocator.Temp))
            {
                foreach (CombatBotState Bot in Bots)
                {
                    Require(math.all(math.isfinite(Bot.Position)) && Bot.Health > 0 && Bot.Health <= Balance.Enemies[3].Health * 4,
                        "ECS bot state was corrupted during the job pipeline.");
                    Checks++;
                }
            }
            Simulation.ResetMatch(false, true, 54321, 2, 0.2f, 3);
            Require(Simulation.Bots.IsEmptyIgnoreFilter && Buildings.IsEmptyIgnoreFilter, "ECS reset retained old actors.");
            Checks++;
            Player = Manager.GetComponentData<CombatPlayerState>(Simulation.PlayerEntity);
            Require(Player.AttackUpgrades == 0 && Player.SpecialUses == 0 && Player.ComboPoints == 0, "ECS reset retained player progression.");
            Checks++;
            Buildings.Dispose();
            return $"Passed {Checks} ECS model checks and 120 simulation steps.";
        }
        finally
        {
            Simulation.CompleteSimulation();
            World.DestroySystemManaged(Simulation);
            Prefabs.Dispose();
            Configuration.Dispose();
        }
    }

    private static void Require(bool bCondition, string Message)
    {
        if (!bCondition)
        {
            throw new InvalidOperationException(Message);
        }
    }
}
