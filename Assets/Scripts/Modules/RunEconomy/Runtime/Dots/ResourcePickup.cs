using System;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using VaticanDefendor.Session;

namespace VaticanDefendor.RunEconomy
{
    [Serializable]
    public struct ResourcePickup : IBufferElementData
    {
        [GhostField] public uint Identifier;
        [GhostField] public ResourceKind Kind;
        [GhostField] public long Amount;
        [GhostField(Quantization = 1000)] public float3 Position;
        [GhostField] public float Age;
        [GhostField] public byte Attracted;
    }

    public struct ResourcePickupSequence : IComponentData
    {
        public uint NextIdentifier;
    }

    public struct ResourcePickupTuning : IComponentData
    {
        public float AttractionRadius;
        public float CollectionRadius;
        public float AttractionSpeed;
        public float ScatterRadius;
        public float ReleaseDelay;
        public int AmountPerPickup;
        public int MaximumPiecesPerDrop;
    }

    public static class ResourcePickupDrops
    {
        public static void Spawn(EntityManager Entities, float3 Position, ResourceAmounts Amounts)
        {
            if (Amounts.IsEmpty)
            {
                return;
            }
            using EntityQuery Church = Entities.CreateEntityQuery(typeof(ChurchTag), typeof(ResourcePickup));
            DynamicBuffer<ResourcePickup> Pickups = Entities.GetBuffer<ResourcePickup>(Church.GetSingletonEntity());
            using EntityQuery SequenceQuery = Entities.CreateEntityQuery(typeof(ResourcePickupSequence));
            ResourcePickupSequence Sequence = SequenceQuery.GetSingleton<ResourcePickupSequence>();
            using EntityQuery TuningQuery = Entities.CreateEntityQuery(typeof(ResourcePickupTuning));
            ResourcePickupTuning Tuning = TuningQuery.GetSingleton<ResourcePickupTuning>();
            for (int KindIndex = 1; KindIndex <= 3; KindIndex++)
            {
                ResourceKind Kind = (ResourceKind)KindIndex;
                long Remaining = Amounts.Get(Kind);
                int PieceCount = (int)Math.Min(Tuning.MaximumPiecesPerDrop,
                    (Remaining + Tuning.AmountPerPickup - 1) / Tuning.AmountPerPickup);
                for (int PieceIndex = 0; PieceIndex < PieceCount; PieceIndex++)
                {
                    long Amount = Remaining / (PieceCount - PieceIndex);
                    uint Identifier = Sequence.NextIdentifier;
                    Sequence.NextIdentifier = checked(Identifier + 1);
                    var Random = new Unity.Mathematics.Random(math.hash(new uint2(Identifier, 9137)) | 1);
                    float Angle = Random.NextFloat(0, math.PI * 2);
                    float Radius = Random.NextFloat(0.2f, 1) * Tuning.ScatterRadius;
                    Pickups.Add(new ResourcePickup
                    {
                        Identifier = Identifier, Kind = Kind, Amount = Amount,
                        Position = Position + new float3(math.cos(Angle) * Radius, 0.35f, math.sin(Angle) * Radius)
                    });
                    Remaining -= Amount;
                }
            }
            SequenceQuery.SetSingleton(Sequence);
        }
    }
}
