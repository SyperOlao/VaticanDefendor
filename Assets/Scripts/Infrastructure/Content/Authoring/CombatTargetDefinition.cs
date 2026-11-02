using UnityEngine;

namespace VaticanDefendor.Content
{
    [CreateAssetMenu(menuName = "VaticanDefendor/Content/Combat Target")]
    public sealed class CombatTargetDefinition : ScriptableObject
    {
        [SerializeField, Tooltip("Устойчивый ID слабого врага для минимального боевого сценария.")]
        private string _stableId = "enemy.weak";

        [SerializeField, Min(0.01f), Tooltip("Максимальное здоровье цели, единиц здоровья.")]
        private float _maximumHealth = 53f;

        [SerializeField, Min(0.01f), Tooltip("Положение неподвижной цели перед точкой появления героя, метров.")]
        private float _spawnForwardMeters = 2f;

        public string StableId => _stableId;
        public float MaximumHealth => _maximumHealth;
        public float SpawnForwardMeters => _spawnForwardMeters;
    }
}
