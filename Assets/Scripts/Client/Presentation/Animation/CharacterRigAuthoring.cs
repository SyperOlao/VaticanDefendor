using System;
using System.IO;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

public sealed class CharacterRigAuthoring : MonoBehaviour
{
    [SerializeField] private CharacterAnimationDefinition _definition;
    [SerializeField] private Transform _modelRoot;

    public CharacterAnimationDefinition Definition => _definition;
    public Transform ModelRoot => _modelRoot;

    private sealed class CharacterRigBaker : Baker<CharacterRigAuthoring>
    {
        public override void Bake(CharacterRigAuthoring Authoring)
        {
            CharacterAnimationDefinition Definition = Authoring.Definition;
            if (Definition == null || Definition.Poses == null || Authoring.ModelRoot == null)
            {
                throw new InvalidOperationException("Character rig requires a model and baked animation data: " + Authoring.name);
            }
            DependsOn(Definition);
            DependsOn(Definition.Poses);
            using var Reader = new BinaryReader(new MemoryStream(Definition.Poses.bytes));
            if (Reader.ReadInt32() != 0x5644414E || Reader.ReadInt32() != 1)
            {
                throw new InvalidDataException("Unsupported character animation file: " + Definition.name);
            }
            CharacterAnimationFile File = JsonUtility.FromJson<CharacterAnimationFile>(Reader.ReadString());
            using var Builder = new BlobBuilder(Allocator.Temp);
            ref CharacterAnimationGraph Graph = ref Builder.ConstructRoot<CharacterAnimationGraph>();
            Graph.DefaultState = File.DefaultState;
            Graph.BoneCount = File.BonePaths.Length;
            Graph.RightHand = File.RightHand;
            Graph.LeftHand = File.LeftHand;
            Graph.SpeedParameter = File.SpeedParameter;
            Graph.ModeParameter = File.ModeParameter;
            Graph.WeaponParameter = File.WeaponParameter;
            Graph.VariationParameter = File.VariationParameter;
            Graph.ProgressParameter = File.ProgressParameter;
            Graph.AttackParameter = File.AttackParameter;
            Builder.Construct(ref Graph.Parents, File.Parents);
            Builder.Construct(ref Graph.ParameterDefaults, File.ParameterDefaults);
            Builder.Construct(ref Graph.ParameterTypes, File.ParameterTypes);
            Builder.Construct(ref Graph.Clips, File.Clips);
            Builder.Construct(ref Graph.States, File.States);
            Builder.Construct(ref Graph.Motions, File.Motions);
            Builder.Construct(ref Graph.Transitions, File.Transitions);
            Builder.Construct(ref Graph.Conditions, File.Conditions);
            int Count = Reader.ReadInt32();
            BlobBuilderArray<CharacterBonePose> Poses = Builder.Allocate(ref Graph.Poses, Count);
            for (int Index = 0; Index < Count; Index++)
            {
                Poses[Index] = new CharacterBonePose
                {
                    Position = new float3(Reader.ReadSingle(), Reader.ReadSingle(), Reader.ReadSingle()),
                    Rotation = new quaternion(Reader.ReadSingle(), Reader.ReadSingle(), Reader.ReadSingle(), Reader.ReadSingle()),
                    Scale = new float3(Reader.ReadSingle(), Reader.ReadSingle(), Reader.ReadSingle())
                };
            }
            BlobAssetReference<CharacterAnimationGraph> Animation = Builder.CreateBlobAssetReference<CharacterAnimationGraph>(Allocator.Persistent);
            AddBlobAsset(ref Animation, out _);
            Entity Root = GetEntity(TransformUsageFlags.Dynamic);
            AddComponent(Root, new CharacterAnimationGraphData
            {
                Value = Animation, ContactTime = Definition.ContactTime,
                WeaponRotation = quaternion.EulerXYZ(math.radians((float3)Definition.WeaponRotation)),
                WeaponOffset = Definition.WeaponOffset, WeaponScale = Definition.WeaponScale
            });
            AddComponent(Root, new CharacterVisualRoot { Color = new float4(1), Scale = 1 });
            AddComponent<CharacterHandPose>(Root);
            DynamicBuffer<CharacterPoseMatrix> Matrices = AddBuffer<CharacterPoseMatrix>(Root);
            Matrices.ResizeUninitialized(File.BonePaths.Length);
            DynamicBuffer<CharacterAnimationParameter> Parameters = AddBuffer<CharacterAnimationParameter>(Root);
            foreach (float DefaultValue in File.ParameterDefaults)
            {
                Parameters.Add(new CharacterAnimationParameter { Value = DefaultValue });
            }
        }
    }
}
