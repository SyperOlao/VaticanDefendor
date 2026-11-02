using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using VaticanDefendor.Combat;
using VaticanDefendor.Navigation;
using VaticanDefendor.Production;
using VaticanDefendor.RunEconomy;
using VaticanDefendor.Session;

namespace VaticanDefendor.Construction
{
    public static class ConstructionSession
    {
        public static Entity Create(EntityManager entities, ConstructionRunRules rules,
            ConstructionDefinitionRuntime[] definitions)
        {
            if (rules.GridWidth < 3 || rules.GridHeight < 3 || rules.CellSizeMeters <= 0f ||
                rules.MaximumWallCells < 1 || rules.MinimumCommandIntervalSeconds <= 0f ||
                rules.HeroClearanceMeters <= 0f || definitions == null || definitions.Length != 2)
                throw new ArgumentException("Construction rules or definitions are invalid.");
            Entity run = entities.CreateEntity(typeof(ConstructionRunRules), typeof(ConstructionRunState));
            entities.SetComponentData(run, rules);
            entities.SetComponentData(run, new ConstructionRunState { NextStructureId = 1 });
            DynamicBuffer<ConstructionDefinitionRuntime> buffer =
                entities.AddBuffer<ConstructionDefinitionRuntime>(run);
            foreach (ConstructionDefinitionRuntime definition in definitions) buffer.Add(definition);
            entities.AddBuffer<OccupiedCell>(run);
            return run;
        }

        public static ConstructionResultRpc Apply(EntityManager entities, Entity runEntity,
            Entity prefab, Entity navigationEntity, NavigationFlowSystem navigation,
            Entity hero, RunActivityState activity, RunSimulationTime time,
            ref RunEconomyState economy, ConstructionRequest request)
        {
            ConstructionRunState state = entities.GetComponentData<ConstructionRunState>(runEntity);
            var result = new ConstructionResultRpc
            {
                OperationId = request.OperationId,
                GoldAfter = economy.Gold
            };
            if (request.OperationId == 0 || request.OperationId <= state.LastOperationId)
            {
                result.Outcome = ConstructionOutcome.Duplicate;
                if (request.OperationId != 0 && request.OperationId == state.LastOperationId)
                {
                    if (request.DefinitionId == state.LastDefinitionId &&
                        request.StartX == state.LastStartX && request.StartZ == state.LastStartZ &&
                        request.EndX == state.LastEndX && request.EndZ == state.LastEndZ)
                    {
                        result.Outcome = state.LastOutcome;
                        result.BuiltCount = state.LastBuiltCount;
                        result.GoldAfter = state.LastGoldAfter;
                    }
                    else
                    {
                        result.Outcome = ConstructionOutcome.ConflictingOperation;
                    }
                }
                return result;
            }

            ConstructionRunRules rules = entities.GetComponentData<ConstructionRunRules>(runEntity);
            DynamicBuffer<ConstructionDefinitionRuntime> definitions =
                entities.GetBuffer<ConstructionDefinitionRuntime>(runEntity);
            ConstructionDefinitionRuntime definition = default;
            bool found = false;
            for (int index = 0; index < definitions.Length; index++)
            {
                if (definitions[index].StableId != request.DefinitionId) continue;
                definition = definitions[index];
                found = true;
                break;
            }

            if (activity.Paused != 0 || activity.Terminal != 0 ||
                hero == Entity.Null || !entities.Exists(hero) ||
                entities.GetComponentData<CombatHealth>(hero).Dead != 0)
                result.Outcome = ConstructionOutcome.InactiveRun;
            else if (!found) result.Outcome = ConstructionOutcome.InvalidDefinition;
            else if (time.ElapsedSeconds < state.NextAllowedSeconds)
                result.Outcome = ConstructionOutcome.RateLimited;
            else if (!InBounds(request.StartX, request.StartZ, rules) ||
                     !InBounds(request.EndX, request.EndZ, rules))
                result.Outcome = ConstructionOutcome.OutOfBounds;
            else if (definition.Kind != StructureKind.Wall &&
                     (request.StartX != request.EndX || request.StartZ != request.EndZ))
                result.Outcome = ConstructionOutcome.InvalidPath;
            else
            {
                long length = Math.Abs((long)request.EndX - request.StartX) +
                    Math.Abs((long)request.EndZ - request.StartZ) + 1L;
                if (length > (definition.Kind == StructureKind.Wall ? rules.MaximumWallCells : 1))
                    result.Outcome = ConstructionOutcome.InvalidPath;
                else
                {
                    List<GridCell> cells = GridCell.Trace(
                        new GridCell(request.StartX, request.StartZ),
                        new GridCell(request.EndX, request.EndZ));
                    result.Outcome = ValidatePlacement(entities, runEntity, navigationEntity,
                        navigation, hero, rules, definition, cells, economy.Gold);
                    if (result.Outcome == ConstructionOutcome.Built)
                    {
                        long cost = checked((long)definition.GoldPrice * cells.Count);
                        if (!RunEconomySession.TrySpend(ref economy, cost))
                            throw new InvalidOperationException("Validated construction cost exceeded RunEconomy balance.");
                        DynamicBuffer<OccupiedCell> occupied = entities.GetBuffer<OccupiedCell>(runEntity);
                        DynamicBuffer<NavigationObstacleRequest> blockers =
                            entities.GetBuffer<NavigationObstacleRequest>(navigationEntity);
                        var commands = new EntityCommandBuffer(Allocator.Temp);
                        foreach (GridCell cell in cells)
                        {
                            int structureId = state.NextStructureId++;
                            float2 position = CellCenter(cell, rules);
                            Entity instance = commands.Instantiate(prefab);
                            commands.SetComponent(instance, LocalTransform.FromPosition(
                                new float3(position.x, 0f, position.y)));
                            commands.SetComponent(instance, new StructureInstance
                            {
                                StructureId = structureId,
                                Kind = definition.Kind,
                                CellX = cell.Horizontal,
                                CellZ = cell.Vertical
                            });
                            commands.SetComponent(instance, new ProductionSource
                            {
                                CycleSeconds = definition.ProductionCycleSeconds,
                                GoldPerCycle = definition.ProductionGoldPerCycle,
                                PickupOffsetZMeters = definition.PickupOffsetZMeters,
                                PickupRadiusMeters = definition.PickupRadiusMeters
                            });
                            occupied.Add(new OccupiedCell
                            {
                                StructureId = structureId,
                                X = cell.Horizontal,
                                Z = cell.Vertical,
                                HalfExtentMeters = definition.HalfExtentMeters
                            });
                            blockers.Add(new NavigationObstacleRequest
                            {
                                StableId = structureId,
                                Center = position,
                                RadiusMeters = definition.HalfExtentMeters
                            });
                        }
                        commands.Playback(entities);
                        commands.Dispose();
                        result.BuiltCount = cells.Count;
                        result.GoldAfter = economy.Gold;
                    }
                }
            }

            if (result.Outcome != ConstructionOutcome.RateLimited)
                state.NextAllowedSeconds = time.ElapsedSeconds + rules.MinimumCommandIntervalSeconds;
            state.LastOperationId = request.OperationId;
            state.LastOutcome = result.Outcome;
            state.LastBuiltCount = result.BuiltCount;
            state.LastGoldAfter = result.GoldAfter;
            state.LastDefinitionId = request.DefinitionId;
            state.LastStartX = request.StartX;
            state.LastStartZ = request.StartZ;
            state.LastEndX = request.EndX;
            state.LastEndZ = request.EndZ;
            entities.SetComponentData(runEntity, state);
            return result;
        }

