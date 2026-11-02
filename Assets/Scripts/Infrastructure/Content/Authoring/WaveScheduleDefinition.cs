using UnityEngine;

namespace VaticanDefendor.Content
{
    [CreateAssetMenu(menuName = "VaticanDefendor/Content/Wave Schedule")]
    public sealed class WaveScheduleDefinition : ScriptableObject
    {
        [SerializeField, Tooltip("Устойчивый ID последовательности волн.")]
        private string _stableId = "waves.weak.default";

        [SerializeField, Tooltip("Тип врага, создаваемого этой последовательностью.")]
        private EnemyAgentDefinition _enemy;

        [SerializeField, Min(0f), Tooltip("Задержка первой волны после начала сессии, секунд.")]
        private float _initialDelaySeconds = 5f;

        [SerializeField, Min(1), Tooltip("Число врагов первой волны.")]
        private int _initialEnemyCount = 7;

        [SerializeField, Min(0), Tooltip("Прибавка числа врагов в следующей волне.")]
        private int _enemiesAddedPerWave = 2;

        [SerializeField, Min(0.01f), Tooltip("Интервал появления врагов внутри волны, секунд.")]
        private float _spawnIntervalSeconds = 0.3f;

        [SerializeField, Min(1), Tooltip("Максимум созданий врагов за один серверный tick.")]
        private int _maximumSpawnsPerTick = 4;

        [SerializeField, Min(0f), Tooltip("Пауза после уничтожения волны до следующей, секунд.")]
        private float _waveDelaySeconds = 5f;

        [SerializeField, Min(1), Tooltip("Предел одновременно живых врагов обычного забега.")]
        private int _maximumSimultaneousEnemies = 32;

        [SerializeField, Min(1), Tooltip("Предел числа волн этого сценария.")]
        private int _maximumWaves = 3;

        [SerializeField, Min(0.1f), Tooltip("Расстояние появления от центра арены, метров.")]
        private float _spawnRadiusMeters = 16.8f;

        public string StableId => _stableId;
        public EnemyAgentDefinition Enemy => _enemy;
        public float InitialDelaySeconds => _initialDelaySeconds;
        public int InitialEnemyCount => _initialEnemyCount;
        public int EnemiesAddedPerWave => _enemiesAddedPerWave;
        public float SpawnIntervalSeconds => _spawnIntervalSeconds;
        public int MaximumSpawnsPerTick => _maximumSpawnsPerTick;
        public float WaveDelaySeconds => _waveDelaySeconds;
        public int MaximumSimultaneousEnemies => _maximumSimultaneousEnemies;
        public int MaximumWaves => _maximumWaves;
        public float SpawnRadiusMeters => _spawnRadiusMeters;
    }
}
