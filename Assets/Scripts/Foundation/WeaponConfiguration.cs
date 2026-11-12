using System;

namespace VaticanDefendor
{
    public enum WeaponRarity : byte
    {
        Common,
        Rare,
        Epic,
        Legendary
    }

    public enum SpecialAbilityKind : byte
    {
        None,
        SweepingStrike,
        Thrust,
        Shockwave,
        Guard,
        HealingPulse
    }

    [Serializable]
    public sealed class WeaponConfiguration
    {
        public string Id;
        public string Name;
        public string IconUrl;
        public string ImageUrl;
        public WeaponRarity Rarity;
        public int ModelIndex;
        public float Damage;
        public float AttackInterval;
        public float Range;
        public float DamagePerLevel;
        public float SpeedPerLevel;
        public SpecialAbilityKind SpecialAbility;
        public float SpecialDamageMultiplier;
        public float SpecialRadius;
        public float SpecialDuration;
        public float SpecialCooldown;
        public float HalfWidth;
        public float TipWidthScale;
        public float DashDamageMultiplier;
        public float DashDistance;
        public float DashRange;
        public float DashDuration;
        public float SpecialDistance;
        public float FinishDamageMultiplier;
        public float FinishRadius;
        public float SpecialPush;
        public float LaunchHeight;
        public float JumpHeight;
        public SpecialAbilityKind DefenseAbility;
        public float DefenseRadius;
        public float DefensePush;
        public float DefenseDuration;
        public float DefenseShield;
        public float DefenseHealing;
        public float DefenseStamina;
        public float DefenseDamageBonus;
        public float DefenseDamageDuration;
        public float DefensePulse;
        public int MaximumLevel;
        public int ExperiencePerLevel;

        public float CalculateDamage(int Level)
        {
            return Damage * (1 + DamagePerLevel * (Level - 1));
        }

        public float CalculateAttackInterval(int Level)
        {
            return AttackInterval / (1 + SpeedPerLevel * (Level - 1));
        }

        public void Validate()
        {
            foreach (float Value in new[] { HalfWidth, TipWidthScale, DashDamageMultiplier, DashDistance,
                DashRange, DashDuration, SpecialDistance, FinishDamageMultiplier, FinishRadius, SpecialPush, LaunchHeight, JumpHeight,
                DefenseRadius, DefensePush, DefenseDuration, DefenseShield, DefenseHealing, DefenseStamina, DefenseDamageBonus, DefenseDamageDuration, DefensePulse })
            {
                if (!float.IsFinite(Value) || Value < 0)
                {
                    throw new ArgumentException("Invalid weapon combat parameter: " + Id);
                }
            }
            if (string.IsNullOrWhiteSpace(Id) || string.IsNullOrWhiteSpace(Name) ||
                !float.IsFinite(Damage) || Damage <= 0 ||
                !float.IsFinite(AttackInterval) || AttackInterval <= 0 ||
                !float.IsFinite(Range) || Range <= 0 ||
                !float.IsFinite(DamagePerLevel) || DamagePerLevel < 0 ||
                !float.IsFinite(SpeedPerLevel) || SpeedPerLevel < 0 ||
                !float.IsFinite(SpecialDamageMultiplier) || SpecialDamageMultiplier < 0 ||
                !float.IsFinite(SpecialRadius) || SpecialRadius < 0 ||
                !float.IsFinite(SpecialDuration) || SpecialDuration < 0 ||
                !float.IsFinite(SpecialCooldown) || SpecialCooldown <= 0 ||
                MaximumLevel < 1 || ExperiencePerLevel < 1 || ModelIndex < 0 || ModelIndex > 2 ||
                HalfWidth <= 0 || TipWidthScale <= 0 || DashDamageMultiplier <= 0 || DashDistance <= 0 || DashRange <= 0 || DashDuration <= 0 ||
                SpecialAbility != SpecialAbilityKind.None && SpecialDuration <= 0 ||
                DefenseAbility != SpecialAbilityKind.None && (DefenseDuration <= 0 || DefenseRadius <= 0) ||
                !Enum.IsDefined(typeof(WeaponRarity), Rarity) ||
                !Enum.IsDefined(typeof(SpecialAbilityKind), SpecialAbility) ||
                !Enum.IsDefined(typeof(SpecialAbilityKind), DefenseAbility))
            {
                throw new ArgumentException("Weapon configuration is invalid: " + Id);
            }
        }
    }

    [Serializable]
    public sealed class OwnedWeapon
    {
        public string InstanceId;
        public string ConfigurationId;
        public int Level;
        public int Experience;
    }

    [Serializable]
    public sealed class WeaponInventory
    {
        public WeaponConfiguration[] Configurations;
        public OwnedWeapon[] Weapons;
        public string[] Loadout;
        public int Revision;
    }
}
