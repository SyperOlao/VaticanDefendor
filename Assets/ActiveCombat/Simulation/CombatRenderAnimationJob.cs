using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Rendering;
using Unity.Transforms;

[BurstCompile]
public struct CombatEntityAnimationJob : IJobChunk
{
    [ReadOnly] public ComponentTypeHandle<CombatRenderPart> PartType;
    public ComponentTypeHandle<LocalToWorld> TransformType;
    public ComponentTypeHandle<MaterialMeshInfo> MeshType;
    public ComponentTypeHandle<URPMaterialPropertyBaseColor> ColorType;
    public ComponentTypeHandle<CombatEffectMaterialParameters> EffectParametersType;
    [ReadOnly] public ComponentLookup<CombatModelState> Models;
    [ReadOnly] public ComponentLookup<CombatPlayerState> Players;
    [ReadOnly] public ComponentLookup<CombatBotState> Enemies;
    [ReadOnly] public ComponentLookup<CombatBotStatistics> EnemyStatistics;
    [ReadOnly] public ComponentLookup<CombatBuildingState> Buildings;
    [ReadOnly] public ComponentLookup<CombatProjectileState> Projectiles;
    [ReadOnly] public ComponentLookup<CombatPickupState> Pickups;
    [ReadOnly] public ComponentLookup<CombatEffectState> Effects;
    [ReadOnly] public ComponentLookup<CombatSceneryState> Scenery;
    [ReadOnly] public ComponentLookup<CombatCorpseState> Corpses;
    [ReadOnly] public ComponentLookup<CombatStaticPose> StaticPoses;
    [ReadOnly] public ComponentLookup<CombatBombState> Bombs;
    [ReadOnly] public ComponentLookup<CombatMatchState> Matches;
    public BlobAssetReference<CombatConfiguration> Configuration;
    public Entity MatchEntity;
    public bool bLinearColorSpace;

