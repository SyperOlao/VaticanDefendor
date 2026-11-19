using UnityEngine;
using VaticanDefendor.Construction;

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
        [SerializeField, Min(1)] private float _workshopHeightScale = 1.75f;
        [SerializeField, Min(1)] private float _towerHeightScale = 1.4f;
        [SerializeField, Min(1)] private float _wallHeightScale = 1.25f;
        [SerializeField, Min(1)] private float _churchHeightScale = 1.25f;
        [SerializeField] private Material _obstructingBuildingMaterial;
        [SerializeField, Range(0.05f, 0.5f)] private float _obstructingBuildingOpacity = 0.14f;
        [SerializeField, Min(0.01f)] private float _buildingFadeInSeconds = 0.11f;
        [SerializeField, Min(0.01f)] private float _buildingFadeOutSeconds = 0.22f;

        [SerializeField] private float _cameraImpactScale = 0.5f;
        [SerializeField, Min(0.1f)] private float _pickupMeshScale = 1;
        [SerializeField] private Color _woodPickupColor = new Color(0.65f, 0.39f, 0.16f);
        [SerializeField] private Color _stonePickupColor = new Color(0.50f, 0.55f, 0.57f);
        [SerializeField] private Color _ironPickupColor = new Color(0.58f, 0.70f, 0.76f);
        [SerializeField] private Color _infiniteWoodColor = new Color(1f, 0.74f, 0.28f);
        [SerializeField] private Color _infiniteStoneColor = new Color(0.48f, 0.84f, 1f);

        public float PickupMeshScale => _pickupMeshScale;
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
        public Material ObstructingBuildingMaterial => _obstructingBuildingMaterial;
        public float ObstructingBuildingOpacity => _obstructingBuildingOpacity;
        public float BuildingFadeInSeconds => _buildingFadeInSeconds;
        public float BuildingFadeOutSeconds => _buildingFadeOutSeconds;

        public float BuildingHeightScale(StructureKind Kind)
        {
            switch (Kind)
            {
                case StructureKind.Wall: return _wallHeightScale;
                case StructureKind.Tower: return _towerHeightScale;
                case StructureKind.Church: return _churchHeightScale;
                default: return _workshopHeightScale;
            }
        }

        public Color InfiniteDepositColor(ResourceKind Kind)
        {
            if (Kind == ResourceKind.Wood)
            {
                return _infiniteWoodColor;
            }
            return _infiniteStoneColor;
        }
    }
}
