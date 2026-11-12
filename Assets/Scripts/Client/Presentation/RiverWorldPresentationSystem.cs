#if !UNITY_SERVER || UNITY_EDITOR
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Rendering;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.Rendering;
using VaticanDefendor;
using VaticanDefendor.Combat;
using VaticanDefendor.Construction;
using VaticanDefendor.Maps;

public struct RiverTreeVisual : ICleanupBufferElementData
{
    public Entity Value;
}

public struct RiverVisualPart : IComponentData
{
    public Entity Owner;
    public float4x4 Offset;
}

[WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.Presentation)]
[UpdateInGroup(typeof(PresentationSystemGroup))]
public partial class RiverWorldPresentationSystem : SystemBase
{
    private readonly List<Mesh> _meshes = new List<Mesh>();
    private RenderMeshArray _renderMeshes;
    private bool _initialized;
    protected override void OnCreate()
    {
        RequireForUpdate<RiverMapState>();
    }

    protected override void OnUpdate()
    {
        if (!_initialized)
        {
            Initialize(SystemAPI.ManagedAPI.GetSingleton<RiverMapState>().Configuration);
            _initialized = true;
        }
    }

    private void Initialize(RiverMapConfiguration Map)
    {
        RiverWorldMaterials Settings = Resources.Load<RiverWorldMaterials>("RiverWorldMaterials");
        if (Settings == null || Settings.Materials == null || Settings.Materials.Length != 6)
        {
            throw new System.InvalidOperationException("River world materials are missing.");
        }
        int SampleCount = Map.SampleCount;
        var Vertices = new Vector3[SampleCount * SampleCount];
        for (int Row = 0; Row < SampleCount; Row++)
        {
            for (int Column = 0; Column < SampleCount; Column++)
            {
                int Index = Row * SampleCount + Column;
                Vertices[Index] = new Vector3((Column / (float)(SampleCount - 1) - 0.5f) * Map.WidthMeters,
                    Map.Heights[Index], (Row / (float)(SampleCount - 1) - 0.5f) * Map.WidthMeters);
            }
        }
        var Ground = new List<int>[] { new List<int>(), new List<int>(), new List<int>() };
        var Liquid = new List<int>();
        var Shallows = new List<int>();
        for (int Row = 0; Row < SampleCount - 1; Row++)
        {
            for (int Column = 0; Column < SampleCount - 1; Column++)
            {
                int Start = Row * SampleCount + Column;
                foreach (int[] Triangle in new[] { new[] { Start, Start + SampleCount, Start + 1 }, new[] { Start + 1, Start + SampleCount, Start + SampleCount + 1 } })
                {
                    Vector3 Center = (Vertices[Triangle[0]] + Vertices[Triangle[1]] + Vertices[Triangle[2]]) / 3;
                    if (!Map.Contains(Center.x, Center.z))
                    {
                        continue;
                    }
                    int Material = 0;
                    if (Center.y < Map.WaterLevel + 0.5f)
                    {
                        Material = 1;
                    }
                    else if (Center.y > 2.5f)
                    {
                        Material = 2;
                    }
                    Ground[Material].AddRange(Triangle);
                    if (Center.y < Map.WaterLevel - Map.WadingDepth)
                    {
                        Liquid.AddRange(Triangle);
                    }
                    else if (Center.y < Map.WaterLevel + 0.03f)
                    {
                        Shallows.AddRange(Triangle);
                    }
                }
            }
        }
        for (int Index = 0; Index < Ground.Length; Index++)
        {
            _meshes.Add(CreateMesh(Vertices, Ground[Index]));
        }
        var WaterVertices = (Vector3[])Vertices.Clone();
        for (int Index = 0; Index < WaterVertices.Length; Index++)
        {
            WaterVertices[Index].y = Map.WaterLevel + 0.015f;
        }
        _meshes.Add(CreateMesh(WaterVertices, Liquid));
        _meshes.Add(CreateBox());
        _meshes.Add(CreateMesh(WaterVertices, Shallows));
        _renderMeshes = new RenderMeshArray(Settings.Materials, _meshes.ToArray());
        _renderMeshes.Retain();
        for (int Index = 0; Index < 4; Index++)
        {
            AddMesh(Index, Index, float4x4.identity, Entity.Null);
        }
        AddMesh(5, 5, float4x4.identity, Entity.Null);
        foreach (RiverMapPlacement Rock in Map.Rocks)
        {
            float3 Position = RiverMapAccess.Position(Map, new float2(Rock.Horizontal, Rock.Vertical));
            AddMesh(4, 4, float4x4.TRS(Position + new float3(0, Rock.Radius * 0.5f, 0),
                quaternion.EulerXYZ(0.13f, Rock.Rotation, 0.17f), new float3(Rock.Radius * 1.6f, Rock.Radius, Rock.Radius * 1.4f)), Entity.Null);
        }
    }

    private void AddMesh(int MeshIndex, int MaterialIndex, float4x4 Matrix, Entity Owner)
    {
        Entity Visual = EntityManager.CreateEntity();
        RenderMeshUtility.AddComponents(Visual, EntityManager, new RenderMeshDescription(ShadowCastingMode.On, true),
            _renderMeshes, MaterialMeshInfo.FromRenderMeshArrayIndices(MaterialIndex, MeshIndex));
        EntityManager.SetComponentData(Visual, new LocalToWorld { Value = Matrix });
        if (Owner != Entity.Null)
        {
            EntityManager.AddComponentData(Visual, new RiverVisualPart { Owner = Owner, Offset = Matrix });
            EntityManager.GetBuffer<RiverTreeVisual>(Owner).Add(new RiverTreeVisual { Value = Visual });
        }
    }

    private Mesh CreateMesh(Vector3[] Vertices, List<int> Triangles)
    {
        var Result = new Mesh { name = "River world geometry", indexFormat = IndexFormat.UInt32 };
        Result.vertices = Vertices;
        Result.SetTriangles(Triangles, 0);
        Result.RecalculateNormals();
        Result.RecalculateBounds();
        return Result;
    }

    private Mesh CreateBox()
    {
        var Vertices = new[]
        {
            new Vector3(-0.5f,-0.5f,-0.5f), new Vector3(0.5f,-0.5f,-0.5f), new Vector3(0.5f,0.5f,-0.5f), new Vector3(-0.5f,0.5f,-0.5f),
            new Vector3(-0.5f,-0.5f,0.5f), new Vector3(0.5f,-0.5f,0.5f), new Vector3(0.5f,0.5f,0.5f), new Vector3(-0.5f,0.5f,0.5f)
        };
        return CreateMesh(Vertices, new List<int> {0,2,1,0,3,2,4,5,6,4,6,7,0,4,7,0,7,3,1,2,6,1,6,5,3,7,6,3,6,2,0,1,5,0,5,4});
    }

    protected override void OnDestroy()
    {
        _renderMeshes.Release();
        foreach (Mesh Mesh in _meshes)
        {
#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                Object.DestroyImmediate(Mesh);
                continue;
            }
#endif
            Object.Destroy(Mesh);
        }
    }
}
#endif
