using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

public sealed class TacticalMapInput : MonoBehaviour, IPointerDownHandler, IDragHandler, IPointerUpHandler, IScrollHandler, ICancelHandler, IPointerMoveHandler, IPointerExitHandler
{
    public TacticalMapPresenter Map;
    private readonly Dictionary<int, Vector2> Pointers = new Dictionary<int, Vector2>();
    private Vector2 Start;
    private Vector2 Last;
    private Vector2 Velocity;
    private float LastTime;
    private bool bMoved;
    private bool bMultiple;
    private float StartedAt;
    private bool bDrawingWall;
    private GridCell LastWallCell;
    private bool bQuickZoom;
    private bool bPendingTap;
    private Vector2 PendingTapPosition;
    private float PendingTapTime;
    private float PendingTapDuration;
    private bool bPendingTapTouch;
    private float GestureZoom;

    public void OnPointerDown(PointerEventData Event)
    {
        if (Map.Game.bSimulationPaused)
        {
            return;
        }
        Pointers[Event.pointerId] = Event.position;
        EventSystem.current.SetSelectedGameObject(gameObject);
        UpdatePlacementPreview(Event.position);
        if (Pointers.Count > 1)
        {
            bMultiple = true;
            bDrawingWall = false;
            Map.PreviewCells.Clear();
            Map.bPlacementPreview = false;
            bPendingTap = false;
            return;
        }
        bQuickZoom = bPendingTap && Time.unscaledTime - PendingTapTime <= 0.25f && PendingTapDuration <= 0.3f && bPendingTapTouch == IsTouch(Event) && Vector2.Distance(PendingTapPosition, Event.position) <= 28 * Map.Game.Hud.Mobile.InputScale;
        if (bPendingTap && !bQuickZoom)
        {
            CommitNavigationTap();
        }
        bPendingTap = false;
        bMoved = Velocity.sqrMagnitude > 1 || Map.bTransitioning;
        Map.StopTransition();
        bMultiple = false;
        Velocity = Vector2.zero;
        Start = Event.position;
        Last = Start;
        LastTime = Time.unscaledTime;
        StartedAt = LastTime;
        bDrawingWall = false;
        Map.PreviewCells.Clear();
        GestureZoom = Map.Zoom;
        Map.Game.SlowMotionRemaining = Map.Game.Rules.MobileView.SlowMotionDuration;
    }

    public void OnDrag(PointerEventData Event)
    {
        if (!Pointers.TryGetValue(Event.pointerId, out Vector2 Previous))
        {
            return;
        }
        Pointers[Event.pointerId] = Event.position;
        if (Pointers.Count == 1 && !bMultiple && !bQuickZoom && !Map.bPanMode && !Map.bOverview && Map.Game.StructureIndex == 1 && !Map.Game.bTrebuchet && !Map.Game.bSellBuildings && (bDrawingWall || !bMoved && Time.unscaledTime - StartedAt >= 0.18f))
        {
            if (Map.Surface.TryGetWorldPoint(Event.position, out Vector3 Position))
            {
                GridCell Cell = new GridCell(Mathf.RoundToInt(Position.x / Map.Game.Rules.CellSize), Mathf.RoundToInt(Position.z / Map.Game.Rules.CellSize));
                if (!bDrawingWall)
                {
                    LastWallCell = Cell;
                }
                foreach (GridCell Segment in GridCell.Trace(LastWallCell, Cell))
                {
                    if (!Map.PreviewCells.Contains(Segment))
                    {
                        Map.PreviewCells.Add(Segment);
                    }
                }
                LastWallCell = Cell;
                bDrawingWall = true;
                bMoved = true;
                Map.Refresh();
            }
            return;
        }
        if (Pointers.Count > 1)
        {
            foreach (KeyValuePair<int, Vector2> Other in Pointers)
            {
                if (Other.Key != Event.pointerId)
                {
                    float Before = Vector2.Distance(Previous, Other.Value);
                    if (Before > 1)
                    {
                        ZoomAt((Event.position + Other.Value) * 0.5f, Vector2.Distance(Event.position, Other.Value) / Before);
                    }
                    break;
                }
            }
            return;
        }
        float Threshold = 8;
        if (!IsTouch(Event))
        {
            Threshold = 4;
        }
        if ((Event.position - Start).magnitude < Threshold * Map.Game.Hud.Mobile.InputScale && !bMoved)
        {
            return;
        }
        bMoved = true;
        Map.bPlacementPreview = false;
        Vector2 Delta = Event.position - Last;
        if (bQuickZoom)
        {
            ZoomAt(Start, Mathf.Exp(-Delta.y / (160 * Map.Game.Hud.Mobile.InputScale)));
            Last = Event.position;
            return;
        }
        if (Map.bOverview)
        {
            return;
        }
        float Scale = Map.Surface.OutputCamera.orthographicSize * 2 / Mathf.Max(1, Map.Surface.Image.rectTransform.rect.height * Map.Game.Hud.Mobile.InputScale);
        Map.Center += Delta * Scale;
        Velocity = Delta / Mathf.Max(0.001f, Time.unscaledTime - LastTime) * Scale;
        LastTime = Time.unscaledTime;
        Last = Event.position;
        Map.Refresh();
    }

