using Unity.Profiling;

public static class CombatPerformanceMarkers
{
    public static readonly ProfilerMarker Simulation = new ProfilerMarker("ActiveCombat.Simulation");
    public static readonly ProfilerMarker Hud = new ProfilerMarker("ActiveCombat.Hud");
    public static readonly ProfilerMarker Map = new ProfilerMarker("ActiveCombat.Map");
    public static readonly ProfilerMarker Navigation = new ProfilerMarker("ActiveCombat.Navigation");
    public static readonly ProfilerMarker Targets = new ProfilerMarker("ActiveCombat.Targets");
    public static readonly ProfilerMarker Bots = new ProfilerMarker("ActiveCombat.Bots");
}
