using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;

namespace VaticanDefendor.Network
{
    public static class JoinProtocol
    {
        public const int Version = 5;
    }

    public struct JoinApproval : IApprovalRpcCommand
    {
        public int ProtocolVersion;
        public FixedString64Bytes RunId;
        public FixedString128Bytes ContentReleaseId;
        public FixedString128Bytes ConnectionCredential;
    }

    public enum JoinRejectionCode : byte
    {
        None = 0,
        ProtocolMismatch = 1,
        ContentMismatch = 2,
        RunMismatch = 3,
        Unauthorized = 4,
        RunOccupied = 5,
        ExpiredGrant = 6,
        StaleAssignment = 7,
        SessionExpired = 8
    }

    public struct JoinRejected : IApprovalRpcCommand
    {
        public byte Code;
    }

    public struct RunPauseRequestRpc : IRpcCommand
    {
        public byte Pause;
    }

    public struct RunSnapshotRpc : IRpcCommand
    {
        public FixedString64Bytes RunId;
        public float SimulationSeconds;
        public uint SimulationTick;
        public uint LastConstructionOperationId;
        public int StructureCount;
        public byte Paused;
    }

    public struct ClientJoinConfiguration : IComponentData
    {
        public int ProtocolVersion;
        public FixedString64Bytes RunId;
        public FixedString128Bytes ContentReleaseId;
        public FixedString128Bytes ConnectionCredential;
        public FixedString128Bytes ProfileDisplayName;
        public int ProfileVersion;
    }

    public struct ClientConnectionStatus : IComponentData
    {
        public ClientConnectionPhase Phase;
        public JoinRejectionCode Rejection;
        public FixedString128Bytes Message;
    }

    public enum ClientConnectionPhase : byte
    {
        Idle = 0,
        Connecting = 1,
        InGame = 2,
        Lost = 3,
        Error = 4
    }

    public struct JoinSent : IComponentData
    {
    }
}
