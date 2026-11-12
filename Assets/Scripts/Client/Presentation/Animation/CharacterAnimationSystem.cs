#if !UNITY_SERVER || UNITY_EDITOR
using Unity.Burst;
using Unity.Collections;
using Unity.Deformations;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Rendering;
using Unity.Transforms;
using VaticanDefendor;
using VaticanDefendor.Combat;
using VaticanDefendor.Session;

[WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.Presentation)]
[UpdateInGroup(typeof(PresentationSystemGroup))]
[UpdateAfter(typeof(EcsWorldPresentationSystem))]
[UpdateBefore(typeof(DeformationsInPresentation))]
public partial class CharacterAnimationSystem : SystemBase
{
    protected override void OnCreate()
    {
        RequireForUpdate<CharacterVisualRoot>();
        RequireForUpdate<CombatFeelSettings>();
    }

    protected override void OnUpdate()
    {
        float Delta = UnityEngine.Time.deltaTime;
        using (EntityQuery Activity = EntityManager.CreateEntityQuery(typeof(RunActivityState)))
        {
            if (!Activity.IsEmptyIgnoreFilter)
            {
                RunActivityState State = Activity.GetSingleton<RunActivityState>();
                if (State.Paused != 0 || State.Terminal != 0)
                {
                    Delta = 0;
                }
            }
        }
        CombatFeelConfiguration Feel = SystemAPI.ManagedAPI.GetSingleton<CombatFeelSettings>().Configuration;
        Dependency = new CharacterPoseJob
        {
            Delta = Delta, SwingDuration = Feel.SwingDuration, FlashDuration = Feel.HitFlashDuration, CorpseDuration = Feel.CorpseDuration,
            Positions = GetComponentLookup<LocalTransform>(true), Health = GetComponentLookup<CombatHealth>(true),
            Actions = GetComponentLookup<HeroActionState>(true), Impacts = GetComponentLookup<CombatImpactState>(true),
            EnemyAttacks = GetComponentLookup<EnemyCombatAttackState>(true), EnemyTuning = GetComponentLookup<EnemyCombatTuning>(true),
            Weapons = GetBufferLookup<EquippedWeapon>(true)
        }.ScheduleParallel(Dependency);
        Dependency = new CharacterSkinJob { Poses = GetBufferLookup<CharacterPoseMatrix>(true) }.ScheduleParallel(Dependency);
        Dependency = new CharacterRenderJob { Roots = GetComponentLookup<CharacterVisualRoot>(true) }.ScheduleParallel(Dependency);
        Dependency = new CharacterColorJob
        {
            Roots = GetComponentLookup<CharacterVisualRoot>(true), Impacts = GetComponentLookup<CombatImpactState>(true),
            Attacks = GetComponentLookup<EnemyCombatAttackState>(true), FlashDuration = Feel.HitFlashDuration,
            bLinear = UnityEngine.QualitySettings.activeColorSpace == UnityEngine.ColorSpace.Linear
        }.ScheduleParallel(Dependency);
    }
}

[BurstCompile]
public partial struct CharacterPoseJob : IJobEntity
{
    public float Delta;
    public float SwingDuration;
    public float FlashDuration;
    public float CorpseDuration;
    [ReadOnly] public ComponentLookup<LocalTransform> Positions;
    [ReadOnly] public ComponentLookup<CombatHealth> Health;
    [ReadOnly] public ComponentLookup<HeroActionState> Actions;
    [ReadOnly] public ComponentLookup<CombatImpactState> Impacts;
    [ReadOnly] public ComponentLookup<EnemyCombatAttackState> EnemyAttacks;
    [ReadOnly] public ComponentLookup<EnemyCombatTuning> EnemyTuning;
    [ReadOnly] public BufferLookup<EquippedWeapon> Weapons;

