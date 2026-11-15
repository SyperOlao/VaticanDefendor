#if !UNITY_SERVER || UNITY_EDITOR
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Rendering;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.Rendering;
using VaticanDefendor.Content;
using VaticanDefendor.RunEconomy;
using VaticanDefendor.Session;

[WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.Presentation)]
[UpdateInGroup(typeof(PresentationSystemGroup))]
[UpdateBefore(typeof(EntitiesGraphicsSystem))]
public partial class ResourcePickupPresentationSystem : SystemBase
{
    private readonly Dictionary<uint, Entity> Visuals = new Dictionary<uint, Entity>();
    private readonly HashSet<uint> Present = new HashSet<uint>();
    private readonly List<uint> Removed = new List<uint>();
    private ClientPresentationDefinition Settings;
    private RenderMeshArray RenderMeshes;
    private Mesh[] Meshes;
    private Material[] Materials;
    private EntityQuery PickupOwner;

    protected override void OnCreate()
    {
        PickupOwner = GetEntityQuery(typeof(ResourcePickup), typeof(ChurchTag));
        RequireForUpdate(PickupOwner);
    }

    public void Initialize(ClientPresentationDefinition Presentation)
    {
        Settings = Presentation;
    }

    protected override void OnUpdate()
    {
        if (Settings == null)
        {
            return;
        }
        if (Meshes == null)
        {
            Meshes = new[] { CreateBoards(), CreateStone(), CreateIngot() };
            Materials = new[]
            {
                CreateMaterial("Pickup wood", Settings.WoodPickupColor, 0),
                CreateMaterial("Pickup stone", Settings.StonePickupColor, 0),
                CreateMaterial("Pickup iron", Settings.IronPickupColor, 0.65f)
            };
            RenderMeshes = new RenderMeshArray(Materials, Meshes);
            RenderMeshes.Retain();
        }
        Dependency.Complete();
        Present.Clear();
        float Delta = UnityEngine.Time.unscaledDeltaTime;
        if (SystemAPI.TryGetSingleton(out VaticanDefendor.Client.ClientRunStatus Status) && Status.Paused != 0)
        {
            Delta = 0;
        }
        using NativeArray<ResourcePickup> Pickups = EntityManager.GetBuffer<ResourcePickup>(PickupOwner.GetSingletonEntity()).ToNativeArray(Allocator.Temp);
        foreach (ResourcePickup Pickup in Pickups)
        {
                Present.Add(Pickup.Identifier);
                float3 Position = Pickup.Position;
                if (!Visuals.TryGetValue(Pickup.Identifier, out Entity Visual))
                {
                    Visual = EntityManager.CreateEntity();
                    int ModelIndex = (int)Pickup.Kind - 1;
                    RenderMeshUtility.AddComponents(Visual, EntityManager,
                        new RenderMeshDescription(ShadowCastingMode.On, true), RenderMeshes,
                        MaterialMeshInfo.FromRenderMeshArrayIndices(ModelIndex, ModelIndex));
                    Visuals.Add(Pickup.Identifier, Visual);
                }
                else
                {
                    float3 Previous = EntityManager.GetComponentData<LocalToWorld>(Visual).Position;
                    Position = math.lerp(Previous, Position, 1 - math.exp(-Settings.PickupSmoothing * Delta));
                }
                float Angle = (Pickup.Identifier % 31) * 0.43f;
                EntityManager.SetComponentData(Visual, new LocalToWorld
                {
                    Value = float4x4.TRS(Position, quaternion.RotateY(Angle), new float3(Settings.PickupMeshScale))
                });
        }
        Removed.Clear();
        foreach (var Pair in Visuals)
        {
            if (!Present.Contains(Pair.Key))
            {
                EntityManager.DestroyEntity(Pair.Value);
                Removed.Add(Pair.Key);
            }
        }
        foreach (uint Identifier in Removed)
        {
            Visuals.Remove(Identifier);
        }
    }

