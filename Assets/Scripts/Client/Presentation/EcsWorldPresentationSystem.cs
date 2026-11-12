#if !UNITY_SERVER || UNITY_EDITOR
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Rendering;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.Rendering;
using VaticanDefendor;
using VaticanDefendor.Combat;
using VaticanDefendor.Construction;
using VaticanDefendor.Production;

public struct WorldVisualChild : ICleanupBufferElementData
{
    public Entity Value;
}

public struct WorldVisualFeedback : IComponentData
{
    public uint HitSequence;
    public uint AttackSequence;
    public uint SpecialSequence;
}

public struct WorldDamageNumber : IComponentData
{
    public float Amount;
    public byte Hero;
}

public struct WorldProjectileVisual : IComponentData
{
    public float2 Direction;
    public byte Friendly;
}

public struct WorldCombatEffect : IComponentData
{
    public byte Kind;
    public float Age;
    public float Duration;
    public float3 Scale;
    public float Expansion;
    public float4 Color;
    public float2 Direction;
    public float3 Velocity;
}

public struct WorldVisualAttached : IComponentData { }

public struct WorldVisualPart : IComponentData
{
    public Entity Owner;
    public float4x4 LocalMatrix;
    public float4 Color;
    public float3 Pivot;
    public int WeaponIndex;
    public int Animation;
}

[WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.Presentation)]
[CreateAfter(typeof(EntitiesGraphicsSystem))]
[UpdateInGroup(typeof(PresentationSystemGroup), OrderFirst = true)]
public partial class EcsWorldPresentationSystem : SystemBase
{
    private CombatRenderCatalog _catalog;
    private RenderMeshArray _meshes;
    private EntityQuery _logical;
    private EntityQuery _cleanup;
    private EntityQuery _effects;
    private EntitiesGraphicsSystem _characterGraphics;
    private readonly Dictionary<Mesh, BatchMeshID> _characterMeshes = new();
    private readonly Dictionary<Material, BatchMaterialID> _characterMaterials = new();
    private readonly Dictionary<(int Source, uint Sequence, byte Friendly), Entity> _projectileVisuals = new();
    private readonly HashSet<(int Source, uint Sequence, byte Friendly)> _activeProjectiles = new();
    private readonly List<(int Source, uint Sequence, byte Friendly)> _removedProjectiles = new();

    protected override void OnCreate()
    {
        RequireForUpdate(GetEntityQuery(typeof(CombatFeelSettings)));
        RequireForUpdate<CharacterVisualPrefab>();
        _effects = GetEntityQuery(typeof(WorldCombatEffect));
        _logical = GetEntityQuery(new EntityQueryDesc
        {
            All = new[] { ComponentType.ReadOnly<LocalTransform>() },
            Any = new[] { ComponentType.ReadOnly<HeroActionState>(), ComponentType.ReadOnly<CombatTargetTag>(), ComponentType.ReadOnly<StructureInstance>() },
            None = new[] { ComponentType.ReadOnly<WorldVisualAttached>() }
        });
        _cleanup = GetEntityQuery(new EntityQueryDesc
        {
            All = new[] { ComponentType.ReadOnly<WorldVisualChild>() },
            None = new[] { ComponentType.ReadOnly<LocalTransform>() }
        });
    }

    protected override void OnDestroy()
    {
        _meshes.Release();
        EntitiesGraphicsSystem Graphics = _characterGraphics;
        if (Graphics != null)
        {
            foreach (BatchMeshID Mesh in _characterMeshes.Values)
            {
                Graphics.UnregisterMesh(Mesh);
            }
            foreach (BatchMaterialID Material in _characterMaterials.Values)
            {
                Graphics.UnregisterMaterial(Material);
            }
        }
    }

    public void Initialize(CombatRenderCatalog Catalog)
    {
        if (_catalog != null)
        {
            return;
        }
        if (Catalog == null || Catalog.Models == null || Catalog.Meshes == null || Catalog.Materials == null)
        {
            throw new System.InvalidOperationException("ECS presentation catalog is required.");
        }
        _catalog = Catalog;
        _meshes = new RenderMeshArray(Catalog.Materials, Catalog.Meshes);
        _meshes.Retain();
        using EntityQuery Map = EntityManager.CreateEntityQuery(typeof(VaticanDefendor.Maps.RiverMapState));
        foreach (CombatStaticDefinition Static in Catalog.StaticObjects)
        {
            if (!Map.IsEmptyIgnoreFilter)
            {
                break;
            }
            if (Static.ModelIndex >= 45)
            {
                AddModel(Entity.Null, Static.ModelIndex, Static.Matrix);
            }
        }
    }

