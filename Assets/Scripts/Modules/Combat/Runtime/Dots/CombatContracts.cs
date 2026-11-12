using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

namespace VaticanDefendor.Combat
{
    public struct DestructibleTree : Unity.Entities.IComponentData { }



    [System.Serializable]
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


    [System.Serializable]
    public struct CombatImpactState : IComponentData
    {
        [GhostField] public float2 Velocity;
        [GhostField] public float PushRemaining;
        [GhostField] public float PushDuration;
        [GhostField] public float LaunchHeight;
        [GhostField] public float FlashRemaining;
        [GhostField] public float DeathElapsed;
        [GhostField] public float LastDamage;
        [GhostField] public float2 Direction;
    }

    [System.Serializable]
    public struct CombatHitState : IComponentData
    {
        [GhostField] public Entity Attacker;
        [GhostField] public uint MovementSequence;
        [GhostField] public uint FinishSequence;
    }

    public sealed class CombatFeelSettings : IComponentData
    {
        public CombatFeelConfiguration Configuration;
    }

    public struct CombatTargetTag : IComponentData { }

    public struct PersistentTargetId : IComponentData
    {
        [GhostField] public int Value;
    }

    public struct CombatTargetRole : IComponentData
    {
        [GhostField] public byte AgentControlled;
    }

    [System.Serializable]
    public struct EnemyCombatTuning : IComponentData
    {
        [GhostField] public int Kind;
        [GhostField] public int ArmorTier;
        [GhostField] public float HalfWidth;
        [GhostField] public float MinimumRange;
        [GhostField] public float DashDistance;
        [GhostField] public float DashDuration;
        [GhostField] public float ThrustLength;
        [GhostField] public float ProjectileSpeed;
        [GhostField] public float Damage;
        [GhostField] public float AttackRadiusMeters;
        [GhostField] public float WindupSeconds;
        [GhostField] public float RecoverySeconds;
    }

    public struct EnemyCombatConcurrency : IComponentData
    {
        public int MaximumSimultaneousWindups;
    }

    public struct EnemyCombatAttackState : IComponentData
    {
        [GhostField] public Entity Target;
        [GhostField] public double PhaseEndsSeconds;
        [GhostField] public float Remaining;
        [GhostField] public byte Phase;
        [GhostField] public float2 Origin;
        [GhostField] public float2 Direction;
        [GhostField] public uint Sequence;
    }

    [System.Serializable]
    public struct CombatProjectileState : IBufferElementData
    {
        [GhostField] public float2 Position;
        [GhostField] public float2 Direction;
        [GhostField] public float Speed;
        [GhostField] public float Damage;
        [GhostField] public float Remaining;
        [GhostField] public float Push;
        [GhostField] public byte Friendly;
        [GhostField] public uint Sequence;
        [GhostField] public int SourceId;
    }

    public struct CombatAttackVictim : IBufferElementData
    {
        [GhostField] public Entity Target;
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


}
