using System;
using UnityEngine;

namespace VaticanDefendor.Content
{
    [Serializable]
    public struct HeroMovementSnapshot
    {
        [SerializeField] private string _stableId;
        [SerializeField] private float _walkSpeedMetersPerSecond;
        [SerializeField] private float _runSpeedMetersPerSecond;

        public HeroMovementSnapshot(string stableId, float walkSpeedMetersPerSecond, float runSpeedMetersPerSecond)
        {
            _stableId = stableId;
            _walkSpeedMetersPerSecond = walkSpeedMetersPerSecond;
            _runSpeedMetersPerSecond = runSpeedMetersPerSecond;
        }

        public string StableId => _stableId;
        public float WalkSpeedMetersPerSecond => _walkSpeedMetersPerSecond;
        public float RunSpeedMetersPerSecond => _runSpeedMetersPerSecond;
    }

    [Serializable]
    public struct HeroAttackSnapshot
    {
        [SerializeField] private string _stableId;
        [SerializeField] private float _damage;
        [SerializeField] private float _intervalSeconds;
        [SerializeField] private float _weaponRadiusMeters;
        [SerializeField] private float _rangeScale;
        [SerializeField] private float _hitPaddingMeters;
        [SerializeField] private float _staminaCost;
        [SerializeField] private float _heroMaximumHealth;
        [SerializeField] private float _maximumStamina;
        [SerializeField] private float _staminaRecoveryPerSecond;

        public HeroAttackSnapshot(string stableId, float damage, float intervalSeconds,
            float weaponRadiusMeters, float rangeScale, float hitPaddingMeters, float staminaCost,
            float heroMaximumHealth, float maximumStamina, float staminaRecoveryPerSecond)
        {
            _stableId = stableId;
            _damage = damage;
            _intervalSeconds = intervalSeconds;
            _weaponRadiusMeters = weaponRadiusMeters;
            _rangeScale = rangeScale;
            _hitPaddingMeters = hitPaddingMeters;
            _staminaCost = staminaCost;
            _heroMaximumHealth = heroMaximumHealth;
            _maximumStamina = maximumStamina;
            _staminaRecoveryPerSecond = staminaRecoveryPerSecond;
        }

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

    [Serializable]
    public struct CombatTargetSnapshot
    {
        [SerializeField] private string _stableId;
        [SerializeField] private float _maximumHealth;
        [SerializeField] private float _spawnForwardMeters;

        public CombatTargetSnapshot(string stableId, float maximumHealth, float spawnForwardMeters)
        {
            _stableId = stableId;
            _maximumHealth = maximumHealth;
            _spawnForwardMeters = spawnForwardMeters;
        }

        public string StableId => _stableId;
        public float MaximumHealth => _maximumHealth;
        public float SpawnForwardMeters => _spawnForwardMeters;
    }

    [Serializable]
    public struct RunEconomySnapshot
    {
        [SerializeField] private string _stableId;
        [UnityEngine.Serialization.FormerlySerializedAs("_initialGold")]
        [SerializeField] private int _initialWood;
        [UnityEngine.Serialization.FormerlySerializedAs("_goldPerKill")]
        [SerializeField] private int _woodPerKill;
        [SerializeField] private int _experiencePerKill;

        public RunEconomySnapshot(string stableId, int initialWood, int woodPerKill, int experiencePerKill)
        {
            _stableId = stableId;
            _initialWood = initialWood;
            _woodPerKill = woodPerKill;
            _experiencePerKill = experiencePerKill;
        }

        public string StableId => _stableId;
        public int InitialWood => _initialWood;
        public int WoodPerKill => _woodPerKill;
        public int ExperiencePerKill => _experiencePerKill;
    }

    [Serializable]
    public struct EnemyAgentSnapshot
    {
        [SerializeField] private string _stableId;
        [SerializeField] private string _combatTargetId;
        [SerializeField] private float _speedMetersPerSecond;
        [SerializeField] private float _speedVariationMetersPerSecond;
        [SerializeField] private float _attackDamage;
        [SerializeField] private float _attackRadiusMeters;
        [SerializeField] private float _windupSeconds;
        [SerializeField] private float _recoverySeconds;
        [SerializeField] private float _formationDistanceMeters;
        [SerializeField] private float _despawnDelaySeconds;
        [SerializeField] private int _maximumSimultaneousWindups;

