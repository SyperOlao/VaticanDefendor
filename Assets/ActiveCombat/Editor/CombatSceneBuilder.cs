using System.IO;
using System.Linq;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.TextCore.LowLevel;
public static class CombatSceneBuilder
{
    private const string AssetRoot="Assets/ActiveCombat";
    private static Material Stone;
    private static Material DarkStone;
    private static Material Steel;
    private static Material Wood;
    private static Material Gold;
    private static Material Blue;
    private static Material Green;
    private static Material Cloth;
    [MenuItem("Active Combat/Create Native Scene")]
    public static void BuildScene()
    {
        if(EditorApplication.isPlaying)
        {
            throw new System.InvalidOperationException("Stop Play Mode before rebuilding the scene.");
        }
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
        CombatBalance Balance=AssetDatabase.LoadAssetAtPath<CombatBalance>(AssetRoot+"/Settings/CombatBalance.asset");
        if(Balance==null)
        {
            Balance=ScriptableObject.CreateInstance<CombatBalance>();
            AssetDatabase.CreateAsset(Balance,AssetRoot+"/Settings/CombatBalance.asset");
        }
        Stone=Material("Stone",new Color(0.45f,0.48f,0.46f));
        DarkStone=Material("DarkStone",new Color(0.19f,0.24f,0.24f));
        Steel=Material("Steel",new Color(0.7f,0.8f,0.84f));
        Wood=Material("Wood",new Color(0.3f,0.19f,0.1f));
        Gold=Material("Brass",new Color(0.8f,0.58f,0.22f));
        Blue=Material("KnightBlue",new Color(0.12f,0.37f,0.64f));
        Green=Material("Foliage",new Color(0.13f,0.32f,0.17f));
        Cloth=Blue;
        Material Effect=Material("TransparentEffects",Color.white);
        Effect.shader=Shader.Find("Universal Render Pipeline/Unlit");
        Effect.SetFloat("_Surface",1);
        Effect.SetFloat("_Blend",0);
        Effect.SetFloat("_SrcBlend",(float)BlendMode.SrcAlpha);
        Effect.SetFloat("_DstBlend",(float)BlendMode.OneMinusSrcAlpha);
        Effect.SetFloat("_ZWrite",0);
        Effect.SetFloat("_Cull",0);
        Effect.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        Effect.renderQueue=3000;
        UniversalRenderPipelineAsset Pipeline=GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
        if(Pipeline==null)
        {
            throw new System.InvalidOperationException("URP is required by the native combat project.");
        }
        Pipeline.supportsHDR=true;
        GameObject Root=new GameObject("ActiveCombat");
        ActiveCombatGame Game=Root.AddComponent<ActiveCombatGame>();
        Game.Balance=Balance;
        Game.EffectMaterial=Effect;
        Game.DynamicRoot=new GameObject("RuntimeActors").transform;
        Game.DynamicRoot.SetParent(Root.transform);
        Game.SceneryRoot=new GameObject("Scenery").transform;
        Game.SceneryRoot.SetParent(Root.transform);
        Game.WeaponPrefabs=new GameObject[3];
        Game.EnemyPrefabs=new GameObject[4];
        Game.StructurePrefabs=new GameObject[4];
        ReferenceSceneAssets.Create(Game,Balance,Effect);
        GameObject CameraObject=new GameObject("Main Camera",typeof(Camera),typeof(AudioListener),typeof(UniversalAdditionalCameraData));
        CameraObject.tag="MainCamera";
        Game.WorldCamera=CameraObject.GetComponent<Camera>();
        Game.WorldCamera.transform.position=new Vector3(0,13.5f,11.5f);
        Game.WorldCamera.transform.LookAt(new Vector3(0,0.45f,0));
        Game.WorldCamera.fieldOfView=48;
        Game.WorldCamera.nearClipPlane=0.1f;
        Game.WorldCamera.farClipPlane=130;
        Game.WorldCamera.backgroundColor=CombatUIFactory.Hex("#162214");
        Game.WorldCamera.clearFlags=CameraClearFlags.SolidColor;
        UniversalAdditionalCameraData CameraData=CameraObject.GetComponent<UniversalAdditionalCameraData>();
        CameraData.renderPostProcessing=true;
        CameraData.volumeLayerMask=1;
        CameraData.antialiasing=AntialiasingMode.FastApproximateAntialiasing;
        GameObject LightObject=new GameObject("Sun",typeof(Light));
        Light Sun=LightObject.GetComponent<Light>();
        Sun.type=LightType.Directional;
        Sun.color=CombatUIFactory.Hex("#fffbeb");
        Sun.intensity=0.9f;
        Sun.shadows=LightShadows.Soft;
        LightObject.transform.rotation=Quaternion.LookRotation(-new Vector3(14,26,16));
        RenderSettings.ambientMode=AmbientMode.Trilight;
        RenderSettings.ambientSkyColor=CombatUIFactory.Hex("#fef3c7")*0.65f;
        RenderSettings.ambientEquatorColor=CombatUIFactory.Hex("#a3a89c")*0.65f;
        RenderSettings.ambientGroundColor=CombatUIFactory.Hex("#1f2937")*0.65f;
        RenderSettings.fog=true;
        RenderSettings.fogMode=FogMode.ExponentialSquared;
        RenderSettings.fogColor=CombatUIFactory.Hex("#162214");
        RenderSettings.fogDensity=0.022f;
        RenderSettings.ambientLight=new Color(0.45f,0.5f,0.55f);
        VolumeProfile Profile=AssetDatabase.LoadAssetAtPath<VolumeProfile>(AssetRoot+"/Settings/PostProcessing.asset");
        if(Profile==null)
        {
            Profile=ScriptableObject.CreateInstance<VolumeProfile>();
            AssetDatabase.CreateAsset(Profile,AssetRoot+"/Settings/PostProcessing.asset");
        }
        if(!Profile.TryGet<Tonemapping>(out Tonemapping Tonemapping))
        {
            Tonemapping=Profile.Add<Tonemapping>(true);
            AssetDatabase.AddObjectToAsset(Tonemapping,Profile);
        }
        Tonemapping.mode.Override(TonemappingMode.None);
        if(!Profile.TryGet<Bloom>(out Bloom Bloom))
        {
            Bloom=Profile.Add<Bloom>(true);
            AssetDatabase.AddObjectToAsset(Bloom,Profile);
        }
        Bloom.intensity.Override(0);
        Bloom.threshold.Override(0.9f);
        if(!Profile.TryGet<Vignette>(out Vignette Vignette))
        {
            Vignette=Profile.Add<Vignette>(true);
            AssetDatabase.AddObjectToAsset(Vignette,Profile);
        }
        Vignette.intensity.Override(0);
        Vignette.smoothness.Override(0.6f);
        GameObject VolumeObject=new GameObject("Global Volume",typeof(Volume));
        Volume Volume=VolumeObject.GetComponent<Volume>();
        Volume.isGlobal=true;
        Volume.sharedProfile=Profile;
        TMP_FontAsset Font=CreateFont();
        CombatUIFactory.RoundSprite=CreateRoundedSprite();
        Game.Hud=CombatUIFactory.Build(Game,Font);
        Game.Hud.Mobile.ApplyLayout(0);
        Canvas.ForceUpdateCanvases();
        Game.Hud.Mobile.ApplyLayout(0);
        Shader BlurShader=Shader.Find("ActiveCombat/PhoneBlur");
        if(BlurShader!=null)
        {
            Material Blur=AssetDatabase.LoadAssetAtPath<Material>(AssetRoot+"/Materials/PhoneBlur.mat");
            if(Blur==null)
            {
                Blur=new Material(BlurShader);
                AssetDatabase.CreateAsset(Blur,AssetRoot+"/Materials/PhoneBlur.mat");
            }
            Game.Hud.Mobile.WheelBlur.material=Blur;
        }
        Texture2D White=new Texture2D(1,1,TextureFormat.RGBA32,false);
        White.SetPixel(0,0,Color.white);
        White.Apply();
        string TexturePath=AssetRoot+"/Materials/UIWhite.asset";
        if(AssetDatabase.LoadAssetAtPath<Texture2D>(TexturePath)!=null)
        {
            Object.DestroyImmediate(White);
            White=AssetDatabase.LoadAssetAtPath<Texture2D>(TexturePath);
        }
        else
        {
            AssetDatabase.CreateAsset(White,TexturePath);
        }
        Sprite WhiteSprite=AssetDatabase.LoadAssetAtPath<Sprite>(AssetRoot+"/Materials/UIWhiteSprite.asset");
        if(WhiteSprite==null)
        {
            WhiteSprite=Sprite.Create(White,new Rect(0,0,1,1),new Vector2(0.5f,0.5f));
            AssetDatabase.CreateAsset(WhiteSprite,AssetRoot+"/Materials/UIWhiteSprite.asset");
        }
        foreach(UnityEngine.UI.Image Image in Game.Hud.GetComponentsInChildren<UnityEngine.UI.Image>(true))
        {
            if(Image.sprite==null)
            {
                Image.sprite=WhiteSprite;
            }
        }
        PlayerSettings.companyName="Local Prototype";
        PlayerSettings.productName="Active Combat";
        PlayerSettings.runInBackground=true;
        PlayerSettings.defaultScreenWidth=460;
        PlayerSettings.defaultScreenHeight=940;
        PlayerSettings.fullScreenMode=FullScreenMode.Windowed;
        PlayerSettings.resizableWindow=true;
        PlayerSettings.SetScriptingBackend(UnityEditor.Build.NamedBuildTarget.Standalone,ScriptingImplementation.Mono2x);
        PlayerSettings.defaultInterfaceOrientation=UIOrientation.Portrait;
        PlayerSettings.allowedAutorotateToPortrait=true;
        PlayerSettings.allowedAutorotateToPortraitUpsideDown=false;
        PlayerSettings.allowedAutorotateToLandscapeLeft=false;
        PlayerSettings.allowedAutorotateToLandscapeRight=false;
        PlayerSettings.SetApplicationIdentifier(UnityEditor.Build.NamedBuildTarget.Android,"com.local.activecombat");
        PlayerSettings.SetApplicationIdentifier(UnityEditor.Build.NamedBuildTarget.iOS,"com.local.activecombat");
        PlayerSettings.Android.minSdkVersion=AndroidSdkVersions.AndroidApiLevel26;
        PlayerSettings.Android.targetSdkVersion=AndroidSdkVersions.AndroidApiLevelAuto;
        PlayerSettings.Android.targetArchitectures=AndroidArchitecture.ARM64;
        PlayerSettings.SetScriptingBackend(UnityEditor.Build.NamedBuildTarget.Android,ScriptingImplementation.IL2CPP);
        PlayerSettings.SetScriptingBackend(UnityEditor.Build.NamedBuildTarget.iOS,ScriptingImplementation.IL2CPP);
        PlayerSettings.iOS.targetOSVersionString="15.0";
        PlayerSettings.iOS.requiresFullScreen=true;
        PlayerSettings.iOS.hideHomeButton=true;
        QualitySettings.SetQualityLevel(0,true);
        Pipeline.msaaSampleCount=4;
        Pipeline.shadowDistance=44;
        SetPortraitPreview(460,940);
        EditorUtility.SetDirty(Profile);
        EditorUtility.SetDirty(Balance);
        EditorSceneManager.SaveScene(UnityEngine.SceneManagement.SceneManager.GetActiveScene(),AssetRoot+"/Scenes/ActiveCombat.unity");
        EditorBuildSettings.scenes=new[]
        {
            new EditorBuildSettingsScene(AssetRoot+"/Scenes/ActiveCombat.unity",true)
        }
        ;
        AssetDatabase.SaveAssets();
        Selection.activeGameObject=Root;
        Debug.Log("Native Active Combat scene created.");
    }
    private static TMP_FontAsset CreateFont()
    {
        string Path=AssetRoot+"/Fonts/ReferenceInter.asset";
        TMP_FontAsset Result=AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(Path);
        if(Result!=null)
        {
            return Result;
        }
        Font Source=AssetDatabase.LoadAssetAtPath<Font>(AssetRoot+"/Fonts/Inter.ttf");
        Result=TMP_FontAsset.CreateFontAsset(Source,48,6,GlyphRenderMode.SDFAA,2048,2048,AtlasPopulationMode.Dynamic,true);
        Result.name="Reference Inter";
        AssetDatabase.CreateAsset(Result,Path);
        string Characters="";
        for(int Code=32;Code<127;Code++)
        {
            Characters+=(char)Code;
        }
        for(int Code=0x400;Code<=0x4ff;Code++)
        {
            Characters+=(char)Code;
        }
        Characters+="−—–×%→•ёЁ∞";
        Result.TryAddCharacters(Characters,out string Missing);
        Result.atlasPopulationMode=AtlasPopulationMode.Static;
        AssetDatabase.AddObjectToAsset(Result.material,Result);
        foreach(Texture2D Atlas in Result.atlasTextures)
        {
            AssetDatabase.AddObjectToAsset(Atlas,Result);
        }
        EditorUtility.SetDirty(Result);
        return Result;
    }
    private static Material Material(string Name,Color Color)
    {
        string Path=AssetRoot+"/Materials/"+Name+".mat";
        Material Result=AssetDatabase.LoadAssetAtPath<Material>(Path);
        if(Result==null)
        {
            Shader Shader=Shader.Find("Universal Render Pipeline/Lit");
            if(Shader==null)
            {
                throw new System.InvalidOperationException("URP Lit shader is unavailable.");
            }
            Result=new Material(Shader);
            AssetDatabase.CreateAsset(Result,Path);
        }
        Result.color=Color;
        Result.SetFloat("_Smoothness",0.15f);
        return Result;
    }
    private static GameObject Primitive(Transform Parent,string Name,PrimitiveType Type,Vector3 Position,Vector3 Scale,Material Material)
    {
        GameObject Model=GameObject.CreatePrimitive(Type);
        Object.DestroyImmediate(Model.GetComponent<Collider>());
        Model.name=Name;
        Model.transform.SetParent(Parent,false);
        Model.transform.localPosition=Position;
        Model.transform.localScale=Scale;
        Model.GetComponent<Renderer>().sharedMaterial=Material;
        return Model;
    }
    private static GameObject SavePrefab(GameObject Model,string Name)
    {
        GameObject Result=PrefabUtility.SaveAsPrefabAsset(Model,AssetRoot+"/Prefabs/"+Name+".prefab");
        Object.DestroyImmediate(Model);
        return Result;
    }
    private static Sprite CreateRoundedSprite()
    {
        string Path=AssetRoot+"/Materials/PhoneRounded.asset";
        Sprite Existing=AssetDatabase.LoadAssetAtPath<Sprite>(Path);
        if(Existing!=null)
        {
            return Existing;
        }
        Texture2D Texture=new Texture2D(32,32,TextureFormat.RGBA32,false);
        Texture.name="PhoneRounded";
        for(int Horizontal=0;Horizontal<32;Horizontal++)
        {
            for(int Vertical=0;Vertical<32;Vertical++)
            {
                float Distance=Vector2.Distance(new Vector2(Horizontal+0.5f,Vertical+0.5f),new Vector2(Mathf.Clamp(Horizontal+0.5f,8,24),Mathf.Clamp(Vertical+0.5f,8,24)));
                Texture.SetPixel(Horizontal,Vertical,new Color(1,1,1,Mathf.Clamp01(8.5f-Distance)));
            }
        }
        Texture.Apply();
        Texture.filterMode=FilterMode.Bilinear;
        Sprite Result=Sprite.Create(Texture,new Rect(0,0,32,32),new Vector2(0.5f,0.5f),100,0,SpriteMeshType.FullRect,new Vector4(8,8,8,8));
        AssetDatabase.CreateAsset(Result,Path);
        AssetDatabase.AddObjectToAsset(Texture,Result);
        return Result;
    }
    public static void SetPortraitPreview(int Width,int Height)
    {
        System.Type SizesType=typeof(Editor).Assembly.GetType("UnityEditor.GameViewSizes");
        System.Type Singleton=typeof(ScriptableSingleton<>).MakeGenericType(SizesType);
        object Sizes=Singleton.GetProperty("instance").GetValue(null);
        System.Reflection.MethodInfo GetGroup=SizesType.GetMethod("GetGroup");
        string GroupName="Standalone";
        if(EditorUserBuildSettings.activeBuildTarget==BuildTarget.Android)
        {
            GroupName="Android";
        }
        else if(EditorUserBuildSettings.activeBuildTarget==BuildTarget.iOS)
        {
            GroupName="iOS";
        }
        object Group=GetGroup.Invoke(Sizes,new object[] {System.Enum.Parse(GetGroup.GetParameters()[0].ParameterType,GroupName)});
        System.Type SizeType=typeof(Editor).Assembly.GetType("UnityEditor.GameViewSize");
        System.Type Kind=typeof(Editor).Assembly.GetType("UnityEditor.GameViewSizeType");
        object Size=System.Activator.CreateInstance(SizeType,new object[]
        {
            System.Enum.ToObject(Kind,1),Width,Height,"Phone "+Width+"x"+Height
        }
        );
        Group.GetType().GetMethod("AddCustomSize").Invoke(Group,new[]
        {
            Size
        }
        );
        int Count=(int)Group.GetType().GetMethod("GetTotalCount").Invoke(Group,null);
        System.Type ViewType=typeof(Editor).Assembly.GetType("UnityEditor.GameView");
        EditorWindow View=EditorWindow.GetWindow(ViewType);
        ViewType.GetProperty("selectedSizeIndex",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.NonPublic).SetValue(View,Count-1);
    }
    [MenuItem("Active Combat/Build Android APK")]
    public static void BuildAndroid()
    {
        Directory.CreateDirectory("Build/Android");
        EditorUserBuildSettings.buildAppBundle=false;
        BuildPlayerOptions Options=new BuildPlayerOptions
        {
            scenes=new[]
            {
                AssetRoot+"/Scenes/ActiveCombat.unity"
            }
            ,locationPathName="Build/Android/ActiveCombat.apk",target=BuildTarget.Android,options=BuildOptions.Development
        }
        ;
        UnityEditor.Build.Reporting.BuildReport Report=BuildPipeline.BuildPlayer(Options);
        if(Report.summary.result!=UnityEditor.Build.Reporting.BuildResult.Succeeded)
        {
            throw new System.Exception("Android APK build failed.");
        }
        Debug.Log("Android APK build succeeded.");
    }
    [MenuItem("Active Combat/Export iOS Xcode Project")]
    public static void BuildIOS()
    {
        Directory.CreateDirectory("Build/iOS");
        BuildPlayerOptions Options=new BuildPlayerOptions
        {
            scenes=new[]
            {
                AssetRoot+"/Scenes/ActiveCombat.unity"
            }
            ,locationPathName="Build/iOS",target=BuildTarget.iOS,options=BuildOptions.Development
        }
        ;
        UnityEditor.Build.Reporting.BuildReport Report=BuildPipeline.BuildPlayer(Options);
        if(Report.summary.result!=UnityEditor.Build.Reporting.BuildResult.Succeeded)
        {
            throw new System.Exception("iOS Xcode export failed.");
        }
        Debug.Log("iOS Xcode project exported successfully.");
    }
    [MenuItem("Active Combat/Build Windows Player")]
    public static void BuildWindows()
    {
        Directory.CreateDirectory("Build/Windows");
        BuildPlayerOptions Options=new BuildPlayerOptions
        {
            scenes=new[]
            {
                AssetRoot+"/Scenes/ActiveCombat.unity"
            }
            ,locationPathName="Build/Windows/ActiveCombat.exe",target=BuildTarget.StandaloneWindows64,options=BuildOptions.Development
        }
        ;
        UnityEditor.Build.Reporting.BuildReport Report=BuildPipeline.BuildPlayer(Options);
        if(Report.summary.result!=UnityEditor.Build.Reporting.BuildResult.Succeeded)
        {
            throw new System.Exception("Windows player build failed.");
        }
        Debug.Log("Windows player build succeeded.");
    }
}
