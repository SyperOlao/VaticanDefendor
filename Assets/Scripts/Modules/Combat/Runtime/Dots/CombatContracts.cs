using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

namespace VaticanDefendor.Combat
{
    public enum CombatAttackOutcome : byte
    {
        Hit = 1,
        Miss = 2,
        Cooldown = 3,
        InvalidAim = 4,
        NoStamina = 5,
        HeroDead = 6,
        RunFinished = 7
    }

    public struct CombatAttackRequest : IRpcCommand
    {
        public uint Sequence;
        public float2 Aim;
    }

    public struct CombatAttackResultRpc : IRpcCommand
    {
        public uint EventSequence;
        public uint CommandSequence;
        public CombatAttackOutcome Outcome;
        public float DamageApplied;
        public float TargetHealth;
        public float TargetMaximumHealth;
        public byte TargetDied;
        public long RunGold;
        public long RunExperience;
        public int RunKills;
    }

    public struct CombatHealth : IComponentData
    {
        [GhostField] public float Current;
        [GhostField] public float Maximum;
        [GhostField] public byte Dead;
        [GhostField] public uint HitSequence;
    }

    public struct HeroCombatStamina : IComponentData
    {
        [GhostField] public float Current;
        [GhostField] public float Maximum;
    }

    public struct CombatTargetTag : IComponentData { }

    // Zero identifies the fixed practice target; wave spawns use monotonically increasing IDs.
    public struct PersistentTargetId : IComponentData
    {
        public int Value;
    }

    public struct CombatTargetRole : IComponentData
    {
        [GhostField] public byte AgentControlled;
    }

    public struct EnemyCombatTuning : IComponentData
    {
        public float Damage;
        public float AttackRadiusMeters;
        public float WindupSeconds;
        public float RecoverySeconds;
    }

    public struct EnemyCombatConcurrency : IComponentData
    {
        public int MaximumSimultaneousWindups;
    }

    public struct EnemyCombatAttackState : IComponentData
    {
        public Entity Target;
        public double PhaseEndsSeconds;
        public byte Phase;
    }

    public struct EnemyAttackIntent : IBufferElementData
    {
        public Entity Target;
    }

    public struct CombatDeathLifecycle : IComponentData
    {
        public float DespawnDelaySeconds;
        public double DespawnAtSeconds;
        public byte Scheduled;
    }

    public struct HeroCombatAttackState : IComponentData
    {
        public uint LastProcessedCommandSequence;
        public double NextAllowedTime;
    }

    public struct CombatRunTuning : IComponentData
    {
        public float Damage;
        public float IntervalSeconds;
        public float RangeMeters;
        public float HitPaddingMeters;
        public float StaminaCost;
        public float StaminaRecoveryPerSecond;
        public float HeroMaximumHealth;
        public float MaximumStamina;
        public float TargetMaximumHealth;
        public float TargetSpawnForwardMeters;
    }

    public struct CombatRunState : IComponentData
    {
        public uint NextEventSequence;
    }

    public struct CombatAttackCommand : IBufferElementData
    {
        public uint Sequence;
        public float2 Aim;
    }

    public struct CombatAttackResult : IBufferElementData
    {
        public uint EventSequence;
        public uint CommandSequence;
        public CombatAttackOutcome Outcome;
        public float DamageApplied;
        public float TargetHealth;
        public float TargetMaximumHealth;
        public byte TargetDied;
        public long RunGold;
        public long RunExperience;
        public int RunKills;
    }
}
