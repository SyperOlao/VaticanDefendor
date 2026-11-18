using UnityEngine;

namespace VaticanDefendor.Network
{
    [CreateAssetMenu(menuName = "VaticanDefendor/Network/Deployment Settings")]
    public sealed class NetworkDeploymentSettings : ScriptableObject
    {
        [SerializeField] private string _backendUrl;
        [SerializeField, TextArea(3, 12)] private string _trustedGameServerCaPem;
        public string BackendUrl => _backendUrl;
        public string TrustedGameServerCaPem => _trustedGameServerCaPem;
    }
}