    public void OnPointerUp(PointerEventData Event)
    {
        Map.bPlacementPreview = false;
        if (Time.unscaledTime - LastTime > 0.1f)
        {
            Velocity = Vector2.zero;
        }
        if (!Pointers.Remove(Event.pointerId))
        {
            return;
        }
        if (bDrawingWall && !bMultiple)
        {
            Map.Game.BuildWallPath(Map.PreviewCells);
            Map.PreviewCells.Clear();
            bDrawingWall = false;
            Map.Refresh();
            return;
        }
        if (Pointers.Count != 0 || bMultiple || bMoved)
        {
            return;
        }
        if (!RectTransformUtility.RectangleContainsScreenPoint(Map.Surface.Image.rectTransform, Event.position, Event.pressEventCamera))
        {
            return;
        }
        if (bQuickZoom)
        {
            ZoomAt(Start, Mathf.Max(48f / 36, GestureZoom * 1.6f) / Map.Zoom);
            return;
        }
        if (Map.Surface.TryGetWorldPoint(Event.position, out Vector3 Position))
        {
            if (Map.bOverview || Map.bPanMode)
            {
                bPendingTap = true;
                PendingTapPosition = Event.position;
                PendingTapTime = Time.unscaledTime;
                PendingTapDuration = Time.unscaledTime - StartedAt;
                bPendingTapTouch = IsTouch(Event);
            }
            else
            {
                float CellSize = Map.Game.Rules.CellSize;
                Map.Game.PlaceAtCell(new Vector2Int(Mathf.RoundToInt(Position.x / CellSize), Mathf.RoundToInt(Position.z / CellSize)), Event.button == PointerEventData.InputButton.Right);
            }
        }
        Map.Refresh();
        Map.SaveView();
    }

    public void OnScroll(PointerEventData Event)
    {
        OnCancel(null);
        if (!Map.Game.bSimulationPaused && Event.scrollDelta.y != 0)
        {
            ZoomAt(Event.position, Mathf.Pow(1.25f, Mathf.Sign(Event.scrollDelta.y)));
        }
    }

    private void ZoomAt(Vector2 Position, float Factor)
    {
        Map.Surface.TryGetWorldPoint(Position, out Vector3 Before);
        Map.StopTransition();
        Map.bOverview = false;
        Map.Zoom = Mathf.Clamp(Map.Zoom * Factor, Mathf.Min(Map.Zoom, Map.WorkingMinimumZoom()), 4);
        Map.Refresh();
        Map.SaveView();
        Map.Surface.TryGetWorldPoint(Position, out Vector3 After);
        Map.Center += new Vector2(Before.x - After.x, Before.z - After.z);
        Map.Refresh();
    }