    protected override void OnUpdate()
    {
        if (_catalog == null)
        {
            return;
        }
        if (!_logical.IsEmptyIgnoreFilter || !_cleanup.IsEmptyIgnoreFilter)
        {
            Dependency.Complete();
        }
        using (NativeArray<Entity> Removed = _cleanup.ToEntityArray(Allocator.Temp))
        {
            foreach (Entity Owner in Removed)
            {
                using NativeArray<WorldVisualChild> Children = EntityManager.GetBuffer<WorldVisualChild>(Owner).ToNativeArray(Allocator.Temp);
                foreach (WorldVisualChild Child in Children)
                {
                    if (EntityManager.Exists(Child.Value))
                    {
                        EntityManager.DestroyEntity(Child.Value);
                    }
                }
                EntityManager.RemoveComponent<WorldVisualChild>(Owner);
            }
        }
        using (NativeArray<Entity> Logical = _logical.ToEntityArray(Allocator.Temp))
        {
            foreach (Entity Owner in Logical)
            {
                int Model = 4;
                if (EntityManager.HasComponent<HeroActionState>(Owner))
                {
                    Model = 0;
                }
                else if (EntityManager.HasComponent<EnemyCombatTuning>(Owner))
                {
                    EnemyCombatTuning Enemy = EntityManager.GetComponentData<EnemyCombatTuning>(Owner);
                    Model = 4 + Enemy.Kind * 4 + Enemy.ArmorTier;
                }
                else if (EntityManager.HasComponent<StructureInstance>(Owner))
                {
                    StructureKind Kind = EntityManager.GetComponentData<StructureInstance>(Owner).Kind;
                    if (Kind == 0 || Kind == StructureKind.Tree || Kind == StructureKind.Rock)
                    {
                        continue;
                    }
                    switch (Kind)
                    {
                        case StructureKind.Wall: Model = 21; break;
                        case StructureKind.Tower: Model = 20; break;
                        case StructureKind.Sawmill: Model = 25; break;
                        case StructureKind.Quarry: Model = 50; break;
                        case StructureKind.IronMine: Model = 24; break;
                        case StructureKind.Forge: Model = 24; break;
                        case StructureKind.HealingShrine: Model = 23; break;
                        case StructureKind.Church: Model = 26; break;
                        case StructureKind.Artillery: Model = 42; break;
                        case StructureKind.ResourceDeposit:
                            Model = 50;
                            if (EntityManager.GetComponentData<ResourceNode>(Owner).Kind == ResourceKind.Wood)
                            {
                                Model = 45;
                            }
                            break;
                    }
                }
                EntityManager.AddBuffer<WorldVisualChild>(Owner);
                if (EntityManager.HasComponent<HeroActionState>(Owner) || EntityManager.HasComponent<EnemyCombatTuning>(Owner))
                {
                    int Kind = 0;
                    if (EntityManager.HasComponent<EnemyCombatTuning>(Owner))
                    {
                        Kind = EntityManager.GetComponentData<EnemyCombatTuning>(Owner).Kind + 1;
                    }
                    AddCharacter(Owner, Kind);
                    AddModel(Owner, Model, Matrix4x4.identity, true);
                    if (Kind == 3)
                    {
                        AddModel(Owner, 0, Matrix4x4.identity, true, 1);
                    }
                }
                else
                {
                    AddModel(Owner, Model, Matrix4x4.identity);
                }
                EntityManager.AddComponent<WorldVisualAttached>(Owner);
            }
        }
        Dependency.Complete();
        UpdateEffects();
        UpdateProjectiles();
    }

