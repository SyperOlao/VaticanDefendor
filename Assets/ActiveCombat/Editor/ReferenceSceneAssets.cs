using UnityEditor;
using UnityEngine;
public static partial class ReferenceSceneAssets
{
    private const string Root="Assets/ActiveCombat";
    private static Material Metal(string Name,string Hex,float Metallic=0,float Smoothness=0.35f)
    {
        string Path=Root+"/Materials/Ref"+Name+".mat";
        Material Result=AssetDatabase.LoadAssetAtPath<Material>(Path);
        if(Result==null)
        {
            Result=new Material(Shader.Find("Universal Render Pipeline/Lit"));
            AssetDatabase.CreateAsset(Result,Path);
        }
        Result.color=CombatUIFactory.Hex(Hex);
        Result.SetFloat("_Metallic",Metallic);
        Result.SetFloat("_Smoothness",Smoothness);
        return Result;
    }
    private static GameObject Part(Transform Parent,string Name,Mesh Geometry,Vector3 Position,Material Material)
    {
        GameObject Result=new GameObject(Name,typeof(MeshFilter),typeof(MeshRenderer));
        Result.transform.SetParent(Parent,false);
        Result.transform.localPosition=Position;
        Result.GetComponent<MeshFilter>().sharedMesh=Geometry;
        Result.GetComponent<MeshRenderer>().sharedMaterial=Material;
        return Result;
    }
    private static GameObject Box(Transform Parent,string Name,Vector3 Size,Vector3 Position,Material Material)
    {
        GameObject Result=GameObject.CreatePrimitive(PrimitiveType.Cube);
        Object.DestroyImmediate(Result.GetComponent<Collider>());
        Result.name=Name;
        Result.transform.SetParent(Parent,false);
        Result.transform.localScale=Size;
        Result.transform.localPosition=Position;
        Result.GetComponent<Renderer>().sharedMaterial=Material;
        return Result;
    }
    private static GameObject Save(GameObject Model,string Name)
    {
        int Index=0;
        foreach(MeshFilter Filter in Model.GetComponentsInChildren<MeshFilter>())
        {
            if(string.IsNullOrEmpty(AssetDatabase.GetAssetPath(Filter.sharedMesh)))
            {
                string Path=Root+"/Meshes/"+Name+Index+".asset";
                Filter.sharedMesh.name=System.IO.Path.GetFileNameWithoutExtension(Path);
                Mesh Existing=AssetDatabase.LoadAssetAtPath<Mesh>(Path);
                if(Existing==null)
                {
                    AssetDatabase.CreateAsset(Filter.sharedMesh,Path);
                }
                else
                {
                    Mesh Generated=Filter.sharedMesh;
                    EditorUtility.CopySerialized(Generated,Existing);
                    Filter.sharedMesh=Existing;
                    Object.DestroyImmediate(Generated);
                }
            }
            Index++;
        }
        GameObject Result=PrefabUtility.SaveAsPrefabAsset(Model,Root+"/Prefabs/"+Name+".prefab");
        Object.DestroyImmediate(Model);
        return Result;
    }
    public static void Create(ActiveCombatGame Game,CombatBalance Balance,Material Effect)
    {
        if(!AssetDatabase.IsValidFolder(Root+"/Meshes"))
        {
            AssetDatabase.CreateFolder(Root,"Meshes");
        }
        Game.KnightPrefab=Save(Knight(Effect),"Knight");
        for(int Index=0;Index<3;Index++)
        {
            Game.WeaponPrefabs[Index]=Save(Weapon((CombatWeapon)Index),"Weapon"+(CombatWeapon)Index);
        }
        for(int Index=0;Index<4;Index++)
        {
            Game.EnemyPrefabs[Index]=Save(Enemy((CombatEnemyKind)Index),"Enemy"+(CombatEnemyKind)Index);
        }
        for(int Index=0;Index<4;Index++)
        {
            Game.StructurePrefabs[Index]=Save(Structure((CombatStructureKind)Index),"Structure"+(CombatStructureKind)Index);
        }
        Game.PickupPrefab=Save(Pickup(),"HealthPickup");
        GameObject Arena=new GameObject("ReferenceArena");
        Arena.transform.SetParent(Game.transform,false);
        Box(Arena.transform,"Ground",new Vector3(64.8f,0.02f,64.8f),new Vector3(0,-0.02f,0),Metal("Ground","#263c1e"));
        Material Light=Metal("TileLight","#314c26");
        Material Dark=Metal("TileDark","#2a4220");
        int Half=Balance.GridSize/2;
        for(int CellX=-Half;CellX<=Half;CellX++)
        {
            for(int CellZ=-Half;CellZ<=Half;CellZ++)
            {
                Material Tile=Dark;
                if((CellX+CellZ)%2==0)
                {
                    Tile=Light;
                }
                Box(Arena.transform,"Tile",new Vector3(Balance.CellSize*0.96f,0.01f,Balance.CellSize*0.96f),new Vector3(CellX*Balance.CellSize,0.005f,CellZ*Balance.CellSize),Tile);
            }
        }
        Random.InitState(5701);
        for(int CellX=-Half;CellX<=Half;CellX++)
        {
            for(int CellZ=-Half;CellZ<=Half;CellZ++)
            {
                if(Mathf.Abs(CellX)<=4 && Mathf.Abs(CellZ)<=4 || Random.value>=0.16f)
                {
                    continue;
                }
                bool bTree=Random.value<0.72f;
                GameObject Scenery=new GameObject("Rock");
                if(bTree)
                {
                    Scenery.name="Pine";
                }
                Scenery.transform.SetParent(Game.SceneryRoot,false);
                Scenery.transform.position=new Vector3(CellX,0,CellZ)*Balance.CellSize;
                if(bTree)
                {
                    Metal("Trunk","#5c4033");
                    Metal("PineLower","#1e4620");
                    Metal("PineUpper","#265828");
                    PineMeshAuthoring.ApplyToTree(Scenery);
                }
                else
                {
                    Part(Scenery.transform,"Rock",ReferenceGeometry.Dodecahedron(0.65f),Vector3.up*0.35f,Metal("Rock","#6b7280")).transform.localScale=new Vector3(1.1f,0.75f,0.95f);
                }
                foreach(MeshFilter Filter in Scenery.GetComponentsInChildren<MeshFilter>())
                {
                    if(bTree)
                    {
                        continue;
                    }
                    string Path=Root+"/Meshes/Scenery"+CellX+"_"+CellZ+Filter.name+".asset";
                    Filter.sharedMesh.name=System.IO.Path.GetFileNameWithoutExtension(Path);
                    Mesh Existing=AssetDatabase.LoadAssetAtPath<Mesh>(Path);
                    if(Existing==null)
                    {
                        AssetDatabase.CreateAsset(Filter.sharedMesh,Path);
                    }
                    else
                    {
                        Mesh Generated=Filter.sharedMesh;
                        EditorUtility.CopySerialized(Generated,Existing);
                        Filter.sharedMesh=Existing;
                        Object.DestroyImmediate(Generated);
                    }
                }
            }
        }
    }
    private static GameObject Knight(Material Effect)
    {
        GameObject Model=new GameObject("Knight");
        Part(Model.transform,"Torso",ReferenceGeometry.Cylinder(0.42f,0.48f,1.15f,12),Vector3.up*0.6f,Metal("KnightBlue","#3b82f6",0.4f,0.5f));
        Part(Model.transform,"Helmet",ReferenceGeometry.Sphere(0.36f,12,12),Vector3.up*1.38f,Metal("Helmet","#d1d5db",0.7f,0.7f));
        Box(Model.transform,"Visor",new Vector3(0.45f,0.12f,0.25f),new Vector3(0,1.38f,0.24f),Metal("Visor","#111827"));
        Part(Model.transform,"Plume",ReferenceGeometry.Cylinder(0,0.16f,0.45f,8),new Vector3(0,1.82f,-0.05f),Metal("Plume","#ef4444")).transform.localRotation=Quaternion.Euler(-0.3f*Mathf.Rad2Deg,0,0);
        Box(Model.transform,"Shield",new Vector3(0.14f,0.75f,0.55f),new Vector3(0.48f,0.7f,0.15f),Metal("Shield","#b45309",0.3f,0.3f));
        Transform Pivot=new GameObject("WeaponPivot").transform;
        Pivot.SetParent(Model.transform,false);
        Pivot.localPosition=new Vector3(-0.48f,0.75f,0.2f);
        GameObject Ring=Part(Model.transform,"PlayerRing",ReferenceGeometry.Ring(0.65f,0.78f,32),Vector3.up*0.04f,Effect);
        Material RingMaterial=Metal("PlayerRing","#f59e0b");
        RingMaterial.shader=Effect.shader;
        RingMaterial.SetFloat("_Cull",0);
        Ring.GetComponent<Renderer>().sharedMaterial=RingMaterial;
        return Model;
    }
    private static GameObject Weapon(CombatWeapon Kind)
    {
        GameObject RootModel=new GameObject(Kind.ToString());
        Transform Model=new GameObject("WeaponModel").transform;
        Model.SetParent(RootModel.transform,false);
        Model.localRotation=Quaternion.Euler(Mathf.PI/2.3f*Mathf.Rad2Deg,0,0);
        if(Kind==CombatWeapon.Sword)
        {
            Part(Model,"Hilt",ReferenceGeometry.Cylinder(0.05f,0.05f,0.35f,8),Vector3.zero,Metal("Hilt","#78350f"));
            Box(Model,"Guard",new Vector3(0.38f,0.08f,0.12f),Vector3.up*0.18f,Metal("Guard","#f59e0b",0.7f));
            Box(Model,"Blade",new Vector3(0.12f,1.35f,0.04f),Vector3.up*0.88f,Metal("Blade","#e5e7eb",0.8f,0.8f));
        }
        else if(Kind==CombatWeapon.Spear)
        {
            Part(Model,"Shaft",ReferenceGeometry.Cylinder(0.045f,0.045f,2.3f,8),Vector3.up*0.9f,Metal("Shaft","#92400e"));
            Part(Model,"Tip",ReferenceGeometry.Cylinder(0,0.13f,0.48f,8),Vector3.up*2.25f,Metal("SpearTip","#38bdf8",0.85f,0.8f));
        }
        else
        {
            Part(Model,"Handle",ReferenceGeometry.Cylinder(0.055f,0.055f,1.25f,8),Vector3.up*0.45f,Metal("Hilt","#78350f"));
            Box(Model,"Head",new Vector3(0.65f,0.42f,0.1f),Vector3.up*0.95f,Metal("AxeHead","#ef4444",0.7f,0.7f));
        }
        return RootModel;
    }
    private static GameObject Enemy(CombatEnemyKind Kind)
    {
        GameObject Model=new GameObject(Kind.ToString());
        float Radius=0.42f;
        float Height=0.95f;
        float BodyY=0.48f;
        float HeadY=1.12f;
        string BodyColor="#dc2626";
        string HeadColor="#7ec850";
        if(Kind==CombatEnemyKind.Strong)
        {
            Radius=0.58f;
            Height=1.25f;
            BodyY=0.62f;
            HeadY=1.42f;
            BodyColor="#991b1b";
            HeadColor="#fca5a5";
        }
        else if(Kind==CombatEnemyKind.Spearman)
        {
            BodyColor="#287fae";
        }
        else if(Kind==CombatEnemyKind.Archer)
        {
            BodyColor="#8555b5";
        }
        Part(Model.transform,"Body",ReferenceGeometry.Cylinder(Radius*0.85f,Radius,Height,9),Vector3.up*BodyY,Metal("Enemy"+Kind,BodyColor));
        Part(Model.transform,"Head",ReferenceGeometry.Sphere(Radius*0.65f,8,8),Vector3.up*HeadY,Metal("EnemyHead"+Kind,HeadColor));
        if(Kind==CombatEnemyKind.Spearman)
        {
            Part(Model.transform,"Shaft",ReferenceGeometry.Cylinder(0.055f,0.055f,2.7f,6),new Vector3(0.45f,0.85f,0.85f),Metal("EnemySpearShaft","#9a7147")).transform.localRotation=Quaternion.Euler(90,0,0);
            Part(Model.transform,"Tip",ReferenceGeometry.Cylinder(0,0.16f,0.45f,4),new Vector3(0.45f,0.85f,2.35f),Metal("EnemySpearTip","#d5efff")).transform.localRotation=Quaternion.Euler(90,0,0);
        }
        else if(Kind==CombatEnemyKind.Archer)
        {
            Vector3[] Points=new Vector3[13];
            for(int Index=0;Index<=12;Index++)
            {
                float Angle=-Mathf.PI/2+Index*Mathf.PI/12;
                Points[Index]=new Vector3(0.5f,0.85f+Mathf.Sin(Angle)*0.65f,0.35f+Mathf.Cos(Angle)*0.4f);
            }
            LineRenderer Bow=new GameObject("Bow").AddComponent<LineRenderer>();
            Bow.transform.SetParent(Model.transform,false);
            Bow.useWorldSpace=false;
            Bow.positionCount=Points.Length;
            Bow.SetPositions(Points);
            Bow.startWidth=0.035f;
            Bow.endWidth=0.035f;
            Bow.sharedMaterial=Metal("Bow","#ffc875");
            LineRenderer String=new GameObject("String").AddComponent<LineRenderer>();
            String.transform.SetParent(Model.transform,false);
            String.useWorldSpace=false;
            String.positionCount=2;
            String.SetPositions(new[]
            {
                Points[0],Points[12]
            }
            );
            String.startWidth=0.018f;
            String.endWidth=0.018f;
            String.sharedMaterial=Metal("BowString","#fff1d8");
        }
        return Model;
    }
    private static GameObject Structure(CombatStructureKind Kind)
    {
        GameObject Model=new GameObject(Kind.ToString());
        if(Kind==CombatStructureKind.Tower)
        {
            Box(Model.transform,"Base",new Vector3(1.4f,1.9f,1.4f),Vector3.up*0.95f,Metal("TowerBase","#78716c"));
            Part(Model.transform,"Roof",ReferenceGeometry.Cylinder(0,1.15f,1.1f,4),Vector3.up*2.45f,Metal("TowerRoof","#2563eb")).transform.localRotation=Quaternion.Euler(0,45,0);
        }
        else if(Kind==CombatStructureKind.Wall)
        {
            Box(Model.transform,"Wall",new Vector3(2.1f,1.35f,2.1f),Vector3.up*0.68f,Metal("Wall","#9ca3af"));
        }
        else if(Kind==CombatStructureKind.Trap)
        {
            Box(Model.transform,"Plate",new Vector3(1.8f,0.12f,1.8f),Vector3.up*0.06f,Metal("TrapPlate","#44403c"));
            for(int Horizontal=-1;Horizontal<=1;Horizontal++)
            {
                for(int Vertical=-1;Vertical<=1;Vertical++)
                {
                    Part(Model.transform,"Spike",ReferenceGeometry.Cylinder(0,0.14f,0.55f,6),new Vector3(Horizontal*0.5f,0.34f,Vertical*0.5f),Metal("Spike","#f97316",0.7f));
                }
            }
        }
        else
        {
            Part(Model.transform,"Pole",ReferenceGeometry.Cylinder(0.08f,0.1f,2.2f,8),Vector3.up*1.1f,Metal("Pole","#ca8a04"));
            Box(Model.transform,"Cloth",new Vector3(0.85f,0.95f,0.08f),new Vector3(0.4f,1.65f,0),Metal("Cloth","#22c55e"));
        }
        return Model;
    }
    private static GameObject Pickup()
    {
        GameObject Model=new GameObject("HealthPickup");
        Box(Model.transform,"Case",new Vector3(1,0.55f,0.75f),Vector3.up*0.4f,Metal("PickupCase","#169b64"));
        Box(Model.transform,"Lid",new Vector3(1.06f,0.1f,0.8f),Vector3.up*0.7f,Metal("PickupLid","#0a6544"));
        Box(Model.transform,"CrossHorizontal",new Vector3(0.65f,0.04f,0.18f),Vector3.up*0.78f,Metal("PickupCross","#eefff6"));
        Box(Model.transform,"CrossVertical",new Vector3(0.18f,0.04f,0.57f),Vector3.up*0.78f,Metal("PickupCross","#eefff6"));
        return Model;
    }
}
