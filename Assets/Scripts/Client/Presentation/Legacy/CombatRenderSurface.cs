using UnityEngine;

public sealed class CombatRenderSurface : MonoBehaviour
{
    public Camera OutputCamera;
    public UnityEngine.UI.RawImage Image;
    public int MaximumResolution = 2048;
    private RenderTexture Texture;
    private readonly Vector3[] Corners = new Vector3[4];

    public VaticanDefendor.RiverMapConfiguration Surface { get; private set; }

    public void SetSurface(VaticanDefendor.RiverMapConfiguration Configuration)
    {
        Surface = Configuration;
    }

    public void Refresh()
    {
        Image.rectTransform.GetWorldCorners(Corners);
        int Width = Mathf.Max(1, Mathf.RoundToInt(Corners[2].x - Corners[0].x));
        int Height = Mathf.Max(1, Mathf.RoundToInt(Corners[2].y - Corners[0].y));
        float ResolutionScale = Mathf.Min(1, (float)MaximumResolution / Mathf.Max(Width, Height));
        Width = Mathf.Max(1, Mathf.RoundToInt(Width * ResolutionScale));
        Height = Mathf.Max(1, Mathf.RoundToInt(Height * ResolutionScale));
        if (Texture != null && Texture.width == Width && Texture.height == Height)
        {
            return;
        }
        Release();
        Texture = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32);
        Texture.name = name + "RenderTexture";
        Texture.Create();
        Image.texture = Texture;
        OutputCamera.targetTexture = Texture;
        OutputCamera.rect = new Rect(0, 0, 1, 1);
        OutputCamera.aspect = (float)Width / Height;
    }

    public bool TryGetWorldPoint(Vector2 ScreenPosition, out Vector3 Position)
    {
        Position = default;
        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(Image.rectTransform, ScreenPosition, null, out Vector2 Local))
        {
            return false;
        }
        Rect Bounds = Image.rectTransform.rect;
        if (!Bounds.Contains(Local))
        {
            return false;
        }
        Vector2 Viewport = (Local - Bounds.min) / Bounds.size;
        Ray WorldRay = OutputCamera.ViewportPointToRay(Viewport);
        if (Surface != null)
        {
            bool bHit = VaticanDefendor.Maps.RiverMapAccess.TryRaycast(Surface, WorldRay.origin, WorldRay.direction, out Unity.Mathematics.float3 HitPosition);
            Position = HitPosition;
            return bHit;
        }
        Plane Ground = new Plane(Vector3.up, Vector3.zero);
        if (!Ground.Raycast(WorldRay, out float Distance))
        {
            return false;
        }
        Position = WorldRay.GetPoint(Distance);
        return true;
    }

    private void OnDestroy()
    {
        Release();
    }

    private void Release()
    {
        if (Texture == null)
        {
            return;
        }
        if (OutputCamera != null)
        {
            OutputCamera.targetTexture = null;
        }
        if (Image != null)
        {
            Image.texture = null;
        }
        Texture.Release();
        Destroy(Texture);
        Texture = null;
    }
}
