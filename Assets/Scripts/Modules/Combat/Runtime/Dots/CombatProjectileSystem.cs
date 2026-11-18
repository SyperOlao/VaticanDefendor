using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;
using VaticanDefendor.Navigation;
using VaticanDefendor.Session;

namespace VaticanDefendor.Combat
{
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(CombatSimulationGroup))]
    [UpdateAfter(typeof(EnemyCombatResolutionSystem))]
    public partial class CombatProjectileSystem : SystemBase
    {
        private EntityQuery _actors;

        protected override void OnCreate()
        {
            RequireForUpdate(GetEntityQuery(typeof(CombatFeelSettings)));
            RequireForUpdate<CombatProjectileState>();
            _actors = GetEntityQuery(typeof(CombatHealth), typeof(LocalTransform));
        }

        protected override void OnUpdate()
        {
            RunActivityState Activity = SystemAPI.GetSingleton<RunActivityState>();
            if (Activity.Paused != 0 || Activity.Terminal != 0)
            {
                return;
            }
            bool HasProjectiles = false;
            foreach (DynamicBuffer<CombatProjectileState> Buffer in SystemAPI.Query<DynamicBuffer<CombatProjectileState>>().WithAll<ChurchTag, Simulate>())
            {
                if (Buffer.Length == 0) continue;
                HasProjectiles = true;
                break;
            }
            if (!HasProjectiles) return;
            CombatFeelConfiguration Feel = SystemAPI.ManagedAPI.GetSingleton<CombatFeelSettings>().Configuration;
            NavigationFlowSystem Navigation = World.GetExistingSystemManaged<NavigationFlowSystem>();
            using NativeArray<Entity> Actors = _actors.ToEntityArray(Allocator.Temp);
            foreach (DynamicBuffer<CombatProjectileState> ProjectileBuffer in SystemAPI.Query<DynamicBuffer<CombatProjectileState>>().WithAll<ChurchTag, Simulate>())
            {
                DynamicBuffer<CombatProjectileState> Projectiles = ProjectileBuffer;
                for (int Index = Projectiles.Length - 1; Index >= 0; Index--)
                {
                    CombatProjectileState Projectile = Projectiles[Index];
                    float Travel = math.min(Projectile.Remaining, Projectile.Speed * SystemAPI.Time.DeltaTime);
                    float2 Previous = Projectile.Position;
                    float2 Next = Previous + Projectile.Direction * Travel;
                    bool bDestroyed = false;
                    bool bWardImpact = false;
                    float FirstImpact = 1;
                    Entity Victim = Entity.Null;
                    foreach (Entity Actor in Actors)
                    {
                        if (EntityManager.GetComponentData<CombatHealth>(Actor).Dead != 0)
                        {
                            continue;
                        }
                        bool bEnemy = EntityManager.HasComponent<CombatTargetTag>(Actor);
                        bool bHero = EntityManager.HasComponent<HeroActionState>(Actor);
                        float2 Position = EntityManager.GetComponentData<LocalTransform>(Actor).Position.xz;
                        if (Projectile.Friendly == 0 && bHero && EntityManager.HasBuffer<EquippedWeapon>(Actor))
                        {
                            HeroActionState Action = EntityManager.GetComponentData<HeroActionState>(Actor);
                            DynamicBuffer<EquippedWeapon> Weapons = EntityManager.GetBuffer<EquippedWeapon>(Actor, true);
                            if (Action.GuardRemaining > 0 && Action.DefenseSlot < Weapons.Length)
                            {
                                EquippedWeapon Defense = Weapons[Action.DefenseSlot];
                                if (Defense.DefenseAbility == SpecialAbilityKind.Guard && Defense.DefensePulse > 0 &&
                                    TryCircleImpact(Position, Previous, Next, Defense.DefenseRadius, out float WardImpact) &&
                                    WardImpact <= FirstImpact)
                                {
                                    FirstImpact = WardImpact;
                                    bWardImpact = true;
                                    Victim = Entity.Null;
                                }
                            }
                        }
                        if (Projectile.Friendly != 0 && !bEnemy || Projectile.Friendly == 0 && bEnemy)
                        {
                            continue;
                        }
                        if (!bHero && !bEnemy && !EntityManager.HasComponent<ChurchTag>(Actor) &&
                            (!EntityManager.HasComponent<StructureCombatState>(Actor) || EntityManager.GetComponentData<StructureCombatState>(Actor).Level <= 0))
                        {
                            continue;
                        }
                        float Radius = 0.55f;
                        if (EntityManager.HasComponent<StructureCombatState>(Actor) && !bEnemy && !bHero)
                        {
                            Radius = 0.9f;
                        }
                        if (TryCircleImpact(Position, Previous, Next, Radius, out float Weight) &&
                            (Weight < FirstImpact || Weight == FirstImpact && !bWardImpact))
                        {
                            FirstImpact = Weight;
                            bWardImpact = false;
                            Victim = Actor;
                        }
                    }
                    int Steps = math.max(1, (int)math.ceil(Travel / 0.2f));
                    for (int Step = 1; Step <= Steps; Step++)
                    {
                        float Weight = (float)Step / Steps;
                        if (Weight > FirstImpact)
                        {
                            break;
                        }
                        if (Navigation != null && !Navigation.IsStaticWalkable(math.lerp(Previous, Next, Weight)))
                        {
                            bDestroyed = true;
                            break;
                        }
                    }
                    bDestroyed |= bWardImpact;
                    if (!bDestroyed && Victim != Entity.Null)
                    {
                        float Push = Projectile.Push;
                        float Duration = Feel.AutoPushDuration;
                        if (Projectile.Friendly == 0 && EntityManager.HasComponent<HeroActionState>(Victim))
                        {
                            Push = Feel.PlayerHitPush * 0.6f;
                            Duration = Feel.PlayerHitDuration;
                        }
                        CombatDamage.Hit(EntityManager, Victim, Projectile.Damage, Projectile.Direction, Push, Duration, 0, true, Feel, Projectile.Friendly != 0 ? MatchDamageSource.Tower : MatchDamageSource.Unattributed);
                        bDestroyed = true;
                    }
                    Projectile.Position = Next;
                    Projectile.Remaining -= Travel;
                    if (bDestroyed || Projectile.Remaining <= 0)
                    {
                        Projectiles.RemoveAt(Index);
                    }
                    else
                    {
                        Projectiles[Index] = Projectile;
                    }
                }
            }
        }

        private static bool TryCircleImpact(float2 Position, float2 Start, float2 End, float Radius, out float Weight)
        {
            Weight = 0;
            float2 Offset = Start - Position;
            float Constant = math.lengthsq(Offset) - Radius * Radius;
            if (Constant <= 0)
            {
                return true;
            }
            float2 Travel = End - Start;
            float DistanceSquared = math.lengthsq(Travel);
            float Projection = math.dot(Offset, Travel);
            float Discriminant = Projection * Projection - DistanceSquared * Constant;
            if (DistanceSquared <= 0.000001f || Projection >= 0 || Discriminant < 0)
            {
                return false;
            }
            Weight = (-Projection - math.sqrt(Discriminant)) / DistanceSquared;
            return Weight <= 1;
        }
    }
}
