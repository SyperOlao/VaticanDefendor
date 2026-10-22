using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using Unity.Rendering;

public partial struct CombatGameplayContext
{
    public Entity PlayerEntity;
    public Entity MatchEntity;
    public BlobAssetReference<CombatConfiguration> Configuration;
    [ReadOnly] public NativeArray<Entity> Prefabs;
    public EntityCommandBuffer Commands;
    public ComponentLookup<CombatPlayerState> Players;
    public ComponentLookup<CombatMatchState> Matches;
    public ComponentLookup<CombatBotState> Enemies;
    public ComponentLookup<CombatBotStatistics> EnemyStatistics;
    public ComponentLookup<CombatEnemyIdentity> EnemyIdentities;
    public ComponentLookup<CombatBuildingState> Buildings;
    public ComponentLookup<CombatProjectileState> Projectiles;
    public ComponentLookup<CombatPickupState> Pickups;
    public ComponentLookup<CombatBombState> Bombs;
    public ComponentLookup<CombatEffectState> Effects;
    public ComponentLookup<CombatSceneryState> Scenery;
    public ComponentLookup<CombatModelState> Models;
    public BufferLookup<CombatUiEvent> UiEvents;
    public BufferLookup<CombatWaveGroup> WaveGroups;
    public BufferLookup<CombatBotEvent> EnemyEvents;
    [ReadOnly] public BufferLookup<LinkedEntityGroup> RenderGroups;
    [ReadOnly] public ComponentLookup<LocalToWorld> RenderTransforms;
    [ReadOnly] public ComponentLookup<RenderBounds> RenderBounds;
    [ReadOnly] public ComponentLookup<MaterialMeshInfo> RenderMaterials;
    [ReadOnly] public ComponentLookup<CombatRenderPart> RenderParts;
    public NativeList<Entity> EnemyEntities;
    public NativeList<Entity> BuildingEntities;
    public NativeList<Entity> ProjectileEntities;
    public NativeList<Entity> PickupEntities;
    public NativeList<Entity> BombEntities;
    public NativeList<Entity> EffectEntities;
    public NativeList<Entity> SceneryEntities;
    [ReadOnly] public NativeParallelMultiHashMap<int2, CombatBotNeighbor> WorldNeighbors;

    public CombatEnemyRange EnemiesInBounds(float3 Minimum, float3 Maximum)
    {
        return new CombatEnemyRange(WorldNeighbors, Minimum.xz, Maximum.xz);
    }

    public CombatEnemyRange EnemiesInRadius(float3 Position, float Radius)
    {
        return new CombatEnemyRange(WorldNeighbors, Position.xz - Radius, Position.xz + Radius);
    }

    public ref CombatPlayerState Player => ref Players.GetRefRW(PlayerEntity).ValueRW;

    public void SetSceneryVisible(Entity Entity, bool bVisible)
    {
        DynamicBuffer<LinkedEntityGroup> Parts = RenderGroups[Entity];
        for (int Index = 1; Index < Parts.Length; Index++)
        {
            Commands.SetComponentEnabled<MaterialMeshInfo>(Parts[Index].Value, bVisible);
        }
    }
    public ref CombatMatchState Match => ref Matches.GetRefRW(MatchEntity).ValueRW;
    public ref CombatConfiguration Settings => ref Configuration.Value;
    public float ArenaBound => Settings.Rules.GridSize * Settings.Rules.CellSize / 2 + Settings.Rules.PlayableBorder - 0.8f;
    public float MaximumHealth => Settings.Rules.MaximumHealth + Player.HealthUpgrades * 25;

    public int SpecialCost
    {
        get
        {
            if (Player.SpecialUses == 0)
            {
                return 6;
            }
            return 11 + Player.SpecialUses;
        }
    }

