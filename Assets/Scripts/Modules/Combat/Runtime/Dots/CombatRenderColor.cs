using Unity.Mathematics;

public static class CombatRenderColor
{
    public static float4 ToLinear(float4 Color)
    {
        Color.xyz = math.select(math.pow((Color.xyz + 0.055f) / 1.055f, 2.4f), Color.xyz / 12.92f, Color.xyz <= 0.04045f);
        return Color;
    }
}