    private void Execute(ref CharacterVisualRoot Root, ref CharacterHandPose Hands, in CharacterAnimationGraphData Data,
        DynamicBuffer<CharacterPoseMatrix> Poses, DynamicBuffer<CharacterAnimationParameter> Parameters)
    {
        if (!Positions.HasComponent(Root.Owner))
        {
            return;
        }
        if (Delta == 0 && Root.Initialized != 0)
        {
            return;
        }
        ref CharacterAnimationGraph Graph = ref Data.Value.Value;
        LocalTransform OwnerTransform = Positions[Root.Owner];
        float3 Position = OwnerTransform.Position;
        quaternion Rotation = OwnerTransform.Rotation;
        if (Delta > 0 || Root.Initialized == 0)
        {
            float Speed = 0;
            if (Root.Initialized != 0 && Delta > 0)
            {
                Speed = math.length((Position - Root.PreviousPosition).xz) / Delta;
            }
            Root.Speed = math.lerp(Root.Speed, math.min(Speed, 8), math.saturate(Delta * 12));
            Root.Mode = 0;
            Root.Progress = 0;
            Root.Attack = 0;
            if (Actions.HasComponent(Root.Owner))
            {
                HeroActionState Action = Actions[Root.Owner];
                Rotation = quaternion.LookRotationSafe(new float3(Action.Facing.x, 0, Action.Facing.y), math.up());
                if (Weapons.HasBuffer(Root.Owner) && Action.ActiveSlot < Weapons[Root.Owner].Length)
                {
                    EquippedWeapon Weapon = Weapons[Root.Owner][Action.ActiveSlot];
                    Root.Weapon = Weapon.ModelIndex;
                    if (Action.MotionKind == 2)
                    {
                        float Progress = math.saturate(Action.MotionElapsed / Action.MotionDuration);
                        Position.y += math.sin(Progress * math.PI) * Weapon.JumpHeight;
                        if (Weapon.SpecialAbility == SpecialAbilityKind.SweepingStrike)
                        {
                            Rotation = math.mul(Rotation, quaternion.RotateY(Progress * math.PI * 6));
                        }
                    }
                }
                if (Action.AttackSequence > Root.AttackSequence)
                {
                    Root.Attack = 1;
                    Root.Variation = (int)(Action.AttackSequence % 3);
                }
                Root.AttackSequence = math.max(Action.AttackSequence, Root.AttackSequence);
                if (Action.ChargeProgress > 0)
                {
                    Root.Mode = 5;
                    Root.Progress = math.saturate(Action.ChargeProgress / 0.7f) * Data.ContactTime;
                }
                if (Action.GuardRemaining > 0)
                {
                    Root.Mode = 3;
                    Root.Progress = 0.4f;
                }
                if (Action.SwingRemaining > 0)
                {
                    Root.Mode = 1;
                    Root.Progress = 1 - math.saturate(Action.SwingRemaining / SwingDuration);
                }
                if (Action.MotionKind != 0)
                {
                    Root.Mode = 4;
                    if (Action.MotionKind == 2)
                    {
                        Root.Mode = 2;
                    }
                    Root.Progress = math.saturate(Action.MotionElapsed / Action.MotionDuration);
                }
            }
            else if (EnemyAttacks.HasComponent(Root.Owner))
            {
                EnemyCombatAttackState Attack = EnemyAttacks[Root.Owner];
                EnemyCombatTuning Tuning = EnemyTuning[Root.Owner];
                Root.Weapon = Tuning.Kind;
                float3 Movement = OwnerTransform.Position - Root.PreviousPosition;
                if (Root.Initialized != 0 && math.lengthsq(Movement.xz) > 0.000001f)
                {
                    Rotation = quaternion.LookRotationSafe(new float3(Movement.x, 0, Movement.z), math.up());
                }
                if (Attack.Phase == 1 || Attack.Phase == 3)
                {
                    Rotation = quaternion.LookRotationSafe(new float3(Attack.Direction.x, 0, Attack.Direction.y), math.up());
                }
                else if (Positions.HasComponent(Attack.Target))
                {
                    Rotation = quaternion.LookRotationSafe(new float3((Positions[Attack.Target].Position - Position).x, 0, (Positions[Attack.Target].Position - Position).z), math.up());
                }
                if (Attack.Phase == 1)
                {
                    Root.Mode = 1;
                    Root.Progress = (1 - math.saturate(Attack.Remaining / Tuning.WindupSeconds)) * Data.ContactTime;
                }
                if (Attack.Phase == 2 && Tuning.RecoverySeconds - Attack.Remaining < SwingDuration)
                {
                    Root.Mode = 1;
                    Root.Progress = math.lerp(Data.ContactTime, 1, math.saturate((Tuning.RecoverySeconds - Attack.Remaining) / SwingDuration));
                }
                if (Attack.Phase == 3)
                {
                    Root.Mode = 1;
                    Root.Progress = math.lerp(Data.ContactTime, 1, 1 - math.saturate(Attack.Remaining / Tuning.DashDuration));
                }
                if (Attack.Sequence > Root.AttackSequence)
                {
                    Root.Attack = 1;
                }
                Root.AttackSequence = math.max(Attack.Sequence, Root.AttackSequence);
            }
            if (Impacts.HasComponent(Root.Owner))
            {
                CombatImpactState Impact = Impacts[Root.Owner];
                if (Impact.PushRemaining > 0 && Impact.PushDuration > 0)
                {
                    Position.y += math.sin((1 - math.saturate(Impact.PushRemaining / Impact.PushDuration)) * math.PI) * Impact.LaunchHeight;
                }
                if (Impact.FlashRemaining > 0 && Root.Mode != 2 && Root.Mode != 4)
                {
                    Root.Mode = 6;
                    Root.Progress = 1 - math.saturate(Impact.FlashRemaining / FlashDuration);
                }
                if (Health.HasComponent(Root.Owner) && Health[Root.Owner].Dead != 0)
                {
                    Root.Mode = 7;
                    Root.Progress = math.saturate(Impact.DeathElapsed / CorpseDuration);
                }
            }
            Set(Parameters, Graph.SpeedParameter, Root.Speed);
            Set(Parameters, Graph.ModeParameter, Root.Mode);
            Set(Parameters, Graph.WeaponParameter, Root.Weapon);
            Set(Parameters, Graph.VariationParameter, Root.Variation);
            Set(Parameters, Graph.ProgressParameter, Root.Progress);
            Set(Parameters, Graph.AttackParameter, Root.Attack);
            if (Root.Initialized == 0)
            {
                Root.State = Graph.DefaultState;
                Root.PreviousState = Root.State;
                Root.Initialized = 1;
            }
            Root.Time += Delta / Duration(ref Graph, Graph.States[Root.State], Parameters);
            Root.PreviousTime += Delta / Duration(ref Graph, Graph.States[Root.PreviousState], Parameters);
            Root.TransitionTime += Delta;
            for (int Index = 0; Index < Graph.Transitions.Length; Index++)
            {
                CharacterAnimationTransition Transition = Graph.Transitions[Index];
                if ((Transition.Source >= 0 && Transition.Source != Root.State) || (Transition.Destination == Root.State && Transition.CanTransitionToSelf == 0))
                {
                    continue;
                }
                if (Transition.UsesExitTime != 0 && Root.Time < Transition.ExitTime)
                {
                    continue;
                }
                bool bValid = true;
                for (int ConditionIndex = 0; ConditionIndex < Transition.ConditionCount; ConditionIndex++)
                {
                    CharacterAnimationCondition Condition = Graph.Conditions[Transition.FirstCondition + ConditionIndex];
                    float Value = Parameters[Condition.Parameter].Value;
                    switch (Condition.Mode)
                    {
                        case 1: bValid &= Value != 0; break;
                        case 2: bValid &= Value == 0; break;
                        case 3: bValid &= Value > Condition.Threshold; break;
                        case 4: bValid &= Value < Condition.Threshold; break;
                        case 6: bValid &= Value == Condition.Threshold; break;
                        case 7: bValid &= Value != Condition.Threshold; break;
                    }
                }
                if (!bValid)
                {
                    continue;
                }
                Root.PreviousState = Root.State;
                Root.PreviousTime = Root.Time;
                Root.PreviousProgress = Root.LastProgress;
                Root.State = Transition.Destination;
                Root.Time = 0;
                Root.TransitionTime = 0;
                Root.TransitionDuration = Transition.Duration;
                break;
            }
            for (int Index = 0; Index < Parameters.Length; Index++)
            {
                if (Graph.ParameterTypes[Index] == 9)
                {
                    Parameters[Index] = default;
                }
            }
            Root.LastProgress = Root.Progress;
        }
        Root.PreviousPosition = OwnerTransform.Position;
        Root.WorldMatrix = float4x4.TRS(Position, Rotation, new float3(OwnerTransform.Scale * Root.Scale));
        float Weight = 1;
        if (Root.TransitionDuration > 0)
        {
            Weight = math.saturate(Root.TransitionTime / Root.TransitionDuration);
        }
        for (int Bone = 0; Bone < Graph.BoneCount; Bone++)
        {
            CharacterBonePose Pose = SampleState(ref Graph, Root.State, Root.Time, Root.Progress, Parameters, Bone);
            if (Weight < 1)
            {
                CharacterBonePose Previous = SampleState(ref Graph, Root.PreviousState, Root.PreviousTime, Root.PreviousProgress, Parameters, Bone);
                Pose = Blend(Previous, Pose, Weight);
            }
            float4x4 Matrix = float4x4.TRS(Pose.Position, Pose.Rotation, Pose.Scale);
            int Parent = Graph.Parents[Bone];
            if (Parent >= 0)
            {
                Matrix = math.mul(Poses[Parent].Value, Matrix);
            }
            Poses[Bone] = new CharacterPoseMatrix { Value = Matrix };
        }
        float4x4 Grip = float4x4.TRS(Data.WeaponOffset, Data.WeaponRotation, new float3(Data.WeaponScale));
        Hands.Right = math.mul(Root.WorldMatrix, math.mul(Poses[Graph.RightHand].Value, Grip));
        Hands.Left = math.mul(Root.WorldMatrix, math.mul(Poses[Graph.LeftHand].Value, Grip));
    }

