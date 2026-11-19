#if !UNITY_SERVER || UNITY_EDITOR
using UnityEngine;
using UnityEngine.UI;
using VaticanDefendor;

public sealed class ResourceIconGraphic : MaskableGraphic
{
    private ResourceKind _kind;

    public void SetKind(ResourceKind Kind)
    {
        raycastTarget = false;
        if (_kind == Kind)
        {
            return;
        }
        _kind = Kind;
        SetVerticesDirty();
    }

    protected override void OnPopulateMesh(VertexHelper mesh)
    {
        mesh.Clear();
        switch (_kind)
        {
            case ResourceKind.Wood:
                DrawWood(mesh);
                break;
            case ResourceKind.Stone:
                DrawStone(mesh);
                break;
            case ResourceKind.Iron:
                DrawIron(mesh);
                break;
        }
    }

    private void DrawWood(VertexHelper mesh)
    {
        Face(mesh, 0x322024, 0x1e1618, new(-0.44f, -0.28f), new(0.23f, -0.40f), new(0.48f, -0.30f),
            new(0.28f, -0.19f), new(-0.31f, -0.17f));
        DrawPlank(mesh, new Vector2(-0.02f, -0.19f));
        DrawPlank(mesh, new Vector2(0.04f, 0.02f));
        DrawPlank(mesh, new Vector2(-0.02f, 0.23f));
    }

    private void DrawPlank(VertexHelper mesh, Vector2 offset)
    {
        Face(mesh, 0xffd990, 0xb87a46, offset + new Vector2(-0.43f, -0.03f),
            offset + new Vector2(-0.27f, 0.12f), offset + new Vector2(0.44f, 0.04f), offset + new Vector2(0.29f, -0.11f));
        Face(mesh, 0xc18a51, 0x754229, offset + new Vector2(-0.43f, -0.10f),
            offset + new Vector2(-0.43f, -0.03f), offset + new Vector2(0.29f, -0.11f), offset + new Vector2(0.29f, -0.18f));
        Face(mesh, 0xe0a466, 0x99613b, offset + new Vector2(0.29f, -0.18f),
            offset + new Vector2(0.29f, -0.11f), offset + new Vector2(0.44f, 0.04f), offset + new Vector2(0.44f, -0.03f));
        Face(mesh, 0xe6bb79, 0xa66d42, offset + new Vector2(-0.34f, 0.02f),
            offset + new Vector2(-0.31f, 0.05f), offset + new Vector2(0.31f, -0.01f), offset + new Vector2(0.28f, -0.04f));
    }

    private void DrawStone(VertexHelper mesh)
    {
        Face(mesh, 0x5f646a, 0x282d34, new(-0.46f, -0.17f), new(-0.31f, 0.22f), new(0.08f, 0.42f),
            new(0.36f, 0.25f), new(0.48f, -0.05f), new(0.31f, -0.33f), new(-0.14f, -0.41f));
        Face(mesh, 0xe3decc, 0x9e9e94, new(-0.43f, -0.16f), new(-0.30f, 0.20f), new(0.08f, 0.40f),
            new(0.28f, 0.23f), new(-0.12f, -0.06f));
        Face(mesh, 0xbebcb2, 0x727779, new(-0.12f, -0.06f), new(0.28f, 0.23f), new(0.41f, -0.04f), new(0.21f, -0.22f));
        Face(mesh, 0xa1a49e, 0x5c6267, new(-0.43f, -0.16f), new(-0.12f, -0.06f), new(0.21f, -0.22f), new(-0.13f, -0.35f));
        Face(mesh, 0x868a8b, 0x434952, new(-0.13f, -0.35f), new(0.21f, -0.22f), new(0.41f, -0.04f),
            new(0.28f, -0.31f));
        Face(mesh, 0xf3e8cd, 0xc8c2ad, new(-0.31f, 0.19f), new(0.07f, 0.38f), new(-0.01f, 0.31f),
            new(-0.25f, 0.17f));
    }

    private void DrawIron(VertexHelper mesh)
    {
        Face(mesh, 0x3d5163, 0x202831, new(-0.44f, -0.20f), new(-0.37f, 0.10f), new(0.05f, 0.39f),
            new(0.39f, 0.25f), new(0.48f, -0.04f), new(0.04f, -0.38f));
        Face(mesh, 0xf2f0dc, 0x96bac6, new(-0.38f, 0.07f), new(0.04f, 0.35f), new(0.34f, 0.24f), new(-0.06f, -0.06f));
        Face(mesh, 0x9fc1cc, 0x435b70, new(-0.06f, -0.06f), new(0.34f, 0.24f), new(0.44f, -0.03f), new(0.03f, -0.33f));
        Face(mesh, 0x8cabb7, 0x425768, new(-0.43f, -0.16f), new(-0.38f, 0.07f), new(-0.06f, -0.06f), new(0.03f, -0.33f));
        Face(mesh, 0xe6e2c7, 0xa7bec1, new(-0.36f, 0.08f), new(0.04f, 0.34f), new(0.15f, 0.30f), new(-0.30f, 0.04f));
    }

    private void Face(VertexHelper mesh, int upper, int lower, params Vector2[] points)
    {
        int start = mesh.currentVertCount;
        Rect bounds = rectTransform.rect;
        float scale = Mathf.Min(bounds.width, bounds.height);
        Color light = Rgb(upper);
        Color shade = Rgb(lower);
        float minY = points[0].y;
        float maxY = minY;
        foreach (Vector2 point in points)
        {
            minY = Mathf.Min(minY, point.y);
            maxY = Mathf.Max(maxY, point.y);
        }
        foreach (Vector2 point in points)
        {
            Color tint = Color.Lerp(shade, light, Mathf.InverseLerp(minY, maxY, point.y)) * color;
            mesh.AddVert(bounds.center + point * scale, tint, Vector2.zero);
        }
        for (int index = 1; index < points.Length - 1; index++)
        {
            mesh.AddTriangle(start, start + index, start + index + 1);
        }
    }

    private static Color32 Rgb(int value) => new((byte)(value >> 16), (byte)(value >> 8), (byte)value, 255);
}
#endif
