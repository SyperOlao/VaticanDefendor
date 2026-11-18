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
        [SerializeField, Min(80)] private float _buildingPaletteHeight = 92;
        [SerializeField, Range(2, 6)] private int _buildingCardsVisible = 4;
        [SerializeField, Min(0)] private float _buildingCardSpacing = 8;
        [SerializeField, Range(0.4f, 1f)] private float _maximumViewportAspect = 0.65f;

        [SerializeField] private float _cameraImpactScale = 0.5f;
        [SerializeField, Min(0.1f)] private float _pickupMeshScale = 1;
        [SerializeField, Min(1)] private float _pickupSmoothing = 25;
        [SerializeField] private Color _woodPickupColor = new Color(0.65f, 0.39f, 0.16f);
        [SerializeField] private Color _stonePickupColor = new Color(0.50f, 0.55f, 0.57f);
        [SerializeField] private Color _ironPickupColor = new Color(0.58f, 0.70f, 0.76f);

        public float PickupMeshScale => _pickupMeshScale;
        public float PickupSmoothing => _pickupSmoothing;
        public Color WoodPickupColor => _woodPickupColor;
        public Color StonePickupColor => _stonePickupColor;
        public Color IronPickupColor => _ironPickupColor;

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
        public float BuildingPaletteHeight => _buildingPaletteHeight;
        public int BuildingCardsVisible => _buildingCardsVisible;
        public float BuildingCardSpacing => _buildingCardSpacing;
        public float MaximumViewportAspect => _maximumViewportAspect;
    }
}