    public float ProgressionMultiplier(int Bonus)
    {
        float Multiplier = 1;
        for (int Index = 0; Index < Settings.Milestones.Length; Index++)
        {
            CombatMilestoneDefinition Milestone = Settings.Milestones[Index];
            int Count = Player.AttackUpgrades;
            if (Milestone.bHealth)
            {
                Count = Player.HealthUpgrades;
            }
            if (Milestone.Bonus == Bonus && Count >= Milestone.Level)
            {
                Multiplier *= Milestone.Multiplier;
            }
        }
        return Multiplier;
    }

    public CombatWeaponDefinition CurrentWeapon()
    {
        CombatWeaponDefinition Weapon = Settings.Weapons[Player.WeaponIndex];
        Weapon.AutoDamage += Player.WeaponUpgrades[Player.WeaponIndex] * 10;
        return Weapon;
    }

    public void AddExperience(int Amount)
    {
        Player.Experience += Amount;
        while (Player.Experience >= (int)math.round(50 * math.pow(Settings.Rules.PrestigeGrowth, Player.Rank - 1)))
        {
            Player.Experience -= (int)math.round(50 * math.pow(Settings.Rules.PrestigeGrowth, Player.Rank - 1));
            Player.Rank++;
        }
    }

    public void RecordPersonalDamage(float Amount)
    {
        if (Amount > 0)
        {
            Player.ComboPoints = math.min(SpecialCost, Player.ComboPoints + Amount / 50);
            Player.ComboIdleSeconds = 0;
        }
    }

    public void ShowError(int Code)
    {
        Match.ErrorCode = Code;
        Match.ErrorRemaining = 2.5f;
    }

    public void FloatText(float3 Position, float Amount, int Kind)
    {
        UiEvents[MatchEntity].Add(new CombatUiEvent { Position = Position, Amount = Amount, Kind = Kind });
    }

    public Entity SpawnModel(int ModelIndex)
    {
        Entity Entity = Commands.Instantiate(Prefabs[ModelIndex]);
        Commands.AddComponent<CombatMatchEntity>(Entity);
        return Entity;
    }

    public Entity SpawnEffect(CombatEffectState Effect)
    {
        int ModelIndex = CombatModelIndex.Area;
        if (Effect.Kind == CombatEffectKind.Weapon)
        {
            ModelIndex = CombatModelIndex.Weapon + Effect.WeaponKind;
        }
        else if (Effect.Kind == CombatEffectKind.Shard)
        {
            ModelIndex = CombatModelIndex.Shard;
        }
        Entity Entity = SpawnModel(ModelIndex);
        Commands.AddComponent(Entity, Effect);
        return Entity;
    }

    public void AreaEffect(float3 Position, float Radius, float4 Color, float Duration)
    {
        SpawnEffect(new CombatEffectState { Kind = CombatEffectKind.Area, Position = Position + new float3(0, 0.1f, 0), Direction = new float3(0, 0, 1), Radius = Radius, Duration = Duration, Color = Color, Expansion = 0.5f });
    }

    public void WeaponEffect(float3 Position, float3 Direction, float Radius, float HalfWidth, int Kind, float4 Color, float Duration, float TipWidthScale)
    {
        SpawnEffect(new CombatEffectState { Kind = CombatEffectKind.Weapon, Position = Position + new float3(0, 0.09f, 0), Direction = Direction, Radius = Radius, HalfWidth = HalfWidth, WeaponKind = Kind, Color = Color, Duration = Duration, TipWidthScale = TipWidthScale });
    }

