using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;
using VaticanDefendor.Construction;

namespace VaticanDefendor.Construction
{
    public enum ConstructionOutcome : byte
    {
        Built = 0,
        Duplicate = 1,
        Unauthorized = 2,
        InactiveRun = 3,
        InvalidDefinition = 4,
        InvalidPath = 5,
        OutOfBounds = 6,
        Occupied = 7,
        Blocked = 8,
        HeroTooClose = 9,
        InsufficientGold = 10,
        RateLimited = 11,
        CapacityReached = 12,
        ConflictingOperation = 13
    }

    public struct ConstructionRequest : IRpcCommand
    {
        public uint OperationId;
        public FixedString64Bytes DefinitionId;
        public int StartX;
        public int StartZ;
        public int EndX;
        public int EndZ;
    }

    public struct ConstructionResultRpc : IRpcCommand
    {
        public uint OperationId;
        public ConstructionOutcome Outcome;
        public int BuiltCount;
        public long GoldAfter;
    }

    public struct ConstructionOfferRpc : IRpcCommand
    {
        public FixedString64Bytes DefinitionId;
        public StructureKind Kind;
        public int GoldPrice;
        public float HalfExtentMeters;
        public int GridWidth;
        public int GridHeight;
        public float CellSizeMeters;
        public int MaximumWallCells;
        public int ProductionGoldPerCycle;
        public float ProductionCycleSeconds;
        public float PickupOffsetZMeters;
    }

    public struct ConstructionRunRules : IComponentData
    {
        public int GridWidth;
        public int GridHeight;
        public float CellSizeMeters;
        public int MaximumWallCells;
        public float MinimumCommandIntervalSeconds;
        public float HeroClearanceMeters;
    }

    public struct ConstructionDefinitionRuntime : IBufferElementData
    {
        public FixedString64Bytes StableId;
        public StructureKind Kind;
        public int GoldPrice;
        public float HalfExtentMeters;
        public float ProductionCycleSeconds;
        public int ProductionGoldPerCycle;
        public float PickupOffsetZMeters;
        public float PickupRadiusMeters;
    }

    public struct ConstructionRunState : IComponentData
    {
        public int NextStructureId;
        public uint LastOperationId;
        public ConstructionOutcome LastOutcome;
        public int LastBuiltCount;
        public long LastGoldAfter;
        public double NextAllowedSeconds;
        public FixedString64Bytes LastDefinitionId;
        public int LastStartX;
        public int LastStartZ;
        public int LastEndX;
        public int LastEndZ;
    }

    public struct OccupiedCell : IBufferElementData
    {
        public int StructureId;
        public int X;
        public int Z;
        public float HalfExtentMeters;
    }

    public struct StructureInstance : IComponentData
    {
        [GhostField] public int StructureId;
        [GhostField] public StructureKind Kind;
        [GhostField] public int CellX;
        [GhostField] public int CellZ;
    }

    public struct StructureGhostPrefab : IComponentData
    {
        public Entity Value;
    }
}
