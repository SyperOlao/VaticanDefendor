using UnityEngine;

namespace VaticanDefendor.Content
{
    [CreateAssetMenu(menuName = "VaticanDefendor/Content/Construction Rules")]
    public sealed class ConstructionRulesDefinition : ScriptableObject
    {
        [SerializeField, Tooltip("Устойчивый ID правил строительства.")]
        private string _stableId = "construction.arena.default";

        [SerializeField, Range(1, 64), Tooltip("Максимальное число клеток в одном жесте стены.")]
        private int _maximumWallCellsPerCommand = 12;

        [SerializeField, Min(0.01f), Tooltip("Минимальный интервал между новыми командами строительства, секунды симуляции.")]
        private float _minimumCommandIntervalSeconds = 0.25f;

        [SerializeField, Min(0.01f), Tooltip("Минимальная дистанция от героя до занятой клеткой площади, метры.")]
        private float _heroClearanceMeters = 1.4f;

        public string StableId => _stableId;
        public int MaximumWallCellsPerCommand => _maximumWallCellsPerCommand;
        public float MinimumCommandIntervalSeconds => _minimumCommandIntervalSeconds;
        public float HeroClearanceMeters => _heroClearanceMeters;
    }
}
