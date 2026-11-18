using System.Collections.Generic;
using UnityEngine;
public static class ReferenceGeometry
{
    public static Mesh Cylinder(float TopRadius,float BottomRadius,float Height,int Segments)
    {
        List<Vector3> Vertices=new List<Vector3>();
        List<Vector3> Normals=new List<Vector3>();
        List<int> Triangles=new List<int>();
        for(int Index=0;Index<=Segments;Index++)
        {
            float Angle=Index*Mathf.PI*2/Segments;
            Vector3 Direction=new Vector3(Mathf.Sin(Angle),0,Mathf.Cos(Angle));
            Vertices.Add(Direction*BottomRadius-Vector3.up*Height/2);
            Vertices.Add(Direction*TopRadius+Vector3.up*Height/2);
            Vector3 Normal=(Direction+Vector3.up*(BottomRadius-TopRadius)/Height).normalized;
            Normals.Add(Normal);
            Normals.Add(Normal);
            if(Index<Segments)
            {
                int First=Index*2;
                Triangles.AddRange(new[]
                {
                    First,First+3,First+1,First,First+2,First+3
                }
                );
            }
        }
        for(int Side=0;Side<2;Side++)
        {
            float Radius=BottomRadius;
            float Vertical=-Height/2;
            Vector3 Normal=Vector3.down;
            if(Side==1)
            {
                Radius=TopRadius;
                Vertical=Height/2;
                Normal=Vector3.up;
            }
            int Center=Vertices.Count;
            Vertices.Add(new Vector3(0,Vertical,0));
            Normals.Add(Normal);
            for(int Index=0;Index<=Segments;Index++)
            {
                float Angle=Index*Mathf.PI*2/Segments;
                Vertices.Add(new Vector3(Mathf.Sin(Angle)*Radius,Vertical,Mathf.Cos(Angle)*Radius));
                Normals.Add(Normal);
                if(Index==0)
                {
                    continue;
                }
                if(Side==0)
                {
                    Triangles.AddRange(new[]
                    {
                        Center,Center+Index+1,Center+Index
                    }
                    );
                }
                else
                {
                    Triangles.AddRange(new[]
                    {
                        Center,Center+Index,Center+Index+1
                    }
                    );
                }
            }
        }
        Mesh Geometry=new Mesh
        {
            name="ReferenceCylinder"
        }
        ;
        Geometry.SetVertices(Vertices);
        Geometry.SetNormals(Normals);
        Geometry.SetTriangles(Triangles,0);
        Geometry.RecalculateBounds();
        return Geometry;
    }
    public static Mesh Sphere(float Radius,int Width,int Height,float Arc = Mathf.PI)
    {
        List<Vector3> Vertices=new List<Vector3>();
        List<Vector3> Normals=new List<Vector3>();
        List<int> Triangles=new List<int>();
        for(int Latitude=0;Latitude<=Height;Latitude++)
        {
            float Vertical=Latitude*Arc/Height;
            for(int Longitude=0;Longitude<=Width;Longitude++)
            {
                float Angle=Longitude*Mathf.PI*2/Width;
                Vector3 Direction=new Vector3(-Mathf.Cos(Angle)*Mathf.Sin(Vertical),Mathf.Cos(Vertical),Mathf.Sin(Angle)*Mathf.Sin(Vertical));
                Vertices.Add(Direction*Radius);
                Normals.Add(Direction);
                if(Latitude==Height || Longitude==Width)
                {
                    continue;
                }
                int First=Latitude*(Width+1)+Longitude;
                Triangles.AddRange(new[]
                {
                    First,First+Width+1,First+1,First+1,First+Width+1,First+Width+2
                }
                );
            }
        }
        Mesh Geometry=new Mesh
        {
            name="ReferenceSphere"
        }
        ;
        Geometry.SetVertices(Vertices);
        Geometry.SetNormals(Normals);
        Geometry.SetTriangles(Triangles,0);
        Geometry.RecalculateBounds();
        return Geometry;
    }
    public static Mesh Ring(float Inner,float Outer,int Segments)
    {
        List<Vector3> Vertices=new List<Vector3>();
        List<int> Triangles=new List<int>();
        for(int Index=0;Index<=Segments;Index++)
        {
            float Angle=Index*Mathf.PI*2/Segments;
            Vector3 Direction=new Vector3(Mathf.Sin(Angle),0,Mathf.Cos(Angle));
            Vertices.Add(Direction*Inner);
            Vertices.Add(Direction*Outer);
            if(Index<Segments)
            {
                int First=Index*2;
                Triangles.AddRange(new[]
                {
                    First,First+3,First+1,First,First+2,First+3
                }
                );
            }
        }
        Mesh Geometry=new Mesh
        {
            name="ReferenceRing"
        }
        ;
        Geometry.SetVertices(Vertices);
        Geometry.SetTriangles(Triangles,0);
        Geometry.RecalculateNormals();
        return Geometry;
    }
    public static Mesh Dodecahedron(float Radius)
    {
        float Wooden=(1+Mathf.Sqrt(5))/2;
        List<Vector3> Points=new List<Vector3>();
        foreach(float First in new[]
        {
            -1f,1f
        }
        )
        {
            foreach(float Second in new[]
            {
                -1f,1f
            }
            )
            {
                foreach(float Third in new[]
                {
                    -1f,1f
                }
                )
                {
                    Points.Add(new Vector3(First,Second,Third));
                }
                Points.Add(new Vector3(0,First/Wooden,Second*Wooden));
                Points.Add(new Vector3(First/Wooden,Second*Wooden,0));
                Points.Add(new Vector3(First*Wooden,0,Second/Wooden));
            }
        }
        List<Vector3> Vertices=new List<Vector3>();
        List<int> Triangles=new List<int>();
        HashSet<string> Faces=new HashSet<string>();
        for(int First=0;First<Points.Count;First++)
        {
            for(int Second=First+1;Second<Points.Count;Second++)
            {
                for(int Third=Second+1;Third<Points.Count;Third++)
                {
                    Vector3 Normal=Vector3.Cross(Points[Second]-Points[First],Points[Third]-Points[First]).normalized;
                    if(Normal.sqrMagnitude<0.1f)
                    {
                        continue;
                    }
                    float Distance=Vector3.Dot(Normal,Points[First]);
                    if(Distance<0)
                    {
                        Normal=-Normal;
                        Distance=-Distance;
                    }
                    bool bOutside=false;
                    List<int> Face=new List<int>();
                    for(int Index=0;Index<Points.Count;Index++)
                    {
                        float Offset=Vector3.Dot(Normal,Points[Index])-Distance;
                        if(Offset>0.001f)
                        {
                            bOutside=true;
                            break;
                        }
                        if(Mathf.Abs(Offset)<0.001f)
                        {
                            Face.Add(Index);
                        }
                    }
                    if(bOutside || Face.Count!=5)
                    {
                        continue;
                    }
                    string Key=string.Join(",",Face);
                    if(!Faces.Add(Key))
                    {
                        continue;
                    }
                    Vector3 Center=Normal*Distance;
                    Vector3 Tangent=(Points[Face[0]]-Center).normalized;
                    Vector3 Side=Vector3.Cross(Normal,Tangent);
                    Face.Sort((Left,Right)=>Mathf.Atan2(Vector3.Dot(Points[Left]-Center,Side),Vector3.Dot(Points[Left]-Center,Tangent)).CompareTo(Mathf.Atan2(Vector3.Dot(Points[Right]-Center,Side),Vector3.Dot(Points[Right]-Center,Tangent))));
                    int Start=Vertices.Count;
                    foreach(int Index in Face)
                    {
                        Vertices.Add(Points[Index].normalized*Radius);
                    }
                    for(int Index=1;Index<Face.Count-1;Index++)
                    {
                        Triangles.AddRange(new[]
                        {
                            Start,Start+Index,Start+Index+1
                        }
                        );
                    }
                }
            }
        }
        Mesh Geometry=new Mesh
        {
            name="ReferenceDodecahedron"
        }
        ;
        Geometry.SetVertices(Vertices);
        Geometry.SetTriangles(Triangles,0);
        Geometry.RecalculateNormals();
        return Geometry;
    }
}
