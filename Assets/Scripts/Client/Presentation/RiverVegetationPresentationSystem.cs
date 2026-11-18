#if !UNITY_SERVER || UNITY_EDITOR
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Rendering;
using Unity.Profiling;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.Rendering;
using VaticanDefendor;
using VaticanDefendor.Combat;
using VaticanDefendor.Construction;
using VaticanDefendor.Maps;

public struct RiverGrassInstance : IComponentData
{
    public float2 Position;
}

[WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.Presentation)]
[UpdateInGroup(typeof(PresentationSystemGroup))]
[UpdateBefore(typeof(EntitiesGraphicsSystem))]
public partial class RiverVegetationPresentationSystem : SystemBase
{
    private static readonly ProfilerMarker GrassOcclusionMarker = new ProfilerMarker("VaticanDefendor.Vegetation.GrassOcclusion");
    private static readonly ProfilerMarker TreeRemovalMarker = new ProfilerMarker("VaticanDefendor.Vegetation.TreeRemoval");
    private RiverWorldMaterials _settings;
    private RenderMeshArray[] _treeModels;
    private RenderMeshArray[] _grassModels;
    private EntityQuery _structures;
    private EntityQuery _treeOwners;
    private EntityQuery _grass;
    private uint _lastObstacleHash;
    private readonly List<float4> _grassObstacles = new List<float4>();

    protected override void OnCreate()
    {
        RequireForUpdate<RiverMapState>();
        _structures = GetEntityQuery(typeof(StructureInstance), typeof(LocalTransform), typeof(CombatHealth));
        _treeOwners = GetEntityQuery(typeof(RiverTreeVisual));
        _grass = GetEntityQuery(typeof(RiverGrassInstance));
    }

    protected override void OnUpdate()
    {
        if (_settings == null)
        {
            _settings = Resources.Load<RiverWorldMaterials>("RiverWorldMaterials");
            _treeModels = CreateModels(_settings.Trees);
            _grassModels = CreateModels(_settings.Grass);
            PopulateGrass(SystemAPI.ManagedAPI.GetSingleton<RiverMapState>().Configuration);
        }
        Dependency.Complete();
        _grassObstacles.Clear();
        uint ObstacleHash = 2166136261;
        using (NativeArray<Entity> Structures = _structures.ToEntityArray(Allocator.Temp))
        {
            foreach (Entity Owner in Structures)
            {
                StructureInstance Structure = EntityManager.GetComponentData<StructureInstance>(Owner);
                if (EntityManager.GetComponentData<CombatHealth>(Owner).Dead != 0)
                {
                    continue;
                }
                if (Structure.Kind != StructureKind.Tree)
                {
                    float2 Position = EntityManager.GetComponentData<LocalTransform>(Owner).Position.xz;
                    _grassObstacles.Add(new float4(Position, Structure.HalfExtent + 0.25f, Structure.RotationRadians));
                    ObstacleHash = math.hash(new uint2(ObstacleHash, (uint)Structure.StructureId));
                    continue;
                }
                if (EntityManager.HasBuffer<RiverTreeVisual>(Owner))
                {
                    continue;
                }
                EntityManager.AddBuffer<RiverTreeVisual>(Owner);
                EntityManager.AddComponent<WorldVisualAttached>(Owner);
                AddModel(_treeModels[Structure.StructureId % _treeModels.Length], float4x4.identity, Owner, false);
            }
        }
        using (TreeRemovalMarker.Auto())
        using (NativeArray<Entity> Owners = _treeOwners.ToEntityArray(Allocator.Temp))
        {
            foreach (Entity Owner in Owners)
            {
                if (EntityManager.HasComponent<LocalTransform>(Owner) && EntityManager.GetComponentData<CombatHealth>(Owner).Dead == 0)
                {
                    continue;
                }
                using NativeArray<RiverTreeVisual> Children = EntityManager.GetBuffer<RiverTreeVisual>(Owner).ToNativeArray(Allocator.Temp);
                foreach (RiverTreeVisual Child in Children)
                {
                    EntityManager.DestroyEntity(Child.Value);
                }
                EntityManager.RemoveComponent<RiverTreeVisual>(Owner);
            }
        }
        foreach (var (Part, Matrix) in SystemAPI.Query<RefRO<RiverVisualPart>, RefRW<LocalToWorld>>())
        {
            LocalTransform Transform = EntityManager.GetComponentData<LocalTransform>(Part.ValueRO.Owner);
            Matrix.ValueRW.Value = math.mul(float4x4.TRS(Transform.Position, Transform.Rotation, new float3(Transform.Scale)), Part.ValueRO.Offset);
        }
        if (_lastObstacleHash != ObstacleHash)
        {
            UpdateGrassOcclusion();
            _lastObstacleHash = ObstacleHash;
        }
    }