    public void Execute(in ArchetypeChunk Chunk, int ChunkIndex, bool bUseEnabledMask, in Unity.Burst.Intrinsics.v128 ChunkEnabledMask)
    {
        NativeArray<CombatRenderPart> Parts = Chunk.GetNativeArray(ref PartType);
        NativeArray<LocalToWorld> Transforms = Chunk.GetNativeArray(ref TransformType);
        NativeArray<MaterialMeshInfo> Meshes = Chunk.GetNativeArray(ref MeshType);
        NativeArray<URPMaterialPropertyBaseColor> Colors = Chunk.GetNativeArray(ref ColorType);
        NativeArray<CombatEffectMaterialParameters> Parameters = Chunk.GetNativeArray(ref EffectParametersType);
        EnabledMask Enabled = Chunk.GetEnabledMask(ref MeshType);
        CombatMatchState Match = Matches[MatchEntity];
        for (int Index = 0; Index < Chunk.Count; Index++)
        {
            CombatRenderPart Part = Parts[Index];
            CombatModelState Model = Models[Part.Owner];
            float4x4 Matrix = Part.LocalMatrix;
            float4 Color = Part.Color;
            float4 EffectParameters = new float4(1, 0, 1, 0);
            bool bVisible = Model.bVisible != 0;
            int MaterialIndex = Part.Materials.x;
            if (Players.HasComponent(Part.Owner))
            {
                AnimatePlayer(Part, Players[Part.Owner], Match, ref Matrix, ref Color, ref bVisible, ref MaterialIndex);
            }
            else if (Enemies.HasComponent(Part.Owner))
            {
                CombatBotState Enemy = Enemies[Part.Owner];
                quaternion Rotation = quaternion.LookRotationSafe(Enemy.Facing, math.up());
                float3 Scale = Model.Scale;
                if (Corpses.HasComponent(Part.Owner))
                {
                    float Progress = math.saturate(Corpses[Part.Owner].Age / 0.5f);
                    Rotation = math.mul(Rotation, quaternion.RotateX(math.radians(Corpses[Part.Owner].Age * 300)));
                    Scale *= 1 - Progress * 0.8f;
                }
                Matrix = math.mul(float4x4.TRS(Enemy.Position + new float3(0, Enemy.Height, 0), Rotation, Scale), Matrix);
                if (Part.Animation == 7)
                {
                    bVisible = Enemy.WindupRemaining > 0 && Enemy.Health > 0;
                    CombatBotStatistics Statistics = EnemyStatistics[Part.Owner];
                    float3 ShapeScale = new float3(Statistics.HalfWidth, 1, Enemy.AttackRange);
                    if (Statistics.Kind == CombatBotKind.Strong)
                    {
                        ShapeScale.x = Enemy.AttackRange;
                    }
                    Matrix = math.mul(float4x4.TRS(Enemy.AttackOrigin + new float3(0, 0.07f, 0), quaternion.LookRotationSafe(Enemy.AttackDirection, math.up()), ShapeScale), Part.LocalMatrix);
                    Color = new float4(1, 0.22f, 0.08f, 0.15f + (1 - Enemy.WindupRemaining / Statistics.Windup) * 0.6f);
                }
            }
            else if (Buildings.HasComponent(Part.Owner))
            {
                CombatBuildingState Building = Buildings[Part.Owner];
                bVisible &= Building.Health > 0;
                Matrix = math.mul(float4x4.TRS(Building.Position, quaternion.RotateY(Model.Angle), Model.Scale), Matrix);
            }
            else if (Projectiles.HasComponent(Part.Owner))
            {
                CombatProjectileState Projectile = Projectiles[Part.Owner];
                bVisible &= Projectile.Remaining > 0;
                Matrix = math.mul(float4x4.TRS(Projectile.Position + new float3(0, 0.9f, 0), quaternion.LookRotationSafe(Projectile.Direction, math.up()), Model.Scale), Matrix);
                Color = new float4(1, 0.35f, 0.2f, 1);
                if (Projectile.bFriendly != 0)
                {
                    Color = new float4(1, 0.9f, 0.5f, 1);
                }
            }
            else if (Pickups.HasComponent(Part.Owner))
            {
                CombatPickupState Pickup = Pickups[Part.Owner];
                float3 Position = Pickup.Position;
                if (Pickup.Kind == 0)
                {
                    Position.y += 0.13f + math.sin(Match.ElapsedSeconds * 3) * 0.1f;
                }
                else
                {
                    bVisible &= Buildings.HasComponent(Pickup.Owner) && Buildings[Pickup.Owner].StoredGold > 0;
                }
                Matrix = math.mul(float4x4.Translate(Position), Matrix);
            }
            else if (Effects.HasComponent(Part.Owner))
            {
                CombatEffectState Effect = Effects[Part.Owner];
                float Progress = math.saturate(Effect.Age / Effect.Duration);
                float3 Scale = new float3(1 + Effect.Expansion * Progress);
                if (Effect.Kind == CombatEffectKind.Area)
                {
                    Scale.xz *= Effect.Radius;
                    EffectParameters = new float4(Effect.Radius, 1, 1, 0);
                }
                else if (Effect.Kind == CombatEffectKind.Weapon)
                {
                    Scale.x = Effect.HalfWidth;
                    Scale.z = Effect.Radius;
                    if (Effect.WeaponKind == 1)
                    {
                        EffectParameters = new float4(1, 3, Effect.TipWidthScale / Part.Pivot.x, 0);
                    }
                    if (Effect.WeaponKind != 1)
                    {
                        Scale.x = Effect.Radius;
                    }
                }
                Matrix = math.mul(float4x4.TRS(Effect.Position, quaternion.LookRotationSafe(Effect.Direction, math.up()), Scale), Matrix);
                Color = Effect.Color;
                Color.w = (1 - Progress) * 0.65f;
            }
            else if (Bombs.HasComponent(Part.Owner))
            {
                CombatBombState Bomb = Bombs[Part.Owner];
                float CellSize = Configuration.Value.Rules.CellSize;
                float Diameter = CellSize * (Configuration.Value.Rules.TrebuchetCellRadius * 2 + 1);
                Matrix = math.mul(float4x4.TRS(new float3(Bomb.Cell.x, 0.08f / CellSize, Bomb.Cell.y) * CellSize, quaternion.identity, new float3(Diameter)), Matrix);
                EffectParameters = new float4(Configuration.Value.Rules.GridSize / 2, 2, CellSize, 0);
                Color = new float4(1, 0.4f, 0.1f, 0.28f + math.sin(Bomb.Remaining * 20) * 0.13f);
            }
            else if (StaticPoses.HasComponent(Part.Owner))
            {
                Matrix = math.mul(StaticPoses[Part.Owner].Matrix, Matrix);
                if (Scenery.HasComponent(Part.Owner))
                {
                    bVisible &= Scenery[Part.Owner].bRemoved == 0;
                }
            }
            Transforms[Index] = new LocalToWorld { Value = Matrix };
            if (bLinearColorSpace)
            {
                Color = CombatRenderColor.ToLinear(Color);
            }
            Colors[Index] = new URPMaterialPropertyBaseColor { Value = Color };
            Parameters[Index] = new CombatEffectMaterialParameters { Value = EffectParameters };
            if (Meshes[Index].Material != MaterialMeshInfo.ArrayIndexToStaticIndex(MaterialIndex))
            {
                Meshes[Index] = MaterialMeshInfo.FromRenderMeshArrayIndices(MaterialIndex, Part.Mesh);
            }
            if (Enabled[Index] != bVisible)
            {
                Enabled[Index] = bVisible;
            }
        }
    }

