#if !UNITY_SERVER || UNITY_EDITOR
using System;
using System.IO;
using Unity.Entities;
using Unity.NetCode;
using Unity.Networking.Transport;
using Unity.Networking.Transport.Utilities;
using UnityEngine;

namespace VaticanDefendor.Bootstrap
{
    // Explicit UTP test driver. Production launches keep Netcode's default constructor.
    internal sealed class DevelopmentNetworkSimulator : INetworkStreamDriverConstructor
    {
        private readonly SimulatorUtility.Parameters _parameters;
        private readonly IPCAndSocketDriverConstructor _default = new IPCAndSocketDriverConstructor();

        private DevelopmentNetworkSimulator(SimulatorUtility.Parameters parameters)
        {
            _parameters = parameters;
        }

        public static INetworkStreamDriverConstructor Load(string path)
        {
            if (!Debug.isDebugBuild)
                throw new InvalidOperationException("Network simulation requires a Development Build.");
            if (!File.Exists(path))
                throw new FileNotFoundException("Network simulation profile is missing.", path);
            SimulatorUtility.Parameters parameters =
                JsonUtility.FromJson<SimulatorUtility.Parameters>(File.ReadAllText(path));
            if (parameters.Mode != ApplyMode.AllPackets ||
                parameters.MaxPacketCount < 1 || parameters.MaxPacketCount > 1000 ||
                parameters.MaxPacketSize < 64 || parameters.MaxPacketSize > 1400 ||
                parameters.PacketDelayMs < 0 || parameters.PacketDelayMs > 1000 ||
                parameters.PacketJitterMs < 0 || parameters.PacketJitterMs > parameters.PacketDelayMs ||
                parameters.PacketDropPercentage < 0 || parameters.PacketDropPercentage > 50 ||
                parameters.PacketDuplicationPercentage < 0 ||
                parameters.PacketDuplicationPercentage > 50 || parameters.FuzzFactor != 0)
                throw new InvalidOperationException("Network simulation profile has invalid limits.");
            Debug.Log($"[NetworkSimulator] UTP delay={parameters.PacketDelayMs}±{parameters.PacketJitterMs}ms " +
                $"drop={parameters.PacketDropPercentage}% duplicate={parameters.PacketDuplicationPercentage}%.");
            return new DevelopmentNetworkSimulator(parameters);
        }

        public void CreateClientDriver(World world, ref NetworkDriverStore store, NetDebug netDebug)
        {
            NetworkSettings settings = DefaultDriverBuilder.GetNetworkClientSettings();
            try
            {
                settings.WithNetworkSimulatorParameters(_parameters.PacketDropPercentage,
                    _parameters.PacketDropPercentage);
                SimulatorUtility.Parameters pipelineParameters = _parameters;
                pipelineParameters.PacketDropPercentage = 0;
                settings.AddRawParameterStruct(ref pipelineParameters);
                var driver = new NetworkDriverStore.NetworkDriverInstance
                {
                    driver = NetworkDriver.Create(new UDPNetworkInterface(), settings)
                };
                driver.unreliablePipeline = driver.driver.CreatePipeline(
                    typeof(UnreliableSequencedPipelineStage), typeof(SimulatorPipelineStage));
                driver.reliablePipeline = driver.driver.CreatePipeline(
                    typeof(ReliableSequencedPipelineStage), typeof(SimulatorPipelineStage));
                driver.unreliableFragmentedPipeline = driver.driver.CreatePipeline(
                    typeof(FragmentationPipelineStage), typeof(SimulatorPipelineStage));
                store.RegisterDriver(TransportType.Socket, driver);
            }
            finally
            {
                settings.Dispose();
            }
        }

        public void CreateServerDriver(World world, ref NetworkDriverStore store, NetDebug netDebug)
        {
            _default.CreateServerDriver(world, ref store, netDebug);
        }
    }
}
#endif