    private static RenderMeshArray[] CreateModels(RiverVegetationModel[] Models)
    {
        var Result = new RenderMeshArray[Models.Length];
        for (int Index = 0; Index < Models.Length; Index++)
        {
            Result[Index] = new RenderMeshArray(Models[Index].Materials, new[] { Models[Index].Mesh });
            Result[Index].Retain();
        }
        return Result;
    }

    private void AddModel(RenderMeshArray Model, float4x4 Matrix, Entity Owner, bool bGrass)
    {
        int MaterialCount = Model.MaterialReferences.Length;
        for (int Submesh = 0; Submesh < MaterialCount; Submesh++)
        {
            Entity Visual = EntityManager.CreateEntity();
            ShadowCastingMode Shadows = ShadowCastingMode.On;
            if (bGrass)
            {
                Shadows = ShadowCastingMode.Off;
            }
            RenderMeshUtility.AddComponents(Visual, EntityManager, new RenderMeshDescription(Shadows, true),
                Model, MaterialMeshInfo.FromRenderMeshArrayIndices(Submesh, 0, (ushort)Submesh));
            EntityManager.SetComponentData(Visual, new LocalToWorld { Value = Matrix });
            if (bGrass)
            {
                EntityManager.AddComponentData(Visual, new RiverGrassInstance { Position = Matrix.c3.xz });
            }
            else
            {
                EntityManager.AddComponentData(Visual, new RiverVisualPart { Owner = Owner, Offset = Matrix });
                EntityManager.GetBuffer<RiverTreeVisual>(Owner).Add(new RiverTreeVisual { Value = Visual });
            }
        }
    }

    private void PopulateGrass(RiverMapConfiguration Map)
    {
        var Random = new Unity.Mathematics.Random((uint)Map.Seed + 89239);
        int Planted = 0;
        for (int Attempt = 0; Attempt < _settings.GrassInstances * 20 && Planted < _settings.GrassInstances; Attempt++)
        {
            float2 Position = Random.NextFloat2(-Map.WidthMeters * 0.5f, Map.WidthMeters * 0.5f);
            float Density = math.sin(Position.x * 0.19f + math.sin(Position.y * 0.21f) * 1.6f) + math.cos(Position.y * 0.16f - Position.x * 0.08f);
            if (Density < Random.NextFloat(-0.5f, 1.3f) || !CanPlant(Map, Position))
            {
                continue;
            }
            float3 Ground = RiverMapAccess.Position(Map, Position) - new float3(0, 0.025f, 0);
            AddModel(_grassModels[Random.NextInt(_grassModels.Length)],
                float4x4.TRS(Ground, quaternion.RotateY(Random.NextFloat(0, math.PI * 2)), new float3(Random.NextFloat(0.7f, 1.3f))), Entity.Null, true);
            Planted++;
        }
    }

    private static bool CanPlant(RiverMapConfiguration Map, float2 Position)
    {
        if (!Map.Contains(Position.x, Position.y, 0.5f) || Map.Height(Position.x, Position.y) < Map.WaterLevel + 0.4f ||
            Map.Slope(Position.x, Position.y) > 22 || math.distance(Position, new float2(Map.ChurchHorizontal, Map.ChurchVertical)) < 3.8f)
        {
            return false;
        }
        foreach (RiverMapPlacement Rock in Map.Rocks)
        {
            if (math.distance(Position, new float2(Rock.Horizontal, Rock.Vertical)) < Rock.Radius + 0.4f)
            {
                return false;
            }
        }
        foreach (RiverMapPoint Entry in Map.Entries)
        {
            if (math.distance(Position, new float2(Entry.Horizontal, Entry.Vertical)) < 2.5f)
            {
                return false;
            }
        }
        return true;
    }

    private void UpdateGrassOcclusion()
    {
        using var Scope = GrassOcclusionMarker.Auto();
        using NativeArray<Entity> Instances = _grass.ToEntityArray(Allocator.Temp);
        foreach (Entity Instance in Instances)
        {
            float2 Position = EntityManager.GetComponentData<RiverGrassInstance>(Instance).Position;
            bool bCovered = false;
            foreach (float4 Obstacle in _grassObstacles)
            {
                if (ConstructionGeometry.Overlaps(Position, 0.2f, 0, Obstacle.xy, Obstacle.z, Obstacle.w))
                {
                    bCovered = true;
                    break;
                }
            }
            bool bHidden = EntityManager.HasComponent<DisableRendering>(Instance);
            if (bCovered && !bHidden)
            {
                EntityManager.AddComponent<DisableRendering>(Instance);
            }
            else if (!bCovered && bHidden)
            {
                EntityManager.RemoveComponent<DisableRendering>(Instance);
            }
        }
    }

    protected override void OnDestroy()
    {
        if (_treeModels == null)
        {
            return;
        }
        foreach (RenderMeshArray Model in _treeModels)
        {
            Model.Release();
        }
        foreach (RenderMeshArray Model in _grassModels)
        {
            Model.Release();
        }
    }
}
#endif
