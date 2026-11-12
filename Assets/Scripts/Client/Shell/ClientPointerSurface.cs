#if !UNITY_SERVER || UNITY_EDITOR
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;

namespace VaticanDefendor.Client.Shell
{
    public sealed class ClientPointerSurface : MonoBehaviour, IPointerDownHandler, IDragHandler, IPointerUpHandler, ICancelHandler
    {
        private PointerOwnership _ownership;
        private PointerRegion _region;
        private int _activePointer = int.MinValue;
        private Vector2 _origin;
        private Vector2 _offset;
        private float _pressedAt;
        private bool _centerHeld;
        private bool _moved;
        private bool _attackSelected;
        private bool _dashArmed;
        private float _radius;
        private float _runRadius;
        private ClientShellView _view;
        private readonly HashSet<int> _ownedPointers = new();

        public bool IsGestureActive => _activePointer != int.MinValue;
        public Vector2 GestureOrigin => _origin;
        public Vector2 GestureOffset => _offset;

        public void Configure(PointerOwnership ownership, PointerRegion region, ClientShellView view,
            float radius, float runRadius)
        {
            _ownership = ownership;
            _region = region;
            _view = view;
            _radius = Mathf.Max(1f, radius);
            _runRadius = Mathf.Max(_radius, runRadius);
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            if (_region == PointerRegion.Combat && (_activePointer != int.MinValue || eventData.button != PointerEventData.InputButton.Left))
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
                _pressedAt = Time.unscaledTime;
                _centerHeld = true;
                _moved = false;
                _attackSelected = false;
                _dashArmed = true;
                _view.SetCharging(true);
                _view.SetTouchMovement(Vector2.zero, false);
            }
            else if (_region == PointerRegion.Map)
            {
                _view.BeginBuildGesture(eventData.pointerId, eventData.position);
            }
            _ownedPointers.Add(eventData.pointerId);
        }

        public void OnDrag(PointerEventData eventData)
        {
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

            Vector2 displacement = eventData.position - _origin;
            _offset = displacement;
            bool bInsideCenter = displacement.magnitude <= _view.CenterRadius;
            if (bInsideCenter && !_centerHeld)
            {
                _pressedAt = Time.unscaledTime;
            }
            if (!bInsideCenter)
            {
                _moved = true;
            }
            if (!_attackSelected && _view.IsSpecialCharged && displacement.magnitude >= _radius)
            {
                _attackSelected = true;
                _view.ReleaseChargedAttack(displacement.normalized);
            }
            if (displacement.magnitude < _radius)
            {
                _dashArmed = true;
            }
            _centerHeld = bInsideCenter;
            _view.SetCharging(_centerHeld);
            Vector2 Movement = Vector2.zero;
            if (!_centerHeld)
            {
                Movement = Vector2.ClampMagnitude(displacement / _radius, 1f);
            }
            _view.SetTouchMovement(Movement, displacement.magnitude >= _runRadius);
            if (!_attackSelected && _dashArmed && displacement.magnitude >= _runRadius)
            {
                _dashArmed = false;
                _view.ReleaseCenter(false, true);
            }
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            if (_ownership == null || !_ownership.IsOwnedBy(eventData.pointerId, _region))
            {
                return;
            }

            if (_region == PointerRegion.Map)
                _view.EndBuildGesture(eventData.pointerId, eventData.position);

            _ownership.Release(eventData.pointerId);
            _ownedPointers.Remove(eventData.pointerId);
            if (_activePointer == eventData.pointerId)
            {
                float Duration = Time.unscaledTime - _pressedAt;
                _view.ReleaseCenter(!_attackSelected && _centerHeld && Duration >= _view.ChargeSeconds, _centerHeld && !_moved && Duration <= _view.TapSeconds);
                _centerHeld = false;
                _activePointer = int.MinValue;
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
                if (_view != null)
                {
                    _view.SetCharging(false);
                    _view.SetTouchMovement(Vector2.zero, false);
                }
            }
        }
    }
}
#endif
