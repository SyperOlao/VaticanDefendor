using Unity.Cinemachine;
using Unity.Cinemachine.TargetTracking;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class MergedFoundationInstaller
{
    public static void Install()
    {
        if (EditorApplication.isPlaying)
        {
            throw new System.InvalidOperationException("Stop Play Mode before installing the foundation.");
        }
        ActiveCombatGame Game = Object.FindFirstObjectByType<ActiveCombatGame>();
        GameRoot Root = Game.GetComponent<GameRoot>();
        if (Root == null)
        {
            Root = Game.gameObject.AddComponent<GameRoot>();
        }
        Root.Game = Game;
        Game.Root = Root;
        CombatTouchRouter Router = Game.GetComponent<CombatTouchRouter>();
        if (Router == null)
        {
            Router = Game.gameObject.AddComponent<CombatTouchRouter>();
        }
        Router.Root = Root;
        Root.Navigation = Game.GetComponent<CombatNavigation>();
        if (Root.Navigation == null)
        {
            Root.Navigation = Game.gameObject.AddComponent<CombatNavigation>();
        }
        Root.Navigation.Game = Game;
        BuildingStatusPresenter Buildings = Game.GetComponent<BuildingStatusPresenter>();
        if (Buildings == null)
        {
            Buildings = Game.gameObject.AddComponent<BuildingStatusPresenter>();
        }
        Buildings.Game = Game;
        if (Root.CameraPresenter == null)
        {
            GameObject Target = new GameObject("PlayerCameraTarget");
            GameObject CameraObject = new GameObject("BattleFollowCamera");
            CinemachineCamera FollowCamera = CameraObject.AddComponent<CinemachineCamera>();
            FollowCamera.Follow = Target.transform;
            FollowCamera.Lens.FieldOfView = 48;
            CameraObject.transform.rotation = Quaternion.LookRotation(new Vector3(0, 0.45f - 13, -11.2f));
            CinemachineFollow Follow = CameraObject.AddComponent<CinemachineFollow>();
            Follow.FollowOffset = new Vector3(0, 13, 11.2f);
            Follow.TrackerSettings.BindingMode = BindingMode.WorldSpace;
            Follow.TrackerSettings.PositionDamping = Vector3.one * 0.2f;
            CinemachineBrain Brain = Game.WorldCamera.GetComponent<CinemachineBrain>();
            if (Brain == null)
            {
                Brain = Game.WorldCamera.gameObject.AddComponent<CinemachineBrain>();
            }
            Brain.IgnoreTimeScale = true;
            Root.CameraPresenter = CameraObject.AddComponent<CombatCameraPresenter>();
            Root.CameraPresenter.Game = Game;
            Root.CameraPresenter.FollowCamera = FollowCamera;
            Root.CameraPresenter.FollowTarget = Target.transform;
        }
        if (Root.BattleSurface == null)
        {
            GameObject ImageObject = new GameObject("BattleRenderSurface", typeof(RectTransform), typeof(UnityEngine.UI.RawImage));
            ImageObject.transform.SetParent(Game.Hud.Mobile.BattlePane, false);
            ImageObject.transform.SetAsFirstSibling();
            UnityEngine.UI.RawImage Image = ImageObject.GetComponent<UnityEngine.UI.RawImage>();
            Image.raycastTarget = false;
            CombatUIFactory.Stretch(Image.rectTransform, Vector2.zero, Vector2.zero);
            Root.BattleSurface = ImageObject.AddComponent<CombatRenderSurface>();
            Root.BattleSurface.OutputCamera = Game.WorldCamera;
            Root.BattleSurface.Image = Image;
        }
        Game.Balance.InitialGold = 2000;
        Game.Balance.Structures[0].Price = 50;
        Game.Balance.Structures[1].Health = 130;
        if (Game.Balance.Structures.Length < 7)
        {
            System.Array.Resize(ref Game.Balance.Structures, 7);
            Game.Balance.Structures[4] = new StructureBalance { Name = "Оружейная", Kind = CombatStructureKind.Armory, Price = 60, Health = 250 };
            Game.Balance.Structures[5] = new StructureBalance { Name = "Лесопилка", Kind = CombatStructureKind.Sawmill, Price = 50, Health = 180 };
            Game.Balance.Structures[6] = new StructureBalance { Name = "Церковь", Kind = CombatStructureKind.Church, Health = 600 };
            System.Array.Resize(ref Game.StructurePrefabs, 7);
            for (int Index = 4; Index < 7; Index++)
            {
                GameObject Building = Object.Instantiate(Game.StructurePrefabs[0]);
                Building.name = Game.Balance.Structures[Index].Kind.ToString();
                Building.transform.localScale = new Vector3(1.5f, 1.2f, 1.5f);
                Game.StructurePrefabs[Index] = PrefabUtility.SaveAsPrefabAsset(Building, "Assets/ActiveCombat/Prefabs/" + Building.name + ".prefab");
                Object.DestroyImmediate(Building);
            }
        }
        MobileCombatInterface Mobile = Game.Hud.Mobile;
        ReferenceSceneAssets.CreateMergedBuildings(Game);
        ReferenceSceneAssets.CreateProgressionAppearance(Game);
        if (Mobile.WheelLabels.Length < 8)
        {
            int PreviousCount = Mobile.WheelLabels.Length;
            System.Array.Resize(ref Mobile.WheelLabels, 8);
            System.Array.Resize(ref Mobile.WheelIcons, 8);
            for (int Index = PreviousCount; Index < 8; Index++)
            {
                Mobile.WheelLabels[Index] = Object.Instantiate(Mobile.WheelLabels[0], Mobile.WheelLabels[0].transform.parent);
                Mobile.WheelIcons[Index] = Object.Instantiate(Mobile.WheelIcons[0], Mobile.WheelIcons[0].transform.parent);
            }
        }
        if (Root.MapPresenter == null)
        {
            GameObject MapCameraObject = new GameObject("TacticalMapCamera", typeof(Camera));
            Camera MapCamera = MapCameraObject.GetComponent<Camera>();
            MapCamera.orthographic = true;
            MapCamera.cullingMask = 1 << 31;
            MapCamera.clearFlags = CameraClearFlags.SolidColor;
            MapCamera.backgroundColor = new Color(0.09f, 0.14f, 0.08f);
            MapCamera.transform.SetPositionAndRotation(new Vector3(0, 100, 0), Quaternion.Euler(90, 180, 0));
            MapCamera.nearClipPlane = 0.1f;
            MapCamera.farClipPlane = 150;
            Game.WorldCamera.cullingMask &= ~(1 << 31);
            CombatRenderSurface Surface = Game.Hud.MapImage.gameObject.AddComponent<CombatRenderSurface>();
            Surface.OutputCamera = MapCamera;
            Surface.Image = Game.Hud.MapImage;
            Root.MapPresenter = MapCameraObject.AddComponent<TacticalMapPresenter>();
            Root.MapPresenter.Game = Game;
            Root.MapPresenter.Surface = Surface;
            foreach (MobileGestureSurface Existing in Game.Hud.MapImage.GetComponents<MobileGestureSurface>())
            {
                Object.DestroyImmediate(Existing);
            }
            TacticalMapInput Input = Game.Hud.MapImage.gameObject.AddComponent<TacticalMapInput>();
            Input.Map = Root.MapPresenter;
        }
        CinemachineBasicMultiChannelPerlin Noise = Root.CameraPresenter.FollowCamera.GetComponent<CinemachineBasicMultiChannelPerlin>();
        if (Noise == null)
        {
            Noise = Root.CameraPresenter.FollowCamera.gameObject.AddComponent<CinemachineBasicMultiChannelPerlin>();
        }
        NoiseSettings ImpactProfile = AssetDatabase.LoadAssetAtPath<NoiseSettings>("Assets/ActiveCombat/Materials/CombatImpact.asset");
        if (ImpactProfile == null)
        {
            ImpactProfile = ScriptableObject.CreateInstance<NoiseSettings>();
            ImpactProfile.PositionNoise = new[]
            {
                new NoiseSettings.TransformNoiseParams
                {
                    X = new NoiseSettings.NoiseParams { Frequency = 17, Amplitude = 0.36f, Constant = true },
                    Y = new NoiseSettings.NoiseParams { Frequency = 14, Amplitude = 0.14f, Constant = true }
                }
            };
            AssetDatabase.CreateAsset(ImpactProfile, "Assets/ActiveCombat/Materials/CombatImpact.asset");
        }
        Noise.NoiseProfile = ImpactProfile;
        Noise.AmplitudeGain = 0;
        Root.CameraPresenter.ImpactNoise = Noise;
        Game.Balance.bSpecialReadyEdgeEffect = false;
        Game.Balance.Weapons[1].Radius = 4.5f;
        Game.Balance.Weapons[1].HalfWidth = 0.4f;
        Game.Balance.Weapons[1].TipWidthScale = 1.8f;
        Game.Balance.Weapons[1].DashDistance = 5.0625f;
        Material MapMaterial = AssetDatabase.LoadAssetAtPath<Material>("Assets/ActiveCombat/Materials/TacticalMap.mat");
        if (MapMaterial == null)
        {
            MapMaterial = new Material(Shader.Find("ActiveCombat/TacticalMap"));
            AssetDatabase.CreateAsset(MapMaterial, "Assets/ActiveCombat/Materials/TacticalMap.mat");
        }
        Root.MapPresenter.SourceMaterial = MapMaterial;
        Root.MapPresenter.bOverview = false;
        MergedHudInstaller.Install(Game);
        EditorUtility.SetDirty(Game.Balance);
        EditorSceneManager.MarkSceneDirty(Game.gameObject.scene);
        EditorSceneManager.SaveScene(Game.gameObject.scene);
        AssetDatabase.SaveAssets();
        CombatDotsInstaller.Configure();
    }
}
