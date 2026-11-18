#if UNITY_SERVER || UNITY_EDITOR
using Unity.Entities;
using Unity.NetCode;
using Unity.Transforms;

namespace VaticanDefendor.Server
{
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup), OrderLast = true)]
    [UpdateBefore(typeof(GhostSendSystem))]
    public partial class ServerSnapshotInterestSystem : SystemBase
    {
        protected override void OnCreate() => RequireForUpdate<RunSession>();

        protected override void OnUpdate()
        {
            RunSession session = SystemAPI.GetSingleton<RunSession>();
            if (session.Hero == Entity.Null || session.Connection == Entity.Null ||
                !EntityManager.Exists(session.Hero) || !EntityManager.Exists(session.Connection) ||
                !EntityManager.HasComponent<NetworkStreamInGame>(session.Connection)) return;
            var focalPoint = new GhostConnectionPosition
            {
                Position = EntityManager.GetComponentData<LocalTransform>(session.Hero).Position
            };
            if (EntityManager.HasComponent<GhostConnectionPosition>(session.Connection))
                EntityManager.SetComponentData(session.Connection, focalPoint);
            else EntityManager.AddComponentData(session.Connection, focalPoint);
        }
    }
}
#endif
