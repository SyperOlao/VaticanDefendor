using UnityEngine;

namespace VaticanDefendor.Content
{
    [CreateAssetMenu(menuName = "VaticanDefendor/Content/Starting Church")]
    public sealed class ChurchDefinition : ScriptableObject
    {
        [SerializeField, Tooltip("Устойчивый ID стартовой церкви.")]
        private string _stableId = "church.starting";

        [SerializeField, Min(1f), Tooltip("Начальное и максимальное здоровье церкви, HP.")]
        private float _maximumHealth = 600f;

        [SerializeField, Tooltip("Координата X центра церкви, метров от центра арены.")]
        private float _xMeters;

        [SerializeField, Tooltip("Координата Z центра церкви, метров от центра арены.")]
        private float _zMeters = -3f;

        [SerializeField, Min(1), Tooltip("Начальный уровень церкви. Повышается только авторитетным Tycoon.")]
        private int _initialLevel = 1;

        public string StableId => _stableId;
        public float MaximumHealth => _maximumHealth;
        public float XMeters => _xMeters;
        public float ZMeters => _zMeters;
        public int InitialLevel => _initialLevel;
    }
}
