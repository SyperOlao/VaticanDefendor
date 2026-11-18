using System;

namespace VaticanDefendor
{
    [Serializable]
    public sealed class CombatFeelConfiguration
    {
        public float AutoPush = 1.2f;
        public float AutoPushDuration = 0.2f;
        public float AutoRecoilDuration = 0.3f;
        public float AutoForwardSpeed = 0.35f;
        public float DashPush = 6.2f;
        public float DashPushDuration = 0.45f;
        public float DashDamageVariation = 0.15f;
        public float PlayerHitPush = 0.7f;
        public float PlayerHitDuration = 0.18f;
        public float InterruptRecovery = 0.5f;
        public float HitFlashDuration = 0.18f;
        public float CorpseDuration = 0.5f;
        public float SwingDuration = 0.3f;
        public float SpecialFinishDuration = 0.25f;
        public float ComboDamagePerPoint = 50;
        public float FirstSpecialCost = 6;
        public float SubsequentSpecialCost = 12;
        public float SpecialCostIncrease = 1;
        public float ComboIdleSeconds = 3;
        public float ComboDecayFraction = 0.1f;
        public float ComboInjuryFraction = 0.15f;
        public float LowStaminaRecovery = 40;
        public float HitCameraStrength = 0.12f;
        public float SpecialCameraStrength = 0.3f;

        public void Validate()
        {
            foreach (float Value in new[] { AutoPushDuration, AutoRecoilDuration, DashPushDuration,
                PlayerHitDuration, InterruptRecovery, HitFlashDuration, CorpseDuration, SwingDuration,
                SpecialFinishDuration, ComboDamagePerPoint, FirstSpecialCost, SubsequentSpecialCost })
            {
                if (!float.IsFinite(Value) || Value <= 0)
                {
                    throw new ArgumentException("Invalid combat feel duration or cost.");
                }
            }
            foreach (float Value in new[] { AutoPush, DashPush, PlayerHitPush, SpecialCostIncrease,
                ComboIdleSeconds, ComboDecayFraction, ComboInjuryFraction, LowStaminaRecovery,
                HitCameraStrength, SpecialCameraStrength })
            {
                if (!float.IsFinite(Value) || Value < 0)
                {
                    throw new ArgumentException("Invalid combat feel parameter.");
                }
            }
            if (!float.IsFinite(AutoForwardSpeed) || AutoForwardSpeed < 0 || AutoForwardSpeed > 1 ||
                !float.IsFinite(DashDamageVariation) || DashDamageVariation < 0 || DashDamageVariation > 1)
            {
                throw new ArgumentException("Invalid combat feel fraction.");
            }
        }
    }
}
