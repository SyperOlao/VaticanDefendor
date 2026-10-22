using Unity.AI.Navigation.LowLevel;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Rendering;
using Unity.Transforms;
using Unity.Profiling;

[DisableAutoCreation]
public partial class CombatMatchSimulationSystem : SystemBase
{
    private static readonly ProfilerMarker StructuralWait = new ProfilerMarker("ActiveCombat.Ecs.StructuralWait");
    private static readonly ProfilerMarker Playback = new ProfilerMarker("ActiveCombat.Ecs.Playback");
    public Entity PlayerEntity;
    public Entity MatchEntity;
    public BlobAssetReference<CombatConfiguration> Configuration;
    public NativeArray<Entity> Prefabs;
    public NativeList<CombatCommand> InputCommands;
    public long ScheduledSteps { get; private set; }
    public long NavigationJobs { get; private set; }
    public long StructuralBarriers { get; private set; }
    public EntityQuery Bots { get; private set; }
    public EntityQuery DynamicEntities { get; private set; }
    public NativeList<CombatNavigationObstacle> Obstacles;
    private EntityQuery Buildings;
    private EntityQuery Projectiles;
    private EntityQuery Pickups;
    private EntityQuery Bombs;
    private EntityQuery Effects;
    private EntityQuery Scenery;
    private EntityQuery Corpses;
    private EntityQuery RenderParts;
    private EntityQuery StaticParts;
    private NativeList<Entity> EnemyEntities;
    private NativeList<Entity> BuildingEntities;
    private NativeList<Entity> ProjectileEntities;
    private NativeList<Entity> PickupEntities;
    private NativeList<Entity> BombEntities;
    private NativeList<Entity> EffectEntities;
    private NativeList<Entity> SceneryEntities;
    private NativeList<int2> WallCells;
    private NativeParallelHashSet<int2> UniqueWallCells;
    private NativeList<CombatTargetCandidate> Candidates;
    private NativeParallelMultiHashMap<int2, CombatBotNeighbor> Neighbors;
    private NativeParallelMultiHashMap<int2, CombatBotNeighbor> WorldNeighbors;
    private NativeReference<CombatBotFrame> FrameData;
    private NativeParallelMultiHashMap<int2, int> TargetCells;
    private NativeParallelMultiHashMap<int2, int> ObstacleCells;
    private NativeReference<float> MaximumTargetRadius;
    private NavWorld Navigation;
    private NavQueryBuffer QueryBuffer;
    private NativeArray<NavNode> Nodes;
    private NativeArray<float3> LeftPortals;
    private NativeArray<float3> RightPortals;
    private EntityCommandBuffer Commands;
    private EntityCommandBuffer.ParallelWriter ParallelCommands;
    private bool bCommandsCreated;
    private bool bNavigationCreated;
    private bool bAnimationRequested;
    private bool bSimulationRequested;
    private bool bCommandsRequested;
    private float RequestedDelta;
    private bool bRequestedSimulationTime;

