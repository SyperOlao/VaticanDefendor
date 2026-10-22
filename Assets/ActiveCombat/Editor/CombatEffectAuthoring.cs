using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
public static class CombatEffectAuthoring
{
    public static GameObject MeshObject(string Name, Transform Parent, Vector3[] Vertices, int[] Triangles, Color Color, Material Material)
    {
        GameObject Model = new GameObject(Name,typeof(MeshFilter),typeof(MeshRenderer));
        Model.transform.SetParent(Parent,false);
        Mesh Geometry = new Mesh
        {
            name=Name+"Geometry"
        }
        ;
        Geometry.vertices=Vertices;
        Geometry.triangles=Triangles;
        Geometry.RecalculateNormals();
        Model.GetComponent<MeshFilter>().sharedMesh=Geometry;
        MeshRenderer Renderer=Model.GetComponent<MeshRenderer>();
        Renderer.sharedMaterial=Material;
        Renderer.shadowCastingMode=ShadowCastingMode.Off;
        Renderer.receiveShadows=false;
        MaterialPropertyBlock Properties = new MaterialPropertyBlock();
        Properties.SetColor("_BaseColor",Color);
        Renderer.SetPropertyBlock(Properties);

        return Model;
    }
    public static GameObject Ring(Transform Parent,float Radius,Color Color,Material Material)
    {
        const int Segments=64;
        Vector3[] Vertices=new Vector3[(Segments+1)*2];
        int[] Triangles=new int[Segments*6];
        for(int Index=0;Index<=Segments;Index++)
        {
            float Angle=Index*Mathf.PI*2/Segments;
            Vector3 Direction=new Vector3(Mathf.Sin(Angle),0,Mathf.Cos(Angle));
            Vertices[Index*2]=Direction*Radius;
            Vertices[Index*2+1]=Direction*(Radius-0.09f);
            if(Index==Segments)
            {
                continue;
            }
            int Triangle=Index*6;
            Triangles[Triangle]=Index*2;
            Triangles[Triangle+1]=Index*2+1;
            Triangles[Triangle+2]=Index*2+2;
            Triangles[Triangle+3]=Index*2+2;
            Triangles[Triangle+4]=Index*2+1;
            Triangles[Triangle+5]=Index*2+3;
        }
        return MeshObject("AreaRing",Parent,Vertices,Triangles,Color,Material);
    }
    public static GameObject Zone(Transform Parent,float Radius,float HalfWidth,CombatWeapon Kind,Color Color,Material Material,bool bCircle,float TipWidthScale = 1)
    {
        if(Kind==CombatWeapon.Spear && !bCircle)
        {
            return MeshObject("ThrustZone",Parent,new[]
            {
                new Vector3(-HalfWidth,0,0),new Vector3(-HalfWidth * TipWidthScale,0,Radius),new Vector3(HalfWidth * TipWidthScale,0,Radius),new Vector3(HalfWidth,0,0)
            }
            ,new[]
            {
                0,1,2,0,2,3
            }
            ,Color,Material);
        }
        List<Vector3> Vertices=new List<Vector3>
        {
            Vector3.zero
        }
        ;
        List<int> Triangles=new List<int>();
        float Arc=90;
        if(bCircle)
        {
            Arc=360;
        }
        for(int Index=0;Index<=48;Index++)
        {
            float Angle=(-Arc/2+Arc*Index/48)*Mathf.Deg2Rad;
            Vertices.Add(new Vector3(Mathf.Sin(Angle),0,Mathf.Cos(Angle))*Radius);
            if(Index>0)
            {
                Triangles.Add(0);
                Triangles.Add(Index);
                Triangles.Add(Index+1);
            }
        }
        return MeshObject("AttackZone",Parent,Vertices.ToArray(),Triangles.ToArray(),Color,Material);
    }
    public static GameObject Tile(Transform Parent,float Size,Color Color,Material Material)
    {
        return MeshObject("TargetCell",Parent,new[]
        {
            new Vector3(-Size/2,0,-Size/2),new Vector3(-Size/2,0,Size/2),new Vector3(Size/2,0,Size/2),new Vector3(Size/2,0,-Size/2)
        }
        ,new[]
        {
            0,1,2,0,2,3
        }
        ,Color,Material);
    }
    public static GameObject Arrow(Transform Parent,bool bFriendly,Material Material)
    {
        Color Color=new Color(1,0.35f,0.2f);
        if(bFriendly)
        {
            Color=new Color(1,0.9f,0.5f);
        }
        return MeshObject("Arrow",Parent,new[]
        {
            new Vector3(-0.045f,0,-0.5f),new Vector3(0.045f,0,-0.5f),new Vector3(0.045f,0,0.2f),new Vector3(0.15f,0,0.2f),new Vector3(0,0,0.55f),new Vector3(-0.15f,0,0.2f),new Vector3(-0.045f,0,0.2f)
        }
        ,new[]
        {
            0,1,2,0,2,6,3,4,5
        }
        ,Color,Material);
    }
    private static GameObject Geometry(Transform Parent,string Name,Mesh Mesh,Vector3 Position,Color Color,Material Material)
    {
        GameObject Result=MeshObject(Name,Parent,Mesh.vertices,Mesh.triangles,Color,Material);
        Object.DestroyImmediate(Mesh);
        Result.transform.localPosition=Position;
        return Result;
    }
    public static GameObject Charge(Transform Parent,Color Color,Material Material)
    {
        GameObject Model=new GameObject("ChargePreparation");
        Model.transform.SetParent(Parent,false);
        Ring(Model.transform,1,Color,Material).transform.localPosition=Vector3.up*0.12f;
        for(int Index=0;Index<8;Index++)
        {
            Vector3[] Vertices=
            {
                Vector3.up*0.08f,Vector3.down*0.08f,Vector3.left*0.08f,Vector3.right*0.08f,Vector3.forward*0.08f,Vector3.back*0.08f
            }
            ;
            MeshObject("ChargeSpark",Model.transform,Vertices,new[]
            {
                0,2,4,0,4,3,0,3,5,0,5,2,1,4,2,1,3,4,1,5,3,1,2,5
            }
            ,Color,Material);
        }
        return Model;
    }
    public static GameObject Special(Transform Parent,WeaponBalance Weapon,float Radius,bool bOffensive,Material Material)
    {
        GameObject Model=new GameObject("Special"+Weapon.Kind);
        Model.transform.SetParent(Parent,false);
        Color Color=CombatUIFactory.Hex("#ffdc80");
        if(Weapon.Kind==CombatWeapon.Spear)
        {
            Color=CombatUIFactory.Hex("#67e8f9");
        }
        if(Weapon.Kind==CombatWeapon.Axe)
        {
            Color=CombatUIFactory.Hex("#fb923c");
            if(!bOffensive)
            {
                Color=CombatUIFactory.Hex("#86efac");
            }
        }
        Geometry(Model.transform,"SpecialRing",ReferenceGeometry.Ring(Radius*0.94f,Radius,64),Vector3.up*0.12f,Color,Material);
        for(int Index=0;Index<8;Index++)
        {
            float Angle=Index*Mathf.PI/4;
            Mesh Shape=ReferenceGeometry.Cylinder(0,0.23f,1.5f,4);
            if(Weapon.Kind==CombatWeapon.Axe)
            {
                Object.DestroyImmediate(Shape);
                Shape=ReferenceGeometry.Cylinder(0,0.45f,1.7f,5);
            }
            if(Weapon.Kind==CombatWeapon.Sword)
            {
                Object.DestroyImmediate(Shape);
                GameObject Temporary=GameObject.CreatePrimitive(PrimitiveType.Cube);
                Shape=Object.Instantiate(Temporary.GetComponent<MeshFilter>().sharedMesh);
                Object.DestroyImmediate(Temporary);
            }
            GameObject Part=Geometry(Model.transform,"SpecialPart",Shape,new Vector3(Mathf.Sin(Angle)*Radius*0.8f,0.65f,Mathf.Cos(Angle)*Radius*0.8f),Color,Material);
            Part.transform.localRotation=Quaternion.Euler(0,Angle*Mathf.Rad2Deg,0);
            if(Weapon.Kind==CombatWeapon.Sword)
            {
                Part.transform.localScale=new Vector3(0.14f,0.18f,1.5f);
            }
        }
        if(!bOffensive && Weapon.Kind==CombatWeapon.Sword)
        {
            for(int Index=0;Index<12;Index++)
            {
                GameObject Meridian=Ring(Model.transform,1.4f,new Color(Color.r,Color.g,Color.b,0.18f),Material);
                Meridian.name="ShieldDome";
                Meridian.transform.localPosition=Vector3.up*0.8f;
                Meridian.transform.localRotation=Quaternion.Euler(90,Index*15,0);
            }
        }
        return Model;
    }
    public static GameObject Defense(Transform Parent,WeaponBalance Weapon,Material Material)
    {
        return Special(Parent,Weapon,Weapon.DefenseRadius,false,Material);
    }
    public static void SetOpacity(GameObject Model,float Opacity)
    {
        if(Model==null)
        {
            return;
        }
        foreach(Renderer Renderer in Model.GetComponentsInChildren<Renderer>())
        {
            MaterialPropertyBlock Properties=new MaterialPropertyBlock();
            Renderer.GetPropertyBlock(Properties);
            Color Color=Properties.GetColor("_BaseColor");
            if(Color==default(Color))
            {
                Color=Renderer.sharedMaterial.color;
            }
            Color.a=Opacity;
            Properties.SetColor("_BaseColor",Color);
            Renderer.SetPropertyBlock(Properties);
        }
    }
}
