using UnityEngine;
using VaticanDefendor.Construction;

namespace VaticanDefendor.Content
{
    [CreateAssetMenu(menuName = "VaticanDefendor/Content/Structure")]
    public sealed class StructureDefinition : ScriptableObject
    {
        [SerializeField, Tooltip("Устойчивый ID постройки, передаваемый в намерении клиента.")]
        private string _stableId;

        [SerializeField, Tooltip("Тип поведения и отображения постройки.")]
        private StructureKind _kind;

        [SerializeField, Min(1), Tooltip("Полная цена за одну клетку стены или одну пилораму, золото забега.")]
        private int _goldPrice;

        [SerializeField, Min(0.1f), Tooltip("Половина ширины занятой площади по X и Z, метры.")]
        private float _halfExtentMeters;

        [SerializeField, Tooltip("Производство пилорамы; для стены должно быть пустым.")]
        private ProductionDefinition _production;

        public string StableId => _stableId;
        public StructureKind Kind => _kind;
        public int GoldPrice => _goldPrice;
        public float HalfExtentMeters => _halfExtentMeters;
        public ProductionDefinition Production => _production;
    }
}
