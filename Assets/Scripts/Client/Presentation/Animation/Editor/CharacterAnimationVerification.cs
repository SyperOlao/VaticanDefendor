using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class CharacterAnimationVerification
{
    [MenuItem("Vatican Defendor/Characters/Verify Baked Poses")]
    public static string Verify()
    {
        int PoseCount = 0;
        foreach (string Identifier in AssetDatabase.FindAssets("t:CharacterAnimationDefinition"))
        {
            CharacterAnimationDefinition Definition = AssetDatabase.LoadAssetAtPath<CharacterAnimationDefinition>(AssetDatabase.GUIDToAssetPath(Identifier));
            if (Definition.SourceHash != CharacterAnimationBaker.SourceHash(Definition))
            {
                throw new InvalidOperationException("Animation source hash is stale: " + Definition.name);
            }
            using var Reader = new BinaryReader(new MemoryStream(Definition.Poses.bytes));
            Reader.ReadInt32(); Reader.ReadInt32();
            CharacterAnimationFile File = JsonUtility.FromJson<CharacterAnimationFile>(Reader.ReadString());
            int Count = Reader.ReadInt32();
            var Poses = new CharacterBonePose[Count];
            for (int Index = 0; Index < Count; Index++)
            {
                Poses[Index] = new CharacterBonePose
                {
                    Position = new Unity.Mathematics.float3(Reader.ReadSingle(), Reader.ReadSingle(), Reader.ReadSingle()),
                    Rotation = new Unity.Mathematics.quaternion(Reader.ReadSingle(), Reader.ReadSingle(), Reader.ReadSingle(), Reader.ReadSingle()),
                    Scale = new Unity.Mathematics.float3(Reader.ReadSingle(), Reader.ReadSingle(), Reader.ReadSingle())
                };
            }
            Scene Preview = EditorSceneManager.NewPreviewScene();
            GameObject Character = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(ShadowCharacterAssets.Root + "/Prefabs/" + Definition.name + ".prefab"));
            SceneManager.MoveGameObjectToScene(Character, Preview);
            try
            {
                Transform Model = Character.transform.GetChild(0);
                Transform[] Bones = File.BonePaths.Select(Path =>
                {
                    if (Path.Length == 0)
                    {
                        return Model;
                    }
                    return Model.Find(Path);
                }).ToArray();
                foreach (CharacterAnimationClip Clip in File.Clips)
                {
                    foreach (int Offset in new[] { 0, Clip.FrameCount / 2, Clip.FrameCount - 1 })
                    {
                        ApplyPose(Bones, Poses, Clip.FirstFrame + Offset);
                        foreach (SkinnedMeshRenderer Renderer in Character.GetComponentsInChildren<SkinnedMeshRenderer>())
                        {
                            var Mesh = new Mesh();
                            Renderer.BakeMesh(Mesh);
                            Vector3 Size = Mesh.bounds.size;
                            if (!float.IsFinite(Size.x) || !float.IsFinite(Size.y) || !float.IsFinite(Size.z) || Size.magnitude > 12 || Size.magnitude < 0.1f)
                            {
                                throw new InvalidDataException("Animation produced an invalid mesh: " + Definition.name + " " + Size);
                            }
                            UnityEngine.Object.DestroyImmediate(Mesh);
                        }
                        PoseCount++;
                    }
                }
                var CameraObject = new GameObject("Verification Camera", typeof(Camera));
                SceneManager.MoveGameObjectToScene(CameraObject, Preview);
                Camera Camera = CameraObject.GetComponent<Camera>();
                Camera.scene = Preview;
                Camera.transform.position = new Vector3(3.5f, 2.2f, 4);
                Camera.transform.LookAt(new Vector3(0, 1, 0));
                Camera.orthographic = true;
                Camera.orthographicSize = 1.6f;
                Camera.clearFlags = CameraClearFlags.SolidColor;
                Camera.backgroundColor = new Color(0.1f, 0.12f, 0.15f);
                var LightObject = new GameObject("Verification Light", typeof(Light));
                SceneManager.MoveGameObjectToScene(LightObject, Preview);
                LightObject.GetComponent<Light>().type = LightType.Directional;
                LightObject.GetComponent<Light>().intensity = 2;
                LightObject.transform.rotation = Quaternion.Euler(35, -35, 0);
                int[] StateIndices = { File.DefaultState, Array.FindIndex(File.States, State => State.UsesTime != 0) };
                SkinnedMeshRenderer[] Renderers = Character.GetComponentsInChildren<SkinnedMeshRenderer>();
                Mesh[] PreviewMeshes = new Mesh[Renderers.Length];
                for (int Index = 0; Index < Renderers.Length; Index++)
                {
                    PreviewMeshes[Index] = new Mesh();
                    var MeshObject = new GameObject("Baked Preview", typeof(MeshFilter), typeof(MeshRenderer));
                    MeshObject.transform.SetParent(Renderers[Index].transform, false);
                    MeshObject.GetComponent<MeshFilter>().sharedMesh = PreviewMeshes[Index];
                    MeshObject.GetComponent<MeshRenderer>().sharedMaterials = Renderers[Index].sharedMaterials;
                    Renderers[Index].enabled = false;
                }
                for (int PreviewIndex = 0; PreviewIndex < StateIndices.Length; PreviewIndex++)
                {
                    CharacterAnimationState State = File.States[StateIndices[PreviewIndex]];
                    CharacterAnimationClip Clip = File.Clips[File.Motions[State.FirstMotion].Clip];
                    int Frame = Clip.FirstFrame + Clip.FrameCount / 2;
                    ApplyPose(Bones, Poses, Frame);
                    for (int Index = 0; Index < Renderers.Length; Index++)
                    {
                        Renderers[Index].BakeMesh(PreviewMeshes[Index]);
                    }
                    var Target = new RenderTexture(640, 640, 24);
                    Camera.targetTexture = Target;
                    Camera.Render();
                    RenderTexture Previous = RenderTexture.active;
                    RenderTexture.active = Target;
                    var Image = new Texture2D(640, 640, TextureFormat.RGB24, false);
                    Image.ReadPixels(new Rect(0, 0, 640, 640), 0, 0);
                    Image.Apply();
                    Directory.CreateDirectory("Builds/ShadowAgesImport/Previews");
                    System.IO.File.WriteAllBytes("Builds/ShadowAgesImport/Previews/" + Definition.name + "-" + PreviewIndex + ".png", Image.EncodeToPNG());
                    RenderTexture.active = Previous;
                    Camera.targetTexture = null;
                    UnityEngine.Object.DestroyImmediate(Image);
                    UnityEngine.Object.DestroyImmediate(Target);
                }
                foreach (Mesh Mesh in PreviewMeshes)
                {
                    UnityEngine.Object.DestroyImmediate(Mesh);
                }
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(Preview);
            }
        }
        return "Verified character poses: " + PoseCount;
    }

    private static void ApplyPose(Transform[] Bones, CharacterBonePose[] Poses, int Frame)
    {
        for (int Index = 0; Index < Bones.Length; Index++)
        {
            CharacterBonePose Pose = Poses[Frame * Bones.Length + Index];
            Bones[Index].localPosition = Pose.Position;
            Bones[Index].localRotation = Pose.Rotation;
            Bones[Index].localScale = Pose.Scale;
        }
    }
}