    private void UpdateProjectiles()
    {
        _activeProjectiles.Clear();
        using EntityQuery Containers = EntityManager.CreateEntityQuery(typeof(CombatProjectileState));
        using NativeArray<Entity> Entities = Containers.ToEntityArray(Allocator.Temp);
        foreach (Entity Container in Entities)
        {
            using NativeArray<CombatProjectileState> Projectiles = EntityManager.GetBuffer<CombatProjectileState>(Container).ToNativeArray(Allocator.Temp);
            foreach (CombatProjectileState Projectile in Projectiles)
            {
                var Key = (Projectile.SourceId, Projectile.Sequence, Projectile.Friendly);
                _activeProjectiles.Add(Key);
                if (!_projectileVisuals.TryGetValue(Key, out Entity Visual))
                {
                    Visual = EntityManager.CreateEntity(typeof(LocalTransform), typeof(WorldProjectileVisual));
                    EntityManager.AddBuffer<WorldVisualChild>(Visual);
                    AddModel(Visual, 29, Matrix4x4.identity);
                    _projectileVisuals.Add(Key, Visual);
                }
                EntityManager.SetComponentData(Visual, LocalTransform.FromPosition(new float3(Projectile.Position.x, 0.9f, Projectile.Position.y)));
                EntityManager.SetComponentData(Visual, new WorldProjectileVisual { Direction = Projectile.Direction, Friendly = Projectile.Friendly });
            }
        }
        _removedProjectiles.Clear();
        foreach (var Pair in _projectileVisuals)
        {
            if (!_activeProjectiles.Contains(Pair.Key))
            {
                Entity Visual = Pair.Value;
                using NativeArray<WorldVisualChild> Children = EntityManager.GetBuffer<WorldVisualChild>(Visual).ToNativeArray(Allocator.Temp);
                foreach (WorldVisualChild Child in Children)
                {
                    EntityManager.DestroyEntity(Child.Value);
                }
                EntityManager.RemoveComponent<WorldVisualChild>(Visual);
                EntityManager.DestroyEntity(Visual);
                _removedProjectiles.Add(Pair.Key);
            }
        }
        foreach (var Key in _removedProjectiles)
        {
            _projectileVisuals.Remove(Key);
        }
    }

