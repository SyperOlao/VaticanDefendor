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
        InsufficientResources = 10,
        RateLimited = 11,
        CapacityReached = 12,
        ConflictingOperation = 13,
        HeroTooFar = 14
    }

    public struct ConstructionRequest : IRpcCommand
    {
        public uint OperationId;
        public ConstructionOperation Operation;
        public int TargetStructureId;
        public FixedString64Bytes DefinitionId;
        public float StartX;
        public float StartZ;
        public float EndX;
        public float EndZ;
    }

    public struct ConstructionResultRpc : IRpcCommand
    {
        public uint OperationId;
        public ConstructionOutcome Outcome;
        public int BuiltCount;
        public long WoodAfter;
    }

    public struct ConstructionOfferRpc : IRpcCommand
    {
        public FixedString64Bytes DefinitionId;
        public StructureKind Kind;
        public int WoodPrice;
        public int StonePrice;
        public int IronPrice;
        public float HalfExtentMeters;
        public float Health;
        public ResourceKind ProductionKind;
        public float AttackRange;
        public float AttackDamage;
        public float AttackInterval;
        public int GridWidth;
        public int GridHeight;
        public float CellSizeMeters;
        public int MaximumWallCells;
        public int ProductionAmountPerCycle;
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
        public int WoodPrice;
        public int StonePrice;
        public int IronPrice;
        public float HalfExtentMeters;
        public float Health;
        public ResourceKind ProductionKind;
        public float AttackRange;
        public float AttackDamage;
        public float AttackInterval;
        public float ProductionCycleSeconds;
        public int ProductionAmountPerCycle;
        public float PickupOffsetZMeters;
        public float PickupRadiusMeters;
    }

    public struct ConstructionRunState : IComponentData
    {
        public int NextStructureId;
        public uint LastOperationId;
        public ConstructionOperation LastOperation;
        public int LastTargetStructureId;
        public ConstructionOutcome LastOutcome;
        public int LastBuiltCount;
        public long LastWoodAfter;
        public double NextAllowedSeconds;
        public FixedString64Bytes LastDefinitionId;
        public float LastStartX;
        public float LastStartZ;
        public float LastEndX;
        public float LastEndZ;
    }

    public struct OccupiedStructure : IBufferElementData
    {
        public float RotationRadians;
        public int StructureId;
        public float X;
        public float Z;
        public float HalfExtentMeters;
        public float Health;
        public ResourceKind ProductionKind;
        public float AttackRange;
        public float AttackDamage;
        public float AttackInterval;
    }

    public struct StructureInstance : IComponentData
    {
        [GhostField] public int StructureId;
        [GhostField] public FixedString64Bytes DefinitionId;
        [GhostField] public float HalfExtent;
        [GhostField] public int Level;
        [GhostField] public int PaidWood;
        [GhostField] public int PaidStone;
        [GhostField] public int PaidIron;
        [GhostField] public StructureKind Kind;
        [GhostField] public float RotationRadians;
        [GhostField] public float PlacementHorizontal;
        [GhostField] public float PlacementVertical;
    }

    public struct StructureGhostPrefab : IComponentData
    {
        public Entity Value;
    }
}
