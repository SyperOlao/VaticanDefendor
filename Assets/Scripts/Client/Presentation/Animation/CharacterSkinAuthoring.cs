using System;
using System.Collections.Generic;
using System.IO;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

[RequireComponent(typeof(SkinnedMeshRenderer))]
public sealed class CharacterSkinAuthoring : MonoBehaviour
{
    [SerializeField] private CharacterRigAuthoring _rig;

    private sealed class CharacterSkinBaker : Baker<CharacterSkinAuthoring>
    {
        public override void Bake(CharacterSkinAuthoring Authoring)
        {
            CharacterRigAuthoring Rig = Authoring._rig;
            DependsOn(Rig.Definition);
            DependsOn(Rig.Definition.Poses);
            using var Reader = new BinaryReader(new MemoryStream(Rig.Definition.Poses.bytes));
            Reader.ReadInt32(); Reader.ReadInt32();
            CharacterAnimationFile File = JsonUtility.FromJson<CharacterAnimationFile>(Reader.ReadString());
            var Indices = new Dictionary<string, int>();
            for (int Index = 0; Index < File.BonePaths.Length; Index++)
            {
                Indices.Add(File.BonePaths[Index], Index);
            }
            SkinnedMeshRenderer Renderer = GetComponent<SkinnedMeshRenderer>();
            DependsOn(Renderer.sharedMesh);
            using var Builder = new BlobBuilder(Allocator.Temp);
            ref CharacterSkinBinding Binding = ref Builder.ConstructRoot<CharacterSkinBinding>();
            Transform[] Bones = Renderer.bones;
            Matrix4x4[] BindPoses = Renderer.sharedMesh.bindposes;
            BlobBuilderArray<int> BoneIndices = Builder.Allocate(ref Binding.Bones, Bones.Length);
            BlobBuilderArray<float4x4> SkinBindPoses = Builder.Allocate(ref Binding.BindPoses, Bones.Length);
            for (int Index = 0; Index < Bones.Length; Index++)
            {
                string Path = RelativePath(Bones[Index], Rig.ModelRoot);
                if (!Indices.TryGetValue(Path, out int BoneIndex))
                {
                    throw new InvalidDataException("Baked animation is missing bone: " + Path);
                }
                BoneIndices[Index] = BoneIndex;
                SkinBindPoses[Index] = BindPoses[Index];
            }
            Binding.RootInverse = math.inverse((float4x4)(Rig.transform.worldToLocalMatrix * Renderer.rootBone.localToWorldMatrix));
            BlobAssetReference<CharacterSkinBinding> Skin = Builder.CreateBlobAssetReference<CharacterSkinBinding>(Allocator.Persistent);
            AddBlobAsset(ref Skin, out _);
            AddComponent(GetEntity(TransformUsageFlags.Dynamic), new CharacterSkinData
            {
                Root = GetEntity(Rig.gameObject, TransformUsageFlags.Dynamic), Binding = Skin
            });
        }

        private static string RelativePath(Transform Bone, Transform Root)
        {
            string Result = "";
            Transform Current = Bone;
            while (Current != Root)
            {
                if (Current == null)
                {
                    throw new InvalidOperationException("Skin bone is outside the imported model.");
                }
                if (Result.Length > 0)
                {
                    Result = Current.name + "/" + Result;
                }
                else
                {
                    Result = Current.name;
                }
                Current = Current.parent;
            }
            return Result;
        }
    }
}
