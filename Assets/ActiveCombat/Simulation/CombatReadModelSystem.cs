using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Profiling;

public struct CombatSnapshotEntry<TState> where TState : unmanaged, IComponentData
{
    public Entity Entity;
    public TState State;
}

public sealed class CombatSnapshotStore<TState> where TState : unmanaged, IComponentData
{
    internal NativeList<CombatSnapshotEntry<TState>> EntryData;
    public NativeArray<CombatSnapshotEntry<TState>>.ReadOnly Entries => EntryData.AsArray().AsReadOnly();
    internal NativeParallelHashMap<Entity, TState> LookupData;
    public NativeParallelHashMap<Entity, TState>.ReadOnly Values => LookupData.AsReadOnly();
    internal EntityQuery Query;

    public CombatSnapshotStore(EntityQuery Query)
    {
        this.Query = Query;
        EntryData = new NativeList<CombatSnapshotEntry<TState>>(256, Allocator.Persistent);
        LookupData = new NativeParallelHashMap<Entity, TState>(256, Allocator.Persistent);
    }

    internal void Prepare()
    {
        EntryData.Clear();
        LookupData.Clear();
        int Count = Query.CalculateEntityCount();
        if (EntryData.Capacity < Count)
        {
            EntryData.Capacity = Unity.Mathematics.math.ceilpow2(Count);
            LookupData.Capacity = EntryData.Capacity;
        }
    }

    internal void Dispose()
    {
        EntryData.Dispose();
        LookupData.Dispose();
    }
}

[BurstCompile]
public struct CombatCaptureSnapshotJob<TState> : IJobChunk where TState : unmanaged, IComponentData
{
    [ReadOnly] public ComponentTypeHandle<TState> StateType;
    [ReadOnly] public EntityTypeHandle EntityType;
    public NativeList<CombatSnapshotEntry<TState>> Entries;
    public NativeParallelHashMap<Entity, TState> Values;

    public void Execute(in ArchetypeChunk Chunk, int ChunkIndex, bool bUseEnabledMask, in Unity.Burst.Intrinsics.v128 ChunkEnabledMask)
    {
        NativeArray<TState> States = Chunk.GetNativeArray(ref StateType);
        NativeArray<Entity> Entities = Chunk.GetNativeArray(EntityType);
        for (int Index = 0; Index < Chunk.Count; Index++)
        {
            Entries.AddNoResize(new CombatSnapshotEntry<TState> { Entity = Entities[Index], State = States[Index] });
            Values.Add(Entities[Index], States[Index]);
        }
    }
}

[BurstCompile]
public struct CombatCapturePlayerJob : IJob
{
    [ReadOnly] public ComponentLookup<CombatPlayerState> Players;
    [ReadOnly] public ComponentLookup<CombatMatchState> Matches;
    public BufferLookup<CombatUiEvent> Events;
    public Entity PlayerEntity;
    public Entity MatchEntity;
    public NativeReference<CombatPlayerState> Player;
    public NativeReference<CombatMatchState> Match;
    public NativeList<CombatUiEvent> UiEvents;

    public void Execute()
    {
        Player.Value = Players[PlayerEntity];
        Match.Value = Matches[MatchEntity];
        DynamicBuffer<CombatUiEvent> Source = Events[MatchEntity];
        UiEvents.Clear();
        UiEvents.AddRange(Source.AsNativeArray());
        Source.Clear();
    }
}

[DisableAutoCreation]
public partial class CombatReadModelSystem : SystemBase
{
    private Entity PlayerEntity;
    private Entity MatchEntity;
    private JobHandle Prerequisite;
    private static readonly ProfilerMarker UiWait = new ProfilerMarker("ActiveCombat.Ecs.UiWait");
    public CombatSnapshotStore<CombatBotState> Enemies;
    public CombatSnapshotStore<CombatBotStatistics> EnemyStatistics;
    public CombatSnapshotStore<CombatEnemyIdentity> EnemyIdentities;
    public CombatSnapshotStore<CombatBuildingState> Buildings;
    public CombatSnapshotStore<CombatProjectileState> Projectiles;
    public CombatSnapshotStore<CombatPickupState> Pickups;
    public CombatSnapshotStore<CombatBombState> Bombs;
    public CombatSnapshotStore<CombatSceneryState> Scenery;
    private NativeReference<CombatPlayerState> PlayerData;
    public CombatPlayerState Player => PlayerData.Value;
    private NativeReference<CombatMatchState> MatchData;
    public CombatMatchState Match => MatchData.Value;
    private NativeList<CombatUiEvent> EventsData;
    public NativeArray<CombatUiEvent>.ReadOnly UiEvents => EventsData.AsArray().AsReadOnly();

