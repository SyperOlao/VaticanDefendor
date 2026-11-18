using UnityEngine;

namespace VaticanDefendor.Content
{
    [CreateAssetMenu(menuName = "VaticanDefendor/Content/Enemy Agent")]
    public sealed class EnemyAgentDefinition : ScriptableObject
    {
        [SerializeField, Tooltip("Устойчивый ID поведения слабого ближнего врага.")]
        private string _stableId = "enemy.weak.melee";

        [SerializeField, Tooltip("Здоровье и награда определяются этим CombatTarget.")]
        private CombatTargetDefinition _combatTarget;

        [SerializeField, Min(0.01f), Tooltip("Базовая скорость передвижения, метров в секунду.")]
        private float _speedMetersPerSecond = 2.55f;

        [SerializeField, Min(0f), Tooltip("Максимальное отклонение скорости конкретного врага, метров в секунду.")]
        private float _speedVariationMetersPerSecond = 0.4f;

        [SerializeField, Min(0.01f), Tooltip("Урон подтверждённого удара, единиц здоровья.")]
        private float _attackDamage = 7f;

        [SerializeField, Min(0.01f), Tooltip("Максимальная дистанция ближнего удара, метров.")]
        private float _attackRadiusMeters = 3.2f;

        [SerializeField, Min(0f), Tooltip("Подготовка удара до проверки попадания, секунд.")]
        private float _windupSeconds = 0.7f;

        [SerializeField, Min(0f), Tooltip("Восстановление после удара, секунд.")]
        private float _recoverySeconds = 1.3f;

        [SerializeField, Min(0.01f), Tooltip("Дистанция, на которой враг прекращает сближение, метров.")]
        private float _formationDistanceMeters = 2.2f;

        [SerializeField, Min(0f), Tooltip("Задержка удаления мёртвого ghost, секунд.")]
        private float _despawnDelaySeconds = 0.3f;

        [SerializeField, Min(1), Tooltip("Максимум врагов одновременно готовящих удар.")]
        private int _maximumSimultaneousWindups = 2;

        public string StableId => _stableId;
        public CombatTargetDefinition CombatTarget => _combatTarget;
        public float SpeedMetersPerSecond => _speedMetersPerSecond;
        public float SpeedVariationMetersPerSecond => _speedVariationMetersPerSecond;
        public float AttackDamage => _attackDamage;
        public float AttackRadiusMeters => _attackRadiusMeters;
        public float WindupSeconds => _windupSeconds;
        public float RecoverySeconds => _recoverySeconds;
        public float FormationDistanceMeters => _formationDistanceMeters;
        public float DespawnDelaySeconds => _despawnDelaySeconds;
        public int MaximumSimultaneousWindups => _maximumSimultaneousWindups;
    }
}
