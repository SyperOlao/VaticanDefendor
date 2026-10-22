using System;
using UnityEngine;

[Serializable]
public sealed class CombatRenderPartDefinition
{
    public int Mesh;
    public int[] Materials;
    public Matrix4x4 Matrix;
    public Color Color;
    public int WeaponIndex = -1;
    public int Appearance;
    public int Animation;
    public int PartIndex;
    public Vector3 Pivot;
    public bool bCastShadows;
}

[Serializable]
public sealed class CombatRenderModelDefinition
{
    public string Name;
    public CombatRenderPartDefinition[] Parts;
}

[Serializable]
public sealed class CombatStaticDefinition
{
    public int ModelIndex;
    public Matrix4x4 Matrix;
    public bool bScenery;
    public int Identity;
}

public sealed class CombatRenderCatalog : ScriptableObject
{
    public Mesh[] Meshes;
    public Material[] Materials;
    public CombatRenderModelDefinition[] Models;
    public CombatStaticDefinition[] StaticObjects;
}
