using UnityEngine;

namespace VaticanDefendor.Content
{
    [CreateAssetMenu(menuName = "VaticanDefendor/Content/Wood Production")]
    public sealed class ProductionDefinition : ScriptableObject
    {
        [SerializeField, Tooltip("Устойчивый ID производства пилорамы.")]
        private string _stableId = "production.sawmill.wood";

        [SerializeField, Min(0.01f), Tooltip("Время одного цикла производства, секунд симуляции.")]
        private float _cycleSeconds = 10f;

        [SerializeField, Min(1), Tooltip("Дерево, накапливаемое за цикл до подбора героем.")]
        [UnityEngine.Serialization.FormerlySerializedAs("_goldPerCycle")]
        private int _woodPerCycle = 25;

        [SerializeField, Tooltip("Смещение точки подбора от центра постройки по Z, метры.")]
        private float _pickupOffsetZMeters = 2.6f;

        [SerializeField, Min(0.01f), Tooltip("Радиус подбора накопленного дерева героем, метры.")]
        private float _pickupRadiusMeters = 1.1f;

        public string StableId => _stableId;
        public float CycleSeconds => _cycleSeconds;
        public int AmountPerCycle => _woodPerCycle;
        public float PickupOffsetZMeters => _pickupOffsetZMeters;
        public float PickupRadiusMeters => _pickupRadiusMeters;
    }
}
