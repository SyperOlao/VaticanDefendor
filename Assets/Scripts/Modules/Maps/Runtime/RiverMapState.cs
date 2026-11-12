using System;
using Unity.Entities;
using Unity.Mathematics;

namespace VaticanDefendor.Maps
{
    public sealed class RiverMapState : IComponentData
    {
        public RiverMapConfiguration Configuration;
    }

    public static class RiverMapAccess
    {
        public static RiverMapConfiguration Get(EntityManager Entities)
        {
            using EntityQuery Query = Entities.CreateEntityQuery(typeof(RiverMapState));
            if (Query.IsEmptyIgnoreFilter)
            {
                return null;
            }
            return Entities.GetComponentObject<RiverMapState>(Query.GetSingletonEntity()).Configuration;
        }

        public static void Install(EntityManager Entities, RiverMapConfiguration Configuration)
        {
            Configuration.Validate();
            if (Get(Entities) != null)
            {
                return;
            }
            Entity Settings = Entities.CreateEntity();
            Entities.AddComponentObject(Settings, new RiverMapState { Configuration = Configuration });
        }

        public static float3 Position(RiverMapConfiguration Configuration, float2 HorizontalPosition)
        {
            float Height = 0;
            if (Configuration != null)
            {
                Height = Configuration.Height(HorizontalPosition.x, HorizontalPosition.y);
            }
            return new float3(HorizontalPosition.x, Height, HorizontalPosition.y);
        }

        public static bool TryRaycast(RiverMapConfiguration Configuration, float3 Origin, float3 Direction, out float3 Position)
        {
            Position = default;
            if (Configuration == null)
            {
                return false;
            }
            float PreviousDistance = Origin.y - Configuration.Height(Origin.x, Origin.z);
            for (float Distance = 0.5f; Distance <= 600; Distance += 0.5f)
            {
                float3 Sample = Origin + Direction * Distance;
                float Difference = Sample.y - Configuration.Height(Sample.x, Sample.z);
                if (PreviousDistance >= 0 && Difference <= 0)
                {
                    float Lower = Distance - 0.5f;
                    float Upper = Distance;
                    for (int Iteration = 0; Iteration < 12; Iteration++)
                    {
                        float Middle = (Lower + Upper) * 0.5f;
                        float3 Candidate = Origin + Direction * Middle;
                        if (Candidate.y > Configuration.Height(Candidate.x, Candidate.z))
                        {
                            Lower = Middle;
                        }
                        else
                        {
                            Upper = Middle;
                        }
                    }
                    Position = Origin + Direction * ((Lower + Upper) * 0.5f);
                    return Configuration.Contains(Position.x, Position.z);
                }
                PreviousDistance = Difference;
            }
            return false;
        }
    }
}
