using Unity.Entities;
using Unity.Mathematics;

public struct CharacterBonePose
{
    public float3 Position;
    public quaternion Rotation;
    public float3 Scale;
}

[System.Serializable]
public struct CharacterAnimationClip
{
    public int FirstFrame;
    public int FrameCount;
    public float Duration;
    public byte Loop;
}

[System.Serializable]
public struct CharacterAnimationMotion
{
    public int Clip;
    public int Parameter;
    public float Threshold;
    public float Speed;
}

[System.Serializable]
public struct CharacterAnimationState
{
    public int FirstMotion;
    public int MotionCount;
    public int SpeedParameter;
    public int TimeParameter;
    public float Speed;
    public byte UsesTime;
    public byte UsesSpeed;
}

[System.Serializable]
public struct CharacterAnimationCondition
{
    public int Parameter;
    public int Mode;
    public float Threshold;
}

[System.Serializable]
public struct CharacterAnimationTransition
{
    public int Source;
    public int Destination;
    public int FirstCondition;
    public int ConditionCount;
    public float Duration;
    public float ExitTime;
    public byte UsesExitTime;
    public byte CanTransitionToSelf;
}

public struct CharacterAnimationGraph
{
    public int DefaultState;
    public int BoneCount;
    public int RightHand;
    public int LeftHand;
    public int SpeedParameter;
    public int ModeParameter;
    public int WeaponParameter;
    public int VariationParameter;
    public int ProgressParameter;
    public int AttackParameter;
    public BlobArray<float> ParameterDefaults;
    public BlobArray<byte> ParameterTypes;
    public BlobArray<int> Parents;
    public BlobArray<CharacterBonePose> Poses;
    public BlobArray<CharacterAnimationClip> Clips;
    public BlobArray<CharacterAnimationState> States;
    public BlobArray<CharacterAnimationMotion> Motions;
    public BlobArray<CharacterAnimationTransition> Transitions;
    public BlobArray<CharacterAnimationCondition> Conditions;
}

public struct CharacterAnimationGraphData : IComponentData
{
    public BlobAssetReference<CharacterAnimationGraph> Value;
    public float ContactTime;
    public quaternion WeaponRotation;
    public float3 WeaponOffset;
    public float WeaponScale;
}

public struct CharacterVisualRoot : IComponentData
{
    public Entity Owner;
    public int State;
    public int PreviousState;
    public float Time;
    public float PreviousTime;
    public float PreviousProgress;
    public float LastProgress;
    public float4x4 WorldMatrix;
    public float4 Color;
    public float Scale;
    public float TransitionTime;
    public float TransitionDuration;
    public float3 PreviousPosition;
    public float Speed;
    public float Progress;
    public int Mode;
    public int Weapon;
    public int Variation;
    public byte Attack;
    public uint AttackSequence;
    public uint HitSequence;
    public byte Initialized;
}

public struct CharacterPoseMatrix : IBufferElementData
{
    public float4x4 Value;
}

public struct CharacterSkinBinding
{
    public BlobArray<int> Bones;
    public BlobArray<float4x4> BindPoses;
    public float4x4 RootInverse;
}

public struct CharacterSkinData : IComponentData
{
    public Entity Root;
    public BlobAssetReference<CharacterSkinBinding> Binding;
}

public struct CharacterVisualPrefab : IBufferElementData
{
    public int Kind;
    public Entity Value;
    public float4 Color;
    public float Scale;
}

public struct CharacterHandPose : IComponentData
{
    public float4x4 Right;
    public float4x4 Left;
}

public struct CharacterAnimationParameter : IBufferElementData
{
    public float Value;
}

public struct CharacterVisualInstance : IComponentData
{
    public Entity Root;
}

public struct CharacterRenderTransform : IComponentData
{
    public Entity Root;
    public float4x4 LocalMatrix;
}

[System.Serializable]
public sealed class CharacterAnimationFile
{
    public string[] BonePaths;
    public int[] Parents;
    public CharacterAnimationClip[] Clips;
    public CharacterAnimationState[] States;
    public CharacterAnimationMotion[] Motions;
    public CharacterAnimationTransition[] Transitions;
    public CharacterAnimationCondition[] Conditions;
    public float[] ParameterDefaults;
    public byte[] ParameterTypes;
    public int DefaultState;
    public int RightHand;
    public int LeftHand;
    public int SpeedParameter;
    public int ModeParameter;
    public int WeaponParameter;
    public int VariationParameter;
    public int ProgressParameter;
    public int AttackParameter;
}
