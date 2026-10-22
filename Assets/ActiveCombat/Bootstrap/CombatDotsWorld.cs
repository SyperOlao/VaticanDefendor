using System;
using Unity.Collections;
using Unity.Core;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using Unity.Profiling;

public sealed class CombatDotsWorld : IDisposable
{
    private static readonly ProfilerMarker EntityRendering = new ProfilerMarker("ActiveCombat.EntitiesGraphics");
    public World World { get; private set; }
    public CombatMatchSimulationSystem Simulation { get; private set; }
    public CombatReadModelSystem ReadModel { get; private set; }
    public Entity PlayerEntity => Simulation.PlayerEntity;
    public Entity MatchEntity => Simulation.MatchEntity;
    public EntityManager Manager => World.EntityManager;
    public bool bAcceptsCommands => !bDisposed && World != null && World.IsCreated && Simulation != null && Simulation.InputCommands.IsCreated;
    private readonly CombatEntityContent Content = new CombatEntityContent();
    private BlobAssetReference<CombatConfiguration> Configuration;
    private double ElapsedSeconds;
    private int ResetSequence;
    private bool bDisposed;

    public void Initialize(CombatRenderCatalog Catalog, CombatBalance Balance)
    {
        World = new World("ActiveCombatMatch");
        DefaultWorldInitialization.AddSystemsToRootLevelSystemGroups(World,
            DefaultWorldInitialization.GetAllSystems(WorldSystemFilterFlags.Default));
        Configuration = CombatConfigurationBuilder.Build(Balance);
        Content.Create(Manager, Catalog);
        Simulation = World.GetOrCreateSystemManaged<CombatMatchSimulationSystem>();
        ReadModel = World.GetOrCreateSystemManaged<CombatReadModelSystem>();
        Simulation.Configuration = Configuration;
        Simulation.Prefabs = Content.Prefabs;
        Simulation.PlayerEntity = Manager.Instantiate(Content.Prefabs[CombatModelIndex.Player]);
        MakePersistent(Simulation.PlayerEntity);
        Manager.AddComponent<CombatPlayerState>(Simulation.PlayerEntity);
        Simulation.MatchEntity = Manager.CreateEntity(typeof(CombatMatchState), typeof(CombatConfigurationData),
            typeof(CombatWaveGroup), typeof(CombatUiEvent));
        Manager.SetComponentData(Simulation.MatchEntity, new CombatConfigurationData { Value = Configuration });
        foreach (CombatStaticDefinition Definition in Catalog.StaticObjects)
        {
            Entity Entity = Manager.Instantiate(Content.Prefabs[Definition.ModelIndex]);
            MakePersistent(Entity);
            Manager.AddComponentData(Entity, new CombatStaticPose { Matrix = Definition.Matrix });
            if (Definition.bScenery)
            {
                Manager.AddComponentData(Entity, new CombatSceneryState
                {
                    Position = ((float4x4)Definition.Matrix).c3.xyz, Identity = -Definition.Identity, InitialModel = Definition.ModelIndex
                });
            }
        }
        Simulation.InitializeStaticRendering();
    }

    private void MakePersistent(Entity Entity)
    {
        DynamicBuffer<LinkedEntityGroup> Group = Manager.GetBuffer<LinkedEntityGroup>(Entity);
        NativeArray<LinkedEntityGroup> Parts = Group.ToNativeArray(Allocator.Temp);
        foreach (LinkedEntityGroup Part in Parts)
        {
            Manager.RemoveComponent<CombatMatchEntity>(Part.Value);
        }
        Parts.Dispose();
    }

    public void Reset(bool bMainMenu, bool bTutorial, uint Seed, CombatBalance Balance)
    {
        ResetSequence++;
        Simulation.ResetMatch(bMainMenu, bTutorial, Seed, ResetSequence, Balance.MobileView.SlowMotionFactor,
            Balance.MobileView.SlowMotionDuration);
        Publish();
    }

    public void ReplaceConfiguration(CombatBalance Balance)
    {
        Simulation.CompleteSimulation();
        ReadModel.CompleteForPresentation();
        BlobAssetReference<CombatConfiguration> Previous = Configuration;
        Configuration = CombatConfigurationBuilder.Build(Balance);
        Simulation.Configuration = Configuration;
        Manager.SetComponentData(MatchEntity, new CombatConfigurationData { Value = Configuration });
        Previous.Dispose();
    }

    public void Enqueue(CombatCommand Command)
    {
        Simulation.InputCommands.Add(Command);
    }

    public void FlushCommands()
    {
        if (Simulation.InputCommands.Length == 0)
        {
            return;
        }
        Simulation.FlushStructuralCommands();
        Publish();
    }

    public void Step(float DeltaSeconds, bool bUseSimulationTime)
    {
        Simulation.ScheduleStep(DeltaSeconds, bUseSimulationTime);
        Simulation.CompleteSimulation();
    }

    public void Publish()
    {
        ReadModel.ScheduleCapture(PlayerEntity, MatchEntity, Simulation.ScheduleAnimation());
        ReadModel.CompleteForPresentation();
    }

    public void Present(float UnscaledDeltaSeconds)
    {
        using var Measurement = EntityRendering.Auto();
        ElapsedSeconds += UnscaledDeltaSeconds;
        World.SetTime(new TimeData(ElapsedSeconds, UnscaledDeltaSeconds));
        World.Update();
    }

    public void Dispose()
    {
        bDisposed = true;
        if (World != null && World.IsCreated)
        {
            Simulation.CompleteSimulation();
            ReadModel.CompleteForPresentation();
            World.Dispose();
        }
        Content.Dispose();
        if (Configuration.IsCreated)
        {
            Configuration.Dispose();
        }
    }
}