    private void UpdateEffects()
    {
        float Delta = UnityEngine.Time.deltaTime;
        using EntityQuery ActivityQuery = EntityManager.CreateEntityQuery(typeof(VaticanDefendor.Session.RunActivityState));
        if (!ActivityQuery.IsEmptyIgnoreFilter)
        {
            var Activity = ActivityQuery.GetSingleton<VaticanDefendor.Session.RunActivityState>();
            if (Activity.Paused != 0 || Activity.Terminal != 0)
            {
                Delta = 0;
            }
        }
        using (NativeArray<Entity> Effects = _effects.ToEntityArray(Allocator.Temp))
        {
            foreach (Entity EffectEntity in Effects)
            {
                WorldCombatEffect Effect = EntityManager.GetComponentData<WorldCombatEffect>(EffectEntity);
                Effect.Age += Delta;
                if (Effect.Age >= Effect.Duration)
                {
                    using NativeArray<WorldVisualChild> Children = EntityManager.GetBuffer<WorldVisualChild>(EffectEntity).ToNativeArray(Allocator.Temp);
                    foreach (WorldVisualChild Child in Children)
                    {
                        EntityManager.DestroyEntity(Child.Value);
                    }
                    EntityManager.RemoveComponent<WorldVisualChild>(EffectEntity);
                    EntityManager.DestroyEntity(EffectEntity);
                    continue;
                }
                LocalTransform Position = EntityManager.GetComponentData<LocalTransform>(EffectEntity);
                Position.Position += Effect.Velocity * Delta;
                if (Effect.Kind == 1)
                {
                    Effect.Velocity.y -= 8 * Delta;
                }
                EntityManager.SetComponentData(EffectEntity, Position);
                EntityManager.SetComponentData(EffectEntity, Effect);
            }
        }
        using EntityQuery Actors = EntityManager.CreateEntityQuery(typeof(CombatHealth), typeof(LocalTransform));
        using NativeArray<Entity> Entities = Actors.ToEntityArray(Allocator.Temp);
        foreach (Entity Actor in Entities)
        {
            CombatHealth Health = EntityManager.GetComponentData<CombatHealth>(Actor);
            HeroActionState Action = default;
            bool bHero = EntityManager.HasComponent<HeroActionState>(Actor);
            if (bHero)
            {
                Action = EntityManager.GetComponentData<HeroActionState>(Actor);
            }
            if (!EntityManager.HasComponent<WorldVisualFeedback>(Actor))
            {
                EntityManager.AddComponentData(Actor, new WorldVisualFeedback
                {
                    HitSequence = Health.HitSequence, AttackSequence = Action.AttackSequence, SpecialSequence = Action.SpecialSequence
                });
                continue;
            }
            WorldVisualFeedback Previous = EntityManager.GetComponentData<WorldVisualFeedback>(Actor);
            float3 Position = EntityManager.GetComponentData<LocalTransform>(Actor).Position;
            if (Health.HitSequence > Previous.HitSequence && EntityManager.HasComponent<CombatImpactState>(Actor))
            {
                CombatImpactState Impact = EntityManager.GetComponentData<CombatImpactState>(Actor);
                float4 Color = new float4(1, 0.8f, 0.3f, 1);
                if (bHero)
                {
                    Color = new float4(1, 0.25f, 0.12f, 1);
                }
                if (_effects.CalculateEntityCount() < 128)
                {
                    Entity Number = EntityManager.CreateEntity(typeof(LocalTransform), typeof(WorldCombatEffect), typeof(WorldDamageNumber));
                    EntityManager.SetComponentData(Number, LocalTransform.FromPosition(Position + new float3(0, 1.8f, 0)));
                    byte HeroNumber = 0;
                    if (bHero)
                    {
                        HeroNumber = 1;
                    }
                    EntityManager.SetComponentData(Number, new WorldDamageNumber { Amount = Impact.LastDamage, Hero = HeroNumber });
                    EntityManager.SetComponentData(Number, new WorldCombatEffect { Kind = 2, Duration = 0.65f, Velocity = new float3(0, 1.4f, 0) });
                    EntityManager.AddBuffer<WorldVisualChild>(Number);
                }
                for (int Index = 0; Index < 4; Index++)
                {
                    float Angle = Index * math.PI * 0.5f;
                    SpawnEffect(41, Position + new float3(0, 0.8f, 0), new WorldCombatEffect
                    {
                        Kind = 1, Duration = 0.22f, Scale = new float3(0.45f), Color = Color,
                        Direction = Impact.Direction,
                        Velocity = new float3(math.sin(Angle) * 2, 2, math.cos(Angle) * 2)
                    });
                }
            }
            if (bHero && Action.AttackSequence > Previous.AttackSequence && Action.MotionKind != 2)
            {
                DynamicBuffer<EquippedWeapon> Weapons = EntityManager.GetBuffer<EquippedWeapon>(Actor);
                EquippedWeapon Weapon = Weapons[Action.ActiveSlot];
                float Radius = Weapon.Range;
                if (Action.MotionKind == 1)
                {
                    Radius = Weapon.DashRange;
                }
                float Width = Radius;
                if (Weapon.ModelIndex == 1)
                {
                    Width = Weapon.HalfWidth;
                }
                SpawnEffect(31 + Weapon.ModelIndex, Position + new float3(0, 0.1f, 0), new WorldCombatEffect
                {
                    Duration = 0.16f, Scale = new float3(Width, 1, Radius),
                    Color = WeaponColor(Weapon.ModelIndex), Direction = Action.Facing
                });
            }
            if (bHero && Action.SpecialSequence > Previous.SpecialSequence)
            {
                SpawnEffect(30, Position + new float3(0, 0.1f, 0), new WorldCombatEffect
                {
                    Duration = 0.4f, Expansion = 0.5f, Scale = new float3(Action.SpecialVisualRadius, 1, Action.SpecialVisualRadius),
                    Color = WeaponColor(Action.SpecialWeaponIndex), Direction = Action.Facing
                });
                for (int Index = 0; Index < 12; Index++)
                {
                    float Angle = Index * math.PI / 6;
                    SpawnEffect(41, Position + new float3(0, 0.4f, 0), new WorldCombatEffect
                    {
                        Kind = 1, Duration = 0.5f, Scale = new float3(0.7f), Color = WeaponColor(Action.SpecialWeaponIndex),
                        Direction = Action.Facing,
                        Velocity = new float3(math.sin(Angle), 0.6f, math.cos(Angle)) * Action.SpecialVisualRadius * 2
                    });
                }
            }
            Previous.HitSequence = math.max(Previous.HitSequence, Health.HitSequence);
            Previous.AttackSequence = math.max(Previous.AttackSequence, Action.AttackSequence);
            Previous.SpecialSequence = math.max(Previous.SpecialSequence, Action.SpecialSequence);
            EntityManager.SetComponentData(Actor, Previous);
        }
    }

    private static float4 WeaponColor(int ModelIndex)
    {
        if (ModelIndex == 1)
        {
            return new float4(0.25f, 0.8f, 1, 0.65f);
        }
        if (ModelIndex == 2)
        {
            return new float4(1, 0.35f, 0.25f, 0.65f);
        }
        return new float4(1, 0.67f, 0.24f, 0.65f);
    }

    private void SpawnEffect(int ModelIndex, float3 Position, WorldCombatEffect Effect)
    {
        if (_effects.CalculateEntityCount() >= 128)
        {
            return;
        }
        Entity EffectEntity = EntityManager.CreateEntity(typeof(LocalTransform), typeof(WorldCombatEffect));
        EntityManager.SetComponentData(EffectEntity, LocalTransform.FromPosition(Position));
        EntityManager.SetComponentData(EffectEntity, Effect);
        EntityManager.AddBuffer<WorldVisualChild>(EffectEntity);
        AddModel(EffectEntity, ModelIndex, Matrix4x4.identity);
    }