        public EnemyAgentSnapshot(string stableId, string combatTargetId, float speedMetersPerSecond,
            float speedVariationMetersPerSecond, float attackDamage, float attackRadiusMeters,
            float windupSeconds, float recoverySeconds, float formationDistanceMeters,
            float despawnDelaySeconds, int maximumSimultaneousWindups)
        {
            _stableId = stableId;
            _combatTargetId = combatTargetId;
            _speedMetersPerSecond = speedMetersPerSecond;
            _speedVariationMetersPerSecond = speedVariationMetersPerSecond;
            _attackDamage = attackDamage;
            _attackRadiusMeters = attackRadiusMeters;
            _windupSeconds = windupSeconds;
            _recoverySeconds = recoverySeconds;
            _formationDistanceMeters = formationDistanceMeters;
            _despawnDelaySeconds = despawnDelaySeconds;
            _maximumSimultaneousWindups = maximumSimultaneousWindups;
        }

        public string StableId => _stableId;
        public string CombatTargetId => _combatTargetId;
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

    [Serializable]
    public struct NavigationSnapshot
    {
        [SerializeField] private string _stableId;
        [SerializeField] private int _widthCells;
        [SerializeField] private int _heightCells;
        [SerializeField] private float _cellSizeMeters;
        [SerializeField] private int _flowCellsPerTick;
        [SerializeField] private float _goalRefreshSeconds;
        [SerializeField] private float _goalMoveThresholdMeters;
        [SerializeField] private int _maximumDynamicObstacles;
        [SerializeField] private NavigationObstacleDefinition[] _staticObstacles;

        public NavigationSnapshot(string stableId, int widthCells, int heightCells,
            float cellSizeMeters, int flowCellsPerTick, float goalRefreshSeconds,
            float goalMoveThresholdMeters, int maximumDynamicObstacles,
            NavigationObstacleDefinition[] staticObstacles)
        {
            _stableId = stableId;
            _widthCells = widthCells;
            _heightCells = heightCells;
            _cellSizeMeters = cellSizeMeters;
            _flowCellsPerTick = flowCellsPerTick;
            _goalRefreshSeconds = goalRefreshSeconds;
            _goalMoveThresholdMeters = goalMoveThresholdMeters;
            _maximumDynamicObstacles = maximumDynamicObstacles;
            _staticObstacles = (NavigationObstacleDefinition[])staticObstacles.Clone();
        }

        public string StableId => _stableId;
        public int WidthCells => _widthCells;
        public int HeightCells => _heightCells;
        public float CellSizeMeters => _cellSizeMeters;
        public int FlowCellsPerTick => _flowCellsPerTick;
        public float GoalRefreshSeconds => _goalRefreshSeconds;
        public float GoalMoveThresholdMeters => _goalMoveThresholdMeters;
        public int MaximumDynamicObstacles => _maximumDynamicObstacles;
        public NavigationObstacleDefinition[] GetStaticObstacles() =>
            _staticObstacles == null ? null : (NavigationObstacleDefinition[])_staticObstacles.Clone();
    }

    [Serializable]
    public struct WaveScheduleSnapshot
    {
        [SerializeField] private string _stableId;
        [SerializeField] private string _enemyId;
        [SerializeField] private float _initialDelaySeconds;
        [SerializeField] private int _initialEnemyCount;
        [SerializeField] private int _enemiesAddedPerWave;
        [SerializeField] private float _spawnIntervalSeconds;
        [SerializeField] private int _maximumSpawnsPerTick;
        [SerializeField] private float _waveDelaySeconds;
        [SerializeField] private int _maximumSimultaneousEnemies;
        [SerializeField] private int _maximumWaves;
        [SerializeField] private float _spawnRadiusMeters;

        public WaveScheduleSnapshot(string stableId, string enemyId, float initialDelaySeconds,
            int initialEnemyCount, int enemiesAddedPerWave, float spawnIntervalSeconds,
            int maximumSpawnsPerTick,
            float waveDelaySeconds, int maximumSimultaneousEnemies, int maximumWaves,
            float spawnRadiusMeters)
        {
            _stableId = stableId;
            _enemyId = enemyId;
            _initialDelaySeconds = initialDelaySeconds;
            _initialEnemyCount = initialEnemyCount;
            _enemiesAddedPerWave = enemiesAddedPerWave;
            _spawnIntervalSeconds = spawnIntervalSeconds;
            _maximumSpawnsPerTick = maximumSpawnsPerTick;
            _waveDelaySeconds = waveDelaySeconds;
            _maximumSimultaneousEnemies = maximumSimultaneousEnemies;
            _maximumWaves = maximumWaves;
            _spawnRadiusMeters = spawnRadiusMeters;
        }

        public string StableId => _stableId;
        public string EnemyId => _enemyId;
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

    [Serializable]
    public struct ChurchSnapshot
    {
        [SerializeField] private string _stableId;
        [SerializeField] private float _maximumHealth;
        [SerializeField] private float _xMeters;
        [SerializeField] private float _zMeters;
        [SerializeField] private int _initialLevel;

