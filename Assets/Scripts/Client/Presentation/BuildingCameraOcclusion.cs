#if !UNITY_SERVER || UNITY_EDITOR
using Unity.Collections;
using Unity.Entities;
using Unity.Entities.Graphics;
using Unity.Mathematics;
using Unity.Rendering;
using Unity.Transforms;
using UnityEngine;
using VaticanDefendor.Combat;

internal struct BuildingOcclusion : IComponentData
{
    public float3 Extents;
    public float CenterHeight;
    public float Rotation;
    public float Opacity;
    public int FadedPartStart;
}

public partial class EcsWorldPresentationSystem
{
    private EntityQuery _buildingOccluders;
    private int _obstructingBuildingMaterialIndex;
    private int _buildingMapLayer;
    private int _buildingBattleLayer;
    private bool _hasBuildingOcclusionTarget;
    private float3 _occlusionCamera;
    private float3 _occlusionHero;
    private float3 _occlusionSide;

    public void ConfigureOcclusionCameras(Camera Battle, Camera Map)
    {
        _buildingMapLayer = LayerMask.NameToLayer("BuildingMapOnly");
        _buildingBattleLayer = LayerMask.NameToLayer("BuildingBattleOnly");
        if (_buildingMapLayer < 0 || _buildingBattleLayer < 0)
        {
            throw new System.InvalidOperationException("The occlusion camera layers are required.");
        }
        Battle.cullingMask = (Battle.cullingMask | (1 << _buildingBattleLayer)) & ~(1 << _buildingMapLayer);
        Map.cullingMask = (Battle.cullingMask | (1 << _buildingMapLayer)) & ~(1 << _buildingBattleLayer);
    }

    public void SetOcclusionTarget(Camera Battle, Vector3 Hero)
    {
        Transform CameraTransform = Battle.transform;
        _hasBuildingOcclusionTarget = true;
        _occlusionCamera = CameraTransform.position + CameraTransform.forward * Battle.nearClipPlane;
        _occlusionHero = Hero;
        _occlusionSide = CameraTransform.right * 0.23f;
    }

    public void ClearOcclusionTarget()
    {
        _hasBuildingOcclusionTarget = false;
    }

    private void UpdateBuildingOcclusion()
    {
        if (_buildingOccluders.IsEmptyIgnoreFilter)
        {
            return;
        }
        using NativeArray<Entity> Buildings = _buildingOccluders.ToEntityArray(Allocator.Temp);
        foreach (Entity Building in Buildings)
        {
            BuildingOcclusion State = EntityManager.GetComponentData<BuildingOcclusion>(Building);
            float3 Center = EntityManager.GetComponentData<LocalTransform>(Building).Position + new float3(0, State.CenterHeight, 0);
            bool bObstructsHero = EntityManager.GetComponentData<CombatHealth>(Building).Dead == 0 &&
                ObstructsHero(Center, quaternion.RotateY(State.Rotation), State.Extents);
            float Opacity = StepOcclusionOpacity(State.Opacity, bObstructsHero);
            if (Opacity == State.Opacity)
            {
                continue;
            }
            if (Opacity == 1)
            {
                RestoreBuilding(Building, State.FadedPartStart);
                State.FadedPartStart = 0;
            }
            else
            {
                if (State.FadedPartStart == 0)
                {
                    State.FadedPartStart = AddFadedBuilding(Building);
                }
                DynamicBuffer<WorldVisualChild> Children = EntityManager.GetBuffer<WorldVisualChild>(Building);
                for (int Index = State.FadedPartStart; Index < Children.Length; Index++)
                {
                    Entity Fade = Children[Index].Value;
                    WorldVisualPart Part = EntityManager.GetComponentData<WorldVisualPart>(Fade);
                    Part.Color.w = Opacity;
                    EntityManager.SetComponentData(Fade, Part);
                }
            }
            State.Opacity = Opacity;
            EntityManager.SetComponentData(Building, State);
        }
    }

