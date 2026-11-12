using UnityEngine;

namespace VaticanDefendor.Content
{
    [CreateAssetMenu(menuName = "VaticanDefendor/Content/Catalog")]
    public sealed class ContentCatalog : ScriptableObject
    {
        [SerializeField] private TextAsset _riverMap;
        public TextAsset RiverMap => _riverMap;
        [SerializeField, Tooltip("Все определения движения, включаемые в release.")]
        private HeroMovementDefinition[] _heroMovementDefinitions;

        [SerializeField, Tooltip("Определение движения героя для нового забега. Должно входить в список выше.")]
        private HeroMovementDefinition _defaultHeroMovement;

        [SerializeField, Tooltip("Обычный удар мечом, включаемый в release.")]
        private HeroAttackDefinition _heroAttack;

        [SerializeField, Tooltip("Слабый враг минимального боевого сценария.")]
        private CombatTargetDefinition _combatTarget;

        [SerializeField, Tooltip("Ресурсы текущего забега и награда за убийство.")]
        private RunEconomyDefinition _runEconomy;

        [SerializeField, Tooltip("Поведение слабого врага и связь с его CombatTarget.")]
        private EnemyAgentDefinition _enemyAgent;

        [SerializeField, Tooltip("Проходимость арены и бюджет общего поля направлений.")]
        private NavigationDefinition _navigation;

        [SerializeField, Tooltip("Настройки появления волн слабого врага.")]
        private WaveScheduleDefinition _waveSchedule;

        [SerializeField, Tooltip("Правила серверной проверки строительства.")]
        private ConstructionRulesDefinition _constructionRules;

        [SerializeField, Tooltip("Стена и пилорама, доступные для строительства.")]
        private StructureDefinition[] _structures;

        [SerializeField, Tooltip("Производство дерева пилорамой.")]
        private ProductionDefinition _sawmillProduction;

        [SerializeField, Tooltip("Стартовая церковь и её защитное здоровье.")]
        private ChurchDefinition _church;

        public HeroMovementDefinition[] GetHeroMovementDefinitions()
        {
            return _heroMovementDefinitions == null
                ? null
                : (HeroMovementDefinition[])_heroMovementDefinitions.Clone();
        }

        public HeroMovementDefinition DefaultHeroMovement => _defaultHeroMovement;
        public HeroAttackDefinition HeroAttack => _heroAttack;
        public CombatTargetDefinition CombatTarget => _combatTarget;
        public RunEconomyDefinition RunEconomy => _runEconomy;
        public EnemyAgentDefinition EnemyAgent => _enemyAgent;
        public NavigationDefinition Navigation => _navigation;
        public WaveScheduleDefinition WaveSchedule => _waveSchedule;
        public ConstructionRulesDefinition ConstructionRules => _constructionRules;
        public StructureDefinition[] GetStructures() => _structures == null
            ? null : (StructureDefinition[])_structures.Clone();
        public ProductionDefinition SawmillProduction => _sawmillProduction;
        public ChurchDefinition Church => _church;
        [SerializeField] private GameplayDefinition _gameplay;
        public GameplayDefinition Gameplay => _gameplay;
    }
}