    public Entity SpawnEnemy(int KindIndex, float3 Position)
    {
        CombatEnemyDefinition Definition = Settings.Enemies[KindIndex];
        Definition.Health *= 1 + math.max(0, Match.WaveNumber - 1) * Settings.Rules.EnemyHealthGrowth;
        Definition.Damage *= Settings.Rules.EnemyDamageMultiplier * (1 + math.max(0, Match.WaveNumber - 1) * Settings.Rules.EnemyDamageGrowth);
        int Tier = 0;
        if (Match.WaveNumber >= 3)
        {
            Tier = 1;
        }
        if (Match.WaveNumber >= 6)
        {
            Tier = 2;
        }
        if (Match.WaveNumber >= 9)
        {
            Tier = 3;
        }
        Entity Enemy = SpawnModel(CombatModelIndex.Enemy + KindIndex * 4 + Tier);
        Random RandomSequence = new Random(Match.RandomState);
        CombatBotState State = new CombatBotState
        {
            Position = Position, Health = Definition.Health, AttackRange = Definition.AttackRadius,
            Speed = Definition.Speed + RandomSequence.NextFloat() * Definition.SpeedVariation,
            FormationAngle = RandomSequence.NextFloat(0, math.PI * 2), Cooldown = RandomSequence.NextFloat(0.4f, 1.5f),
            Facing = new float3(0, 0, 1), NavigationRevision = -1, RandomState = RandomSequence.NextUInt(1, uint.MaxValue)
        };
        Match.RandomState = RandomSequence.state;
        Commands.AddComponent(Enemy, State);
        Commands.AddComponent(Enemy, new CombatBotStatistics
        {
            Kind = (CombatBotKind)Definition.Kind, Damage = Definition.Damage, HalfWidth = Definition.HalfWidth,
            Windup = Definition.Windup, Recovery = Definition.Recovery, FormationDistance = Definition.FormationDistance,
            MinimumRange = Definition.MinimumRange, DashDistance = Definition.DashDistance, DashDuration = Definition.DashDuration,
            ThrustLength = Definition.ThrustLength, ProjectileSpeed = Definition.ProjectileSpeed
        });
        Commands.AddComponent(Enemy, new CombatEnemyIdentity { SpawnIdentity = ++Match.NextIdentity, MaximumHealth = Definition.Health, KindIndex = KindIndex, ArmorTier = Tier });
        Commands.AddBuffer<CombatPathCorner>(Enemy);
        Commands.AddBuffer<CombatAttackVictim>(Enemy);
        Commands.AddBuffer<CombatBotEvent>(Enemy);
        return Enemy;
    }

    public void DamageEnemy(Entity Target, float Damage, float3 Direction, float Push, float PushDuration, float LaunchHeight, bool bKeepStronger, bool bPersonal)
    {
        CombatBotState Enemy = Enemies[Target];
        if (Enemy.Health <= 0)
        {
            return;
        }
        if (bPersonal)
        {
            RecordPersonalDamage(math.min(math.max(0, Damage), Enemy.Health));
        }
        CombatBotDamage.Apply(ref Enemy, Damage, Direction, Push, PushDuration, LaunchHeight, bKeepStronger);
        Enemies[Target] = Enemy;
        if (Damage > 0)
        {
            FloatText(Enemy.Position + new float3(0, 1.8f, 0), math.round(Damage), 0);
        }
        if (Enemy.Health <= 0)
        {
            CombatBotDamage.CancelAttack(ref Enemy);
            Enemies[Target] = Enemy;
            Commands.AddComponent(Target, new CombatCorpseState { Velocity = Enemy.Velocity * 0.4f });
            Player.Kills++;
            Player.Gold += Settings.Rules.KillGold;
            AddExperience(Settings.Rules.KillExperience);
        }
    }

    public void DamageBuilding(Entity Target, float Damage)
    {
        CombatBuildingState Building = Buildings[Target];
        if (Building.Health <= 0)
        {
            return;
        }
        Building.Health -= Damage;
        Buildings[Target] = Building;
        if (Building.Health <= 0)
        {
            if (Building.Kind == 6)
            {
                Match.bDefeated = 1;
                Match.bPaused = 1;
                ShowError(7);
            }
            Commands.DestroyEntity(Target);
            Match.NavigationRevision++;
            foreach (Entity PickupEntity in PickupEntities)
            {
                if (Pickups[PickupEntity].Owner == Target)
                {
                    Commands.DestroyEntity(PickupEntity);
                }
            }
        }
    }

