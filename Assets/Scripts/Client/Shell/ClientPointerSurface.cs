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
        private float _radius;
        private float _runRadius;
        private ClientShellView _view;
        private readonly HashSet<int> _ownedPointers = new();

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
            if (_ownership == null || !_ownership.TryAcquire(eventData.pointerId, _region))
            {
                return;
            }

            if (_region == PointerRegion.Combat)
            {
                _activePointer = eventData.pointerId;
                _origin = eventData.position;
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
            _view.SetTouchMovement(Vector2.ClampMagnitude(displacement / _radius, 1f),
                displacement.magnitude >= _runRadius);
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
                _activePointer = int.MinValue;
                _view.SetTouchMovement(Vector2.zero, false);
            }
        }

        public void OnCancel(BaseEventData eventData)
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
                if (_view != null)
                {
                    _view.SetTouchMovement(Vector2.zero, false);
                }
            }
        }
    }
}
#endif