    private static Material CreateMaterial(string Name, Color Tint, float Metallic)
    {
        var Result = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = Name, enableInstancing = true };
        Result.SetColor("_BaseColor", Tint);
        Result.SetFloat("_Metallic", Metallic);
        Result.SetFloat("_Smoothness", 0.15f + Metallic * 0.5f);
        return Result;
    }

    private static Mesh CreateBoards()
    {
        var Vertices = new List<Vector3>();
        var Triangles = new List<int>();
        Vector3[] Corners = BoxCorners(0.43f, 0.035f, 0.13f, 1);
        int[] Faces = BoxTriangles();
        for (int BoardIndex = 0; BoardIndex < 3; BoardIndex++)
        {
            int Start = Vertices.Count;
            Quaternion Rotation = Quaternion.Euler(0, (BoardIndex - 1) * 16, 0);
            foreach (Vector3 Corner in Corners)
            {
                Vertices.Add(Rotation * Corner + new Vector3(0, BoardIndex * 0.075f, 0));
            }
            foreach (int CornerIndex in Faces)
            {
                Triangles.Add(Start + CornerIndex);
            }
        }
        return CreateMesh("Dropped boards", Vertices.ToArray(), Triangles.ToArray());
    }

    private static Mesh CreateStone()
    {
        return CreateMesh("Dropped stone", new[]
        {
            new Vector3(0.04f, 0.27f, 0), new Vector3(-0.04f, -0.16f, 0),
            new Vector3(-0.30f, 0, -0.20f), new Vector3(0.25f, 0.02f, -0.24f),
            new Vector3(0.31f, -0.01f, 0.17f), new Vector3(-0.22f, 0.04f, 0.25f)
        }, new[] { 0, 3, 2, 0, 4, 3, 0, 5, 4, 0, 2, 5, 1, 2, 3, 1, 3, 4, 1, 4, 5, 1, 5, 2 });
    }

    private static Mesh CreateIngot()
    {
        return CreateMesh("Dropped iron ingot", BoxCorners(0.32f, 0.11f, 0.20f, 0.68f), BoxTriangles());
    }

    private static Vector3[] BoxCorners(float Width, float Height, float Depth, float TopScale)
    {
        return new[]
        {
            new Vector3(-Width, -Height, -Depth), new Vector3(Width, -Height, -Depth),
            new Vector3(Width * TopScale, Height, -Depth * TopScale), new Vector3(-Width * TopScale, Height, -Depth * TopScale),
            new Vector3(-Width, -Height, Depth), new Vector3(Width, -Height, Depth),
            new Vector3(Width * TopScale, Height, Depth * TopScale), new Vector3(-Width * TopScale, Height, Depth * TopScale)
        };
    }

    private static int[] BoxTriangles()
    {
        return new[] { 0, 2, 1, 0, 3, 2, 4, 5, 6, 4, 6, 7, 0, 4, 7, 0, 7, 3, 1, 2, 6, 1, 6, 5, 3, 7, 6, 3, 6, 2, 0, 1, 5, 0, 5, 4 };
    }

    private static Mesh CreateMesh(string Name, Vector3[] Corners, int[] Faces)
    {
        var Vertices = new Vector3[Faces.Length];
        var Triangles = new int[Faces.Length];
        for (int CornerIndex = 0; CornerIndex < Faces.Length; CornerIndex++)
        {
            Vertices[CornerIndex] = Corners[Faces[CornerIndex]];
            Triangles[CornerIndex] = CornerIndex;
        }
        var Result = new Mesh { name = Name, vertices = Vertices, triangles = Triangles };
        Result.RecalculateNormals();
        Result.RecalculateBounds();
        return Result;
    }

    protected override void OnDestroy()
    {
        if (Meshes == null)
        {
            return;
        }
        RenderMeshes.Release();
        foreach (Mesh Geometry in Meshes)
        {
            if (Application.isPlaying)
            {
                UnityEngine.Object.Destroy(Geometry);
            }
            else
            {
                UnityEngine.Object.DestroyImmediate(Geometry);
            }
        }
        foreach (Material Surface in Materials)
        {
            if (Application.isPlaying)
            {
                UnityEngine.Object.Destroy(Surface);
            }
            else
            {
                UnityEngine.Object.DestroyImmediate(Surface);
            }
        }
    }
}
#endif
