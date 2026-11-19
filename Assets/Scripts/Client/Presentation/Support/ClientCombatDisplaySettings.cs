#if !UNITY_SERVER || UNITY_EDITOR
using UnityEngine;

namespace VaticanDefendor.Client.Presentation
{
    public static class ClientCombatDisplaySettings
    {
        private const string DamageNumbersKey = "VaticanDefendor.Client.DamageNumbers";
        private const string DamageFlashKey = "VaticanDefendor.Client.DamageFlash";
        private const string CameraShakeKey = "VaticanDefendor.Client.CameraShake";

        private static bool _damageNumbers = Read(DamageNumbersKey);
        private static bool _damageFlash = Read(DamageFlashKey);
        private static bool _cameraShake = Read(CameraShakeKey);

        public static bool DamageNumbers
        {
            get => _damageNumbers;
            set => Save(ref _damageNumbers, DamageNumbersKey, value);
        }

        public static bool DamageFlash
        {
            get => _damageFlash;
            set => Save(ref _damageFlash, DamageFlashKey, value);
        }

        public static bool CameraShake
        {
            get => _cameraShake;
            set => Save(ref _cameraShake, CameraShakeKey, value);
        }

        private static bool Read(string key) => PlayerPrefs.GetInt(key, 1) != 0;

        private static void Save(ref bool current, string key, bool value)
        {
            current = value;
            PlayerPrefs.SetInt(key, value ? 1 : 0);
            PlayerPrefs.Save();
        }
    }
}
#endif
