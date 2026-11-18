#if !UNITY_SERVER || UNITY_EDITOR
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;

namespace VaticanDefendor.Client.Shell
{
    public sealed class ClientPointerSurface : MonoBehaviour, IPointerDownHandler, IInitializePotentialDragHandler, IDragHandler, IPointerUpHandler, ICancelHandler
    {
        private PointerOwnership _ownership;
        private PointerRegion _region;
        private int _activePointer = int.MinValue;
        private Vector2 _origin;
        private Vector2 _offset;
        private bool _attackSelected;
        private bool _dashArmed;
        private float _radius;
        private ClientShellView _view;
        private readonly HashSet<int> _ownedPointers = new();

        public bool IsGestureActive => _activePointer != int.MinValue;
        public Vector2 GestureOrigin => _origin;
        public Vector2 GestureOffset => _offset;

        public void Configure(PointerOwnership ownership, PointerRegion region, ClientShellView view, float radius)
        {
            _ownership = ownership;
            _region = region;
            _view = view;
            _radius = Mathf.Max(1f, radius);
        }

        public void OnInitializePotentialDrag(PointerEventData Event)
        {
            if (_region == PointerRegion.Combat || _region == PointerRegion.Wheel)
            {
                Event.useDragThreshold = false;
            }
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            if (_region == PointerRegion.Combat && (_activePointer != int.MinValue || !_view.CanStartCombatGesture ||
                eventData.button != PointerEventData.InputButton.Left))
            {
                return;
            }
            if (_region == PointerRegion.Wheel && (_activePointer != int.MinValue ||
                eventData.button != PointerEventData.InputButton.Left))
            {
                return;
            }
            if (_ownership == null || !_ownership.TryAcquire(eventData.pointerId, _region))
            {
                return;
            }

            if (_region == PointerRegion.Combat)
            {
                _activePointer = eventData.pointerId;
                _origin = eventData.position;
                _offset = Vector2.zero;
                _attackSelected = false;
                _dashArmed = true;
                _view.SetCharging(true);
                _view.SetTouchMovement(Vector2.zero, false);
            }
            else if (_region == PointerRegion.Map)
            {
                _view.BeginBuildGesture(eventData.pointerId, eventData.position);
            }
            else if (_region == PointerRegion.Wheel)
            {
                if (!_view.BeginWeaponSelection(eventData.position))
                {
                    _ownership.Release(eventData.pointerId);
                    return;
                }
                _activePointer = eventData.pointerId;
            }
            _ownedPointers.Add(eventData.pointerId);
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (_region == PointerRegion.Wheel && _activePointer == eventData.pointerId)
            {
                _view.UpdateWeaponSelection(eventData.position);
                return;
            }
            if (_region == PointerRegion.Divider && _ownership.IsOwnedBy(eventData.pointerId, _region))
            {
                _view.DragDivider(eventData.position);
                return;
            }
            if (_region == PointerRegion.Map &&
                _ownership.IsOwnedBy(eventData.pointerId, PointerRegion.Map))
            {
                _view.UpdateBuildGesture(eventData.pointerId, eventData.position);
                return;
            }
            if (_region != PointerRegion.Combat || _activePointer != eventData.pointerId ||
                !_ownership.IsOwnedBy(eventData.pointerId, PointerRegion.Combat))
            {
                return;
            }

            _offset = eventData.position - _origin;
            float Distance = _offset.magnitude;
            float CenterRadius = _view.CenterRadius;
            bool bReady = !_attackSelected && _view.IsSpecialCharged;
            bool bOuter = Distance >= _radius;
            if (bReady && bOuter)
            {
                _attackSelected = true;
                _view.ReleaseChargedAttack(_offset.normalized);
            }
            if (!bOuter)
            {
                _dashArmed = true;
            }
            _view.SetCharging(!_attackSelected && (Distance <= CenterRadius || bReady));
            Vector2 Movement = Vector2.zero;
            if (Distance > CenterRadius && (!bReady || _attackSelected))
            {
                Movement = _offset.normalized * Mathf.InverseLerp(CenterRadius, _radius, Distance);
            }
            _view.SetTouchMovement(Movement, bOuter);
            if (!_attackSelected && _dashArmed && bOuter)
            {
                _dashArmed = false;
                _view.ReleaseDash();
            }
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            if (_ownership == null || !_ownership.IsOwnedBy(eventData.pointerId, _region))
            {
                return;
            }
            if (eventData is ExtendedPointerEventData Pointer && Pointer.touchId != 0 && Pointer.device is Touchscreen Screen)
            {
                foreach (var Touch in Screen.touches)
                {
                    if (Touch.touchId.ReadValue() == Pointer.touchId && Touch.phase.ReadValue() == UnityEngine.InputSystem.TouchPhase.Canceled)
                    {
                        ReleaseActive();
                        return;
                    }
                }
            }

            if (_region == PointerRegion.Map)
                _view.EndBuildGesture(eventData.pointerId, eventData.position);

            _ownership.Release(eventData.pointerId);
            _ownedPointers.Remove(eventData.pointerId);
            if (_region == PointerRegion.Wheel && _activePointer == eventData.pointerId)
            {
                _activePointer = int.MinValue;
                _view.EndWeaponSelection(eventData.position);
                return;
            }
            if (_activePointer == eventData.pointerId)
            {
                _view.SetCharging(false);
                if (!_attackSelected && _view.IsSpecialCharged)
                {
                    Vector2 Offset = eventData.position - _origin;
                    if (Offset.magnitude <= _view.CenterRadius)
                    {
                        _view.ReleaseChargedDefense();
                    }
                    else if (Offset.magnitude >= _radius)
                    {
                        _view.ReleaseChargedAttack(Offset.normalized);
                    }
                }
                _activePointer = int.MinValue;
                _offset = Vector2.zero;
                _view.SetTouchMovement(Vector2.zero, false);
            }
        }

        public void OnCancel(BaseEventData eventData)
        {
            ReleaseActive();
        }

        public void CancelInput()
        {
            ReleaseActive();
        }

        private void OnDisable()
        {
            ReleaseActive();
        }

        private void ReleaseActive()
        {
            if (_ownership != null)
            {
                foreach (int pointerId in _ownedPointers) _ownership.Release(pointerId);
            }
            _ownedPointers.Clear();
            if (_region == PointerRegion.Map && _view != null) _view.CancelBuildGesture();
            if (_activePointer != int.MinValue)
            {
                _activePointer = int.MinValue;
                _offset = Vector2.zero;
                if (_view != null && _region == PointerRegion.Wheel)
                {
                    _view.CancelWeaponSelection();
                }
                else if (_view != null)
                {
                    _view.SetCharging(false);
                    _view.SetTouchMovement(Vector2.zero, false);
                }
            }
        }
    }
}
#endif