    private void AddCharacter(Entity Owner, int Kind)
    {
        using EntityQuery Collection = EntityManager.CreateEntityQuery(typeof(CharacterVisualPrefab));
        using NativeArray<CharacterVisualPrefab> Entries = Collection.GetSingletonBuffer<CharacterVisualPrefab>().ToNativeArray(Allocator.Temp);
        CharacterVisualPrefab Entry = default;
        foreach (CharacterVisualPrefab Candidate in Entries)
        {
            if (Candidate.Kind == Kind)
            {
                Entry = Candidate;
                break;
            }
        }
        if (Entry.Value == Entity.Null)
        {
            throw new System.InvalidOperationException("Character visual prefab is missing for kind: " + Kind);
        }
        Entity Root = EntityManager.Instantiate(Entry.Value);
        CharacterVisualRoot State = EntityManager.GetComponentData<CharacterVisualRoot>(Root);
        State.Owner = Owner;
        State.Color = Entry.Color;
        State.Scale = Entry.Scale;
        EntityManager.SetComponentData(Root, State);
        EntityManager.AddComponentData(Owner, new CharacterVisualInstance { Root = Root });
        using NativeArray<LinkedEntityGroup> Linked = EntityManager.GetBuffer<LinkedEntityGroup>(Root).ToNativeArray(Allocator.Temp);
        EntitiesGraphicsSystem Graphics = World.GetExistingSystemManaged<EntitiesGraphicsSystem>();
        _characterGraphics = Graphics;
        foreach (LinkedEntityGroup Child in Linked)
        {
            if (EntityManager.HasComponent<LocalToWorld>(Child.Value))
            {
                EntityManager.AddComponentData(Child.Value, new CharacterRenderTransform
                {
                    Root = Root, LocalMatrix = EntityManager.GetComponentData<LocalToWorld>(Child.Value).Value
                });
            }
            if (EntityManager.HasComponent<MaterialMeshInfo>(Child.Value))
            {
                MaterialMeshInfo Information = EntityManager.GetComponentData<MaterialMeshInfo>(Child.Value);
                RenderMeshArray Meshes = EntityManager.GetSharedComponent<RenderMeshArray>(Child.Value);
                Mesh Mesh = Meshes.GetMesh(Information);
                Material Material = Meshes.GetMaterial(Information);
                if (!_characterMeshes.TryGetValue(Mesh, out BatchMeshID MeshId))
                {
                    MeshId = Graphics.RegisterMesh(Mesh);
                    _characterMeshes.Add(Mesh, MeshId);
                }
                if (!_characterMaterials.TryGetValue(Material, out BatchMaterialID MaterialId))
                {
                    MaterialId = Graphics.RegisterMaterial(Material);
                    _characterMaterials.Add(Material, MaterialId);
                }
                EntityManager.SetComponentData(Child.Value, new MaterialMeshInfo(MaterialId, MeshId, Information.SubMesh));
                RenderBounds Bounds = EntityManager.GetComponentData<RenderBounds>(Child.Value);
                Bounds.Value.Extents += new float3(1.5f);
                EntityManager.SetComponentData(Child.Value, Bounds);
                if (!EntityManager.HasComponent<URPMaterialPropertyBaseColor>(Child.Value))
                {
                    EntityManager.AddComponentData(Child.Value, new URPMaterialPropertyBaseColor { Value = new float4(1) });
                }
            }
        }
        EntityManager.GetBuffer<WorldVisualChild>(Owner).Add(new WorldVisualChild { Value = Root });
    }