    private static void Set(DynamicBuffer<CharacterAnimationParameter> Parameters, int Index, float Value)
    {
        if (Index >= 0)
        {
            Parameters[Index] = new CharacterAnimationParameter { Value = Value };
        }
    }

    private static float Duration(ref CharacterAnimationGraph Graph, CharacterAnimationState State, DynamicBuffer<CharacterAnimationParameter> Parameters)
    {
        SelectMotions(ref Graph, State, Parameters, out int Lower, out int Upper, out float Weight);
        CharacterAnimationMotion First = Graph.Motions[Lower];
        CharacterAnimationMotion Second = Graph.Motions[Upper];
        float Speed = State.Speed;
        if (State.UsesSpeed != 0)
        {
            Speed *= Parameters[State.SpeedParameter].Value;
        }
        return math.max(0.0001f, math.lerp(Graph.Clips[First.Clip].Duration / First.Speed, Graph.Clips[Second.Clip].Duration / Second.Speed, Weight) / math.max(0.0001f, Speed));
    }

    private static CharacterBonePose SampleState(ref CharacterAnimationGraph Graph, int StateIndex, float Time, float Progress,
        DynamicBuffer<CharacterAnimationParameter> Parameters, int Bone)
    {
        CharacterAnimationState State = Graph.States[StateIndex];
        float Normalized = Time;
        if (State.UsesTime != 0)
        {
            Normalized = Parameters[State.TimeParameter].Value;
            if (State.TimeParameter == Graph.ProgressParameter)
            {
                Normalized = Progress;
            }
        }
        SelectMotions(ref Graph, State, Parameters, out int Lower, out int Upper, out float Weight);
        CharacterBonePose First = SampleClip(ref Graph, Graph.Motions[Lower], Normalized, State.UsesTime == 0, Bone);
        if (Lower == Upper)
        {
            return First;
        }
        return Blend(First, SampleClip(ref Graph, Graph.Motions[Upper], Normalized, State.UsesTime == 0, Bone), Weight);
    }

