using Unity.AI.Navigation.LowLevel;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

[DisableAutoCreation]
public partial class CombatBotSimulationSystem : SystemBase
{
    public NativeList<CombatTargetCandidate> Candidates;
    public NativeList<CombatNavigationObstacle> Obstacles;
    public CombatBotFrame Frame;
    public EntityQuery Bots { get; private set; }
    public EntityQuery Projectiles { get; private set; }
    public long ScheduledSteps { get; private set; }
    public long NavigationJobs { get; private set; }
    private NativeReference<CombatBotFrame> FrameData;
    private NativeParallelMultiHashMap<int2, CombatBotNeighbor> Neighbors;
    private NavWorld Navigation;
    private NavQueryBuffer QueryBuffer;
    private NativeArray<NavNode> Nodes;
    private NativeArray<float3> LeftPortals;
    private NativeArray<float3> RightPortals;
    private EntityCommandBuffer Commands;
    private bool bNavigationCreated;
    private bool bCommandsCreated;

    protected override void OnCreate()
    {
        Bots = GetEntityQuery(ComponentType.ReadWrite<CombatBotState>(), ComponentType.ReadOnly<CombatBotStatistics>(),
            ComponentType.ReadWrite<CombatPathCorner>(), ComponentType.ReadWrite<CombatAttackVictim>(), ComponentType.ReadWrite<CombatBotEvent>());
        Projectiles = GetEntityQuery(ComponentType.ReadWrite<CombatHostileProjectile>(), ComponentType.ReadWrite<CombatBotEvent>());
        FrameData = new NativeReference<CombatBotFrame>(Allocator.Persistent);
        Candidates = new NativeList<CombatTargetCandidate>(32, Allocator.Persistent);
        Obstacles = new NativeList<CombatNavigationObstacle>(64, Allocator.Persistent);
        Neighbors = new NativeParallelMultiHashMap<int2, CombatBotNeighbor>(128, Allocator.Persistent);
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
        if (Bots.IsEmptyIgnoreFilter)
        {
            return;
        }
        FrameData.Value = Frame;
        Neighbors.Clear();
        if (Neighbors.Capacity < Frame.Population)
        {
            Neighbors.Capacity = math.ceilpow2(Frame.Population);
        }
        NativeArray<Entity> Entities = Bots.ToEntityArray(Allocator.TempJob);
        Commands = new EntityCommandBuffer(Allocator.TempJob);
        bCommandsCreated = true;
        Dependency = new CombatBotSpatialIndexJob
        {
            BotType = GetComponentTypeHandle<CombatBotState>(true),
            EntityType = GetEntityTypeHandle(),
            Neighbors = Neighbors.AsParallelWriter()
        }.ScheduleParallel(Bots, Dependency);
        Dependency = new CombatBotTargetSelectionJob
        {
            Candidates = Candidates.AsArray(),
            Obstacles = Obstacles.AsArray(),
            BotType = GetComponentTypeHandle<CombatBotState>(false)
        }.ScheduleParallel(Bots, Dependency);
        Dependency = new CombatBotPreparationJob
        {
            BotType = GetComponentTypeHandle<CombatBotState>(false),
            StatisticsType = GetComponentTypeHandle<CombatBotStatistics>(true),
            EventType = GetBufferTypeHandle<CombatBotEvent>(),
            Candidates = Candidates.AsArray(),
            Obstacles = Obstacles.AsArray(),
            FrameData = FrameData
        }.ScheduleParallel(Bots, Dependency);
        Dependency = new CombatBotAttackAdmissionJob
        {
            Entities = Entities,
            Bots = GetComponentLookup<CombatBotState>(),
            Statistics = GetComponentLookup<CombatBotStatistics>(true),
            Candidates = Candidates.AsArray(),
            FrameData = FrameData
        }.Schedule(Dependency);
        Dependency = new CombatBotNavigationJob
        {
            BotType = GetComponentTypeHandle<CombatBotState>(false),
            PathType = GetBufferTypeHandle<CombatPathCorner>(),
            Navigation = Navigation,
            QueryBuffer = QueryBuffer,
            Nodes = Nodes,
            LeftPortals = LeftPortals,
            RightPortals = RightPortals,
            FrameData = FrameData
        }.Schedule(Bots, Dependency);
        Navigation.AddDependency(Dependency);
        NavigationJobs++;
        Dependency = new CombatBotMovementJob
        {
            BotType = GetComponentTypeHandle<CombatBotState>(false),
            StatisticsType = GetComponentTypeHandle<CombatBotStatistics>(true),
            EntityType = GetEntityTypeHandle(),
            PathType = GetBufferTypeHandle<CombatPathCorner>(),
            Obstacles = Obstacles.AsArray(),
            Neighbors = Neighbors,
            FrameData = FrameData
        }.ScheduleParallel(Bots, Dependency);
        Dependency = new CombatBotAttackResolutionJob
        {
            BotType = GetComponentTypeHandle<CombatBotState>(false),
            StatisticsType = GetComponentTypeHandle<CombatBotStatistics>(true),
            VictimType = GetBufferTypeHandle<CombatAttackVictim>(),
            EventType = GetBufferTypeHandle<CombatBotEvent>(),
            Candidates = Candidates.AsArray(),
            FrameData = FrameData
        }.ScheduleParallel(Bots, Dependency);
        Dependency = Entities.Dispose(Dependency);
        ScheduledSteps++;
    }

    public void CompleteBots()
    {
        Dependency.Complete();
        if (bCommandsCreated)
        {
            Commands.Playback(EntityManager);
            Commands.Dispose();
            bCommandsCreated = false;
        }
    }

    public void ScheduleProjectiles()
    {
        if (Projectiles.IsEmptyIgnoreFilter)
        {
            return;
        }
        Dependency = new CombatHostileProjectileJob
        {
            ProjectileType = GetComponentTypeHandle<CombatHostileProjectile>(false),
            EventType = GetBufferTypeHandle<CombatBotEvent>(),
            Candidates = Candidates.AsArray(),
            Obstacles = Obstacles.AsArray(),
            Frame = Frame
        }.ScheduleParallel(Projectiles, Dependency);
    }

    protected override void OnDestroy()
    {
        Dependency.Complete();
        if (bCommandsCreated)
        {
            Commands.Dispose();
        }
        FrameData.Dispose();
        Candidates.Dispose();
        Obstacles.Dispose();
        Neighbors.Dispose();
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