    protected override void OnCreate()
    {
        Bots = GetEntityQuery(new EntityQueryDesc
        {
            All = new[] { ComponentType.ReadWrite<CombatBotState>(), ComponentType.ReadOnly<CombatBotStatistics>(),
                ComponentType.ReadWrite<CombatPathCorner>(), ComponentType.ReadWrite<CombatAttackVictim>(), ComponentType.ReadWrite<CombatBotEvent>() },
            None = new[] { ComponentType.ReadOnly<CombatCorpseState>() }
        });
        Buildings = GetEntityQuery(ComponentType.ReadWrite<CombatBuildingState>());
        Projectiles = GetEntityQuery(ComponentType.ReadWrite<CombatProjectileState>());
        Pickups = GetEntityQuery(ComponentType.ReadWrite<CombatPickupState>());
        Bombs = GetEntityQuery(ComponentType.ReadWrite<CombatBombState>());
        Effects = GetEntityQuery(ComponentType.ReadWrite<CombatEffectState>());
        Scenery = GetEntityQuery(ComponentType.ReadWrite<CombatSceneryState>());
        Corpses = GetEntityQuery(ComponentType.ReadWrite<CombatCorpseState>(), ComponentType.ReadWrite<CombatBotState>());
        DynamicEntities = GetEntityQuery(ComponentType.ReadOnly<CombatMatchEntity>());
        RenderParts = GetEntityQuery(new EntityQueryDesc
        {
            All = new[] { ComponentType.ReadOnly<CombatRenderPart>(), ComponentType.ReadWrite<LocalToWorld>(),
                ComponentType.ReadWrite<MaterialMeshInfo>(), ComponentType.ReadWrite<URPMaterialPropertyBaseColor>(),
                ComponentType.ReadWrite<CombatEffectMaterialParameters>() },
            None = new[] { ComponentType.ReadOnly<Static>() },
            Options = EntityQueryOptions.IgnoreComponentEnabledState
        });
        StaticParts = GetEntityQuery(ComponentType.ReadOnly<Static>(), ComponentType.ReadOnly<CombatRenderPart>(), ComponentType.ReadWrite<LocalToWorld>());
        EnemyEntities = new NativeList<Entity>(256, Allocator.Persistent);
        BuildingEntities = new NativeList<Entity>(128, Allocator.Persistent);
        ProjectileEntities = new NativeList<Entity>(128, Allocator.Persistent);
        PickupEntities = new NativeList<Entity>(64, Allocator.Persistent);
        BombEntities = new NativeList<Entity>(32, Allocator.Persistent);
        EffectEntities = new NativeList<Entity>(256, Allocator.Persistent);
        SceneryEntities = new NativeList<Entity>(256, Allocator.Persistent);
        InputCommands = new NativeList<CombatCommand>(256, Allocator.Persistent);
        WallCells = new NativeList<int2>(512, Allocator.Persistent);
        UniqueWallCells = new NativeParallelHashSet<int2>(512, Allocator.Persistent);
        Candidates = new NativeList<CombatTargetCandidate>(256, Allocator.Persistent);
        Obstacles = new NativeList<CombatNavigationObstacle>(512, Allocator.Persistent);
        Neighbors = new NativeParallelMultiHashMap<int2, CombatBotNeighbor>(256, Allocator.Persistent);
        WorldNeighbors = new NativeParallelMultiHashMap<int2, CombatBotNeighbor>(256, Allocator.Persistent);
        FrameData = new NativeReference<CombatBotFrame>(Allocator.Persistent);
        TargetCells = new NativeParallelMultiHashMap<int2, int>(256, Allocator.Persistent);
        ObstacleCells = new NativeParallelMultiHashMap<int2, int>(4096, Allocator.Persistent);
        MaximumTargetRadius = new NativeReference<float>(Allocator.Persistent);
    }

    public void InitializeNavigation()
    {
        if (bNavigationCreated)
        {
            return;
        }
        Navigation = NavWorld.GetDefaultWorld();
        QueryBuffer = new NavQueryBuffer(Navigation, Allocator.Persistent, 4096);
        Nodes = new NativeArray<NavNode>(1024, Allocator.Persistent);
        LeftPortals = new NativeArray<float3>(1025, Allocator.Persistent);
        RightPortals = new NativeArray<float3>(1025, Allocator.Persistent);
        bNavigationCreated = true;
    }

    protected override void OnUpdate()
    {
        if (bSimulationRequested)
        {
            bSimulationRequested = false;
            ScheduleGameplayJobs(RequestedDelta, bRequestedSimulationTime);
        }
        else if (bCommandsRequested)
        {
            bCommandsRequested = false;
            CollectWorksets();
            BeginCommands();
            ScheduleInput(CreateContext());
        }
        else if (bAnimationRequested)
        {
            bAnimationRequested = false;
            ScheduleAnimationJobs();
        }
    }

    private void Collect(EntityQuery Query, NativeList<Entity> Entities)
    {
        Entities.Clear();
        int Count = Query.CalculateEntityCount();
        if (Entities.Capacity < Count)
        {
            Entities.Capacity = math.ceilpow2(Count);
        }
        Dependency = new CombatCollectEntitiesJob { EntityType = GetEntityTypeHandle(), Entities = Entities }.Schedule(Query, Dependency);
    }

    private void CollectWorksets()
    {
        Collect(Bots, EnemyEntities);
        Collect(Buildings, BuildingEntities);
        Collect(Projectiles, ProjectileEntities);
        Collect(Pickups, PickupEntities);
        Collect(Bombs, BombEntities);
        Collect(Effects, EffectEntities);
        Collect(Scenery, SceneryEntities);
        int EnemyCount = Bots.CalculateEntityCount();
        if (Neighbors.Capacity < EnemyCount)
        {
            Neighbors.Capacity = math.ceilpow2(EnemyCount);
            WorldNeighbors.Capacity = Neighbors.Capacity;
        }
        int ObstacleCount = Buildings.CalculateEntityCount() + Scenery.CalculateEntityCount();
        if (Candidates.Capacity < ObstacleCount + 1)
        {
            Candidates.Capacity = math.ceilpow2(ObstacleCount + 1);
        }
        if (Obstacles.Capacity < ObstacleCount)
        {
            Obstacles.Capacity = math.ceilpow2(ObstacleCount);
        }
        if (TargetCells.Capacity < ObstacleCount + 1)
        {
            TargetCells.Capacity = math.ceilpow2(ObstacleCount + 1);
        }
        if (ObstacleCells.Capacity < ObstacleCount * 64)
        {
            ObstacleCells.Capacity = math.ceilpow2(ObstacleCount * 64);
        }
        Neighbors.Clear();
    }

