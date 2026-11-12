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
                rules.HeroClearanceMeters <= 0f || definitions == null)
                throw new ArgumentException("Construction rules or definitions are invalid.");
            Entity run = entities.CreateEntity(typeof(ConstructionRunRules), typeof(ConstructionRunState));
            entities.SetComponentData(run, rules);
            entities.SetComponentData(run, new ConstructionRunState { NextStructureId = 1 });
            DynamicBuffer<ConstructionDefinitionRuntime> buffer =
                entities.AddBuffer<ConstructionDefinitionRuntime>(run);
            foreach (ConstructionDefinitionRuntime definition in definitions) buffer.Add(definition);
            entities.AddBuffer<OccupiedStructure>(run);
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
                WoodAfter = economy.Wood
            };
            if (request.OperationId == 0 || request.OperationId <= state.LastOperationId)
            {
                result.Outcome = ConstructionOutcome.Duplicate;
                if (request.OperationId != 0 && request.OperationId == state.LastOperationId)
                {
                    if (request.Operation == state.LastOperation && request.TargetStructureId == state.LastTargetStructureId &&
                        request.DefinitionId == state.LastDefinitionId &&
                        request.StartX == state.LastStartX && request.StartZ == state.LastStartZ &&
                        request.EndX == state.LastEndX && request.EndZ == state.LastEndZ)
                    {
                        result.Outcome = state.LastOutcome;
                        result.BuiltCount = state.LastBuiltCount;
                        result.WoodAfter = state.LastWoodAfter;
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
            else if (time.ElapsedSeconds < state.NextAllowedSeconds)
            {
                result.Outcome = ConstructionOutcome.RateLimited;
            }
            else if (request.Operation != ConstructionOperation.Build)
            {
                result.Outcome = StructureActionRules.Apply(entities, runEntity, hero, request, ref economy);
                state.NextStructureId = entities.GetComponentData<ConstructionRunState>(runEntity).NextStructureId;
                result.WoodAfter = economy.Wood;
            }
            else if (!found) result.Outcome = ConstructionOutcome.InvalidDefinition;
            else if (time.ElapsedSeconds < state.NextAllowedSeconds)
                result.Outcome = ConstructionOutcome.RateLimited;
            else if (!InBounds(request.StartX, request.StartZ, rules) ||
                     !InBounds(request.EndX, request.EndZ, rules))
                result.Outcome = ConstructionOutcome.OutOfBounds;
            else
            {
                int length = ConstructionGeometry.SegmentCount(new float2(request.StartX, request.StartZ),
                    new float2(request.EndX, request.EndZ), definition.HalfExtentMeters, definition.Kind == StructureKind.Wall);
                if (length > (definition.Kind == StructureKind.Wall ? rules.MaximumWallCells : 1))
                    result.Outcome = ConstructionOutcome.InvalidPath;
                else
                {
                    List<float2> cells = ConstructionGeometry.Positions(new float2(request.StartX, request.StartZ),
                        new float2(request.EndX, request.EndZ), definition.HalfExtentMeters, definition.Kind == StructureKind.Wall);
                    float Rotation = ConstructionGeometry.Rotation(new float2(request.StartX, request.StartZ), new float2(request.EndX, request.EndZ));
                    result.Outcome = ValidatePlacement(entities, runEntity, navigationEntity,
                        navigation, hero, rules, definition, cells, Rotation, economy.Resources);
                    if (result.Outcome == ConstructionOutcome.Built)
                    {
                        ResourceAmounts cost = new ResourceAmounts(definition.WoodPrice, definition.StonePrice, definition.IronPrice) * cells.Count;
                        if (!RunEconomySession.TrySpend(ref economy, cost))
                            throw new InvalidOperationException("Validated construction cost exceeded RunEconomy balance.");
                        DynamicBuffer<OccupiedStructure> occupied = entities.GetBuffer<OccupiedStructure>(runEntity);
                        DynamicBuffer<NavigationObstacleRequest> blockers =
                            entities.GetBuffer<NavigationObstacleRequest>(navigationEntity);
                        var commands = new EntityCommandBuffer(Allocator.Temp);
                        foreach (float2 cell in cells)
                        {
                            int structureId = state.NextStructureId++;
                            float2 position = CellCenter(cell, rules);
                            Entity instance = commands.Instantiate(prefab);
                            float Height = 0;
                            if (navigation.Map != null)
                            {
                                Height = navigation.Map.Height(position.x, position.y);
                            }
                            commands.SetComponent(instance, LocalTransform.FromPositionRotation(
                                new float3(position.x, Height, position.y), quaternion.RotateY(Rotation)));
                            commands.SetComponent(instance, new StructureInstance
                            {
                                StructureId = structureId,
                                DefinitionId = definition.StableId,
                                HalfExtent = definition.HalfExtentMeters,
                                Level = 1,
                                PaidWood = definition.WoodPrice,
                                PaidStone = definition.StonePrice,
                                PaidIron = definition.IronPrice,
                                Kind = definition.Kind,
                                PlacementHorizontal = cell.x,
                                PlacementVertical = cell.y,
                                RotationRadians = Rotation
                            });
                            commands.SetComponent(instance, new ProductionSource
                            {
                                Kind = definition.ProductionKind,
                                CycleSeconds = definition.ProductionCycleSeconds,
                                AmountPerCycle = definition.ProductionAmountPerCycle
                            });
                            commands.SetComponent(instance, new CombatHealth { Current = definition.Health, Maximum = definition.Health });
                            commands.SetComponent(instance, new StructureCombatState
                            {
                                Range = definition.AttackRange, Damage = definition.AttackDamage, Interval = definition.AttackInterval,
                                Healing = (byte)(definition.Kind == StructureKind.HealingShrine ? 1 : 0), Level = 1
                            });
                            occupied.Add(new OccupiedStructure
                            {
                                StructureId = structureId,
                                X = cell.x,
                                Z = cell.y,
                                RotationRadians = Rotation,
                                HalfExtentMeters = definition.HalfExtentMeters
                            });
                            blockers.Add(new NavigationObstacleRequest
                            {
                                StableId = structureId,
                                Center = position,
                                RadiusMeters = definition.HalfExtentMeters, RotationRadians = Rotation
                            });
                        }
                        commands.Playback(entities);
                        commands.Dispose();
                        result.BuiltCount = cells.Count;
                        result.WoodAfter = economy.Wood;
                    }
                }
            }

            if (result.Outcome != ConstructionOutcome.RateLimited)
                state.NextAllowedSeconds = time.ElapsedSeconds + rules.MinimumCommandIntervalSeconds;
            state.LastOperationId = request.OperationId;
            state.LastOperation = request.Operation;
            state.LastTargetStructureId = request.TargetStructureId;
            state.LastOutcome = result.Outcome;
            state.LastBuiltCount = result.BuiltCount;
            state.LastWoodAfter = result.WoodAfter;
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
            List<float2> cells, float Rotation, ResourceAmounts resources)
        {
            NavigationGridConfig grid = entities.GetComponentData<NavigationGridConfig>(navigationEntity);
            DynamicBuffer<NavigationObstacleRequest> pending =
                entities.GetBuffer<NavigationObstacleRequest>(navigationEntity);
            if (navigation.DynamicObstacleCount + pending.Length + cells.Count > grid.MaximumDynamicObstacles ||
                entities.GetComponentData<ConstructionRunState>(runEntity).NextStructureId >
                int.MaxValue - cells.Count)
                return ConstructionOutcome.CapacityReached;
            float3 heroPosition = entities.GetComponentData<LocalTransform>(hero).Position;
            DynamicBuffer<OccupiedStructure> occupied = entities.GetBuffer<OccupiedStructure>(runEntity);
            foreach (float2 cell in cells)
            {
                float2 center = CellCenter(cell, rules);
                if (navigation.Map != null)
                {
                    if (!navigation.Map.CanSupport(center.x, center.y, definition.HalfExtentMeters, Rotation))
                    {
                        return ConstructionOutcome.Blocked;
                    }
                    foreach (RiverMapPlacement Rock in navigation.Map.Rocks)
                    {
                        if (math.distance(center, new float2(Rock.Horizontal, Rock.Vertical)) < definition.HalfExtentMeters * 1.42f + Rock.Radius)
                        {
                            return ConstructionOutcome.Blocked;
                        }
                    }
                }
                else if (!navigation.IsStaticWalkable(center))
                {
                    return ConstructionOutcome.Blocked;
                }
                float clearance = rules.HeroClearanceMeters + definition.HalfExtentMeters;
                if (math.distancesq(center, new float2(heroPosition.x, heroPosition.z)) <
                    clearance * clearance) return ConstructionOutcome.HeroTooClose;
                foreach (OccupiedStructure other in occupied)
                {
                    float2 otherCenter = new float2(other.X, other.Z);
                    if (ConstructionGeometry.Overlaps(center, definition.HalfExtentMeters, Rotation,
                        otherCenter, other.HalfExtentMeters, other.RotationRadians))
                        return ConstructionOutcome.Occupied;
                }
            }
            using (EntityQuery Structures = entities.CreateEntityQuery(typeof(StructureInstance), typeof(CombatHealth), typeof(LocalTransform)))
            using (NativeArray<Entity> StructuresPresent = Structures.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity StructureEntity in StructuresPresent)
                {
                    if (entities.GetComponentData<CombatHealth>(StructureEntity).Dead != 0)
                    {
                        continue;
                    }
                    StructureInstance Structure = entities.GetComponentData<StructureInstance>(StructureEntity);
                    if (Structure.Kind == StructureKind.Artillery)
                    {
                        continue;
                    }
                    float2 StructurePosition = entities.GetComponentData<LocalTransform>(StructureEntity).Position.xz;
                    foreach (float2 Cell in cells)
                    {
                        if (ConstructionGeometry.Overlaps(CellCenter(Cell, rules), definition.HalfExtentMeters, Rotation,
                            StructurePosition, Structure.HalfExtent, Structure.RotationRadians))
                        {
                            return ConstructionOutcome.Occupied;
                        }
                    }
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
                    foreach (float2 cell in cells)
                    {
                        float2 center = CellCenter(cell, rules);
                        float radius = definition.HalfExtentMeters + 0.4f;
                        if (math.distancesq(center, new float2(enemyPosition.x, enemyPosition.z)) <
                            radius * radius) return ConstructionOutcome.Occupied;
                    }
                }
            }
            ResourceAmounts cost = new ResourceAmounts(definition.WoodPrice, definition.StonePrice, definition.IronPrice) * cells.Count;
            return resources.Covers(cost) ? ConstructionOutcome.Built : ConstructionOutcome.InsufficientResources;
        }

        private static bool InBounds(float Horizontal, float Vertical, ConstructionRunRules Rules)
        {
            return math.isfinite(Horizontal) && math.isfinite(Vertical) &&
                math.abs(Horizontal) <= Rules.GridWidth * Rules.CellSizeMeters * 0.5f &&
                math.abs(Vertical) <= Rules.GridHeight * Rules.CellSizeMeters * 0.5f;
        }

        private static float2 CellCenter(float2 Position, ConstructionRunRules Rules)
        {
            return Position;
        }
    }
}
