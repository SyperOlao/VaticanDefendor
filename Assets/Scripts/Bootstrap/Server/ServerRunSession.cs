#if UNITY_SERVER || UNITY_EDITOR
using Unity.Collections;
using Unity.Entities;

namespace VaticanDefendor.Server
{
    public struct RunSession : IComponentData
    {
        public FixedString64Bytes RunId;
        public FixedString64Bytes AccountId;
        public int ProfileVersion;
        public FixedString128Bytes ProfileDisplayName;
        public FixedString128Bytes ContentReleaseId;
        public float WalkSpeedMetersPerSecond;
        public float RunSpeedMetersPerSecond;
        public Entity Connection;
        public Entity Hero;
        public byte Occupied;
    }

    public struct RunDisconnectPolicy : IComponentData
    {
        public float GraceSeconds;
        public float RetentionSeconds;
        public byte PauseAfterGrace;
        public byte ContinueAfterExpiry;
    }

    public struct RunConnectionState : IComponentData
    {
        public double DisconnectedAtWorldSeconds;
        public byte HasDisconnected;
        public byte Expired;
        public byte ManualPaused;
        public byte AutomaticPaused;
    }

    public struct DevelopmentIdentityGrant : IComponentData
    {
        public FixedString128Bytes Ticket;
        public FixedString64Bytes RunId;
        public FixedString64Bytes AccountId;
    }

    public struct BackendIdentityGrant : IComponentData
    {
        public FixedString64Bytes ServerId;
        public FixedString128Bytes Secret;
        public int Generation;
        public byte Active;
    }

    public struct JoinValidated : IComponentData
    {
    }

    public struct HeroSpawned : IComponentData
    {
    }

    public struct RejectedJoin : IComponentData
    {
        public float DisconnectAfterSeconds;
    }
}
#endif