    private CombatGameplayContext CreateContext()
    {
        return new CombatGameplayContext
        {
            PlayerEntity = PlayerEntity, MatchEntity = MatchEntity, Configuration = Configuration, Prefabs = Prefabs, Commands = Commands,
            Players = GetComponentLookup<CombatPlayerState>(), Matches = GetComponentLookup<CombatMatchState>(),
            Enemies = GetComponentLookup<CombatBotState>(), EnemyStatistics = GetComponentLookup<CombatBotStatistics>(),
            EnemyIdentities = GetComponentLookup<CombatEnemyIdentity>(), Buildings = GetComponentLookup<CombatBuildingState>(),
            Projectiles = GetComponentLookup<CombatProjectileState>(), Pickups = GetComponentLookup<CombatPickupState>(),
            Bombs = GetComponentLookup<CombatBombState>(), Effects = GetComponentLookup<CombatEffectState>(),
            Scenery = GetComponentLookup<CombatSceneryState>(), Models = GetComponentLookup<CombatModelState>(),
            UiEvents = GetBufferLookup<CombatUiEvent>(), WaveGroups = GetBufferLookup<CombatWaveGroup>(),
            EnemyEvents = GetBufferLookup<CombatBotEvent>(), EnemyEntities = EnemyEntities, BuildingEntities = BuildingEntities,
            RenderGroups = GetBufferLookup<LinkedEntityGroup>(true),
            RenderTransforms = GetComponentLookup<LocalToWorld>(true), RenderBounds = GetComponentLookup<RenderBounds>(true),
            RenderMaterials = GetComponentLookup<MaterialMeshInfo>(true), RenderParts = GetComponentLookup<CombatRenderPart>(true),
            ProjectileEntities = ProjectileEntities, PickupEntities = PickupEntities, BombEntities = BombEntities,
            EffectEntities = EffectEntities, SceneryEntities = SceneryEntities, WorldNeighbors = WorldNeighbors
        };
    }

    private void BeginCommands()
    {
        Commands = new EntityCommandBuffer(Allocator.TempJob);
        ParallelCommands = Commands.AsParallelWriter();
        bCommandsCreated = true;
    }

    public void ResetMatch(bool bMainMenu, bool bTutorial, uint Seed, int ResetSequence, float SlowMotionFactor, float SlowMotionDuration)
    {
        CompleteSimulation();
        EntityManager.DestroyEntity(DynamicEntities);
        InputCommands.Clear();
        CollectWorksets();
        BeginCommands();
        int MainMenu = 0;
        int Tutorial = 0;
        if (bMainMenu)
        {
            MainMenu = 1;
        }
        if (bTutorial)
        {
            Tutorial = 1;
        }
        Dependency = new CombatInitializeMatchJob
        {
            Context = CreateContext(), bMainMenu = MainMenu, bTutorial = Tutorial, Seed = Seed,
            ResetSequence = ResetSequence, SlowMotionFactor = SlowMotionFactor, SlowMotionDuration = SlowMotionDuration
        }.Schedule(Dependency);
        CompleteSimulation();
    }

    public void FlushStructuralCommands()
    {
        bCommandsRequested = true;
        Update();
        CompleteSimulation();
    }

    private void ScheduleInput(CombatGameplayContext Context)
    {
        if (InputCommands.Length > 0)
        {
            int Count = math.max(512, InputCommands.Length);
            if (WallCells.Capacity < Count)
            {
                WallCells.Capacity = math.ceilpow2(Count);
                UniqueWallCells.Capacity = WallCells.Capacity;
            }
            Dependency = new CombatInputCommandsJob
            {
                Context = Context, InputCommands = InputCommands.AsArray(), WallCells = WallCells, UniqueWallCells = UniqueWallCells
            }.Schedule(Dependency);
        }
    }

    public void ScheduleStep(float DeltaSeconds, bool bUseSimulationTime)
    {
        RequestedDelta = DeltaSeconds;
        bRequestedSimulationTime = bUseSimulationTime;
        bSimulationRequested = true;
        Update();
    }

