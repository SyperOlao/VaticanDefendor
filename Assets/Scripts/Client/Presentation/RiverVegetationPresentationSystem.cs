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
using VaticanDefendor.Production;

public struct RiverGrassInstance : IComponentData
{
    public float2 Position;
}

internal struct RiverTreeAppearance : IComponentData
{
    public float Opacity;
    public bool bDepleted;
}

[WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.Presentation)]
[UpdateInGroup(typeof(PresentationSystemGroup))]
[UpdateBefore(typeof(EntitiesGraphicsSystem))]
public partial class RiverVegetationPresentationSystem : SystemBase
{
    private static readonly ProfilerMarker GrassOcclusionMarker = new ProfilerMarker("VaticanDefendor.Vegetation.GrassOcclusion");
    private static readonly ProfilerMarker TreeRemovalMarker = new ProfilerMarker("VaticanDefendor.Vegetation.TreeRemoval");
    private RiverWorldMaterials _settings;
    private EcsWorldPresentationSystem _occlusion;
    private RenderMeshArray[] _treeModels;
    private (Bounds Bounds, float4 Tint, float Cutoff)[][] _treeParts;
    private int _infiniteWoodModel;
    private int _infiniteStoneModel;
    private RenderMeshArray[] _grassModels;
    private EntityQuery _structures;
    private EntityQuery _treeOwners;
    private EntityQuery _grass;
    private uint _lastObstacleHash;
    private readonly List<(float2 Position, float2 Extents, float Rotation)> _grassObstacles = new();
    private readonly List<float2> _depositClearings = new();

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
            _occlusion = World.GetExistingSystemManaged<EcsWorldPresentationSystem>();
            var Materials = new Dictionary<Material, Material>();
            foreach (RiverTreeOcclusionMaterial Pair in _settings.TreeOcclusionMaterials)
            {
                Materials.Add(Pair.Opaque, Pair.Translucent);
            }
            var Models = new RiverVegetationModel[_settings.Trees.Length + 2];
            _settings.Trees.CopyTo(Models, 0);
            _infiniteWoodModel = _settings.Trees.Length;
            _infiniteStoneModel = _infiniteWoodModel + 1;
            Models[_infiniteWoodModel] = _settings.InfiniteWood;
            Models[_infiniteStoneModel] = _settings.InfiniteStone;
            _treeModels = CreateModels(Models, Materials);
            _treeParts = new (Bounds, float4, float)[Models.Length][];
            for (int Index = 0; Index < _treeParts.Length; Index++)
            {
                RiverVegetationModel Model = Models[Index];
                _treeParts[Index] = new (Bounds, float4, float)[Model.Materials.Length];
                for (int Submesh = 0; Submesh < Model.Materials.Length; Submesh++)
                {
                    Material Material = Model.Materials[Submesh];
                    Color Color = Material.color;
                    if (QualitySettings.activeColorSpace == ColorSpace.Linear)
                    {
                        Color = Color.linear;
                    }
                    _treeParts[Index][Submesh] = (Model.Mesh.GetSubMesh(Submesh).bounds,
                        new float4(Color.r, Color.g, Color.b, Color.a), Material.GetFloat("_Cutoff"));
                }
            }
            _grassModels = CreateModels(_settings.Grass);
            PopulateGrass(SystemAPI.ManagedAPI.GetSingleton<RiverMapState>().Configuration);
        }
        Dependency.Complete();
        _grassObstacles.Clear();
        _depositClearings.Clear();
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
                LocalTransform Transform = EntityManager.GetComponentData<LocalTransform>(Owner);
                ResourceNode Node = default;
                if (Structure.Kind == StructureKind.ResourceDeposit)
                {
                    Node = EntityManager.GetComponentData<ResourceNode>(Owner);
                }
                bool bWoodDeposit = Node.Kind == ResourceKind.Wood;
                bool bInfiniteStone = Node.Kind == ResourceKind.Stone && Node.Infinite != 0;
                bool bDepositVisual = bWoodDeposit || bInfiniteStone;
                if (Structure.Kind != StructureKind.Tree)
                {
                    if (Node.Infinite != 0)
                    {
                        _depositClearings.Add(Transform.Position.xz);
                    }
                    else
                    {
                        float2 Extents = ConstructionGeometry.HalfExtents(Structure.Kind, Structure.HalfExtent) + 0.25f;
                        _grassObstacles.Add((Transform.Position.xz, Extents, Structure.RotationRadians));
                    }
                    ObstacleHash = math.hash(new uint4(ObstacleHash, (uint)Structure.StructureId, math.hash(Transform.Position.xz), Node.Infinite));
                    if (!bDepositVisual)
                    {
                        continue;
                    }
                }
                int ModelIndex = Structure.StructureId % _settings.Trees.Length;
                if (bWoodDeposit)
                {
                    ModelIndex = 0;
                    if (Node.Infinite != 0)
                    {
                        ModelIndex = _infiniteWoodModel;
                    }
                }
                else if (bInfiniteStone)
                {
                    ModelIndex = _infiniteStoneModel;
                }
                if (!EntityManager.HasBuffer<RiverTreeVisual>(Owner))
                {
                    EntityManager.AddBuffer<RiverTreeVisual>(Owner);
                    EntityManager.AddComponentData(Owner, new RiverTreeAppearance { Opacity = 1 });
                    if (!bDepositVisual)
                    {
                        EntityManager.AddComponent<WorldVisualAttached>(Owner);
                    }
                    AddModel(_treeModels[ModelIndex], float4x4.identity, Owner, ModelIndex, bDepositVisual);
                }
                UpdateTreeAppearance(Owner, Transform, ModelIndex, bWoodDeposit && Node.Infinite == 0 && Node.Remaining == 0);
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
                    if (Child.Translucent != Entity.Null)
                    {
                        EntityManager.DestroyEntity(Child.Translucent);
                    }
                }
                EntityManager.RemoveComponent<RiverTreeAppearance>(Owner);
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

    private void UpdateTreeAppearance(Entity Owner, LocalTransform Transform, int ModelIndex, bool bDepleted)
    {
        var Parts = _treeParts[ModelIndex];
        bool bObstructsHero = false;
        foreach (var Part in Parts)
        {
            if (_occlusion.ObstructsHero(Transform.TransformPoint(Part.Bounds.center), Transform.Rotation,
                (float3)Part.Bounds.extents * Transform.Scale))
            {
                bObstructsHero = true;
                break;
            }
        }
        RiverTreeAppearance Appearance = EntityManager.GetComponentData<RiverTreeAppearance>(Owner);
        float Opacity = _occlusion.StepOcclusionOpacity(Appearance.Opacity, bObstructsHero);
        bool bDepletionChanged = Appearance.bDepleted != bDepleted;
        if (Opacity == Appearance.Opacity && !bDepletionChanged)
        {
            return;
        }
        float DepletionTint = 1;
        if (bDepleted)
        {
            DepletionTint = 0.35f;
            if (QualitySettings.activeColorSpace == ColorSpace.Linear)
            {
                DepletionTint = Mathf.GammaToLinearSpace(DepletionTint);
            }
        }
        for (int Index = 0; Index < Parts.Length; Index++)
        {
            RiverTreeVisual Part = EntityManager.GetBuffer<RiverTreeVisual>(Owner)[Index];
            float4 Tint = Parts[Index].Tint;
            Tint.xyz *= DepletionTint;
            if (bDepletionChanged)
            {
                EntityManager.SetComponentData(Part.Value, new URPMaterialPropertyBaseColor { Value = Tint });
            }
            if (Opacity < 1)
            {
                Tint.w *= Opacity;
                var Color = new URPMaterialPropertyBaseColor { Value = Tint };
                var Cutoff = new URPMaterialPropertyCutoff { Value = Parts[Index].Cutoff * Opacity };
                if (Part.Translucent == Entity.Null)
                {
                    int MaterialIndex = MaterialMeshInfo.ArrayIndexToStaticIndex(Parts.Length + Index);
                    Part.Translucent = _occlusion.CreateTranslucentPart(Part.Value, MaterialIndex);
                    EntityManager.AddComponentData(Part.Translucent, Color);
                    EntityManager.AddComponentData(Part.Translucent, Cutoff);
                    DynamicBuffer<RiverTreeVisual> Visuals = EntityManager.GetBuffer<RiverTreeVisual>(Owner);
                    Visuals[Index] = Part;
                }
                else
                {
                    EntityManager.SetComponentData(Part.Translucent, Color);
                    EntityManager.SetComponentData(Part.Translucent, Cutoff);
                }
            }
            else if (Part.Translucent != Entity.Null)
            {
                _occlusion.RestoreOpaquePart(Part.Value);
                EntityManager.DestroyEntity(Part.Translucent);
                Part.Translucent = Entity.Null;
                DynamicBuffer<RiverTreeVisual> Visuals = EntityManager.GetBuffer<RiverTreeVisual>(Owner);
                Visuals[Index] = Part;
            }
        }
        EntityManager.SetComponentData(Owner, new RiverTreeAppearance { Opacity = Opacity, bDepleted = bDepleted });
    }

    private static RenderMeshArray[] CreateModels(RiverVegetationModel[] Models, Dictionary<Material, Material> Translucent = null)
    {
        var Result = new RenderMeshArray[Models.Length];
        for (int Index = 0; Index < Models.Length; Index++)
        {
            Material[] Materials = Models[Index].Materials;
            if (Translucent != null)
            {
                var Combined = new Material[Materials.Length * 2];
                Materials.CopyTo(Combined, 0);
                for (int Submesh = 0; Submesh < Materials.Length; Submesh++)
                {
                    Combined[Materials.Length + Submesh] = Translucent[Materials[Submesh]];
                }
                Materials = Combined;
            }
            Result[Index] = new RenderMeshArray(Materials, new[] { Models[Index].Mesh });
            Result[Index].Retain();
        }
        return Result;
    }

    private void AddModel(RenderMeshArray Model, float4x4 Matrix, Entity Owner, int ModelIndex = -1, bool bDepositVisual = false)
    {
        bool bGrass = Owner == Entity.Null;
        int MaterialCount = Model.MaterialReferences.Length;
        if (!bGrass)
        {
            MaterialCount = _treeParts[ModelIndex].Length;
        }
        for (int Submesh = 0; Submesh < MaterialCount; Submesh++)
        {
            Entity Visual = EntityManager.CreateEntity();
            ShadowCastingMode Shadows = ShadowCastingMode.On;
            if (bGrass || bDepositVisual && ModelIndex == 0)
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
                if (bDepositVisual)
                {
                    EntityManager.AddComponentData(Visual, new URPMaterialPropertyBaseColor { Value = _treeParts[ModelIndex][Submesh].Tint });
                }
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
            RenderMeshArray Model = _grassModels[Random.NextInt(_grassModels.Length)];
            AddModel(Model,
                float4x4.TRS(Ground, quaternion.RotateY(Random.NextFloat(0, math.PI * 2)), new float3(Random.NextFloat(0.7f, 1.3f))), Entity.Null);
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
            foreach (float2 Deposit in _depositClearings)
            {
                if (math.distancesq(Position, Deposit) < math.square(RiverMapConfiguration.InfiniteDepositClearingRadiusMeters))
                {
                    bCovered = true;
                    break;
                }
            }
            if (!bCovered)
            {
                foreach (var Obstacle in _grassObstacles)
                {
                    if (ConstructionGeometry.Overlaps(Position, new float2(0.2f), 0, Obstacle.Position, Obstacle.Extents, Obstacle.Rotation))
                    {
                        bCovered = true;
                        break;
                    }
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
