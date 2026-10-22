using System;
using System.Collections;
using UnityEngine;

public static class MapLayoutVerification
{
    public static object Result;
    public static string Failure;
    private static int Checks;
    private static float MaximumError;

    public static string Run()
    {
        Result = null;
        Failure = null;
        Checks = 0;
        MaximumError = 0;
        UnityEngine.Object.FindFirstObjectByType<ActiveCombatGame>().StartCoroutine(Sequence());
        return "Map layout verification started.";
    }

    private static void Check(TacticalMapPresenter Map)
    {
        Rect Bounds = Map.Surface.Image.rectTransform.rect;
        float ExpectedSize = Bounds.height * Map.Game.Rules.CellSize / (72 * Map.Zoom);
        if (Mathf.Abs(Map.Surface.OutputCamera.orthographicSize - ExpectedSize) > 0.001f)
        {
            Failure = "Map projection must match the current viewport before rendering.";
            throw new InvalidOperationException(Failure);
        }
        Checks++;
        Transform Overlay = Map.Surface.Image.transform.Find("MapAnnotations");
        Transform Geometry = Map.transform.Find("TacticalMarkers");
        Vector3[] Vertices = Geometry.GetComponent<MeshFilter>().sharedMesh.vertices;
        int GridSide = Map.Game.Rules.GridSize / 2 * 2 + 1;
        int VertexIndex = GridSide * GridSide * 4;
        foreach (CombatSnapshotEntry<CombatSceneryState> Entry in Map.Game.Root.Simulation.ReadModel.Scenery.Entries)
        {
            if (Entry.State.bRemoved == 0)
            {
                VertexIndex += 4;
            }
        }
        int AnnotationIndex = 0;
        foreach (ActiveCombatGame.StructureState Structure in Map.Game.Structures)
        {
            Vector3 Footprint = Geometry.TransformPoint((Vertices[VertexIndex] + Vertices[VertexIndex + 2]) * 0.5f);
            VertexIndex += 8;
            if (Vector2.Distance(new Vector2(Footprint.x, Footprint.z), new Vector2(Structure.Position.x, Structure.Position.z)) > 0.001f)
            {
                Failure = "Map geometry must retain its world positions when the camera moves.";
                throw new InvalidOperationException(Failure);
            }
            Checks++;
            if (Structure.Rules.Kind == CombatStructureKind.Wall)
            {
                continue;
            }
            Vector3 Viewport = Map.Surface.OutputCamera.WorldToViewportPoint(Footprint);
            if (Viewport.x < 0 || Viewport.x > 1 || Viewport.y < 0 || Viewport.y > 1)
            {
                continue;
            }
            RectTransform Annotation = (RectTransform)Overlay.GetChild(AnnotationIndex++);
            Vector3 Expected = Map.Surface.Image.rectTransform.TransformPoint(Bounds.min + new Vector2(Viewport.x, Viewport.y) * Bounds.size);
            float Error = Vector2.Distance(Expected, Annotation.TransformPoint(Annotation.rect.center));
            MaximumError = Mathf.Max(MaximumError, Error);
            if (!Annotation.gameObject.activeSelf || Error > 0.5f)
            {
                Failure = "Building icons must remain centered on their map footprints after resizing.";
                throw new InvalidOperationException(Failure);
            }
            Checks++;
        }
    }

    private static IEnumerator Sequence()
    {
        ActiveCombatGame Game = UnityEngine.Object.FindFirstObjectByType<ActiveCombatGame>();
        bool bEnabled = Game.enabled;
        Game.enabled = false;
        MobileCombatInterface Mobile = Game.Hud.Mobile;
        TacticalMapPresenter Map = Game.Root.MapPresenter;
        try
        {
            Game.Session.StartGame();
            Map.StopTransition();
            Vector2Int[] Resolutions = { new Vector2Int(460, 818), new Vector2Int(360, 800), new Vector2Int(1080, 2400) };
            foreach (Vector2Int Resolution in Resolutions)
            {
                CombatSceneBuilder.SetPortraitPreview(Resolution.x, Resolution.y);
                yield return null;
                yield return null;
                for (int Mode = 0; Mode < 2; Mode++)
                {
                    Map.bOverview = Mode == 0;
                    Map.Center = new Vector2(3, 2);
                    Map.Zoom = 1.4f;
                    Map.Refresh();
                    Mobile.bCurtainDragging = true;
                    for (int Step = 0; Step < 6; Step++)
                    {
                        Mobile.bMapFullscreen = Step == 4;
                        Mobile.TopFraction = 0.25f + Step % 4 * 0.2f;
                        Mobile.SafeAreaOverride = new Rect(12, 24, Screen.width - 24, Screen.height - 48);
                        Mobile.ApplyLayout(0);
                        Canvas.ForceUpdateCanvases();
                        Check(Map);
                        yield return null;
                    }
                    Mobile.bCurtainDragging = false;
                    Mobile.bMapFullscreen = false;
                    Mobile.SetCurtain(false);
                    for (int Frame = 0; Frame < 24; Frame++)
                    {
                        Mobile.ApplyLayout(1f / 60);
                        Canvas.ForceUpdateCanvases();
                        Check(Map);
                        yield return null;
                    }
                }
            }
            Result = new { Success = true, Checks, MaximumPixelError = MaximumError };
        }
        finally
        {
            Mobile.bCurtainDragging = false;
            Mobile.bMapFullscreen = false;
            Mobile.SafeAreaOverride = default;
            Game.Session.ShowMainMenu();
            Game.enabled = bEnabled;
            CombatSceneBuilder.SetPortraitPreview(460, 818);
        }
    }
}