    private static void SelectMotions(ref CharacterAnimationGraph Graph, CharacterAnimationState State,
        DynamicBuffer<CharacterAnimationParameter> Parameters, out int Lower, out int Upper, out float Weight)
    {
        Lower = State.FirstMotion;
        Upper = Lower;
        Weight = 0;
        if (State.MotionCount > 1)
        {
            float Value = Parameters[Graph.Motions[Lower].Parameter].Value;
            for (int Index = 1; Index < State.MotionCount; Index++)
            {
                Upper = State.FirstMotion + Index;
                float Threshold = Graph.Motions[Upper].Threshold;
                if (Value <= Threshold)
                {
                    Weight = math.saturate((Value - Graph.Motions[Lower].Threshold) / (Threshold - Graph.Motions[Lower].Threshold));
                    break;
                }
                Lower = Upper;
            }
        }
    }

    private static CharacterBonePose SampleClip(ref CharacterAnimationGraph Graph, CharacterAnimationMotion Motion, float Normalized, bool bLoop, int Bone)
    {
        CharacterAnimationClip Clip = Graph.Clips[Motion.Clip];
        if (bLoop && Clip.Loop != 0)
        {
            Normalized = math.frac(Normalized);
        }
        float Frame = math.saturate(Normalized) * (Clip.FrameCount - 1);
        int First = (int)Frame;
        int Second = math.min(First + 1, Clip.FrameCount - 1);
        return Blend(Graph.Poses[(Clip.FirstFrame + First) * Graph.BoneCount + Bone], Graph.Poses[(Clip.FirstFrame + Second) * Graph.BoneCount + Bone], Frame - First);
    }