        private static ConstructionOutcome ValidatePlacement(EntityManager entities, Entity runEntity,
            Entity navigationEntity, NavigationFlowSystem navigation, Entity hero,
            ConstructionRunRules rules, ConstructionDefinitionRuntime definition,
            List<GridCell> cells, long gold)
        {
            NavigationGridConfig grid = entities.GetComponentData<NavigationGridConfig>(navigationEntity);
            DynamicBuffer<NavigationObstacleRequest> pending =
                entities.GetBuffer<NavigationObstacleRequest>(navigationEntity);
            if (navigation.DynamicObstacleCount + pending.Length + cells.Count > grid.MaximumDynamicObstacles ||
                entities.GetComponentData<ConstructionRunState>(runEntity).NextStructureId >
                int.MaxValue - cells.Count)
                return ConstructionOutcome.CapacityReached;
            float3 heroPosition = entities.GetComponentData<LocalTransform>(hero).Position;
            DynamicBuffer<OccupiedCell> occupied = entities.GetBuffer<OccupiedCell>(runEntity);
            foreach (GridCell cell in cells)
            {
                float2 center = CellCenter(cell, rules);
                if (!navigation.IsWalkable(center)) return ConstructionOutcome.Blocked;
                float clearance = rules.HeroClearanceMeters +
                    math.max(0f, definition.HalfExtentMeters - rules.CellSizeMeters * 0.5f);
                if (math.distancesq(center, new float2(heroPosition.x, heroPosition.z)) <
                    clearance * clearance) return ConstructionOutcome.HeroTooClose;
                foreach (OccupiedCell other in occupied)
                {
                    float2 otherCenter = new float2(other.X, other.Z) * rules.CellSizeMeters;
                    float extent = definition.HalfExtentMeters + other.HalfExtentMeters - 0.01f;
                    if (math.abs(center.x - otherCenter.x) < extent &&
                        math.abs(center.y - otherCenter.y) < extent)
                        return ConstructionOutcome.Occupied;
                }
            }
            using (EntityQuery enemies = entities.CreateEntityQuery(
                ComponentType.ReadOnly<CombatTargetRole>(), ComponentType.ReadOnly<CombatHealth>(),
                ComponentType.ReadOnly<LocalTransform>()))
            using (NativeArray<Entity> enemyEntities = enemies.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity enemy in enemyEntities)
                {
                    if (entities.GetComponentData<CombatHealth>(enemy).Dead != 0) continue;
                    float3 enemyPosition = entities.GetComponentData<LocalTransform>(enemy).Position;
                    foreach (GridCell cell in cells)
                    {
                        float2 center = CellCenter(cell, rules);
                        float radius = definition.HalfExtentMeters + 0.4f;
                        if (math.distancesq(center, new float2(enemyPosition.x, enemyPosition.z)) <
                            radius * radius) return ConstructionOutcome.Occupied;
                    }
                }
            }
            long cost = checked((long)definition.GoldPrice * cells.Count);
            return gold >= cost ? ConstructionOutcome.Built : ConstructionOutcome.InsufficientGold;
        }

        private static bool InBounds(int x, int z, ConstructionRunRules rules) =>
            x >= -rules.GridWidth / 2 && x <= rules.GridWidth / 2 &&
            z >= -rules.GridHeight / 2 && z <= rules.GridHeight / 2;

        private static float2 CellCenter(GridCell cell, ConstructionRunRules rules) =>
            new float2(cell.Horizontal, cell.Vertical) * rules.CellSizeMeters;
    }
}