    private void AddModel(Entity Owner, int ModelIndex, Matrix4x4 Matrix, bool bCharacterParts = false, int WeaponFilter = -1)
    {
        CombatRenderModelDefinition Model = _catalog.Models[ModelIndex];
        foreach (CombatRenderPartDefinition Part in Model.Parts)
        {
            if (bCharacterParts && Part.WeaponIndex < 0 && Part.Animation < 4)
            {
                continue;
            }
            if (WeaponFilter >= 0 && Part.WeaponIndex != WeaponFilter)
            {
                continue;
            }
            Entity Child = EntityManager.CreateEntity();
            RenderMeshUtility.AddComponents(Child, EntityManager,
                new RenderMeshDescription(ShadowCastingMode.Off, true), _meshes,
                MaterialMeshInfo.FromRenderMeshArrayIndices(Part.Materials[0], Part.Mesh, (ushort)Part.Submesh));
            EntityManager.AddComponentData(Child, new WorldVisualPart
            {
                Owner = Owner,
                LocalMatrix = math.mul((float4x4)Matrix, (float4x4)Part.Matrix),
                Color = new float4(Part.Color.r, Part.Color.g, Part.Color.b, Part.Color.a),
                Pivot = Part.Pivot,
                WeaponIndex = Part.WeaponIndex,
                Animation = Part.Animation
            });
            float4 Tint = new float4(Part.Color.r, Part.Color.g, Part.Color.b, Part.Color.a);
            if (QualitySettings.activeColorSpace == ColorSpace.Linear)
            {
                Tint = CombatRenderColor.ToLinear(Tint);
            }
            EntityManager.AddComponentData(Child, new URPMaterialPropertyBaseColor { Value = Tint });
            EntityManager.SetComponentData(Child, new LocalToWorld { Value = math.mul((float4x4)Matrix, (float4x4)Part.Matrix) });
            if (Owner != Entity.Null)
            {
                EntityManager.GetBuffer<WorldVisualChild>(Owner).Add(new WorldVisualChild { Value = Child });
            }
        }
    }
}

[WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.Presentation)]
[UpdateInGroup(typeof(PresentationSystemGroup))]
[UpdateAfter(typeof(CharacterAnimationSystem))]
[UpdateBefore(typeof(EntitiesGraphicsSystem))]
public partial class WorldVisualPartSystem : SystemBase
{
    protected override void OnCreate()
    {
        RequireForUpdate<CombatFeelSettings>();
    }

    protected override void OnUpdate()
    {
        Dependency = new WorldVisualAnimationJob
        {
            Instances = GetComponentLookup<CharacterVisualInstance>(true),
            Hands = GetComponentLookup<CharacterHandPose>(true),
            Positions = GetComponentLookup<LocalTransform>(true),
            Health = GetComponentLookup<CombatHealth>(true),
            Actions = GetComponentLookup<HeroActionState>(true),
            Impacts = GetComponentLookup<CombatImpactState>(true),
            Effects = GetComponentLookup<WorldCombatEffect>(true),
            Projectiles = GetComponentLookup<WorldProjectileVisual>(true),
            FlashDuration = SystemAPI.ManagedAPI.GetSingleton<CombatFeelSettings>().Configuration.HitFlashDuration,
            CorpseDuration = SystemAPI.ManagedAPI.GetSingleton<CombatFeelSettings>().Configuration.CorpseDuration,
            SwingDuration = SystemAPI.ManagedAPI.GetSingleton<CombatFeelSettings>().Configuration.SwingDuration,
            FinishDuration = SystemAPI.ManagedAPI.GetSingleton<CombatFeelSettings>().Configuration.SpecialFinishDuration,
            Weapons = GetBufferLookup<EquippedWeapon>(true),
            Nodes = GetComponentLookup<ResourceNode>(true),
            EnemyAttacks = GetComponentLookup<EnemyCombatAttackState>(true),
            EnemyTuning = GetComponentLookup<EnemyCombatTuning>(true),
            bLinear = QualitySettings.activeColorSpace == ColorSpace.Linear
        }.ScheduleParallel(Dependency);
    }

}

[BurstCompile]
public partial struct WorldVisualAnimationJob : IJobEntity
{
    [ReadOnly] public ComponentLookup<CharacterVisualInstance> Instances;
    [ReadOnly] public ComponentLookup<CharacterHandPose> Hands;
    [ReadOnly] public ComponentLookup<LocalTransform> Positions;
    [ReadOnly] public ComponentLookup<CombatHealth> Health;
    [ReadOnly] public ComponentLookup<HeroActionState> Actions;
    [ReadOnly] public BufferLookup<EquippedWeapon> Weapons;
    [ReadOnly] public ComponentLookup<ResourceNode> Nodes;
    [ReadOnly] public ComponentLookup<EnemyCombatAttackState> EnemyAttacks;
    [ReadOnly] public ComponentLookup<EnemyCombatTuning> EnemyTuning;
    [ReadOnly] public ComponentLookup<CombatImpactState> Impacts;
    [ReadOnly] public ComponentLookup<WorldCombatEffect> Effects;
    [ReadOnly] public ComponentLookup<WorldProjectileVisual> Projectiles;
    public float FlashDuration;
    public float CorpseDuration;
    public float SwingDuration;
    public float FinishDuration;
    public bool bLinear;

