using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Rendering;
using Unity.Transforms;
using UnityEngine.Rendering;

public sealed class CombatEntityContent
{
    public NativeArray<Entity> Prefabs;

    public void Create(EntityManager Manager, CombatRenderCatalog Catalog)
    {
        RenderMeshArray RenderMeshes = new RenderMeshArray(Catalog.Materials, Catalog.Meshes);
        Prefabs = new NativeArray<Entity>(Catalog.Models.Length, Allocator.Persistent);
        for (int ModelIndex = 0; ModelIndex < Catalog.Models.Length; ModelIndex++)
        {
            CombatRenderModelDefinition Model = Catalog.Models[ModelIndex];
            Entity Root = Manager.CreateEntity(typeof(Prefab), typeof(CombatModelState));
            Manager.SetComponentData(Root, new CombatModelState { ModelIndex = ModelIndex, Scale = new float3(1), bVisible = 1 });
            DynamicBuffer<LinkedEntityGroup> Group = Manager.AddBuffer<LinkedEntityGroup>(Root);
            Group.Add(new LinkedEntityGroup { Value = Root });
            Prefabs[ModelIndex] = Root;
            foreach (CombatRenderPartDefinition Definition in Model.Parts)
            {
                ShadowCastingMode ShadowMode = ShadowCastingMode.Off;
                if (Definition.bCastShadows)
                {
                    ShadowMode = ShadowCastingMode.On;
                }
                RenderMeshDescription Description = new RenderMeshDescription(shadowCastingMode: ShadowMode, receiveShadows: true);
                Entity Part = Manager.CreateEntity();
                RenderMeshUtility.AddComponents(Part, Manager, Description, RenderMeshes,
                    MaterialMeshInfo.FromRenderMeshArrayIndices(Definition.Materials[0], Definition.Mesh));
                Manager.AddComponent<Prefab>(Part);
                Manager.AddComponent<CombatMatchEntity>(Part);
                if (ModelIndex > CombatModelIndex.WarningCircle)
                {
                    Manager.AddComponent<Static>(Part);
                }
                UnityEngine.Color RenderColor = Definition.Color;
                if (UnityEngine.QualitySettings.activeColorSpace == UnityEngine.ColorSpace.Linear)
                {
                    RenderColor = RenderColor.linear;
                }
                Manager.AddComponentData(Part, new URPMaterialPropertyBaseColor { Value = new float4(RenderColor.r, RenderColor.g, RenderColor.b, RenderColor.a) });
                Manager.AddComponentData(Part, new CombatEffectMaterialParameters { Value = new float4(1, 0, 1, 0) });
                Manager.AddComponentData(Part, new CombatRenderPart
                {
                    Owner = Root,
                    LocalMatrix = Definition.Matrix,
                    Color = new float4(Definition.Color.r, Definition.Color.g, Definition.Color.b, Definition.Color.a),
                    Materials = new int4(Definition.Materials[0], Definition.Materials[1], Definition.Materials[2], Definition.Materials[3]),
                    Mesh = Definition.Mesh,
                    WeaponIndex = Definition.WeaponIndex,
                    Appearance = Definition.Appearance,
                    Animation = Definition.Animation,
                    PartIndex = Definition.PartIndex,
                    Pivot = Definition.Pivot
                });
                Group = Manager.GetBuffer<LinkedEntityGroup>(Root);
                Group.Add(new LinkedEntityGroup { Value = Part });
            }
        }
    }

    public void Dispose()
    {
        if (Prefabs.IsCreated)
        {
            Prefabs.Dispose();
        }
    }
}
