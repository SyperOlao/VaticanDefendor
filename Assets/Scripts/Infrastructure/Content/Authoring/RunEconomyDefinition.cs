using UnityEngine;

namespace VaticanDefendor.Content
{
    [CreateAssetMenu(menuName = "VaticanDefendor/Content/Run Economy")]
    public sealed class RunEconomyDefinition : ScriptableObject
    {
        [SerializeField, Tooltip("Устойчивый ID экономики забега.")]
        private string _stableId = "run.economy.default";

        [SerializeField, Min(0), Tooltip("Начальное золото текущего забега, не постоянного профиля.")]
        private int _initialGold = 150;

        [SerializeField, Min(0), Tooltip("Золото за первое подтверждённое убийство цели.")]
        private int _goldPerKill = 8;

        [SerializeField, Min(0), Tooltip("Опыт забега за первое подтверждённое убийство цели.")]
        private int _experiencePerKill = 18;

        public string StableId => _stableId;
        public int InitialGold => _initialGold;
        public int GoldPerKill => _goldPerKill;
        public int ExperiencePerKill => _experiencePerKill;
    }
}