    private void AnimatePlayer(CombatRenderPart Part, CombatPlayerState Player, CombatMatchState Match, ref float4x4 Matrix, ref float4 Color, ref bool bVisible, ref int MaterialIndex)
    {
        float Height = 0;
        quaternion Rotation = quaternion.LookRotationSafe(Player.Facing, math.up());
        if (Player.bMovementAttack != 0)
        {
            float Progress = math.saturate(Player.AttackElapsed / Player.AttackDuration);
            if (Player.AttackWeapon.Kind == 2)
            {
                if (Player.bSpecial != 0)
                {
                    Height = math.sin(Progress * math.PI) * Player.AttackWeapon.JumpHeight;
                }
                else
                {
                    Rotation = math.mul(Rotation, quaternion.RotateY(math.radians(Progress * 720)));
                }
            }
            else if (Player.bSpecial != 0 && Player.AttackWeapon.Kind == 0)
            {
                Rotation = math.mul(Rotation, quaternion.RotateY(math.radians(Progress * 1080)));
            }
        }
        Matrix = math.mul(float4x4.TRS(Player.Position + new float3(0, Height, 0), Rotation, new float3(1)), Matrix);
        if (Part.Appearance != 0)
        {
            int Count = Player.HealthUpgrades;
            if (Part.Appearance == 3)
            {
                Count = Player.AttackUpgrades;
            }
            int Tier = math.clamp((Count + 2) / 3, 0, 3);
            MaterialIndex = Part.Materials[Tier];
            if (Tier > 0)
            {
                Color = new float4(1);
            }
        }
        if (Part.WeaponIndex >= 0)
        {
            bVisible &= Part.WeaponIndex == Player.WeaponIndex;
        }
        float ChargeSeconds = Configuration.Value.Rules.ChargeSeconds;
        for (int Index = 0; Index < Configuration.Value.Milestones.Length; Index++)
        {
            CombatMilestoneDefinition Milestone = Configuration.Value.Milestones[Index];
            if (!Milestone.bHealth && Milestone.Bonus == 3 && Player.AttackUpgrades >= Milestone.Level)
            {
                ChargeSeconds *= Milestone.Multiplier;
            }
        }
        if (Part.Animation == 1)
        {
            quaternion Swing = quaternion.RotateY(math.radians(math.sin(Player.SwingRemaining * 16) * 70));
            if (Player.bGestureActive != 0 && Player.Charge > 0)
            {
                Swing = quaternion.RotateX(math.radians(-100 * Player.Charge / ChargeSeconds));
            }
            float4x4 Joint = math.mul(float4x4.Translate(Part.Pivot), math.mul(new float4x4(Swing, float3.zero), float4x4.Translate(-Part.Pivot)));
            Matrix = math.mul(float4x4.TRS(Player.Position + new float3(0, Height, 0), Rotation, new float3(1)), math.mul(Joint, Part.LocalMatrix));
        }
        else if (Part.Animation == 4)
        {
            bVisible &= Player.bGestureActive != 0 && Player.Charge > 0;
            float Progress = math.saturate(Player.Charge / ChargeSeconds);
            float4x4 Local = Part.LocalMatrix;
            if (Part.PartIndex == 0)
            {
                Local = math.mul(Local, float4x4.Scale(1.6f - Progress * 0.5f + math.sin(Match.ElapsedSeconds * 14) * 0.04f));
            }
            else
            {
                float Angle = (Part.PartIndex - 1) * math.PI / 4 + Match.ElapsedSeconds * 4;
                Local = float4x4.TRS(new float3(math.sin(Angle) * (1.5f - Progress * 0.8f), 0.3f + Progress * 1.1f + math.sin(Angle * 2) * 0.15f, math.cos(Angle) * (1.5f - Progress * 0.8f)), quaternion.identity, new float3(0.6f + Progress));
            }
            Matrix = math.mul(float4x4.Translate(Player.Position), Local);
            Color = Configuration.Value.Weapons[Player.WeaponIndex].Color;
            if (Part.PartIndex == 0)
            {
                Color.w = 0.25f + Progress * 0.5f;
            }
        }
        else if (Part.Animation >= 10)
        {
            bool bDefense = Part.Animation >= 20;
            int Kind = Part.Animation - 10;
            float3 Position = Player.Position + new float3(0, 0.1f, 0);
            float Radius = Player.AttackWeapon.SpecialRadius;
            float Angle = 0;
            if (bDefense)
            {
                Kind = Part.Animation - 20;
                bVisible &= Player.bDefense != 0 && Player.DefenseWeapon.Kind == Kind;
                Radius = Player.DefenseWeapon.DefenseRadius;
                Angle = math.radians((Player.DefenseWeapon.DefenseDuration - Player.DefenseRemaining) * 60);
                Position.y = 0;
            }
            else
            {
                bVisible &= Player.bMovementAttack != 0 && Player.bSpecial != 0 && Player.AttackWeapon.Kind == Kind;
                if (Kind == 0)
                {
                    Angle = math.radians(Player.AttackElapsed * 1080);
                }
                if (Kind == 2)
                {
                    Radius = Player.AttackWeapon.FinishRadius;
                    Position = Player.AttackTarget + new float3(0, 0.1f, 0);
                }
            }
            float4x4 Local = Part.LocalMatrix;
            if (Part.PartIndex == 0)
            {
                Local = math.mul(Local, float4x4.Scale(new float3(Radius, 1, Radius)));
            }
            else if (Part.PartIndex < 9)
            {
                Local.c3.x *= Radius;
                Local.c3.z *= Radius;
            }
            Matrix = math.mul(float4x4.TRS(Position, quaternion.RotateY(Angle), new float3(1)), Local);
        }
    }
}
