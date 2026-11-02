using UnityEngine;

namespace VaticanDefendor.Content
{
    [CreateAssetMenu(menuName = "VaticanDefendor/Content/Hero Movement")]
    public sealed class HeroMovementDefinition : ScriptableObject
    {
        [SerializeField, Tooltip("Устойчивый ID определения. Не меняйте после публикации release.")]
        private string _stableId = "hero.default";

        [SerializeField, Min(0.01f), Tooltip("Скорость ходьбы героя, метров в секунду.")]
        private float _walkSpeedMetersPerSecond = 4.8f;

        [SerializeField, Min(0.01f), Tooltip("Скорость бега героя, метров в секунду; не меньше скорости ходьбы.")]
        private float _runSpeedMetersPerSecond = 8.6f;

        public string StableId => _stableId;
        public float WalkSpeedMetersPerSecond => _walkSpeedMetersPerSecond;
        public float RunSpeedMetersPerSecond => _runSpeedMetersPerSecond;
    }
}
