using Unity.NetCode;
using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using Unity.Profiling;
using VaticanDefendor.Session;

namespace VaticanDefendor.Navigation
{
    public struct NavigationGridConfig : IComponentData
    {
        public int Width;
        public int Height;
        public float CellSizeMeters;
        public int FlowCellsPerTick;
        public float GoalRefreshSeconds;
        public float GoalMoveThresholdMeters;
        public int MaximumDynamicObstacles;
    }

    public struct NavigationGoalRequest : IComponentData
    {
        public float2 Position;
        public byte Active;
    }

    public struct NavigationGoal : IComponentData
    {
        public Entity Target;
        public float2 Position;
        public float StoppingDistanceMeters;
        public byte Active;
    }

    public struct NavigationSteering : IComponentData
    {
        public Entity Target;
        public float2 Direction;
        public uint FlowVersion;
    }

    // Construction submits upserts/removals here. Navigation owns the resulting occupancy.
    public struct NavigationObstacleRequest : IBufferElementData
    {
        public int StableId;
        public Entity Owner;
        public float2 Center;
        public float RadiusMeters;
        public float RotationRadians;
        public byte Remove;
    }

    internal struct NavigationDynamicObstacle
    {
        public int StableId;
        public Entity Owner;
        public float2 Center;
        public float RadiusMeters;
        public float RotationRadians;
    }

    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(PredictedSimulationSystemGroup))]
    public partial class NavigationFlowSystem : SystemBase
    {
        private static readonly ProfilerMarker OccupancyMarker = new ProfilerMarker("VaticanDefendor.Navigation.RebuildOccupancy");
        private NativeArray<byte> _staticBlocked;
        private NativeArray<byte> _blocked;
        private NativeArray<int> _activeDistances;
        private NativeArray<int> _workingDistances;
        private NativeArray<int> _queue;
        private NativeList<NavigationDynamicObstacle> _dynamicObstacles;
        private Entity _configEntity;
        private int _head;
        private int _tail;
        private int _workingGoal;
        private int _activeGoal;
        private bool _building;
        private bool _hasField;
        private bool _dirty;
        private double _nextGoalRefresh;
        private float2 _lastGoalPosition;
        private uint _version;
        private readonly List<RouteField> _routes = new List<RouteField>();
        private int _routeCount;
        private int[] _routeQueue;

        private sealed class RouteField
        {
            public float2 Goal;
            public float StoppingDistance;
            public bool StaticOnly;
            public int[] Distances;
        }

        protected override void OnCreate()
        {
            RequireForUpdate<NavigationGridConfig>();
            RequireForUpdate<RunSimulationTime>();
        }

        public void Initialize(NavigationGridConfig config, float3[] staticObstacles)
        {
            Map = VaticanDefendor.Maps.RiverMapAccess.Get(EntityManager);
            if (Map != null)
            {
                config.Width = 129;
                config.Height = 129;
                config.CellSizeMeters = Map.WidthMeters / 128;
                config.MaximumDynamicObstacles = 512;
                config.FlowCellsPerTick = 4096;
                staticObstacles = new float3[Map.Rocks.Length];
                for (int Index = 0; Index < Map.Rocks.Length; Index++)
                {
                    RiverMapPlacement Rock = Map.Rocks[Index];
                    staticObstacles[Index] = new float3(Rock.Horizontal, Rock.Vertical, Rock.Radius);
                }
            }
            if (_blocked.IsCreated || config.Width < 3 || config.Height < 3 ||
                config.CellSizeMeters <= 0f || config.FlowCellsPerTick < 1 ||
                config.MaximumDynamicObstacles < 1 || staticObstacles == null)
                throw new ArgumentException("Navigation grid initialization is invalid.");
            int cells = checked(config.Width * config.Height);
            _routeQueue = new int[cells];
            _staticBlocked = new NativeArray<byte>(cells, Allocator.Persistent);
            _blocked = new NativeArray<byte>(cells, Allocator.Persistent);
            _activeDistances = new NativeArray<int>(cells, Allocator.Persistent);
            _workingDistances = new NativeArray<int>(cells, Allocator.Persistent);
            _queue = new NativeArray<int>(cells, Allocator.Persistent);
            _dynamicObstacles = new NativeList<NavigationDynamicObstacle>(Allocator.Persistent);
            _configEntity = EntityManager.CreateEntity(typeof(NavigationGridConfig),
                typeof(NavigationGoalRequest));
            EntityManager.SetComponentData(_configEntity, config);
            EntityManager.AddBuffer<NavigationObstacleRequest>(_configEntity);
            if (Map != null)
            {
                for (int Index = 0; Index < cells; Index++)
                {
                    float2 Center = CellCenter(config, Index);
                    float Margin = config.CellSizeMeters * 0.5f;
                    if (!Map.CanWalk(Center.x, Center.y, 0.55f) ||
                        !Map.CanWalk(Center.x - Margin, Center.y - Margin) ||
                        !Map.CanWalk(Center.x + Margin, Center.y - Margin) ||
                        !Map.CanWalk(Center.x - Margin, Center.y + Margin) ||
                        !Map.CanWalk(Center.x + Margin, Center.y + Margin))
                    {
                        _staticBlocked[Index] = 1;
                    }
                }
            }
            foreach (float3 obstacle in staticObstacles)
            {
                MarkCircle(_staticBlocked, config, obstacle.xy, obstacle.z);
            }
            CopyBlocked(config);
            _activeGoal = -1;
            _dirty = true;
        }

        protected override void OnUpdate()
        {
            NavigationGridConfig config = SystemAPI.GetSingleton<NavigationGridConfig>();
            RunSimulationTime time = SystemAPI.GetSingleton<RunSimulationTime>();
            DynamicBuffer<NavigationObstacleRequest> requests =
                EntityManager.GetBuffer<NavigationObstacleRequest>(_configEntity);
            if (requests.Length > 0)
            {
                ApplyRequests(config, requests);
                requests.Clear();
            }

            NavigationGoalRequest goal = SystemAPI.GetSingleton<NavigationGoalRequest>();
            if (goal.Active != 0 && time.ElapsedSeconds >= _nextGoalRefresh &&
                (!_building && (_dirty || !_hasField ||
                 _hasField && math.distancesq(goal.Position, _lastGoalPosition) >=
                 config.GoalMoveThresholdMeters * config.GoalMoveThresholdMeters)))
            {
                int goalCell = NearestWalkableIndex(config, goal.Position);
                BeginBuild(goalCell);
                _lastGoalPosition = goal.Position;
                _nextGoalRefresh = time.ElapsedSeconds + config.GoalRefreshSeconds;
                _dirty = false;
            }
            if (_building) AdvanceBuild(config, config.FlowCellsPerTick);

            _routeCount = 0;
            foreach (var (Steering, Goal, Position) in
                SystemAPI.Query<RefRW<NavigationSteering>, RefRO<NavigationGoal>, RefRO<LocalTransform>>().WithAll<Simulate>())
            {
                Steering.ValueRW.Target = Entity.Null;
                Steering.ValueRW.Direction = float2.zero;
                Steering.ValueRW.FlowVersion = _version;
                if (Goal.ValueRO.Active == 0)
                {
                    continue;
                }
                float2 Origin = Position.ValueRO.Position.xz;
                if (TryRoute(config, Origin, Goal.ValueRO.Position, Goal.ValueRO.StoppingDistanceMeters, out float2 Direction))
                {
                    Steering.ValueRW.Direction = Direction;
                    Steering.ValueRW.Target = Goal.ValueRO.Target;
                    continue;
                }
                if (TryFindBlocker(config, Origin, Goal.ValueRO.Position, Goal.ValueRO.StoppingDistanceMeters,
                    out NavigationDynamicObstacle Blocker) &&
                    TryRoute(config, Origin, Blocker.Center, Goal.ValueRO.StoppingDistanceMeters, out Direction))
                {
                    Steering.ValueRW.Direction = Direction;
                    Steering.ValueRW.Target = Blocker.Owner;
                }
            }
        }

        private RouteField GetRouteField(NavigationGridConfig Configuration, float2 Goal, float StoppingDistance, bool StaticOnly)
        {
            for (int Index = 0; Index < _routeCount; Index++)
            {
                RouteField Existing = _routes[Index];
                if (math.all(Existing.Goal == Goal) && Existing.StoppingDistance == StoppingDistance && Existing.StaticOnly == StaticOnly)
                {
                    return Existing;
                }
            }
            if (_routeCount == _routes.Count)
            {
                _routes.Add(new RouteField { Distances = new int[_blocked.Length] });
            }
            RouteField Field = _routes[_routeCount++];
            Field.Goal = Goal;
            Field.StoppingDistance = StoppingDistance;
            Field.StaticOnly = StaticOnly;
            NativeArray<byte> Blocked = _blocked;
            if (StaticOnly)
            {
                Blocked = _staticBlocked;
            }
            int Head = 0;
            int Tail = 0;
            for (int Index = 0; Index < Field.Distances.Length; Index++)
            {
                Field.Distances[Index] = int.MaxValue;
                if (Blocked[Index] == 0 && math.distancesq(CellCenter(Configuration, Index), Goal) <= StoppingDistance * StoppingDistance)
                {
                    Field.Distances[Index] = 0;
                    _routeQueue[Tail++] = Index;
                }
            }
            while (Head < Tail)
            {
                int Current = _routeQueue[Head++];
                int Horizontal = Current % Configuration.Width;
                int Vertical = Current / Configuration.Width;
                if (Horizontal > 0)
                {
                    VisitRoute(Current - 1, Current, Field, Blocked, ref Tail);
                }
                if (Horizontal + 1 < Configuration.Width)
                {
                    VisitRoute(Current + 1, Current, Field, Blocked, ref Tail);
                }
                if (Vertical > 0)
                {
                    VisitRoute(Current - Configuration.Width, Current, Field, Blocked, ref Tail);
                }
                if (Vertical + 1 < Configuration.Height)
                {
                    VisitRoute(Current + Configuration.Width, Current, Field, Blocked, ref Tail);
                }
            }
            return Field;
        }

        private void VisitRoute(int Candidate, int Current, RouteField Field, NativeArray<byte> Blocked, ref int Tail)
        {
            if (Blocked[Candidate] != 0 || Field.Distances[Candidate] != int.MaxValue)
            {
                return;
            }
            Field.Distances[Candidate] = Field.Distances[Current] + 1;
            _routeQueue[Tail++] = Candidate;
        }

        private bool TryRoute(NavigationGridConfig Configuration, float2 Origin, float2 Goal, float StoppingDistance, out float2 Direction)
        {
            Direction = float2.zero;
            if (math.distancesq(Origin, Goal) <= StoppingDistance * StoppingDistance)
            {
                return true;
            }
            int Start = ToIndex(Configuration, Origin);
            if (Start < 0)
            {
                return false;
            }
            if (_hasField && math.all(Goal == _lastGoalPosition) && _activeDistances[Start] != int.MaxValue)
            {
                Direction = CalculateDirection(Configuration, Origin, Goal, StoppingDistance);
                if (math.lengthsq(Direction) > 0)
                {
                    return true;
                }
            }
            RouteField Field = GetRouteField(Configuration, Goal, StoppingDistance, false);
            if (Field.Distances[Start] == int.MaxValue)
            {
                return false;
            }
            if (Field.Distances[Start] == 0)
            {
                Direction = math.normalizesafe(CellCenter(Configuration, Start) - Origin);
                return true;
            }
            int Next = NextRouteCell(Configuration, Start, Field);
            if (Next < 0)
            {
                return false;
            }
            Direction = math.normalizesafe(CellCenter(Configuration, Next) - Origin);
            return true;
        }

        private static int NextRouteCell(NavigationGridConfig Configuration, int Current, RouteField Field)
        {
            int Best = -1;
            int Distance = Field.Distances[Current];
            int Horizontal = Current % Configuration.Width;
            int Vertical = Current / Configuration.Width;
            void ChooseRoute(int Candidate)
            {
                if (Field.Distances[Candidate] < Distance)
                {
                    Best = Candidate;
                    Distance = Field.Distances[Candidate];
                }
            }
            if (Horizontal > 0)
            {
                ChooseRoute(Current - 1);
            }
            if (Horizontal + 1 < Configuration.Width)
            {
                ChooseRoute(Current + 1);
            }
            if (Vertical > 0)
            {
                ChooseRoute(Current - Configuration.Width);
            }
            if (Vertical + 1 < Configuration.Height)
            {
                ChooseRoute(Current + Configuration.Width);
            }
            return Best;
        }

        private bool TryFindBlocker(NavigationGridConfig Configuration, float2 Origin, float2 Goal, float StoppingDistance,
            out NavigationDynamicObstacle Blocker)
        {
            Blocker = default;
            int Current = ToIndex(Configuration, Origin);
            if (Current < 0)
            {
                return false;
            }
            RouteField Field = GetRouteField(Configuration, Goal, StoppingDistance, true);
            if (Field.Distances[Current] == int.MaxValue)
            {
                return false;
            }
            while (Field.Distances[Current] > 0)
            {
                int Next = NextRouteCell(Configuration, Current, Field);
                if (Next < 0)
                {
                    return false;
                }
                if (_blocked[Next] != 0)
                {
                    float2 Center = CellCenter(Configuration, Next);
                    foreach (NavigationDynamicObstacle Candidate in _dynamicObstacles)
                    {
                        if (Candidate.Owner != Entity.Null && InsideObstacle(Center, Candidate.Center, Candidate.RadiusMeters,
                            Candidate.RotationRadians, Configuration.CellSizeMeters * 0.35f))
                        {
                            Blocker = Candidate;
                            return true;
                        }
                    }
                    return false;
                }
                Current = Next;
            }
            return false;
        }

        public bool IsStaticWalkable(float2 Position)
        {
            if (!_staticBlocked.IsCreated)
            {
                return false;
            }
            NavigationGridConfig Configuration = EntityManager.GetComponentData<NavigationGridConfig>(_configEntity);
            int Index = ToIndex(Configuration, Position);
            return Index >= 0 && _staticBlocked[Index] == 0;
        }

        public bool IsWalkable(float2 position)
        {
            if (!_blocked.IsCreated) return false;
            NavigationGridConfig config = EntityManager.GetComponentData<NavigationGridConfig>(_configEntity);
            int index = ToIndex(config, position);
            return index >= 0 && _blocked[index] == 0;
        }

        public bool IsWalkableIgnoring(float2 Position, Entity IgnoredTarget)
        {
            NavigationGridConfig Configuration = EntityManager.GetComponentData<NavigationGridConfig>(_configEntity);
            int Index = ToIndex(Configuration, Position);
            if (Index < 0 || _staticBlocked[Index] != 0)
            {
                return false;
            }
            if (_blocked[Index] == 0)
            {
                return true;
            }
            if (IgnoredTarget == Entity.Null)
            {
                return false;
            }
            float2 Center = CellCenter(Configuration, Index);
            foreach (NavigationDynamicObstacle Obstacle in _dynamicObstacles)
            {
                if (Obstacle.Owner != IgnoredTarget && InsideObstacle(Center, Obstacle.Center, Obstacle.RadiusMeters,
                    Obstacle.RotationRadians, Configuration.CellSizeMeters * 0.35f))
                {
                    return false;
                }
            }
            return true;
        }

        public float2 NearestWalkable(float2 position)
        {
            NavigationGridConfig config = EntityManager.GetComponentData<NavigationGridConfig>(_configEntity);
            int index = NearestWalkableIndex(config, position);
            return index < 0 ? position : CellCenter(config, index);
        }

        public uint FlowVersion => _version;
        public RiverMapConfiguration Map { get; private set; }
        public int DynamicObstacleCount => _dynamicObstacles.IsCreated ? _dynamicObstacles.Length : 0;

        private void ApplyRequests(NavigationGridConfig config, DynamicBuffer<NavigationObstacleRequest> requests)
        {
            for (int requestIndex = 0; requestIndex < requests.Length; requestIndex++)
            {
                NavigationObstacleRequest request = requests[requestIndex];
                if (request.StableId <= 0) throw new InvalidOperationException("Navigation obstacle StableId must be positive.");
                int found = -1;
                for (int index = 0; index < _dynamicObstacles.Length; index++)
                    if (_dynamicObstacles[index].StableId == request.StableId) { found = index; break; }
                if (request.Remove != 0)
                {
                    if (found >= 0) _dynamicObstacles.RemoveAtSwapBack(found);
                }
                else
                {
                    if (!math.all(math.isfinite(request.Center)) ||
                        !math.isfinite(request.RadiusMeters) || request.RadiusMeters <= 0f)
                        throw new InvalidOperationException($"Navigation obstacle {request.StableId} has invalid geometry.");
                    var obstacle = new NavigationDynamicObstacle
                    {
                        StableId = request.StableId,
                        Owner = request.Owner,
                        Center = request.Center,
                        RadiusMeters = request.RadiusMeters,
                        RotationRadians = request.RotationRadians
                    };
                    if (found >= 0)
                    {
                        if (obstacle.Owner == Entity.Null)
                        {
                            obstacle.Owner = _dynamicObstacles[found].Owner;
                        }
                        _dynamicObstacles[found] = obstacle;
                    }
                    else
                    {
                        if (_dynamicObstacles.Length >= config.MaximumDynamicObstacles)
                            throw new InvalidOperationException("Navigation dynamic obstacle capacity exceeded.");
                        _dynamicObstacles.Add(obstacle);
                    }
                }
            }
            CopyBlocked(config);
            _building = false;
            _hasField = false;
            _dirty = true;
            _nextGoalRefresh = 0d;
        }

        private void CopyBlocked(NavigationGridConfig config)
        {
            using var Scope = OccupancyMarker.Auto();
            NativeArray<byte>.Copy(_staticBlocked, _blocked);
            foreach (NavigationDynamicObstacle obstacle in _dynamicObstacles)
            {
                MarkBox(_blocked, config, obstacle.Center, obstacle.RadiusMeters, obstacle.RotationRadians);
            }
        }

        private static bool InsideObstacle(float2 Position, float2 Center, float HalfExtent, float Rotation, float Margin)
        {
            float2 Offset = Position - Center;
            float2 Local = new float2(Offset.x * math.cos(Rotation) - Offset.y * math.sin(Rotation),
                Offset.x * math.sin(Rotation) + Offset.y * math.cos(Rotation));
            return math.all(math.abs(Local) <= HalfExtent + Margin);
        }

        private static void MarkBox(NativeArray<byte> Grid, NavigationGridConfig Configuration,
            float2 Center, float HalfExtent, float Rotation)
        {
            float Cosine = math.cos(Rotation);
            float Sine = math.sin(Rotation);
            float PaddedExtent = HalfExtent + Configuration.CellSizeMeters * 0.35f;
            float BoundsExtent = PaddedExtent * (math.abs(Cosine) + math.abs(Sine));
            int2 GridCenter = new int2(Configuration.Width / 2, Configuration.Height / 2);
            int2 Minimum = math.max((int2)math.floor((Center - BoundsExtent) / Configuration.CellSizeMeters) + GridCenter, 0);
            int2 Maximum = math.min((int2)math.ceil((Center + BoundsExtent) / Configuration.CellSizeMeters) + GridCenter,
                new int2(Configuration.Width - 1, Configuration.Height - 1));
            for (int Vertical = Minimum.y; Vertical <= Maximum.y; Vertical++)
            {
                for (int Horizontal = Minimum.x; Horizontal <= Maximum.x; Horizontal++)
                {
                    float2 Offset = new float2(Horizontal - GridCenter.x, Vertical - GridCenter.y) * Configuration.CellSizeMeters - Center;
                    float2 Distance = math.abs(new float2(Offset.x * Cosine - Offset.y * Sine,
                        Offset.x * Sine + Offset.y * Cosine));
                    if (math.all(Distance <= PaddedExtent))
                    {
                        Grid[Horizontal + Vertical * Configuration.Width] = 1;
                    }
                }
            }
        }

        private static void MarkCircle(NativeArray<byte> grid, NavigationGridConfig config,
            float2 center, float radius)
        {
            for (int Index = 0; Index < grid.Length; Index++)
            {
                float2 Distance = math.max(math.abs(CellCenter(config, Index) - center) - config.CellSizeMeters * 0.5f, 0);
                if (math.lengthsq(Distance) <= radius * radius)
                {
                    grid[Index] = 1;
                }
            }
        }

        private void BeginBuild(int goalCell)
        {
            _head = 0;
            _tail = 0;
            _workingGoal = goalCell;
            for (int index = 0; index < _workingDistances.Length; index++)
                _workingDistances[index] = int.MaxValue;
            if (goalCell < 0)
            {
                _building = false;
                _hasField = false;
                return;
            }
            _workingDistances[goalCell] = 0;
            _queue[_tail++] = goalCell;
            _building = true;
        }

        private void AdvanceBuild(NavigationGridConfig config, int budget)
        {
            int processed = 0;
            while (_head < _tail && processed++ < budget)
            {
                int cell = _queue[_head++];
                int x = cell % config.Width;
                int z = cell / config.Width;
                if (x > 0) Visit(cell - 1, cell);
                if (x + 1 < config.Width) Visit(cell + 1, cell);
                if (z > 0) Visit(cell - config.Width, cell);
                if (z + 1 < config.Height) Visit(cell + config.Width, cell);
            }
            if (_head == _tail)
            {
                (_activeDistances, _workingDistances) = (_workingDistances, _activeDistances);
                _activeGoal = _workingGoal;
                _building = false;
                _hasField = true;
                _version++;
            }
        }

        private void Visit(int neighbor, int source)
        {
            if (_blocked[neighbor] != 0 || _workingDistances[neighbor] != int.MaxValue) return;
            _workingDistances[neighbor] = _workingDistances[source] + 1;
            _queue[_tail++] = neighbor;
        }

        private float2 CalculateDirection(NavigationGridConfig config, float2 position,
            float2 goal, float stoppingDistance)
        {
            float2 toGoal = goal - position;
            if (math.lengthsq(toGoal) <= stoppingDistance * stoppingDistance) return float2.zero;
            if (math.lengthsq(toGoal) < 36f)
            {
                float2 Direction = math.normalizesafe(toGoal);
                if (IsWalkable(position + Direction * config.CellSizeMeters * 0.5f))
                {
                    return Direction;
                }
            }
            int cell = ToIndex(config, position);
            if (cell < 0) return float2.zero;
            if (cell == _activeGoal) return math.normalizesafe(toGoal);
            int x = cell % config.Width;
            int z = cell / config.Width;
            int best = -1;
            int distance = _activeDistances[cell];
            if (x > 0) Choose(cell - 1, ref best, ref distance);
            if (x + 1 < config.Width) Choose(cell + 1, ref best, ref distance);
            if (z > 0) Choose(cell - config.Width, ref best, ref distance);
            if (z + 1 < config.Height) Choose(cell + config.Width, ref best, ref distance);
            if (best < 0) return float2.zero;
            return math.normalizesafe(CellCenter(config, best) - position);
        }

        private void Choose(int candidate, ref int best, ref int distance)
        {
            if (_blocked[candidate] == 0 && _activeDistances[candidate] < distance)
            {
                best = candidate;
                distance = _activeDistances[candidate];
            }
        }

        private int NearestWalkableIndex(NavigationGridConfig config, float2 position)
        {
            int nearest = -1;
            float distance = float.MaxValue;
            for (int index = 0; index < _blocked.Length; index++)
            {
                if (_blocked[index] != 0) continue;
                float candidate = math.distancesq(position, CellCenter(config, index));
                if (candidate < distance) { distance = candidate; nearest = index; }
            }
            return nearest;
        }

        private static int ToIndex(NavigationGridConfig config, float2 position)
        {
            int x = (int)math.round(position.x / config.CellSizeMeters) + config.Width / 2;
            int z = (int)math.round(position.y / config.CellSizeMeters) + config.Height / 2;
            return x < 0 || z < 0 || x >= config.Width || z >= config.Height
                ? -1 : x + z * config.Width;
        }

        private static float2 CellCenter(NavigationGridConfig config, int index) =>
            new float2((index % config.Width - config.Width / 2) * config.CellSizeMeters,
                (index / config.Width - config.Height / 2) * config.CellSizeMeters);

        protected override void OnDestroy()
        {
            if (_staticBlocked.IsCreated) _staticBlocked.Dispose();
            if (_blocked.IsCreated) _blocked.Dispose();
            if (_activeDistances.IsCreated) _activeDistances.Dispose();
            if (_workingDistances.IsCreated) _workingDistances.Dispose();
            if (_queue.IsCreated) _queue.Dispose();
            if (_dynamicObstacles.IsCreated) _dynamicObstacles.Dispose();
        }
    }
}
