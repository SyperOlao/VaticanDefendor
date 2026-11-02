using Unity.NetCode;
using Unity.NetCode.Hybrid;
using UnityEditor;
using UnityEngine;

namespace VaticanDefendor.Editor
{
    public static class NetworkTransportLimits
    {
        private const string AssetPath = "Assets/Resources/NetworkTransportLimits.asset";

        [MenuItem("VaticanDefendor/Network/Apply Transport Limits")]
        public static void Apply()
        {
            NetCodeConfig config = AssetDatabase.LoadAssetAtPath<NetCodeConfig>(AssetPath);
            if (config == null)
            {
                config = ScriptableObject.CreateInstance<NetCodeConfig>();
                AssetDatabase.CreateAsset(config, AssetPath);
            }

            config.IsGlobalConfig = true;
            config.ClientReceiveQueueCapacity = 64;
            config.ClientSendQueueCapacity = 16;
            config.ServerReceiveQueueCapacity = 64;
            config.ServerSendQueueCapacity = 64;
            EditorUtility.SetDirty(config);
            NetCodeClientAndServerSettings.instance.GlobalNetCodeConfig = config;
            EditorUtility.SetDirty(NetCodeClientAndServerSettings.instance);
            AssetDatabase.SaveAssets();
            Debug.Log("[NetworkTransportLimits] Client 64/16 and server 64/64 " +
                "receive/send packets per pipeline stage, per update.");
        }
    }
}