    internal float StepOcclusionOpacity(float Opacity, bool bObstructsHero)
    {
        if (!bObstructsHero && Opacity == 1)
        {
            return 1;
        }
        float MinimumOpacity = _presentation.ObstructingBuildingOpacity;
        float TargetOpacity = 1;
        float Duration = _presentation.BuildingFadeOutSeconds;
        if (bObstructsHero)
        {
            TargetOpacity = MinimumOpacity;
            Duration = _presentation.BuildingFadeInSeconds;
        }
        return Mathf.MoveTowards(Opacity, TargetOpacity, UnityEngine.Time.unscaledDeltaTime * (1 - MinimumOpacity) / Duration);
    }

    internal bool ObstructsHero(float3 Position, quaternion Rotation, float3 Extents)
    {
        if (!_hasBuildingOcclusionTarget)
        {
            return false;
        }
        quaternion Inverse = math.conjugate(Rotation);
        float3 Camera = math.rotate(Inverse, _occlusionCamera - Position);
        float3 Torso = math.rotate(Inverse, _occlusionHero - Position + new float3(0, 0.95f, 0));
        float3 Side = math.rotate(Inverse, _occlusionSide);
        return Intersects(Camera, Torso, Extents) ||
            Intersects(Camera, Torso + Side, Extents) ||
            Intersects(Camera, Torso - Side, Extents) ||
            Intersects(Camera, Torso - new float3(0, 0.45f, 0), Extents) ||
            Intersects(Camera, Torso + new float3(0, 0.6f, 0), Extents);
    }

    private static bool Intersects(float3 Start, float3 End, float3 Extents)
    {
        float3 HalfTravel = (End - Start) * 0.5f;
        float3 Midpoint = Start + HalfTravel;
        float3 Lengths = math.abs(HalfTravel);
        return math.all(math.abs(Midpoint) <= Extents + Lengths) &&
            math.all(math.abs(math.cross(HalfTravel, Midpoint)) <= Lengths.yzx * Extents.zxy + Lengths.zxy * Extents.yzx);
    }

    private int AddFadedBuilding(Entity Building)
    {
        using var Originals = EntityManager.GetBuffer<WorldVisualChild>(Building).ToNativeArray(Allocator.Temp);
        foreach (WorldVisualChild Original in Originals)
        {
            Entity Fade = CreateTranslucentPart(Original.Value, _obstructingBuildingMaterialIndex);
            EntityManager.GetBuffer<WorldVisualChild>(Building).Add(new WorldVisualChild { Value = Fade });
        }
        return Originals.Length;
    }

    private void RestoreBuilding(Entity Building, int FadedPartStart)
    {
        using var Children = EntityManager.GetBuffer<WorldVisualChild>(Building).ToNativeArray(Allocator.Temp);
        EntityManager.GetBuffer<WorldVisualChild>(Building).ResizeUninitialized(FadedPartStart);
        for (int Index = 0; Index < FadedPartStart; Index++)
        {
            RestoreOpaquePart(Children[Index].Value);
            EntityManager.DestroyEntity(Children[FadedPartStart + Index].Value);
        }
    }

    internal Entity CreateTranslucentPart(Entity Original, int MaterialIndex)
    {
        RenderFilterSettings Filter = EntityManager.GetSharedComponent<RenderFilterSettings>(Original);
        Filter.Layer = _buildingMapLayer;
        EntityManager.SetSharedComponent(Original, Filter);

        Entity Fade = EntityManager.Instantiate(Original);
        Filter.Layer = _buildingBattleLayer;
        Filter.ShadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        EntityManager.SetSharedComponent(Fade, Filter);
        MaterialMeshInfo Mesh = EntityManager.GetComponentData<MaterialMeshInfo>(Fade);
        Mesh.Material = MaterialIndex;
        EntityManager.SetComponentData(Fade, Mesh);
        EntityManager.AddComponent<DepthSorted_Tag>(Fade);
        return Fade;
    }

    internal void RestoreOpaquePart(Entity Original)
    {
        RenderFilterSettings Filter = EntityManager.GetSharedComponent<RenderFilterSettings>(Original);
        Filter.Layer = 0;
        EntityManager.SetSharedComponent(Original, Filter);
    }
}
#endif