    private void Update()
    {
        if (Map.Game.bSimulationPaused)
        {
            OnCancel(null);
            return;
        }
        if (bPendingTap && Time.unscaledTime - PendingTapTime >= 0.25f)
        {
            CommitNavigationTap();
        }
        UpdateKeyboard();
        if (Pointers.Count == 1 && !bMultiple && !bMoved && !bQuickZoom && !Map.bPanMode && !Map.bOverview && Map.Game.StructureIndex == 1 && !Map.Game.bTrebuchet && !Map.Game.bSellBuildings && Time.unscaledTime - StartedAt >= 0.18f)
        {
            if (Map.Surface.TryGetWorldPoint(Start, out Vector3 Position))
            {
                LastWallCell = new GridCell(Mathf.RoundToInt(Position.x / Map.Game.Rules.CellSize), Mathf.RoundToInt(Position.z / Map.Game.Rules.CellSize));
                Map.PreviewCells.Add(LastWallCell);
                bDrawingWall = true;
                bMoved = true;
                Map.Refresh();
            }
        }
        if (Pointers.Count == 0 && Velocity.sqrMagnitude > 0.01f)
        {
            float Scale = Map.Surface.OutputCamera.orthographicSize * 2 / Mathf.Max(1, Map.Surface.Image.rectTransform.rect.height);
            Velocity = Vector2.ClampMagnitude(Velocity, 2200 * Scale);
            float Decay = Mathf.Exp(-Time.unscaledDeltaTime / 0.24f);
            Map.Center += Velocity * (1 - Decay) * 0.24f;
            Velocity *= Decay;
            Map.Refresh();
            if (Velocity.sqrMagnitude <= 0.01f)
            {
                Map.SaveView();
            }
        }
    }

    public void OnCancel(BaseEventData Event)
    {
        Map.bPlacementPreview = false;
        Pointers.Clear();
        Velocity = Vector2.zero;
        bMultiple = true;
        bDrawingWall = false;
        bPendingTap = false;
        bQuickZoom = false;
        Map.PreviewCells.Clear();
    }

    private void OnDisable()
    {
        OnCancel(null);
    }

    private void OnApplicationFocus(bool bFocused)
    {
        if (!bFocused)
        {
            OnCancel(null);
        }
    }

    public void OnPointerMove(PointerEventData Event)
    {
        if (Pointers.Count == 0 || !bMoved)
        {
            UpdatePlacementPreview(Event.position);
        }
    }

    public void OnPointerExit(PointerEventData Event)
    {
        Map.bPlacementPreview = false;
        Map.Refresh();
    }

    private void UpdatePlacementPreview(Vector2 Position)
    {
        if (Map.Game.bSimulationPaused || Map.Game.Rules == null)
        {
            return;
        }
        Vector3 WorldPosition = Vector3.zero;
        Map.bPlacementPreview = !Map.bPanMode && Map.Surface.TryGetWorldPoint(Position, out WorldPosition);
        Map.PlacementCell = new Vector2Int(Mathf.RoundToInt(WorldPosition.x / Map.Game.Rules.CellSize), Mathf.RoundToInt(WorldPosition.z / Map.Game.Rules.CellSize));
        Map.Refresh();
    }

    private static bool IsTouch(PointerEventData Event)
    {
        return Event is UnityEngine.InputSystem.UI.ExtendedPointerEventData Extended && Extended.touchId != 0;
    }

    private void CommitNavigationTap()
    {
        bPendingTap = false;
        if (Map.bOverview && Map.Surface.TryGetWorldPoint(PendingTapPosition, out Vector3 Position))
        {
            Map.FocusCell(Position);
        }
    }

    private void UpdateKeyboard()
    {
        Keyboard Device = Keyboard.current;
        if (Device == null || EventSystem.current.currentSelectedGameObject != gameObject)
        {
            return;
        }
        Vector2 Direction = Vector2.zero;
        if (Device.leftArrowKey.wasPressedThisFrame)
        {
            Direction.x = 1;
        }
        if (Device.rightArrowKey.wasPressedThisFrame)
        {
            Direction.x = -1;
        }
        if (Device.upArrowKey.wasPressedThisFrame)
        {
            Direction.y = 1;
        }
        if (Device.downArrowKey.wasPressedThisFrame)
        {
            Direction.y = -1;
        }
        if (Direction != Vector2.zero)
        {
            OnCancel(null);
            Map.StopTransition();
            Map.Center += Direction * Map.Game.Rules.CellSize * 2;
            Map.Refresh();
            Map.SaveView();
        }
        Vector2 Position = RectTransformUtility.WorldToScreenPoint(null, Map.Surface.Image.rectTransform.TransformPoint(Map.Surface.Image.rectTransform.rect.center));
        if (Device.equalsKey.wasPressedThisFrame || Device.numpadPlusKey.wasPressedThisFrame)
        {
            ZoomAt(Position, 1.25f);
        }
        if (Device.minusKey.wasPressedThisFrame || Device.numpadMinusKey.wasPressedThisFrame)
        {
            ZoomAt(Position, 0.8f);
        }
        if (Device.digit0Key.wasPressedThisFrame)
        {
            OnCancel(null);
            Map.ShowOverview();
        }
    }

}
