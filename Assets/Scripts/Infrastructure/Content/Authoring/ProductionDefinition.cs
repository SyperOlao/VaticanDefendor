using UnityEngine;

namespace VaticanDefendor.Content
{
    [CreateAssetMenu(menuName = "VaticanDefendor/Content/Gold Production")]
    public sealed class ProductionDefinition : ScriptableObject
    {
        [SerializeField, Tooltip("Устойчивый ID производства пилорамы.")]
        private string _stableId = "production.sawmill.gold";

        [SerializeField, Min(0.01f), Tooltip("Время одного цикла производства, секунд симуляции.")]
        private float _cycleSeconds = 10f;

        [SerializeField, Min(1), Tooltip("Золото, накапливаемое за цикл до подбора героем.")]
        private int _goldPerCycle = 25;

        [SerializeField, Tooltip("Смещение точки подбора от центра постройки по Z, метры.")]
        private float _pickupOffsetZMeters = 2.6f;

        [SerializeField, Min(0.01f), Tooltip("Радиус подбора накопленного золота героем, метры.")]
        private float _pickupRadiusMeters = 1.1f;

        public string StableId => _stableId;
        public float CycleSeconds => _cycleSeconds;
        public int GoldPerCycle => _goldPerCycle;
        public float PickupOffsetZMeters => _pickupOffsetZMeters;
        public float PickupRadiusMeters => _pickupRadiusMeters;
    }
}
