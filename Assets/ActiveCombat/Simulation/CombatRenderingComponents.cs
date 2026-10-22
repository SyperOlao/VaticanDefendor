using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Rendering;

public static class CombatModelIndex
{
    public const int Player = 0;
    public const int Enemy = 4;
    public const int Building = 20;
    public const int HealthPickup = 27;
    public const int GoldBag = 28;
    public const int Arrow = 29;
    public const int Area = 30;
    public const int Weapon = 31;
    public const int Charge = 34;
    public const int Special = 35;
    public const int Defense = 38;
    public const int Shard = 41;
    public const int Bomb = 42;
    public const int WarningSpear = 43;
    public const int WarningCircle = 44;
}

public struct CombatRenderPart : IComponentData
{
    public Entity Owner;
    public float4x4 LocalMatrix;
    public float4 Color;
    public int4 Materials;
    public int Mesh;
    public int WeaponIndex;
    public int Appearance;
    public int Animation;
    public int PartIndex;
    public float3 Pivot;
}

[MaterialProperty("_CombatEffectParameters")]
public struct CombatEffectMaterialParameters : IComponentData
{
    public float4 Value;
}

public struct CombatStaticPose : IComponentData
{
    public float4x4 Matrix;
}

public struct CombatModelState : IComponentData
{
    public int ModelIndex;
    public float3 Scale;
    public float Angle;
    public int bVisible;
}

