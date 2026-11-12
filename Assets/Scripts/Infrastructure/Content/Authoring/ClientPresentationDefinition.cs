using UnityEngine;

namespace VaticanDefendor.Content
{
    [CreateAssetMenu(menuName = "VaticanDefendor/Content/Client Presentation")]
    public sealed class ClientPresentationDefinition : ScriptableObject
    {
        [SerializeField] private Vector3 _battleCameraOffset = new Vector3(0, 13, -17);
        [SerializeField] private float _mapHeight = 45;
        [SerializeField] private float _mapSize = 22;
        [SerializeField] private float _joystickRadius = 52;
        [SerializeField] private float _runRadius = 64;
        [SerializeField] private float _centerRadius = 18;
        [SerializeField] private float _tapSeconds = 0.18f;
        [SerializeField] private float _wheelRadius = 105;
        [SerializeField] private float _buttonSize = 82;
        [SerializeField, Range(0.4f, 1f)] private float _maximumViewportAspect = 0.65f;

        [SerializeField] private float _cameraImpactScale = 0.5f;

        public float CameraImpactScale => _cameraImpactScale;
        public Vector3 BattleCameraOffset => _battleCameraOffset;
        public float MapHeight => _mapHeight;
        public float MapSize => _mapSize;
        public float JoystickRadius => _joystickRadius;
        public float RunRadius => _runRadius;
        public float CenterRadius => _centerRadius;
        public float TapSeconds => _tapSeconds;
        public float WheelRadius => _wheelRadius;
        public float ButtonSize => _buttonSize;
        public float MaximumViewportAspect => _maximumViewportAspect;
    }
}
