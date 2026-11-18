using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VaticanDefendor.Content;
using VaticanDefendor.Client.Shell;

public static class ClientPresentationMigration
{
    public static string Install()
    {
        const string ScenePath = "Assets/Scenes/ActiveCombat.unity";
        Scene Scene = SceneManager.GetSceneByPath(ScenePath);
        bool bOpened = !Scene.isLoaded;
        if (bOpened)
        {
            Scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Additive);
        }
        else if (Scene.isDirty)
        {
            throw new InvalidOperationException("ActiveCombat contains unsaved changes; migration did not modify it.");
        }
        try
        {
            const string SettingsPath = "Assets/ActiveCombat/Content/ClientPresentation.asset";
            ClientPresentationDefinition Settings = AssetDatabase.LoadAssetAtPath<ClientPresentationDefinition>(SettingsPath);
            if (Settings == null)
            {
                Settings = ScriptableObject.CreateInstance<ClientPresentationDefinition>();
                AssetDatabase.CreateAsset(Settings, SettingsPath);
            }
            ClientShellView View = null;
            foreach (GameObject Root in Scene.GetRootGameObjects())
            {
                foreach (Renderer Renderer in Root.GetComponentsInChildren<Renderer>(true))
                {
                    Renderer.enabled = false;
                }
                ClientShellView Candidate = Root.GetComponentInChildren<ClientShellView>(true);
                if (Candidate != null)
                {
                    View = Candidate;
                }
            }
            if (View == null)
            {
                throw new InvalidOperationException("ActiveCombat has no client shell.");
            }
            UnityEngine.UI.CanvasScaler Scaler = View.GetComponent<UnityEngine.UI.CanvasScaler>();
            Scaler.uiScaleMode = UnityEngine.UI.CanvasScaler.ScaleMode.ScaleWithScreenSize;
            Scaler.referenceResolution = new Vector2(720, 1280);
            Scaler.screenMatchMode = UnityEngine.UI.CanvasScaler.ScreenMatchMode.Expand;
            foreach (GameObject Root in Scene.GetRootGameObjects())
            {
                foreach (Behaviour Behaviour in Root.GetComponentsInChildren<Behaviour>(true))
                {
                    if (Behaviour != null && Behaviour.GetType().Namespace == "Unity.Cinemachine")
                    {
                        UnityEngine.Object.DestroyImmediate(Behaviour);
                    }
                }
            }
            foreach (GameObject Root in Scene.GetRootGameObjects())
            {
                if (Root.name == "PlayerCameraTarget" || Root.name == "BattleFollowCamera")
                {
                    UnityEngine.Object.DestroyImmediate(Root);
                }
            }
            Transform Authoring = View.transform.parent.Find("EntityAuthoring");
            if (Authoring != null)
            {
                UnityEngine.Object.DestroyImmediate(Authoring.gameObject);
            }
            string[] ObsoleteNames = { "WheelOverlay", "WheelDock", "WheelBlur", "WeaponZone", "EquippedWeapon", "JoystickAction", "WaveStatus", "CombatHelp", "GoldEnemies", "PrestigeBadge", "MapModeControls", "MapSell", "MapTrebuchet", "MapFullscreen", "CollapseMap", "BuildHint", "Training", "Exhaustion", "HitFlash", "HealthState", "CombatHud", "CommandError", "TutorialInstruction", "BattleVitals", "ExpandMap", "DamageEdges", "MapOverview" };
            foreach (Transform Child in View.GetComponentsInChildren<Transform>(true))
            {
                if (Child != null && Array.IndexOf(ObsoleteNames, Child.name) >= 0)
                {
                    UnityEngine.Object.DestroyImmediate(Child.gameObject);
                }
            }
            Transform Palette = View.transform.Find("PhoneSafeArea/SplitContainer/TacticalMap/BuildingPalette");
            UnityEngine.UI.ScrollRect Scroll = Palette.GetComponent<UnityEngine.UI.ScrollRect>();
            for (int Index = Scroll.content.childCount - 1; Index >= 0; Index--)
            {
                UnityEngine.Object.DestroyImmediate(Scroll.content.GetChild(Index).gameObject);
            }
            Transform SettingsWindow = View.transform.Find("PhoneSafeArea/SettingsModal/SettingsWindow");
            RectTransform SettingsBounds = (RectTransform)SettingsWindow;
            SettingsBounds.anchorMin = SettingsBounds.anchorMax = SettingsBounds.pivot = new Vector2(0.5f, 0.5f);
            SettingsBounds.anchoredPosition = Vector2.zero;
            SettingsBounds.sizeDelta = new Vector2(650, 500);
            foreach (TMPro.TMP_Text Label in SettingsWindow.GetComponentsInChildren<TMPro.TMP_Text>(true))
            {
                if (Label.text.Contains("UX-прототипа"))
                {
                    Label.text = "Настройки";
                }
            }
            UnityEngine.UI.ScrollRect SettingsScroll = SettingsWindow.Find("ScrollView").GetComponent<UnityEngine.UI.ScrollRect>();
            UnityEngine.Object.DestroyImmediate(SettingsScroll.content.GetComponent<UnityEngine.UI.VerticalLayoutGroup>());
            UnityEngine.Object.DestroyImmediate(SettingsScroll.content.GetComponent<UnityEngine.UI.ContentSizeFitter>());
            CombatUIFactory.Stretch((RectTransform)SettingsScroll.transform, new Vector2(20, 90), new Vector2(-20, -70));
            CombatUIFactory.Stretch(SettingsScroll.viewport, Vector2.zero, Vector2.zero);
            SettingsScroll.content.anchorMin = new Vector2(0, 1);
            SettingsScroll.content.anchorMax = Vector2.one;
            SettingsScroll.content.pivot = new Vector2(0.5f, 1);
            SettingsScroll.content.anchoredPosition = Vector2.zero;
            SettingsScroll.content.sizeDelta = new Vector2(0, 260);
            for (int Index = SettingsScroll.content.childCount - 1; Index >= 0; Index--)
            {
                UnityEngine.Object.DestroyImmediate(SettingsScroll.content.GetChild(Index).gameObject);
            }
            foreach (string Name in new[] { "Defaults", "ResetMap", "ValidationError" })
            {
                Transform Child = SettingsWindow.Find(Name);
                if (Child != null)
                {
                    UnityEngine.Object.DestroyImmediate(Child.gameObject);
                }
            }
            foreach (string Name in new[] { "CombatInput", "JoystickMovement" })
            {
                Transform Surface = View.transform.Find("PhoneSafeArea/SplitContainer/BattleViewport/" + Name);
                if (Surface != null)
                {
                    foreach (TMPro.TMP_Text Label in Surface.GetComponentsInChildren<TMPro.TMP_Text>(true))
                    {
                        UnityEngine.Object.DestroyImmediate(Label.gameObject);
                    }
                }
            }
            var Serialized = new SerializedObject(View);
            Serialized.FindProperty("_presentation").objectReferenceValue = Settings;
            Serialized.FindProperty("_renderCatalog").objectReferenceValue = AssetDatabase.LoadAssetAtPath<CombatRenderCatalog>("Assets/ActiveCombat/Settings/CombatRenderCatalog.asset");
            Serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorSceneManager.MarkSceneDirty(Scene);
            EditorSceneManager.SaveScene(Scene);
            AssetDatabase.SaveAssets();
            return "Client ECS presentation configured";
        }
        finally
        {
            if (bOpened)
            {
                EditorSceneManager.CloseScene(Scene, true);
            }
        }
    }

    public static string BuildWeaponImages()
    {
        CombatRenderCatalog Catalog = AssetDatabase.LoadAssetAtPath<CombatRenderCatalog>("Assets/ActiveCombat/Settings/CombatRenderCatalog.asset");
        Directory.CreateDirectory("backend/media");
        string[] Names = { "sword", "spear", "axe" };
        for (int Index = 0; Index < Names.Length; Index++)
        {
            var Preview = new PreviewRenderUtility();
            Texture2D Image = null;
            try
            {
                CombatRenderModelDefinition Model = Catalog.Models[Index + 1];
                Bounds Bounds = new Bounds(Vector3.zero, Vector3.zero);
                bool bFirst = true;
                foreach (CombatRenderPartDefinition Part in Model.Parts)
                {
                    Bounds MeshBounds = Catalog.Meshes[Part.Mesh].bounds;
                    for (int Corner = 0; Corner < 8; Corner++)
                    {
                        Vector3 Sign = Vector3.one;
                        if ((Corner & 1) == 0)
                        {
                            Sign.x = -1;
                        }
                        if ((Corner & 2) == 0)
                        {
                            Sign.y = -1;
                        }
                        if ((Corner & 4) == 0)
                        {
                            Sign.z = -1;
                        }
                        Vector3 Point = Part.Matrix.MultiplyPoint3x4(MeshBounds.center + Vector3.Scale(MeshBounds.extents, Sign));
                        if (bFirst)
                        {
                            Bounds = new Bounds(Point, Vector3.zero);
                            bFirst = false;
                        }
                        Bounds.Encapsulate(Point);
                    }
                }
                Preview.cameraFieldOfView = 30;
                Preview.camera.orthographic = true;
                Preview.camera.orthographicSize = Mathf.Max(Bounds.size.x, Bounds.size.y) * 0.65f;
                Preview.camera.transform.position = Bounds.center + new Vector3(0, 0, -3);
                Preview.camera.clearFlags = CameraClearFlags.SolidColor;
                Preview.camera.backgroundColor = new Color(0.08f, 0.1f, 0.12f, 0);
                Preview.camera.transform.LookAt(Bounds.center);
                Preview.camera.nearClipPlane = 0.01f;
                Preview.camera.farClipPlane = 100;
                Preview.lights[0].intensity = 1.2f;
                Preview.lights[0].transform.rotation = Quaternion.Euler(35, 35, 0);
                Preview.lights[1].intensity = 0.8f;
                Preview.BeginStaticPreview(new Rect(0, 0, 256, 256));
                foreach (CombatRenderPartDefinition Part in Model.Parts)
                {
                    Mesh Mesh = Catalog.Meshes[Part.Mesh];
                    for (int Submesh = 0; Submesh < Mesh.subMeshCount; Submesh++)
                    {
                        Preview.DrawMesh(Mesh, Part.Matrix, Catalog.Materials[Part.Materials[Mathf.Min(Submesh, Part.Materials.Length - 1)]], Submesh);
                    }
                }
                Preview.Render(true);
                Image = Preview.EndStaticPreview();
                File.WriteAllBytes("backend/media/" + Names[Index] + ".png", Image.EncodeToPNG());
            }
            finally
            {
                if (Image != null)
                {
                    UnityEngine.Object.DestroyImmediate(Image);
                }
                Preview.Cleanup();
            }
        }
        return "Weapon images rendered";
    }
}