    protected override void OnCreate()
    {
        EntityQuery LivingEnemies = GetEntityQuery(new EntityQueryDesc
        {
            All = new[] { ComponentType.ReadOnly<CombatBotState>(), ComponentType.ReadOnly<CombatBotStatistics>(), ComponentType.ReadOnly<CombatEnemyIdentity>() }
        });
        Enemies = new CombatSnapshotStore<CombatBotState>(LivingEnemies);
        EnemyStatistics = new CombatSnapshotStore<CombatBotStatistics>(LivingEnemies);
        EnemyIdentities = new CombatSnapshotStore<CombatEnemyIdentity>(LivingEnemies);
        Buildings = new CombatSnapshotStore<CombatBuildingState>(GetEntityQuery(ComponentType.ReadOnly<CombatBuildingState>()));
        Projectiles = new CombatSnapshotStore<CombatProjectileState>(GetEntityQuery(ComponentType.ReadOnly<CombatProjectileState>()));
        Pickups = new CombatSnapshotStore<CombatPickupState>(GetEntityQuery(ComponentType.ReadOnly<CombatPickupState>()));
        Bombs = new CombatSnapshotStore<CombatBombState>(GetEntityQuery(ComponentType.ReadOnly<CombatBombState>()));
        Scenery = new CombatSnapshotStore<CombatSceneryState>(GetEntityQuery(ComponentType.ReadOnly<CombatSceneryState>()));
        PlayerData = new NativeReference<CombatPlayerState>(Allocator.Persistent);
        MatchData = new NativeReference<CombatMatchState>(Allocator.Persistent);
        EventsData = new NativeList<CombatUiEvent>(128, Allocator.Persistent);
    }

    protected override void OnUpdate()
    {
        ScheduleCaptureJobs();
    }

    private void Capture<TState>(CombatSnapshotStore<TState> Store) where TState : unmanaged, IComponentData
    {
        Store.Prepare();
        Dependency = new CombatCaptureSnapshotJob<TState>
        {
            StateType = GetComponentTypeHandle<TState>(true), EntityType = GetEntityTypeHandle(), Entries = Store.EntryData, Values = Store.LookupData
        }.Schedule(Store.Query, Dependency);
    }

    public void ScheduleCapture(Entity PlayerEntity, Entity MatchEntity, JobHandle Prerequisite)
    {
        this.PlayerEntity = PlayerEntity;
        this.MatchEntity = MatchEntity;
        this.Prerequisite = Prerequisite;
        Update();
    }

    private void ScheduleCaptureJobs()
    {
        Dependency = JobHandle.CombineDependencies(Dependency, Prerequisite);
        Capture(Enemies);
        Capture(EnemyStatistics);
        Capture(EnemyIdentities);
        Capture(Buildings);
        Capture(Projectiles);
        Capture(Pickups);
        Capture(Bombs);
        Capture(Scenery);
        Dependency = new CombatCapturePlayerJob
        {
            Players = GetComponentLookup<CombatPlayerState>(true), Matches = GetComponentLookup<CombatMatchState>(true),
            Events = GetBufferLookup<CombatUiEvent>(), PlayerEntity = PlayerEntity, MatchEntity = MatchEntity,
            Player = PlayerData, Match = MatchData, UiEvents = EventsData
        }.Schedule(Dependency);
    }

    public void CompleteForPresentation()
    {
        // UI and cameras consume one immutable batch after all simulation and animation jobs have finished.
        using (UiWait.Auto())
        {
            Dependency.Complete();
        }
    }

    protected override void OnDestroy()
    {
        Dependency.Complete();
        Enemies.Dispose();
        EnemyStatistics.Dispose();
        EnemyIdentities.Dispose();
        Buildings.Dispose();
        Projectiles.Dispose();
        Pickups.Dispose();
        Bombs.Dispose();
        Scenery.Dispose();
        PlayerData.Dispose();
        MatchData.Dispose();
        EventsData.Dispose();
    }
}
