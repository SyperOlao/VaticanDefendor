using System;
using UnityEngine;

namespace VaticanDefendor.Content
{
    [Serializable]
    public struct NavigationObstacleDefinition
    {
        [SerializeField, Tooltip("Центр препятствия по X, метров.")] private float _xMeters;
        [SerializeField, Tooltip("Центр препятствия по Z, метров.")] private float _zMeters;
        [SerializeField, Min(0.01f), Tooltip("Радиус препятствия в горизонтальной плоскости, метров.")]
        private float _radiusMeters;

        public NavigationObstacleDefinition(float xMeters, float zMeters, float radiusMeters)
        {
            _xMeters = xMeters;
            _zMeters = zMeters;
            _radiusMeters = radiusMeters;
        }

        public float XMeters => _xMeters;
        public float ZMeters => _zMeters;
        public float RadiusMeters => _radiusMeters;
    }

    [CreateAssetMenu(menuName = "VaticanDefendor/Content/Navigation Grid")]
    public sealed class NavigationDefinition : ScriptableObject
    {
        [SerializeField, Tooltip("Устойчивый ID навигации арены.")]
        private string _stableId = "navigation.arena.grid";

        [SerializeField, Range(3, 128), Tooltip("Число ячеек по X; нечётное значение оставляет центр для героя.")]
        private int _widthCells = 15;

        [SerializeField, Range(3, 128), Tooltip("Число ячеек по Z.")]
        private int _heightCells = 15;

        [SerializeField, Min(0.1f), Tooltip("Размер квадратной ячейки, метров.")]
        private float _cellSizeMeters = 2.4f;

        [SerializeField, Min(1), Tooltip("Максимум ячеек перестроения общего поля за tick.")]
        private int _flowCellsPerTick = 64;

        [SerializeField, Min(0.01f), Tooltip("Минимальный интервал обновления поля при движении цели, секунд симуляции.")]
        private float _goalRefreshSeconds = 0.5f;

        [SerializeField, Min(0f), Tooltip("Смещение цели, требующее обновления поля, метров.")]
        private float _goalMoveThresholdMeters = 1.2f;

        [SerializeField, Min(1), Tooltip("Максимум динамических препятствий, подаваемых строительством.")]
        private int _maximumDynamicObstacles = 128;

        [SerializeField, Tooltip("Непроходимые камни и деревья сохранённой сцены; обновляются явной командой Editor.")]
        private NavigationObstacleDefinition[] _staticObstacles = Array.Empty<NavigationObstacleDefinition>();

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
}
