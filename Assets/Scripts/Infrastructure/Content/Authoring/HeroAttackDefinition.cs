using UnityEngine;

namespace VaticanDefendor.Content
{
    [CreateAssetMenu(menuName = "VaticanDefendor/Content/Hero Basic Attack")]
    public sealed class HeroAttackDefinition : ScriptableObject
    {
        [SerializeField, Tooltip("Устойчивый ID обычного удара мечом.")]
        private string _stableId = "hero.attack.sword";

        [SerializeField, Min(0.01f), Tooltip("Урон одного попадания, единиц здоровья.")]
        private float _damage = 30f;

        [SerializeField, Min(0.01f), Tooltip("Минимальный интервал между ударами, секунд.")]
        private float _intervalSeconds = 0.25f;

        [SerializeField, Min(0.01f), Tooltip("Радиус оружия до множителя обычного удара, метров.")]
        private float _weaponRadiusMeters = 3.6f;

        [SerializeField, Range(0.01f, 2f), Tooltip("Доля радиуса оружия для обычного удара.")]
        private float _rangeScale = 0.65f;

        [SerializeField, Min(0f), Tooltip("Допуск области попадания у центра цели, метров.")]
        private float _hitPaddingMeters = 0.4f;

        [SerializeField, Min(0f), Tooltip("Стоимость обычного удара в выносливости. В сохранённых правилах меча равна нулю.")]
        private float _staminaCost = 0f;

        [SerializeField, Min(0.01f), Tooltip("Максимальное здоровье героя, единиц здоровья.")]
        private float _heroMaximumHealth = 100f;

        [SerializeField, Min(0.01f), Tooltip("Максимальная выносливость героя, единиц.")]
        private float _maximumStamina = 100f;

        [SerializeField, Min(0f), Tooltip("Восстановление выносливости героя, единиц в секунду.")]
        private float _staminaRecoveryPerSecond = 40f;

        public string StableId => _stableId;
        public float Damage => _damage;
        public float IntervalSeconds => _intervalSeconds;
        public float WeaponRadiusMeters => _weaponRadiusMeters;
        public float RangeScale => _rangeScale;
        public float HitPaddingMeters => _hitPaddingMeters;
        public float StaminaCost => _staminaCost;
        public float HeroMaximumHealth => _heroMaximumHealth;
        public float MaximumStamina => _maximumStamina;
        public float StaminaRecoveryPerSecond => _staminaRecoveryPerSecond;
    }
}