    public void DamagePlayer(float Damage, float3 Direction, float PushScale)
    {
        Damage = math.round(Damage * ProgressionMultiplier(7));
        float Absorbed = math.min(Player.Shield, Damage);
        Player.Shield -= Absorbed;
        Damage -= Absorbed;
        if (Damage <= 0)
        {
            return;
        }
        Player.ComboPendingLoss = math.min(SpecialCost, Player.ComboPendingLoss + SpecialCost * 0.15f);
        Player.Health = math.max(0, Player.Health - Damage);
        Player.PushVelocity = math.normalizesafe(Direction) * Settings.Rules.PlayerHitPush * PushScale * 2 / Settings.Rules.PlayerHitDuration;
        Player.PushRemaining = Settings.Rules.PlayerHitDuration;
        Player.HitFlash = 0.18f;
        Player.CameraShake = 0.12f;
        FloatText(Player.Position + new float3(0, 2, 0), -math.round(Damage), 1);
        if (Player.Health <= 0)
        {
            Respawn();
        }
    }

    public bool IsBlocked(float3 Position)
    {
        if (math.any(math.abs(Position.xz) > ArenaBound))
        {
            return true;
        }
        foreach (Entity Entity in SceneryEntities)
        {
            CombatSceneryState Obstacle = Scenery[Entity];
            if (Obstacle.bRemoved == 0 && math.distancesq(Position, Obstacle.Position) < 1.6f)
            {
                return true;
            }
        }
        foreach (Entity Entity in BuildingEntities)
        {
            CombatBuildingState Building = Buildings[Entity];
            if (Building.Health > 0 && Building.Kind != 2 && math.all(math.abs(Position.xz - Building.Position.xz) < Building.HalfExtents + 0.35f))
            {
                return true;
            }
        }
        return false;
    }

    public void Respawn()
    {
        Player.bGestureActive = 0;
        Player.Charge = 0;
        Player.bMovementAttack = 0;
        Player.DashMomentumRemaining = 0;
        Player.bDefense = 0;
        Player.Shield = 0;
        Player.CastRemaining = 0;
        Player.DamageBonusRemaining = 0;
        Player.DamageBonusPercent = 0;
        Player.PushRemaining = 0;
        Player.AutoRemaining = CurrentWeapon().AutoInterval;
        foreach (Entity Entity in ProjectileEntities)
        {
            CombatProjectileState Projectile = Projectiles[Entity];
            Projectile.Remaining = 0;
            Projectiles[Entity] = Projectile;
        }
        foreach (Entity Entity in EnemyEntities)
        {
            CombatBotState Enemy = Enemies[Entity];
            CombatBotDamage.CancelAttack(ref Enemy);
            Enemy.Cooldown = 1;
            Enemies[Entity] = Enemy;
            EnemyEvents[Entity].Clear();
        }
        float3 Best = float3.zero;
        float BestDistance = -1;
        int Half = Settings.Rules.GridSize / 2;
        for (int Horizontal = -Half; Horizontal <= Half; Horizontal++)
        {
            for (int Vertical = -Half; Vertical <= Half; Vertical++)
            {
                float3 Candidate = new float3(Horizontal, 0, Vertical) * Settings.Rules.CellSize;
                if (IsBlocked(Candidate))
                {
                    continue;
                }
                float Nearest = 10000;
                foreach (Entity Entity in EnemyEntities)
                {
                    if (Enemies[Entity].Health > 0)
                    {
                        Nearest = math.min(Nearest, math.distancesq(Enemies[Entity].Position, Candidate));
                    }
                }
                if (Nearest > BestDistance)
                {
                    BestDistance = Nearest;
                    Best = Candidate;
                }
            }
        }
        Player.Position = Best;
        Player.Health = MaximumHealth;
        Player.Stamina = Settings.Rules.MaximumStamina;
    }
}
