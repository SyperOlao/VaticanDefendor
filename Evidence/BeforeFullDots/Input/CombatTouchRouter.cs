using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.EnhancedTouch;
using EnhancedTouch = UnityEngine.InputSystem.EnhancedTouch.Touch;

public sealed class CombatTouchRouter : MonoBehaviour
{
    public GameRoot Root;
    private readonly List<RaycastResult> Hits = new List<RaycastResult>();
    private readonly Dictionary<int, Vector2> LastPositions = new Dictionary<int, Vector2>();

    private void OnEnable()
    {
        EnhancedTouch.onFingerDown += Begin;
        EnhancedTouch.onFingerMove += Move;
        EnhancedTouch.onFingerUp += End;
    }

    private void OnDisable()
    {
        EnhancedTouch.onFingerDown -= Begin;
        EnhancedTouch.onFingerMove -= Move;
        EnhancedTouch.onFingerUp -= End;
        LastPositions.Clear();
        Root.Pointers.Clear();
        Root.Game.CancelGesture();
    }

    private void Begin(Finger Finger)
    {
        Vector2 Position = Finger.currentTouch.screenPosition;
        PointerEventData Event = new PointerEventData(EventSystem.current);
        Event.position = Position;
        Hits.Clear();
        EventSystem.current.RaycastAll(Event, Hits);
        PointerRegion Region = PointerRegion.Interface;
        if (Hits.Count > 0 && Hits[0].gameObject.GetComponent<CombatJoystick>() != null)
        {
            Region = PointerRegion.Combat;
        }
        if (!Root.Pointers.TryAcquire(Finger.index, Region))
        {
            return;
        }
        LastPositions[Finger.index] = Position;
        if (Region == PointerRegion.Combat && !Root.Game.bGestureActive)
        {
            Root.Game.BeginGesture(Position);
        }
        else if (Region == PointerRegion.Combat)
        {
            Root.Pointers.Release(Finger.index);
            Root.Pointers.TryAcquire(Finger.index, PointerRegion.Interface);
        }
    }

    private void Move(Finger Finger)
    {
        LastPositions[Finger.index] = Finger.currentTouch.screenPosition;
        if (Root.Pointers.IsOwnedBy(Finger.index, PointerRegion.Combat))
        {
            Root.Game.MoveGesture(Finger.currentTouch.screenPosition);
        }
    }

    private void End(Finger Finger)
    {
        if (Root.Pointers.IsOwnedBy(Finger.index, PointerRegion.Combat))
        {
            if (Finger.currentTouch.phase == UnityEngine.InputSystem.TouchPhase.Canceled)
            {
                Root.Game.CancelGesture();
            }
            else
            {
                Root.Game.EndGesture(Finger.currentTouch.screenPosition);
            }
        }
        Root.Pointers.Release(Finger.index);
        LastPositions.Remove(Finger.index);
    }
}
