using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Unity.Mathematics;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

public static class CharacterAnimationBaker
{
    [MenuItem("Vatican Defendor/Characters/Rebuild ECS Animations")]
    public static void RebuildAll()
    {
        foreach (string Identifier in AssetDatabase.FindAssets("t:CharacterAnimationDefinition"))
        {
            Rebuild(AssetDatabase.LoadAssetAtPath<CharacterAnimationDefinition>(AssetDatabase.GUIDToAssetPath(Identifier)));
        }
        AssetDatabase.SaveAssets();
    }

    public static string SourceHash(CharacterAnimationDefinition Definition)
    {
        string Hash = "ShadowRigPose2" + Definition.Model.name + Definition.SampleRate + Definition.Height + Definition.Rotation;
        foreach (CharacterAnimationClipDefinition Clip in Definition.Clips)
        {
            Hash += Clip.Clip.name + Clip.Loop;
        }
        Hash += AssetDatabase.GetAssetDependencyHash(AssetDatabase.GetAssetPath(Definition.Model));
        Hash += AssetDatabase.GetAssetDependencyHash(AssetDatabase.GetAssetPath(Definition.Controller));
        return Hash128.Compute(Hash).ToString();
    }

    public static void Rebuild(CharacterAnimationDefinition Definition)
    {
        AnimatorController Controller = Definition.Controller as AnimatorController;
        if (Controller == null || Controller.layers.Length != 1 || Controller.layers[0].stateMachine.stateMachines.Length != 0)
        {
            throw new InvalidOperationException("ECS character animations require one Animator layer with a flat state machine: " + Definition.name);
        }
        AssetDatabase.SaveAssetIfDirty(Controller);
        AnimatorStateMachine Machine = Controller.layers[0].stateMachine;
        if (Controller.layers[0].avatarMask != null || Machine.entryTransitions.Length != 0)
        {
            throw new InvalidOperationException("Animator masks and entry transitions are not supported by the ECS animation baker.");
        }
        var Parameters = Controller.parameters.ToList();
        int Parameter(string Name)
        {
            return Parameters.FindIndex(Entry => Entry.name == Name);
        }
        var States = Machine.states.Select(Entry => Entry.state).ToList();
        var Clips = new List<AnimationClip>();
        var Motions = new List<CharacterAnimationMotion>();
        var StateData = new List<CharacterAnimationState>();
        var Transitions = new List<CharacterAnimationTransition>();
        var Conditions = new List<CharacterAnimationCondition>();
        foreach (AnimatorState State in States)
        {
            if (State.behaviours.Length != 0 || State.mirror || State.cycleOffset != 0)
            {
                throw new InvalidOperationException("Animator behaviours, mirroring and cycle offsets require explicit ECS implementations: " + State.name);
            }
            int Start = Motions.Count;
            void AddMotion(Motion Motion, float Threshold, float Speed, int BlendParameter)
            {
                AnimationClip Clip = Motion as AnimationClip;
                if (Clip == null)
                {
                    throw new InvalidOperationException("Only clips and one-level 1D Blend Trees are supported: " + State.name);
                }
                if (Clip.humanMotion)
                {
                    throw new InvalidOperationException("Import source clips as Generic on the Shadow Ages skeleton before baking: " + Clip.name);
                }
                if (!Clips.Contains(Clip))
                {
                    Clips.Add(Clip);
                }
                Motions.Add(new CharacterAnimationMotion { Clip = Clips.IndexOf(Clip), Parameter = BlendParameter, Threshold = Threshold, Speed = Speed });
            }
            if (State.motion is BlendTree Tree)
            {
                if (Tree.blendType != BlendTreeType.Simple1D)
                {
                    throw new InvalidOperationException("ECS character locomotion requires a Simple 1D Blend Tree: " + Tree.name);
                }
                foreach (ChildMotion Child in Tree.children.OrderBy(Child => Child.threshold))
                {
                    if (Child.mirror || Child.cycleOffset != 0)
                    {
                        throw new InvalidOperationException("Blend Tree child mirroring and cycle offsets are not supported.");
                    }
                    AddMotion(Child.motion, Child.threshold, Child.timeScale, Parameter(Tree.blendParameter));
                }
            }
            else
            {
                AddMotion(State.motion, 0, 1, -1);
            }
            StateData.Add(new CharacterAnimationState
            {
                FirstMotion = Start, MotionCount = Motions.Count - Start, Speed = State.speed,
                SpeedParameter = Parameter(State.speedParameter), TimeParameter = Parameter(State.timeParameter),
                UsesSpeed = Convert.ToByte(State.speedParameterActive), UsesTime = Convert.ToByte(State.timeParameterActive)
            });
        }
        void AddTransition(AnimatorStateTransition Transition, int Source)
        {
            if (Transition.isExit || Transition.destinationState == null || !Transition.hasFixedDuration || Transition.offset != 0)
            {
                throw new InvalidOperationException("ECS transitions require a destination state, fixed duration and zero offset.");
            }
            int Start = Conditions.Count;
            foreach (AnimatorCondition Condition in Transition.conditions)
            {
                int Index = Parameter(Condition.parameter);
                if (Index < 0)
                {
                    throw new InvalidOperationException("Unknown Animator parameter: " + Condition.parameter);
                }
                Conditions.Add(new CharacterAnimationCondition { Parameter = Index, Mode = (int)Condition.mode, Threshold = Condition.threshold });
            }
            Transitions.Add(new CharacterAnimationTransition
            {
                Source = Source, Destination = States.IndexOf(Transition.destinationState), FirstCondition = Start,
                ConditionCount = Conditions.Count - Start, Duration = Transition.duration, ExitTime = Transition.exitTime,
                UsesExitTime = Convert.ToByte(Transition.hasExitTime), CanTransitionToSelf = Convert.ToByte(Transition.canTransitionToSelf)
            });
        }
        foreach (AnimatorStateTransition Transition in Machine.anyStateTransitions)
        {
            AddTransition(Transition, -1);
        }
        for (int Index = 0; Index < States.Count; Index++)
        {
            foreach (AnimatorStateTransition Transition in States[Index].transitions)
            {
                AddTransition(Transition, Index);
            }
        }
        GameObject Wrapper = new GameObject("AnimationBake");
        GameObject SourceModel = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(ShadowCharacterAssets.Root + "/Models/Player.fbx"));
        try
        {
            GameObject Model = ShadowCharacterAssets.PrepareModel(Definition, Wrapper.transform);
            Transform[] Bones = Model.GetComponentsInChildren<Transform>();
            string[] Paths = Bones.Select(Bone => AnimationUtility.CalculateTransformPath(Bone, Model.transform)).ToArray();
            var Rest = Bones.Select(Bone => new CharacterBonePose { Position = Bone.localPosition, Rotation = Bone.localRotation, Scale = Bone.localScale }).ToArray();
            var SourceRest = SourceModel.GetComponentsInChildren<Transform>().ToDictionary(Bone => AnimationUtility.CalculateTransformPath(Bone, SourceModel.transform), Bone => new CharacterBonePose { Position = Bone.localPosition, Rotation = Bone.localRotation, Scale = Bone.localScale });
            Transform Pelvis = Bones.First(Bone => Bone.name == "pelvis");
            Transform SourcePelvis = SourceModel.GetComponentsInChildren<Transform>().First(Bone => Bone.name == "pelvis");
            float RetargetScale = Pelvis.localPosition.magnitude / SourcePelvis.localPosition.magnitude;
            var PoseData = new List<CharacterBonePose>();
            var ClipData = new List<CharacterAnimationClip>();
            foreach (AnimationClip Clip in Clips)
            {
                var RotationPaths = new HashSet<string>(AnimationUtility.GetCurveBindings(Clip)
                    .Where(Binding => Binding.propertyName.StartsWith("m_LocalRotation") || Binding.propertyName.StartsWith("localEulerAngles"))
                    .Select(Binding => Binding.path));
                var PositionPaths = new HashSet<string>(AnimationUtility.GetCurveBindings(Clip)
                    .Where(Binding => Binding.propertyName.StartsWith("m_LocalPosition")).Select(Binding => Binding.path));
                int Frames = Mathf.Max(2, Mathf.CeilToInt(Clip.length * Definition.SampleRate) + 1);
                bool bLoop = Clip.isLooping || Clip.name.Contains("Idle") || Clip.name.Contains("Walk") || Clip.name.Contains("Run") || Clip.name.Contains("Jog");
                foreach (CharacterAnimationClipDefinition Entry in Definition.Clips)
                {
                    if (Entry.Clip == Clip)
                    {
                        bLoop = Entry.Loop;
                    }
                }
                ClipData.Add(new CharacterAnimationClip { FirstFrame = PoseData.Count / Bones.Length, FrameCount = Frames, Duration = Clip.length, Loop = Convert.ToByte(bLoop) });
                for (int Frame = 0; Frame < Frames; Frame++)
                {
                    for (int Index = 0; Index < Bones.Length; Index++)
                    {
                        Bones[Index].localPosition = Rest[Index].Position;
                        Bones[Index].localRotation = Rest[Index].Rotation;
                        Bones[Index].localScale = Rest[Index].Scale;
                    }
                    Clip.SampleAnimation(Model, Clip.length * Frame / (Frames - 1));
                    for (int Index = 0; Index < Bones.Length; Index++)
                    {
                        CharacterBonePose Pose = Rest[Index];
                        if (Index > 0 && SourceRest.TryGetValue(Paths[Index], out CharacterBonePose Original))
                        {
                            if (Bones[Index].name != "root" && RotationPaths.Contains(Paths[Index]))
                            {
                                Pose.Rotation = math.mul(math.mul(Rest[Index].Rotation, math.inverse(Original.Rotation)), (quaternion)Bones[Index].localRotation);
                            }
                            if (Bones[Index].name == "pelvis" && PositionPaths.Contains(Paths[Index]))
                            {
                                Pose.Position += ((float3)Bones[Index].localPosition - Original.Position) * RetargetScale;
                            }
                        }
                        PoseData.Add(Pose);
                    }
                }
            }
            var File = new CharacterAnimationFile
            {
                BonePaths = Paths, Parents = Bones.Select(Bone => Array.IndexOf(Bones, Bone.parent)).ToArray(),
                RightHand = Array.FindIndex(Bones, Bone => Bone.name == "hand_r"), LeftHand = Array.FindIndex(Bones, Bone => Bone.name == "hand_l"),
                Clips = ClipData.ToArray(), States = StateData.ToArray(), Motions = Motions.ToArray(), Transitions = Transitions.ToArray(), Conditions = Conditions.ToArray(),
                DefaultState = States.IndexOf(Machine.defaultState),
                ParameterDefaults = Parameters.Select(Entry =>
                {
                    if (Entry.type == AnimatorControllerParameterType.Int)
                    {
                        return (float)Entry.defaultInt;
                    }
                    if (Entry.type == AnimatorControllerParameterType.Bool)
                    {
                        return Convert.ToSingle(Entry.defaultBool);
                    }
                    return Entry.defaultFloat;
                }).ToArray(),
                ParameterTypes = Parameters.Select(Entry => (byte)Entry.type).ToArray(),
                SpeedParameter = Parameter("MoveSpeed"), ModeParameter = Parameter("Mode"), WeaponParameter = Parameter("Weapon"),
                VariationParameter = Parameter("Variation"), ProgressParameter = Parameter("ActionProgress"), AttackParameter = Parameter("Attack")
            };
            string Path = ShadowCharacterAssets.Root + "/Baked/" + Definition.name + ".bytes";
            using (var Writer = new BinaryWriter(System.IO.File.Create(Path)))
            {
                Writer.Write(0x5644414E);
                Writer.Write(1);
                Writer.Write(JsonUtility.ToJson(File));
                Writer.Write(PoseData.Count);
                foreach (CharacterBonePose Pose in PoseData)
                {
                    Writer.Write(Pose.Position.x); Writer.Write(Pose.Position.y); Writer.Write(Pose.Position.z);
                    Writer.Write(Pose.Rotation.value.x); Writer.Write(Pose.Rotation.value.y); Writer.Write(Pose.Rotation.value.z); Writer.Write(Pose.Rotation.value.w);
                    Writer.Write(Pose.Scale.x); Writer.Write(Pose.Scale.y); Writer.Write(Pose.Scale.z);
                }
            }
            AssetDatabase.ImportAsset(Path, ImportAssetOptions.ForceSynchronousImport);
            var Serialized = new SerializedObject(Definition);
            Serialized.FindProperty("_poses").objectReferenceValue = AssetDatabase.LoadAssetAtPath<TextAsset>(Path);
            SerializedProperty Definitions = Serialized.FindProperty("_clips");
            Definitions.arraySize = Clips.Count;
            for (int Index = 0; Index < Clips.Count; Index++)
            {
                SerializedProperty Entry = Definitions.GetArrayElementAtIndex(Index);
                Entry.FindPropertyRelative("Clip").objectReferenceValue = Clips[Index];
                Entry.FindPropertyRelative("Loop").boolValue = ClipData[Index].Loop != 0;
            }
            Serialized.ApplyModifiedPropertiesWithoutUndo();
            Serialized.Update();
            Serialized.FindProperty("_sourceHash").stringValue = SourceHash(Definition);
            Serialized.ApplyModifiedPropertiesWithoutUndo();
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(Wrapper);
            UnityEngine.Object.DestroyImmediate(SourceModel);
        }
    }
}

public sealed class CharacterAnimationBuildValidation : IPreprocessBuildWithReport
{
    public int callbackOrder => 0;

    public void OnPreprocessBuild(BuildReport Report)
    {
        foreach (string Identifier in AssetDatabase.FindAssets("t:CharacterAnimationDefinition"))
        {
            CharacterAnimationDefinition Definition = AssetDatabase.LoadAssetAtPath<CharacterAnimationDefinition>(AssetDatabase.GUIDToAssetPath(Identifier));
            if (Definition.Poses == null || Definition.SourceHash != CharacterAnimationBaker.SourceHash(Definition))
            {
                throw new BuildFailedException("Character animations are stale. Run Vatican Defendor/Characters/Rebuild ECS Animations: " + Definition.name);
            }
        }
    }
}
