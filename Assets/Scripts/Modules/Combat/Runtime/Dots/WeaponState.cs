using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

namespace VaticanDefendor.Combat
{
    public struct EquippedWeapon : IBufferElementData
    {
        [GhostField] public FixedString64Bytes InstanceId;
        [GhostField] public FixedString64Bytes ConfigurationId;
        [GhostField] public WeaponRarity Rarity;
        [GhostField] public int ModelIndex;
        [GhostField] public int Level;
        [GhostField] public int MatchUpgrades;
        [GhostField] public float Damage;
        [GhostField] public float Interval;
        [GhostField] public float Range;
        [GhostField] public SpecialAbilityKind SpecialAbility;
        [GhostField] public float SpecialMultiplier;
        [GhostField] public float SpecialRadius;
        [GhostField] public float SpecialDuration;
        [GhostField] public float SpecialCooldown;
        [GhostField] public float HalfWidth;
        [GhostField] public float TipWidthScale;
        [GhostField] public float DashDamageMultiplier;
        [GhostField] public float DashDistance;
        [GhostField] public float DashRange;
        [GhostField] public float DashDuration;
        [GhostField] public float SpecialDistance;
        [GhostField] public float FinishDamageMultiplier;
        [GhostField] public float FinishRadius;
        [GhostField] public float SpecialPush;
        [GhostField] public float LaunchHeight;
        [GhostField] public float JumpHeight;
        [GhostField] public SpecialAbilityKind DefenseAbility;
        [GhostField] public float DefenseRadius;
        [GhostField] public float DefensePush;
        [GhostField] public float DefenseDuration;
        [GhostField] public float DefenseShield;
        [GhostField] public float DefenseHealing;
        [GhostField] public float DefenseStamina;
        [GhostField] public float DefenseDamageBonus;
        [GhostField] public float DefenseDamageDuration;
        [GhostField] public float DefensePulse;


    }

    [System.Serializable]
    public struct HeroActionState : IComponentData
    {
        [GhostField] public byte ActiveSlot;
        [GhostField] public byte Gathering;
        [GhostField] public float2 Facing;
        [GhostField] public float AttackRemaining;
        [GhostField] public float SpecialRemaining;
        [GhostField] public float SpecialVisualRemaining;
        [GhostField] public int SpecialWeaponIndex;
        [GhostField] public float SpecialVisualRadius;
        [GhostField] public float DashRemaining;
        [GhostField] public float GuardRemaining;
        [GhostField] public float RespawnRemaining;
        [GhostField] public int GatheringTarget;
        [GhostField] public float GatheringProgress;
        [GhostField] public uint AttackSequence;
        [GhostField] public float Combo;
        [GhostField] public float ChargeProgress;
        [GhostField] public int HealthUpgrades;
        [GhostField] public float SwingRemaining;
        [GhostField] public float RecoilRemaining;
        [GhostField] public float2 RecoilDirection;
        [GhostField] public float2 PreviousPosition;
        [GhostField] public float2 AttackDirection;
        [GhostField] public float SpecialMotionRemaining;
        [GhostField] public float MotionElapsed;
        [GhostField] public float MotionDuration;
        [GhostField] public float MotionDistance;
        [GhostField] public byte MotionKind;
        [GhostField] public float ComboIdle;
        [GhostField] public float ComboPendingLoss;
        [GhostField] public float ComboRequired;
        [GhostField] public int SpecialUses;
        [GhostField] public float CameraImpactRemaining;
        [GhostField] public uint SpecialSequence;
        [GhostField] public float Shield;
        [GhostField] public byte DefenseSlot;
        [GhostField] public float DefensePulseRemaining;
        [GhostField] public float DamageBonusRemaining;
        [GhostField] public float DamageBonusPercent;
    }

    public struct HeroActionTuning : IComponentData
    {
        public float DashDistance;
        public float DashDuration;
        public float DashStaminaCost;
        public float RespawnSeconds;
        public float ChargeSeconds;
        public float ChurchHealingPerSecond;
        public float HeroHealingPerSecond;
        public float HealingRange;
    }

    public struct RunWeapon : IBufferElementData
    {
        public EquippedWeapon Value;
    }
}

namespace VaticanDefendor.Movement
{
    public struct HeroMoveInput : IInputComponentData
    {
        public float2 Direction;
        public float2 Aim;
        public byte Run;
        public byte SelectedSlot;
        public byte Charging;
        public InputEvent Special;
        public InputEvent Defense;
        public InputEvent Dash;
        public InputEvent Gathering;
    }
}
