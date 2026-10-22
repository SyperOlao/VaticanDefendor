using System;
using UnityEngine;
public enum MobileRatioPreset
{
    Quarter, Third, Free
}
public enum MobileMapTime
{
    Realtime, Pause, SlowMotion
}
public enum MobileSwipeZone
{
    Map, Right, Left, Edges, Handle
}
[Serializable]
public class MobileViewSettings
{
    public MobileRatioPreset Ratio=MobileRatioPreset.Quarter;
    public MobileMapTime TimeMode=MobileMapTime.SlowMotion;
    [InspectorName("Скорость времени при открытой карте")] public float SlowMotionFactor=0.2f;
    [InspectorName("Длительность замедления, с")] public float SlowMotionDuration=4;
    [InspectorName("Замедление без таймера")] public bool bInfiniteSlowMotion;
    public MobileSwipeZone SwipeZone=MobileSwipeZone.Handle;
    [InspectorName("Чувствительность свайпа")] public float SwipeSensitivity=6;
    [InspectorName("Ширина края карты, %")] public float SwipeEdgePercent=24;
    [InspectorName("Затемнение под колесом")] public bool bWheelDim=true;
    [InspectorName("Размытие под колесом")] public bool bWheelBlur=true;
}
