using System;
using UnityEngine;
using VaticanDefendor.Construction;

namespace VaticanDefendor.Content
{
    [Serializable]
    public struct StructureSnapshot
    {
        [SerializeField] private string _stableId;
        [SerializeField] private StructureKind _kind;
        [UnityEngine.Serialization.FormerlySerializedAs("_goldPrice")]
        [SerializeField] private int _woodPrice;
        [SerializeField] private float _halfExtentMeters;
        [SerializeField] private string _productionId;

        public StructureSnapshot(string stableId, StructureKind kind, int woodPrice,
            float halfExtentMeters, string productionId)
        {
            _stableId = stableId;
            _kind = kind;
            _woodPrice = woodPrice;
            _halfExtentMeters = halfExtentMeters;
            _productionId = productionId;
        }

        public string StableId => _stableId;
        public StructureKind Kind => _kind;
        public int WoodPrice => _woodPrice;
        public float HalfExtentMeters => _halfExtentMeters;
        public string ProductionId => _productionId;
    }

    [Serializable]
    public struct ConstructionRulesSnapshot
    {
        [SerializeField] private string _stableId;
        [SerializeField] private int _maximumWallCellsPerCommand;
        [SerializeField] private float _minimumCommandIntervalSeconds;
        [SerializeField] private float _heroClearanceMeters;

        public ConstructionRulesSnapshot(string stableId, int maximumWallCellsPerCommand,
            float minimumCommandIntervalSeconds, float heroClearanceMeters)
        {
            _stableId = stableId;
            _maximumWallCellsPerCommand = maximumWallCellsPerCommand;
            _minimumCommandIntervalSeconds = minimumCommandIntervalSeconds;
            _heroClearanceMeters = heroClearanceMeters;
        }

        public string StableId => _stableId;
        public int MaximumWallCellsPerCommand => _maximumWallCellsPerCommand;
        public float MinimumCommandIntervalSeconds => _minimumCommandIntervalSeconds;
        public float HeroClearanceMeters => _heroClearanceMeters;
    }

    [Serializable]
    public struct ProductionSnapshot
    {
        [SerializeField] private string _stableId;
        [SerializeField] private float _cycleSeconds;
        [UnityEngine.Serialization.FormerlySerializedAs("_goldPerCycle")]
        [SerializeField] private int _woodPerCycle;
        [SerializeField] private float _pickupOffsetZMeters;
        [SerializeField] private float _pickupRadiusMeters;

        public ProductionSnapshot(string stableId, float cycleSeconds, int woodPerCycle,
            float pickupOffsetZMeters, float pickupRadiusMeters)
        {
            _stableId = stableId;
            _cycleSeconds = cycleSeconds;
            _woodPerCycle = woodPerCycle;
            _pickupOffsetZMeters = pickupOffsetZMeters;
            _pickupRadiusMeters = pickupRadiusMeters;
        }

        public string StableId => _stableId;
        public float CycleSeconds => _cycleSeconds;
        public int AmountPerCycle => _woodPerCycle;
        public float PickupOffsetZMeters => _pickupOffsetZMeters;
        public float PickupRadiusMeters => _pickupRadiusMeters;
    }
}