    private void ScheduleGameplayJobs(float DeltaSeconds, bool bUseSimulationTime)
    {
        CollectWorksets();
        BeginCommands();
        CombatGameplayContext Context = CreateContext();
        ScheduleInput(Context);
        int SimulationTime = 0;
        if (bUseSimulationTime)
        {
            SimulationTime = 1;
        }
        Dependency = new CombatPlayerSimulationJob { Context = Context, DeltaSeconds = DeltaSeconds, bUseSimulationTime = SimulationTime }.Schedule(Dependency);
        Dependency = new CombatBuildBotFrameJob
        {
            Context = Context, Candidates = Candidates, Obstacles = Obstacles, FrameData = FrameData,
            TargetCells = TargetCells, ObstacleCells = ObstacleCells, MaximumTargetRadius = MaximumTargetRadius
        }.Schedule(Dependency);
        if (!Bots.IsEmptyIgnoreFilter)
        {
            ScheduleBots();
        }
        Dependency = new CombatClearWorldIndexJob { Neighbors = WorldNeighbors }.Schedule(Dependency);
        Dependency = new CombatWorldSpatialIndexJob
        {
            BotType = GetComponentTypeHandle<CombatBotState>(true), EntityType = GetEntityTypeHandle(), Neighbors = WorldNeighbors.AsParallelWriter()
        }.ScheduleParallel(Bots, Dependency);
        Dependency = new CombatWorldSimulationJob { Context = Context }.Schedule(Dependency);
        Dependency = new CombatLifetimeJob
        {
            EffectType = GetComponentTypeHandle<CombatEffectState>(), Matches = GetComponentLookup<CombatMatchState>(true),
            EntityType = GetEntityTypeHandle(), MatchEntity = MatchEntity, Commands = ParallelCommands
        }.ScheduleParallel(Effects, Dependency);
        Dependency = new CombatCorpseLifetimeJob
        {
            CorpseType = GetComponentTypeHandle<CombatCorpseState>(), BotType = GetComponentTypeHandle<CombatBotState>(),
            Matches = GetComponentLookup<CombatMatchState>(true), EntityType = GetEntityTypeHandle(),
            MatchEntity = MatchEntity, Commands = ParallelCommands
        }.ScheduleParallel(Corpses, Dependency);
        ScheduledSteps++;
    }

    private void ScheduleBots()
    {
        NativeArray<CombatTargetCandidate> TargetValues = Candidates.AsDeferredJobArray();
        NativeArray<CombatNavigationObstacle> ObstacleValues = Obstacles.AsDeferredJobArray();
        Dependency = new CombatBotSpatialIndexJob
        {
            BotType = GetComponentTypeHandle<CombatBotState>(true), EntityType = GetEntityTypeHandle(), Neighbors = Neighbors.AsParallelWriter()
        }.ScheduleParallel(Bots, Dependency);
        Dependency = new CombatBotTargetSelectionJob
        {
            Candidates = TargetValues, Obstacles = ObstacleValues, BotType = GetComponentTypeHandle<CombatBotState>(),
            TargetCells = TargetCells, ObstacleCells = ObstacleCells, MaximumTargetRadius = MaximumTargetRadius, FrameData = FrameData
        }.ScheduleParallel(Bots, Dependency);
        Dependency = new CombatBotPreparationJob
        {
            BotType = GetComponentTypeHandle<CombatBotState>(), StatisticsType = GetComponentTypeHandle<CombatBotStatistics>(true),
            EventType = GetBufferTypeHandle<CombatBotEvent>(), Candidates = TargetValues, Obstacles = ObstacleValues, FrameData = FrameData
        }.ScheduleParallel(Bots, Dependency);
        Dependency = new CombatBotAttackAdmissionJob
        {
            Entities = EnemyEntities.AsDeferredJobArray(), Bots = GetComponentLookup<CombatBotState>(),
            Statistics = GetComponentLookup<CombatBotStatistics>(true), Candidates = TargetValues, FrameData = FrameData
        }.Schedule(Dependency);
        if (bNavigationCreated)
        {
            Dependency = new CombatBotNavigationJob
            {
                BotType = GetComponentTypeHandle<CombatBotState>(), PathType = GetBufferTypeHandle<CombatPathCorner>(),
                Navigation = Navigation, QueryBuffer = QueryBuffer, Nodes = Nodes, LeftPortals = LeftPortals,
                RightPortals = RightPortals, FrameData = FrameData
            }.Schedule(Bots, Dependency);
            Navigation.AddDependency(Dependency);
            NavigationJobs++;
        }
        Dependency = new CombatBotMovementJob
        {
            BotType = GetComponentTypeHandle<CombatBotState>(), StatisticsType = GetComponentTypeHandle<CombatBotStatistics>(true),
            EntityType = GetEntityTypeHandle(), PathType = GetBufferTypeHandle<CombatPathCorner>(),
            Obstacles = ObstacleValues, Neighbors = Neighbors, FrameData = FrameData
        }.ScheduleParallel(Bots, Dependency);
        Dependency = new CombatBotAttackResolutionJob
        {
            BotType = GetComponentTypeHandle<CombatBotState>(), StatisticsType = GetComponentTypeHandle<CombatBotStatistics>(true),
            VictimType = GetBufferTypeHandle<CombatAttackVictim>(), EventType = GetBufferTypeHandle<CombatBotEvent>(),
            Candidates = TargetValues, FrameData = FrameData
        }.ScheduleParallel(Bots, Dependency);
    }

