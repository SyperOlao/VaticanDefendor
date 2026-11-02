using UnityEngine;

namespace VaticanDefendor.Content
{
    public sealed class ContentSelection : ScriptableObject
    {
        [SerializeField, Tooltip("Проверенный release, включённый в эту сборку. Не содержит удостоверений или секретов.")]
        private ContentRelease _release;

        public ContentRelease Release => _release;
    }
}
