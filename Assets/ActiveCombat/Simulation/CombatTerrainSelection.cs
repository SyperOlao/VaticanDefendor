using Unity.Entities;
using Unity.Mathematics;

public partial struct CombatGameplayContext
{
    public bool TerrainOccluded(CombatCommand Command)
    {
        if (ModelOccludes(PlayerEntity, Command, true))
        {
            return true;
        }
        foreach (Entity Entity in BuildingEntities)
        {
            if (Buildings[Entity].Health > 0 && ModelOccludes(Entity, Command, false))
            {
                return true;
            }
        }
        foreach (Entity Entity in SceneryEntities)
        {
            if (Scenery[Entity].bRemoved == 0 && ModelOccludes(Entity, Command, false))
            {
                return true;
            }
        }
        return false;
    }

    private bool ModelOccludes(Entity Entity, CombatCommand Command, bool bPlayer)
    {
        DynamicBuffer<LinkedEntityGroup> Parts = RenderGroups[Entity];
        for (int Index = 1; Index < Parts.Length; Index++)
        {
            Entity Part = Parts[Index].Value;
            if (!RenderMaterials.IsComponentEnabled(Part) || bPlayer && RenderParts[Part].Animation >= 4)
            {
                continue;
            }
            float4x4 Matrix = RenderTransforms[Part].Value;
            AABB Bounds = AABB.Transform(Matrix, RenderBounds[Part].Value);
            if (CombatTerrainGeometry.Intersects(Command.RayOrigin, Command.RayDirection, Command.ExtraValue, Bounds.Center, Bounds.Extents))
            {
                return true;
            }
        }
        return false;
    }
}

public static class CombatTerrainGeometry
{
    public static bool Intersects(float3 Origin, float3 Direction, float Distance, float3 Center, float3 Extents)
    {
        float Near = 0;
        float Far = Distance;
        for (int Axis = 0; Axis < 3; Axis++)
        {
            float Minimum = Center[Axis] - Extents[Axis];
            float Maximum = Center[Axis] + Extents[Axis];
            if (math.abs(Direction[Axis]) < 0.0000001f)
            {
                if (Origin[Axis] < Minimum || Origin[Axis] > Maximum)
                {
                    return false;
                }
            }
            else
            {
                float First = (Minimum - Origin[Axis]) / Direction[Axis];
                float Last = (Maximum - Origin[Axis]) / Direction[Axis];
                Near = math.max(Near, math.min(First, Last));
                Far = math.min(Far, math.max(First, Last));
                if (Near > Far)
                {
                    return false;
                }
            }
        }
        return Near < Distance;
    }
}