        public ChurchSnapshot(string stableId, float maximumHealth, float xMeters,
            float zMeters, int initialLevel)
        {
            _stableId = stableId;
            _maximumHealth = maximumHealth;
            _xMeters = xMeters;
            _zMeters = zMeters;
            _initialLevel = initialLevel;
        }

        public string StableId => _stableId;
        public float MaximumHealth => _maximumHealth;
        public float XMeters => _xMeters;
        public float ZMeters => _zMeters;
        public int InitialLevel => _initialLevel;
    }

    public sealed class ContentRelease : ScriptableObject
    {
        public const int CurrentContentSchemaVersion = 8;

        [SerializeField, HideInInspector] private string _releaseId;
        [SerializeField, HideInInspector] private int _contentSchemaVersion;
        [SerializeField, HideInInspector] private string _defaultHeroMovementId;
        [SerializeField, HideInInspector] private HeroMovementSnapshot[] _movementManifest;
        [SerializeField, HideInInspector] private HeroAttackSnapshot _heroAttack;
        [SerializeField, HideInInspector] private CombatTargetSnapshot _combatTarget;
        [SerializeField, HideInInspector] private RunEconomySnapshot _runEconomy;
        [SerializeField, HideInInspector] private EnemyAgentSnapshot _enemyAgent;
        [SerializeField, HideInInspector] private NavigationSnapshot _navigation;
        [SerializeField, HideInInspector] private WaveScheduleSnapshot _waveSchedule;
        [SerializeField, HideInInspector] private ConstructionRulesSnapshot _constructionRules;
        [SerializeField, HideInInspector] private StructureSnapshot[] _structures;
        [SerializeField, HideInInspector] private ProductionSnapshot _sawmillProduction;
        [SerializeField, HideInInspector] private ChurchSnapshot _church;

        [SerializeField, HideInInspector] private string _gameplayJson;
        public string GameplayJson => _gameplayJson;
        public GameplayConfiguration GetGameplay() => JsonUtility.FromJson<GameplayConfiguration>(_gameplayJson);

        public string ReleaseId => _releaseId;
        public int ContentSchemaVersion => _contentSchemaVersion;
        public string DefaultHeroMovementId => _defaultHeroMovementId;
        public HeroAttackSnapshot HeroAttack => _heroAttack;
        public CombatTargetSnapshot CombatTarget => _combatTarget;
        public RunEconomySnapshot RunEconomy => _runEconomy;
        public EnemyAgentSnapshot EnemyAgent => _enemyAgent;
        public NavigationSnapshot Navigation => _navigation;
        public WaveScheduleSnapshot WaveSchedule => _waveSchedule;
        public ConstructionRulesSnapshot ConstructionRules => _constructionRules;
        public ProductionSnapshot SawmillProduction => _sawmillProduction;
        public ChurchSnapshot Church => _church;
        public StructureSnapshot[] GetStructures() => _structures == null
            ? null : (StructureSnapshot[])_structures.Clone();

        public HeroMovementSnapshot[] GetMovementManifest()
        {
            return _movementManifest == null
                ? null
                : (HeroMovementSnapshot[])_movementManifest.Clone();
        }

        public bool TryGetDefaultHeroMovement(out HeroMovementSnapshot movement)
        {
            if (_movementManifest != null)
            {
                foreach (HeroMovementSnapshot entry in _movementManifest)
                {
                    if (string.Equals(entry.StableId, _defaultHeroMovementId, StringComparison.Ordinal))
                    {
                        movement = entry;
                        return true;
                    }
                }
            }

            movement = default;
            return false;
        }

#if UNITY_EDITOR
        public void SetCompiledData(string releaseId, int contentSchemaVersion,
            string defaultHeroMovementId, HeroMovementSnapshot[] movementManifest,
            HeroAttackSnapshot heroAttack, CombatTargetSnapshot combatTarget, RunEconomySnapshot runEconomy,
            EnemyAgentSnapshot enemyAgent, NavigationSnapshot navigation, WaveScheduleSnapshot waveSchedule,
            ConstructionRulesSnapshot constructionRules, StructureSnapshot[] structures,
            ProductionSnapshot sawmillProduction, ChurchSnapshot church = default, string GameplayJson = null)
        {
            _releaseId = releaseId;
            _contentSchemaVersion = contentSchemaVersion;
            _defaultHeroMovementId = defaultHeroMovementId;
            _movementManifest = (HeroMovementSnapshot[])movementManifest.Clone();
            _heroAttack = heroAttack;
            _combatTarget = combatTarget;
            _runEconomy = runEconomy;
            _enemyAgent = enemyAgent;
            _navigation = navigation;
            _waveSchedule = waveSchedule;
            _constructionRules = constructionRules;
            _structures = (StructureSnapshot[])structures.Clone();
            _sawmillProduction = sawmillProduction;
            _church = church;
            _gameplayJson = GameplayJson;
        }
#endif
    }
}
