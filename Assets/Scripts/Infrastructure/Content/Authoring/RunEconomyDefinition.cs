using UnityEngine;

namespace VaticanDefendor.Content
{
    [CreateAssetMenu(menuName = "VaticanDefendor/Content/Run Economy")]
    public sealed class RunEconomyDefinition : ScriptableObject
    {
        [SerializeField, Tooltip("Устойчивый ID экономики забега.")]
        private string _stableId = "run.economy.default";

        [SerializeField, Min(0), Tooltip("Начальное дерево текущего забега, не постоянного профиля.")]
        [UnityEngine.Serialization.FormerlySerializedAs("_initialGold")]
        private int _initialWood = 150;

        [SerializeField, Min(0), Tooltip("Дерево за первое подтверждённое убийство цели.")]
        [UnityEngine.Serialization.FormerlySerializedAs("_goldPerKill")]
        private int _woodPerKill = 8;

        [SerializeField, Min(0), Tooltip("Опыт забега за первое подтверждённое убийство цели.")]
        private int _experiencePerKill = 18;

        public string StableId => _stableId;
        public int InitialWood => _initialWood;
        public int WoodPerKill => _woodPerKill;
        public int ExperiencePerKill => _experiencePerKill;
    }
}
