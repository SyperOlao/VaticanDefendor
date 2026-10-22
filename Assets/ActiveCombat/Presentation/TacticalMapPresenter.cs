using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public sealed class TacticalMapPresenter : MonoBehaviour
{
    public ActiveCombatGame Game;
    public CombatRenderSurface Surface;
    public Vector2 Center;
    public float Zoom = 1;
    public bool bOverview;
    public bool bPanMode;
    public bool bTransitioning { get; private set; }
    public Material SourceMaterial;
    public readonly List<GridCell> PreviewCells = new List<GridCell>();
    public bool bPlacementPreview;
    public Vector2Int PlacementCell;
    private Material MarkerMaterial;
    private Transform MarkerRoot;
    private Mesh MarkerGeometry;
    private readonly List<Vector3> MarkerVertices = new List<Vector3>(2048);
    private readonly List<int> MarkerTriangles = new List<int>(3072);
    private readonly List<Color> MarkerColors = new List<Color>(2048);
    private readonly List<Vector2> MarkerCoordinates = new List<Vector2>(2048);
    private readonly List<Vector2> MarkerShapes = new List<Vector2>(2048);
    private Vector2 TransitionStart;
    private Vector2 TransitionTarget;
    private float TransitionStartZoom;
    private float TransitionTargetZoom;
    private float TransitionAge;
    private float TransitionDuration;
    private float ReturnZoom = 1;
    private RectTransform AnnotationRoot;
    private readonly List<MapAnnotation> Annotations = new List<MapAnnotation>();
    private int UsedAnnotations;
    private Vector2 PresentedViewportSize;

    private sealed class MapAnnotation
    {
        public RectTransform Area;
        public TMP_Text Caption;
        public PhoneGraphic Icon;
    }

    private void Awake()
    {
        MarkerRoot = new GameObject("TacticalMarkers", typeof(MeshFilter), typeof(MeshRenderer)).transform;
        MarkerRoot.SetParent(transform);
        MarkerRoot.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
        MarkerRoot.gameObject.layer = 31;
        MarkerMaterial = new Material(SourceMaterial);
        MarkerGeometry = new Mesh { name = "TacticalMapGeometry", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
        MarkerGeometry.MarkDynamic();
        MarkerRoot.GetComponent<MeshFilter>().sharedMesh = MarkerGeometry;
        MeshRenderer Renderer = MarkerRoot.GetComponent<MeshRenderer>();
        Renderer.sharedMaterial = MarkerMaterial;
        Renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        Renderer.receiveShadows = false;
        Center = new Vector2(PlayerPrefs.GetFloat("MapCenterX", 0), PlayerPrefs.GetFloat("MapCenterZ", 0));
        Zoom = PlayerPrefs.GetFloat("MapZoom", 1);
        bOverview = PlayerPrefs.GetInt("MapOverview", 0) != 0;
        ReturnZoom = PlayerPrefs.GetFloat("MapReturnZoom", 1);
        GameObject Overlay = new GameObject("MapAnnotations", typeof(RectTransform), typeof(RectMask2D));
        Overlay.transform.SetParent(Surface.Image.transform, false);
        AnnotationRoot = (RectTransform)Overlay.transform;
        CombatUIFactory.Stretch(AnnotationRoot, Vector2.zero, Vector2.zero);
    }

    private void OnEnable()
    {
        Canvas.preWillRenderCanvases += RefreshViewport;
    }

    private void OnDisable()
    {
        Canvas.preWillRenderCanvases -= RefreshViewport;
    }

    private void RefreshViewport()
    {
        if (Surface.Image.rectTransform.rect.size != PresentedViewportSize)
        {
            Refresh();
        }
    }

    public void Refresh()
    {
        using var Measurement = CombatPerformanceMarkers.Map.Auto();
        if (Game.Rules == null || MarkerRoot == null)
        {
            return;
        }
        Surface.Refresh();
        PresentedViewportSize = Surface.Image.rectTransform.rect.size;
        if (bOverview && !bTransitioning)
        {
            Zoom = OverviewZoom();
            Center = Vector2.zero;
        }
        Zoom = Mathf.Clamp(Zoom, 0.01f, 4);
        ClampCenter();
        Surface.OutputCamera.orthographicSize = Mathf.Max(1, Surface.Image.rectTransform.rect.height) * Game.Rules.CellSize / (72 * Zoom);
        Surface.OutputCamera.transform.position = new Vector3(Center.x, 100, Center.y);
        MarkerRoot.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
        MarkerVertices.Clear();
        MarkerTriangles.Clear();
        MarkerColors.Clear();
        MarkerCoordinates.Clear();
        MarkerShapes.Clear();
        UsedAnnotations = 0;
        int Half = Game.Rules.GridSize / 2;
        for (int Horizontal = -Half; Horizontal <= Half; Horizontal++)
        {
            for (int Vertical = -Half; Vertical <= Half; Vertical++)
            {
                Mark(new Vector3(Horizontal, 0, Vertical) * Game.Rules.CellSize, Game.Rules.CellSize * 0.96f, new Color(0.13f, 0.22f, 0.12f), 0);
            }
        }
        foreach (CombatSnapshotEntry<CombatSceneryState> Entry in Game.Root.Simulation.ReadModel.Scenery.Entries)
        {
            if (Entry.State.bRemoved == 0)
            {
                Color SceneryColor = new Color(0.04f, 0.35f, 0.19f);
                if (Game.Root.RenderCatalog.Models[Entry.State.InitialModel].Name.Contains("Rock"))
                {
                    SceneryColor = new Color(0.44f, 0.48f, 0.52f);
                }
                Mark(Entry.State.Position, 0.9f, SceneryColor, 1, true);
            }
        }
        foreach (ActiveCombatGame.StructureState Structure in Game.Structures)
        {
            Color BuildingColor = new Color(0.4f, 0.65f, 0.9f);
            if (Structure.Rules.Kind == CombatStructureKind.Wall)
            {
                BuildingColor = new Color(0.72f, 0.75f, 0.69f);
            }
            else if (Structure.Rules.Kind == CombatStructureKind.Church)
            {
                BuildingColor = new Color(0.9f, 0.83f, 0.63f);
            }
            else if (Structure.Rules.Kind == CombatStructureKind.Sawmill)
            {
                BuildingColor = new Color(0.72f, 0.53f, 0.27f);
            }
            Mark(Structure.Position, 1, BuildingColor, 2);
            ResizeMarker(Structure.HalfExtents * 2);
            Mark(Structure.Position + new Vector3(0, 0, -Structure.HalfExtents.y - 0.2f), 1, new Color(0.39f, 0.84f, 0.6f), 2.5f);
            ResizeMarker(new Vector2(Structure.HalfExtents.x * 2 * Mathf.Clamp01(Structure.Health / Structure.Rules.Health), 0.15f));
            PhoneIcon Icon = PhoneIcon.Tower;
            string Caption = "";
            switch (Structure.Rules.Kind)
            {
                case CombatStructureKind.Wall:
                    Icon = PhoneIcon.None;
                    break;
                case CombatStructureKind.Church:
                    Icon = PhoneIcon.Church;
                    break;
                case CombatStructureKind.Trap:
                    Icon = PhoneIcon.Trap;
                    break;
                case CombatStructureKind.Banner:
                    Icon = PhoneIcon.Flag;
                    break;
                case CombatStructureKind.Armory:
                    Icon = PhoneIcon.Armory;
                    if (Structure.ForgeRemaining > 0)
                    {
                        Caption = $"Ковка {Structure.ForgeRemaining:0.0} с";
                    }
                    break;
                case CombatStructureKind.Sawmill:
                    Icon = PhoneIcon.Sawmill;
                    Caption = $"{Structure.StoredGold} золота";
                    break;
            }
            if (Icon != PhoneIcon.None)
            {
                Annotate(Structure.Position, Caption, Icon, Mathf.Clamp(Structure.HalfExtents.x * 2 * 36 * Zoom / Game.Rules.CellSize, 10, 30));
            }
        }
        foreach (ActiveCombatGame.EnemyState Enemy in Game.Enemies)
        {
            Mark(Enemy.Position, 0.8f, new Color(0.94f, 0.27f, 0.3f), 3, true);
        }
        foreach (ActiveCombatGame.PickupState Pickup in Game.Pickups)
        {
            Mark(Pickup.Position, 0.6f, new Color(0.2f, 0.76f, 0.51f), 2);
            Annotate(Pickup.Position, "", PhoneIcon.Church, Mathf.Clamp(0.6f * 36 * Zoom / Game.Rules.CellSize, 6, 12));
        }
        foreach (ActiveCombatGame.BombState Bomb in Game.Bombs)
        {
            for (int Horizontal = -Game.Rules.TrebuchetCellRadius; Horizontal <= Game.Rules.TrebuchetCellRadius; Horizontal++)
            {
                for (int Vertical = -Game.Rules.TrebuchetCellRadius; Vertical <= Game.Rules.TrebuchetCellRadius; Vertical++)
                {
                    Vector2Int Cell = Bomb.Cell + new Vector2Int(Horizontal, Vertical);
                    if (Mathf.Abs(Cell.x) <= Half && Mathf.Abs(Cell.y) <= Half)
                    {
                        Mark(new Vector3(Cell.x, 0, Cell.y) * Game.Rules.CellSize, Game.Rules.CellSize * 0.92f, new Color(0.83f, 0.3f, 0.08f), 3.5f);
                    }
                }
            }
            Annotate(new Vector3(Bomb.Cell.x, 0, Bomb.Cell.y) * Game.Rules.CellSize, $"{Bomb.Remaining:0.0} с", PhoneIcon.None, 12);
        }
        Mark(Game.PlayerPosition, 1.15f, Color.white, 4, true);
        Mark(Game.PlayerPosition, 0.85f, new Color(0.23f, 0.75f, 0.92f), 4.1f, true);
        foreach (GridCell Cell in PreviewCells)
        {
            Color Color = Color.green;
            if (!Game.CanBuildWall(Cell) || Game.Gold < PreviewCells.Count * Game.Rules.Structures[1].Price)
            {
                Color = Color.red;
            }
            Mark(new Vector3(Cell.Horizontal, 0, Cell.Vertical) * Game.Rules.CellSize, Game.Rules.CellSize * 0.8f, Color, 5);
        }
        if (bPlacementPreview && !bOverview && PreviewCells.Count == 0 && !Game.bSellBuildings && !Game.bTrebuchet)
        {
            Vector3 Position = new Vector3(PlacementCell.x, 0, PlacementCell.y) * Game.Rules.CellSize;
            Vector2 Extents = Game.BuildingExtents(Game.StructureIndex);
            Color PreviewColor = Color.green;
            if (Game.PlacementError(PlacementCell, Game.StructureIndex) != null)
            {
                PreviewColor = Color.red;
            }
            for (int Side = -1; Side <= 1; Side += 2)
            {
                Mark(Position + new Vector3(Side * Extents.x, 0, 0), 1, PreviewColor, 5);
                ResizeMarker(new Vector2(0.12f, Extents.y * 2));
                Mark(Position + new Vector3(0, 0, Side * Extents.y), 1, PreviewColor, 5);
                ResizeMarker(new Vector2(Extents.x * 2, 0.12f));
            }
        }
        MarkerGeometry.Clear();
        MarkerGeometry.SetVertices(MarkerVertices);
        MarkerGeometry.SetColors(MarkerColors);
        MarkerGeometry.SetUVs(0, MarkerCoordinates);
        MarkerGeometry.SetUVs(1, MarkerShapes);
        MarkerGeometry.SetTriangles(MarkerTriangles, 0);
        MarkerGeometry.RecalculateBounds();
        for (int Index = UsedAnnotations; Index < Annotations.Count; Index++)
        {
            Annotations[Index].Area.gameObject.SetActive(false);
        }
    }

    private void Mark(Vector3 Position, float Size, Color Color, float Height, bool bCircular = false)
    {
        int Start = MarkerVertices.Count;
        MarkerVertices.Add(new Vector3(Position.x - Size * 0.5f, Height, Position.z - Size * 0.5f));
        MarkerVertices.Add(new Vector3(Position.x - Size * 0.5f, Height, Position.z + Size * 0.5f));
        MarkerVertices.Add(new Vector3(Position.x + Size * 0.5f, Height, Position.z + Size * 0.5f));
        MarkerVertices.Add(new Vector3(Position.x + Size * 0.5f, Height, Position.z - Size * 0.5f));
        MarkerTriangles.Add(Start);
        MarkerTriangles.Add(Start + 1);
        MarkerTriangles.Add(Start + 2);
        MarkerTriangles.Add(Start);
        MarkerTriangles.Add(Start + 2);
        MarkerTriangles.Add(Start + 3);
        MarkerCoordinates.Add(Vector2.zero);
        MarkerCoordinates.Add(Vector2.up);
        MarkerCoordinates.Add(Vector2.one);
        MarkerCoordinates.Add(Vector2.right);
        for (int Index = 0; Index < 4; Index++)
        {
            MarkerColors.Add(Color);
            MarkerShapes.Add(new Vector2(System.Convert.ToSingle(bCircular), 0));
        }
    }

    private void ResizeMarker(Vector2 Size)
    {
        int Start = MarkerVertices.Count - 4;
        Vector3 Center = (MarkerVertices[Start] + MarkerVertices[Start + 2]) * 0.5f;
        MarkerVertices[Start] = Center + new Vector3(-Size.x, 0, -Size.y) * 0.5f;
        MarkerVertices[Start + 1] = Center + new Vector3(-Size.x, 0, Size.y) * 0.5f;
        MarkerVertices[Start + 2] = Center + new Vector3(Size.x, 0, Size.y) * 0.5f;
        MarkerVertices[Start + 3] = Center + new Vector3(Size.x, 0, -Size.y) * 0.5f;
    }

    private void Annotate(Vector3 Position, string Caption, PhoneIcon Icon, float Size)
    {
        Vector3 Viewport = Surface.OutputCamera.WorldToViewportPoint(Position);
        if (Viewport.x < 0 || Viewport.x > 1 || Viewport.y < 0 || Viewport.y > 1)
        {
            return;
        }
        if (UsedAnnotations == Annotations.Count)
        {
            GameObject Model = new GameObject("MapAnnotation", typeof(RectTransform));
            Model.transform.SetParent(AnnotationRoot, false);
            MapAnnotation Display = new MapAnnotation { Area = (RectTransform)Model.transform };
            GameObject Symbol = new GameObject("Icon", typeof(RectTransform), typeof(CanvasRenderer), typeof(PhoneGraphic));
            Symbol.transform.SetParent(Model.transform, false);
            Display.Icon = Symbol.GetComponent<PhoneGraphic>();
            Display.Icon.raycastTarget = false;
            Display.Icon.color = Color.white;
            CombatUIFactory.Stretch((RectTransform)Symbol.transform, Vector2.zero, Vector2.zero);
            Display.Caption = CombatUIFactory.Text(Model.transform, "Caption", "", new Vector2(0.5f, 1), new Vector2(0, 4), new Vector2(120, 20), 11, Game.Hud.Font);
            Display.Caption.alignment = TextAlignmentOptions.Center;
            Display.Caption.rectTransform.pivot = new Vector2(0.5f, 0);
            Display.Caption.raycastTarget = false;
            Display.Caption.outlineWidth = 0.2f;
            Display.Caption.outlineColor = Color.black;
            Annotations.Add(Display);
        }
        MapAnnotation Current = Annotations[UsedAnnotations++];
        Current.Area.gameObject.SetActive(true);
        Current.Area.anchorMin = new Vector2(Viewport.x, Viewport.y);
        Current.Area.anchorMax = Current.Area.anchorMin;
        Current.Area.anchoredPosition = Vector2.zero;
        Current.Area.sizeDelta = Vector2.one * Size;
        Current.Icon.Icon = Icon;
        Current.Icon.Stroke = Mathf.Clamp(Size / 12, 1, 2);
        Current.Icon.SetVerticesDirty();
        Current.Icon.enabled = Icon != PhoneIcon.None;
        Current.Caption.text = Caption;
        Current.Caption.gameObject.SetActive(Caption.Length > 0);
    }

    public void SaveView()
    {
        PlayerPrefs.SetFloat("MapCenterX", Center.x);
        PlayerPrefs.SetFloat("MapCenterZ", Center.y);
        PlayerPrefs.SetFloat("MapZoom", Zoom);
        PlayerPrefs.SetInt("MapOverview", System.Convert.ToInt32(bOverview));
        PlayerPrefs.SetFloat("MapReturnZoom", ReturnZoom);
    }

    public float OverviewZoom()
    {
        Rect Bounds = Surface.Image.rectTransform.rect;
        return Mathf.Min(Bounds.width, Bounds.height) / (Game.Rules.GridSize * 36);
    }

    public float WorkingMinimumZoom()
    {
        Rect Bounds = Surface.Image.rectTransform.rect;
        return Mathf.Max(Bounds.width, Bounds.height) / (Game.Rules.GridSize * 36);
    }

    public void ClampCenter()
    {
        Rect Bounds = Surface.Image.rectTransform.rect;
        float Half = Game.Rules.GridSize * Game.Rules.CellSize * 0.5f;
        Vector2 Limits = new Vector2(Mathf.Max(0, Half - Bounds.width * Game.Rules.CellSize / (72 * Zoom)), Mathf.Max(0, Half - Bounds.height * Game.Rules.CellSize / (72 * Zoom)));
        Center = new Vector2(Mathf.Clamp(Center.x, -Limits.x, Limits.x), Mathf.Clamp(Center.y, -Limits.y, Limits.y));
    }

    public void StopTransition()
    {
        if (bTransitioning)
        {
            bTransitioning = false;
            bOverview = false;
            SaveView();
        }
    }

    public void ShowOverview()
    {
        if (!bOverview)
        {
            ReturnZoom = Mathf.Max(1, Zoom);
        }
        AnimateView(true, Vector2.zero, OverviewZoom());
    }

    public void FocusPlayer()
    {
        AnimateView(false, new Vector2(Game.PlayerPosition.x, Game.PlayerPosition.z), Mathf.Max(1, ReturnZoom));
    }

    public void FocusCell(Vector3 Position)
    {
        AnimateView(false, new Vector2(Position.x, Position.z), 48f / 36);
    }

    private void AnimateView(bool bTargetOverview, Vector2 Target, float TargetZoom)
    {
        TransitionStart = Center;
        TransitionStartZoom = Zoom;
        TransitionTargetZoom = TargetZoom;
        if (!bTargetOverview)
        {
            TransitionTargetZoom = Mathf.Max(WorkingMinimumZoom(), TargetZoom);
        }
        float PixelDistance = Vector2.Distance(Center, Target) * 36 * Mathf.Max(Zoom, TransitionTargetZoom) / Game.Rules.CellSize;
        PixelDistance += Mathf.Abs(Mathf.Log(TransitionTargetZoom / Zoom)) * Mathf.Min(Surface.Image.rectTransform.rect.width, Surface.Image.rectTransform.rect.height);
        TransitionDuration = Mathf.Clamp(0.26f + PixelDistance * 0.0004f, 0.32f, 0.52f);
        TransitionAge = 0;
        bOverview = bTargetOverview;
        bTransitioning = true;
        float PreviousZoom = Zoom;
        Zoom = TransitionTargetZoom;
        Center = Target;
        ClampCenter();
        TransitionTarget = Center;
        Center = TransitionStart;
        Zoom = PreviousZoom;
    }

    private void Update()
    {
        if (!bTransitioning)
        {
            return;
        }
        if (Game.bSimulationPaused)
        {
            StopTransition();
            return;
        }
        TransitionAge += Time.unscaledDeltaTime;
        float Progress = Mathf.Clamp01(TransitionAge / TransitionDuration);
        float Eased = 4 * Progress * Progress * Progress;
        if (Progress >= 0.5f)
        {
            Eased = 1 - Mathf.Pow(-2 * Progress + 2, 3) * 0.5f;
        }
        Zoom = TransitionStartZoom * Mathf.Pow(TransitionTargetZoom / TransitionStartZoom, Eased);
        Center = Vector2.Lerp(TransitionStart, TransitionTarget, Eased);
        if (Progress >= 1)
        {
            bTransitioning = false;
            SaveView();
        }
        Refresh();
    }

    private void OnDestroy()
    {
        SaveView();
        Destroy(MarkerGeometry);
        Destroy(MarkerMaterial);
    }
}