    private static CharacterBonePose Blend(CharacterBonePose First, CharacterBonePose Second, float Weight)
    {
        return new CharacterBonePose { Position = math.lerp(First.Position, Second.Position, Weight), Rotation = math.slerp(First.Rotation, Second.Rotation, Weight), Scale = math.lerp(First.Scale, Second.Scale, Weight) };
    }
}

[BurstCompile]
public partial struct CharacterSkinJob : IJobEntity
{
    [ReadOnly] public BufferLookup<CharacterPoseMatrix> Poses;

    private void Execute(in CharacterSkinData Data, DynamicBuffer<SkinMatrix> Matrices)
    {
        DynamicBuffer<CharacterPoseMatrix> Pose = Poses[Data.Root];
        ref CharacterSkinBinding Binding = ref Data.Binding.Value;
        for (int Index = 0; Index < Matrices.Length; Index++)
        {
            float4x4 Matrix = math.mul(Binding.RootInverse, math.mul(Pose[Binding.Bones[Index]].Value, Binding.BindPoses[Index]));
            Matrices[Index] = new SkinMatrix { Value = new float3x4(Matrix.c0.xyz, Matrix.c1.xyz, Matrix.c2.xyz, Matrix.c3.xyz) };
        }
    }
}

[BurstCompile]
public partial struct CharacterRenderJob : IJobEntity
{
    [ReadOnly] public ComponentLookup<CharacterVisualRoot> Roots;

    private void Execute(ref LocalToWorld Transform, in CharacterRenderTransform Binding)
    {
        Transform.Value = math.mul(Roots[Binding.Root].WorldMatrix, Binding.LocalMatrix);
    }
}

[BurstCompile]
public partial struct CharacterColorJob : IJobEntity
{
    [ReadOnly] public ComponentLookup<CharacterVisualRoot> Roots;
    [ReadOnly] public ComponentLookup<CombatImpactState> Impacts;
    [ReadOnly] public ComponentLookup<EnemyCombatAttackState> Attacks;
    public float FlashDuration;
    public bool bLinear;

    private void Execute(ref URPMaterialPropertyBaseColor Color, in CharacterRenderTransform Binding)
    {
        CharacterVisualRoot Root = Roots[Binding.Root];
        float4 Tint = Root.Color;
        if (Attacks.HasComponent(Root.Owner) && Attacks[Root.Owner].Phase == 1)
        {
            Tint = math.lerp(Tint, new float4(1, 0.2f, 0.1f, 1), 0.6f);
        }
        if (Impacts.HasComponent(Root.Owner))
        {
            Tint.xyz = math.lerp(Tint.xyz, new float3(1.8f), math.saturate(Impacts[Root.Owner].FlashRemaining / FlashDuration));
        }
        if (bLinear)
        {
            Tint = CombatRenderColor.ToLinear(Tint);
        }
        Color.Value = Tint;
    }
}
#endif
