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
    [UpdateAfter(typeof(StructureCombatSystem))]
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
                        if (Projectile.Friendly == 0 && bHero)
                        {
                            HeroActionState Action = EntityManager.GetComponentData<HeroActionState>(Actor);
                            if ((Action.GuardRemaining > 0 || Action.MotionKind == 2 && Action.SpecialWeaponIndex != 2) &&
                                SegmentDistance(Position, Previous, Next, out _) <= Action.SpecialVisualRadius)
                            {
                                bDestroyed = true;
                                break;
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
                        float Distance = SegmentDistance(Position, Previous, Next, out float Weight);
                        if (Distance <= Radius && Weight <= FirstImpact)
                        {
                            FirstImpact = Weight;
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
                    if (!bDestroyed && Victim != Entity.Null)
                    {
                        float Push = Projectile.Push;
                        float Duration = Feel.AutoPushDuration;
                        if (Projectile.Friendly == 0 && EntityManager.HasComponent<HeroActionState>(Victim))
                        {
                            Push = Feel.PlayerHitPush * 0.6f;
                            Duration = Feel.PlayerHitDuration;
                        }
                        CombatDamage.Hit(EntityManager, Victim, Projectile.Damage, Projectile.Direction, Push, Duration, 0, true, Feel);
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

        private static float SegmentDistance(float2 Position, float2 Start, float2 End, out float Weight)
        {
            Weight = 0;
            float Length = math.lengthsq(End - Start);
            if (Length > 0.000001f)
            {
                Weight = math.saturate(math.dot(Position - Start, End - Start) / Length);
            }
            return math.distance(Position, math.lerp(Start, End, Weight));
        }
    }
}