    private void Execute(ref LocalToWorld Transform, ref URPMaterialPropertyBaseColor Color, in WorldVisualPart Part)
    {
        if (Part.Owner == Entity.Null)
        {
            return;
        }
        bool bVisible = Positions.HasComponent(Part.Owner);
        float4 Tint = Part.Color;
        float4x4 Local = Part.LocalMatrix;
        quaternion Rotation = quaternion.identity;
        float3 Position = float3.zero;
        float3 BodyScale = new float3(1);
        float DeathProgress = 0;
        if (bVisible)
        {
            Position = Positions[Part.Owner].Position;
            if (Health.HasComponent(Part.Owner))
            {
                if (Health[Part.Owner].Dead != 0)
                {
                    bVisible = false;
                    if (Impacts.HasComponent(Part.Owner) && EnemyAttacks.HasComponent(Part.Owner))
                    {
                        DeathProgress = math.saturate(Impacts[Part.Owner].DeathElapsed / CorpseDuration);
                        bVisible = DeathProgress < 1;
                        BodyScale *= 1 - DeathProgress * 0.8f;
                    }
                }
            }
            if (Projectiles.HasComponent(Part.Owner))
            {
                WorldProjectileVisual Projectile = Projectiles[Part.Owner];
                Rotation = quaternion.LookRotationSafe(new float3(Projectile.Direction.x, 0, Projectile.Direction.y), math.up());
                Tint = new float4(1, 0.35f, 0.2f, 1);
                if (Projectile.Friendly != 0)
                {
                    Tint = new float4(1, 0.9f, 0.5f, 1);
                }
            }
            else if (Effects.HasComponent(Part.Owner))
            {
                WorldCombatEffect Effect = Effects[Part.Owner];
                float Progress = math.saturate(Effect.Age / Effect.Duration);
                Tint = Effect.Color;
                Tint.w *= 1 - Progress;
                BodyScale = Effect.Scale * (1 + Progress * Effect.Expansion);
                Rotation = quaternion.LookRotationSafe(new float3(Effect.Direction.x, 0, Effect.Direction.y), math.up());
                if (Effect.Kind == 1)
                {
                    BodyScale *= 1 - Progress;
                }
            }
            else if (Actions.HasComponent(Part.Owner))
            {
                HeroActionState Action = Actions[Part.Owner];
                Rotation = quaternion.LookRotationSafe(new float3(Action.Facing.x, 0, Action.Facing.y), math.up());
                if (Action.MotionKind == 2 && Weapons.HasBuffer(Part.Owner))
                {
                    DynamicBuffer<EquippedWeapon> Equipped = Weapons[Part.Owner];
                    if (Action.ActiveSlot < Equipped.Length)
                    {
                        EquippedWeapon Weapon = Equipped[Action.ActiveSlot];
                        float Progress = math.saturate(Action.MotionElapsed / Action.MotionDuration);
                        Position.y += math.sin(Progress * math.PI) * Weapon.JumpHeight;
                        if (Weapon.SpecialAbility == SpecialAbilityKind.SweepingStrike)
                        {
                            Rotation = math.mul(Rotation, quaternion.RotateY(Progress * math.PI * 6));
                        }
                    }
                }
                if (Part.WeaponIndex >= 0 && Weapons.HasBuffer(Part.Owner))
                {
                    DynamicBuffer<EquippedWeapon> Equipped = Weapons[Part.Owner];
                    bVisible &= Action.ActiveSlot < Equipped.Length && Equipped[Action.ActiveSlot].ModelIndex == Part.WeaponIndex;
                }
                if (Part.Animation == 1 && !Instances.HasComponent(Part.Owner))
                {
                    quaternion Swing = quaternion.identity;
                    if (Action.SwingRemaining > 0)
                    {
                        float Progress = 1 - math.saturate(Action.SwingRemaining / SwingDuration);
                        Swing = quaternion.RotateY(math.lerp(-1.2f, 1.2f, math.sin(Progress * math.PI * 0.5f)));
                    }
                    if (Action.ChargeProgress > 0)
                    {
                        Swing = quaternion.RotateX(-math.saturate(Action.ChargeProgress) * 1.5f);
                    }
                    Local = math.mul(float4x4.Translate(Part.Pivot), math.mul(new float4x4(Swing, float3.zero), math.mul(float4x4.Translate(-Part.Pivot), Local)));
                }
                else if (Part.Animation == 4)
                {
                    bVisible &= Action.ChargeProgress > 0;
                    Local = math.mul(float4x4.Scale(new float3(0.5f + math.saturate(Action.ChargeProgress))), Local);
                }
                else if (Part.Animation >= 10)
                {
                    int Model = Part.Animation - 10;
                    bool bGuard = Part.Animation >= 20;
                    if (bGuard)
                    {
                        Model = Part.Animation - 20;
                        bVisible &= Action.GuardRemaining > 0;
                    }
                    else
                    {
                        bVisible &= Action.SpecialVisualRemaining > 0;
                        if (Action.MotionKind == 0)
                        {
                            float Progress = 1 - math.saturate(Action.SpecialVisualRemaining / FinishDuration);
                            Local = math.mul(float4x4.Scale(new float3(1 + Progress * 0.5f)), Local);
                            Tint.w *= 1 - Progress;
                        }
                    }
                    bVisible &= Model == Action.SpecialWeaponIndex;
                    Local = math.mul(float4x4.Scale(new float3(Action.SpecialVisualRadius, 1, Action.SpecialVisualRadius)), Local);
                }
            }
            else if (EnemyAttacks.HasComponent(Part.Owner))
            {
                EnemyCombatAttackState Attack = EnemyAttacks[Part.Owner];
                if (Attack.Phase == 1 || Attack.Phase == 3)
                {
                    Rotation = quaternion.LookRotationSafe(new float3(Attack.Direction.x, 0, Attack.Direction.y), math.up());
                }
                else if (Positions.HasComponent(Attack.Target))
                {
                    Rotation = quaternion.LookRotationSafe(Positions[Attack.Target].Position - Position, math.up());
                }
                if (Part.Animation == 7)
                {
                    bVisible &= Attack.Phase == 1;
                    EnemyCombatTuning Tuning = EnemyTuning[Part.Owner];
                    float Radius = Tuning.AttackRadiusMeters;
                    float Width = Tuning.HalfWidth;
                    if (Tuning.Kind == 1)
                    {
                        Width = Radius;
                    }
                    Position = new float3(Attack.Origin.x, 0.07f, Attack.Origin.y);
                    Local = math.mul(float4x4.Scale(new float3(Width, 1, Radius)), Local);
                    Tint = new float4(1, 0.22f, 0.08f, 0.15f + (1 - math.saturate(Attack.Remaining / Tuning.WindupSeconds)) * 0.6f);
                }
                if (Attack.Phase == 1 && Part.Animation != 7)
                {
                    Tint = math.lerp(Tint, new float4(1, 0.2f, 0.1f, 1), 0.6f);
                }
            }
            if (Impacts.HasComponent(Part.Owner))
            {
                CombatImpactState Impact = Impacts[Part.Owner];
                if (Impact.PushRemaining > 0 && Impact.PushDuration > 0)
                {
                    float Progress = 1 - math.saturate(Impact.PushRemaining / Impact.PushDuration);
                    Position.y += math.sin(Progress * math.PI) * Impact.LaunchHeight;
                    Rotation = math.mul(Rotation, quaternion.RotateX(-math.sin(Progress * math.PI) * 0.25f));
                }
                if (Impact.FlashRemaining > 0 && Part.Animation != 7)
                {
                    Tint.xyz = math.lerp(Tint.xyz, new float3(1), math.saturate(Impact.FlashRemaining / FlashDuration) * 0.85f);
                }
                if (DeathProgress > 0)
                {
                    Rotation = math.mul(Rotation, quaternion.RotateX(DeathProgress * math.radians(150)));
                }
            }
            if (Nodes.HasComponent(Part.Owner) && Nodes[Part.Owner].Kind != ResourceKind.None)
            {
                ResourceNode Node = Nodes[Part.Owner];
                if (Node.Remaining == 0)
                {
                    Tint.xyz *= 0.35f;
                }
            }
        }
        float3 Scale = BodyScale;
        if (!bVisible)
        {
            Scale = float3.zero;
        }
        Transform.Value = math.mul(float4x4.TRS(Position, Rotation, Scale), Local);
        if (bVisible && Part.WeaponIndex >= 0 && Instances.HasComponent(Part.Owner))
        {
            Entity Root = Instances[Part.Owner].Root;
            if (Hands.HasComponent(Root))
            {
                Transform.Value = math.mul(Hands[Root].Right, math.mul(float4x4.Translate(-Part.Pivot), Part.LocalMatrix));
            }
        }
        if (bLinear)
        {
            Tint = CombatRenderColor.ToLinear(Tint);
        }
        Color.Value = Tint;
    }
}
#endif