    public void CompleteSimulation()
    {
        // Structural playback is the simulation boundary; gameplay stages never wait for one another on the main thread.
        using (StructuralWait.Auto())
        {
            Dependency.Complete();
        }
        if (bCommandsCreated)
        {
            bCommandsCreated = false;
            try
            {
                using (Playback.Auto())
                {
                    Commands.Playback(EntityManager);
                }
                InputCommands.Clear();
                StructuralBarriers++;
            }
            finally
            {
                Commands.Dispose();
            }
        }
    }

    public JobHandle ScheduleAnimation()
    {
        bAnimationRequested = true;
        Update();
        return Dependency;
    }

    public void InitializeStaticRendering()
    {
        Dependency = new CombatStaticRenderingJob
        {
            PartType = GetComponentTypeHandle<CombatRenderPart>(true), Poses = GetComponentLookup<CombatStaticPose>(true),
            TransformType = GetComponentTypeHandle<LocalToWorld>()
        }.ScheduleParallel(StaticParts, Dependency);
        Dependency.Complete();
    }

    private void ScheduleAnimationJobs()
    {
        Dependency = new CombatEntityAnimationJob
        {
            PartType = GetComponentTypeHandle<CombatRenderPart>(true), TransformType = GetComponentTypeHandle<LocalToWorld>(),
            MeshType = GetComponentTypeHandle<MaterialMeshInfo>(), ColorType = GetComponentTypeHandle<URPMaterialPropertyBaseColor>(),
            EffectParametersType = GetComponentTypeHandle<CombatEffectMaterialParameters>(),
            Models = GetComponentLookup<CombatModelState>(true), Players = GetComponentLookup<CombatPlayerState>(true),
            Enemies = GetComponentLookup<CombatBotState>(true), EnemyStatistics = GetComponentLookup<CombatBotStatistics>(true),
            Buildings = GetComponentLookup<CombatBuildingState>(true), Projectiles = GetComponentLookup<CombatProjectileState>(true),
            Pickups = GetComponentLookup<CombatPickupState>(true), Effects = GetComponentLookup<CombatEffectState>(true),
            Scenery = GetComponentLookup<CombatSceneryState>(true), Corpses = GetComponentLookup<CombatCorpseState>(true),
            StaticPoses = GetComponentLookup<CombatStaticPose>(true), Bombs = GetComponentLookup<CombatBombState>(true),
            Matches = GetComponentLookup<CombatMatchState>(true), MatchEntity = MatchEntity, Configuration = Configuration,
            bLinearColorSpace = UnityEngine.QualitySettings.activeColorSpace == UnityEngine.ColorSpace.Linear
        }.ScheduleParallel(RenderParts, Dependency);
    }

    protected override void OnDestroy()
    {
        Dependency.Complete();
        if (bCommandsCreated)
        {
            Commands.Dispose();
        }
        InputCommands.Dispose();
        EnemyEntities.Dispose();
        BuildingEntities.Dispose();
        ProjectileEntities.Dispose();
        PickupEntities.Dispose();
        BombEntities.Dispose();
        EffectEntities.Dispose();
        SceneryEntities.Dispose();
        WallCells.Dispose();
        UniqueWallCells.Dispose();
        Candidates.Dispose();
        Obstacles.Dispose();
        Neighbors.Dispose();
        WorldNeighbors.Dispose();
        FrameData.Dispose();
        TargetCells.Dispose();
        ObstacleCells.Dispose();
        MaximumTargetRadius.Dispose();
        if (bNavigationCreated)
        {
            QueryBuffer.Dispose();
            Nodes.Dispose();
            LeftPortals.Dispose();
            RightPortals.Dispose();
            Navigation.Dispose();
        }
    }
}
