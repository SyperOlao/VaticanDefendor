using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Unity.Networking.Transport;
using Unity.Networking.Transport.Utilities;
using Unity.NetCode;
using Unity.Networking.Transport.TLS;
using UnityEngine;

namespace VaticanDefendor.Editor
{
    public static class OnlineTransportVerification
    {
        public static void VerifyLostAccept()
        {
            var serverSettings = DefaultDriverBuilder.GetNetworkServerSettings();
            var clientSettings = DefaultDriverBuilder.GetNetworkClientSettings();
            serverSettings.WithNetworkConfigParameters(fixedFrameTimeMS: 16);
            clientSettings.WithNetworkConfigParameters(fixedFrameTimeMS: 16);
            // Drop the first server update's transport accept, then deliver ordinary data.
            serverSettings.WithNetworkSimulatorParameters(sendPacketLossPercent: 100);
            using NetworkDriver server = NetworkDriver.Create(new UDPNetworkInterface(), serverSettings);
            using NetworkDriver client = NetworkDriver.Create(new UDPNetworkInterface(), clientSettings);
            serverSettings.Dispose();
            clientSettings.Dispose();
            var serverReliable = server.CreatePipeline(typeof(ReliableSequencedPipelineStage));
            client.CreatePipeline(typeof(ReliableSequencedPipelineStage));
            if (server.Bind(NetworkEndpoint.LoopbackIpv4) != 0 || server.Listen() != 0)
                throw new InvalidOperationException("Lost accept test could not bind.");
            NetworkConnection local = client.Connect(server.GetLocalEndpoint());
            NetworkConnection remote = default;
            bool connected = false;
            bool delivered = false;
            for (int frame = 0; frame < 160; frame++)
            {
                client.ScheduleUpdate().Complete();
                server.ScheduleUpdate().Complete();
                if (!remote.IsCreated)
                {
                    remote = server.Accept();
                    if (remote.IsCreated)
                    {
                        server.ModifyNetworkSimulatorParameters(new NetworkSimulatorParameter());
                        if (server.BeginSend(serverReliable, remote, out var writer) != 0)
                            throw new InvalidOperationException("Lost accept test BeginSend failed.");
                        writer.WriteInt(271);
                        server.EndSend(writer);
                    }
                }
                NetworkEvent.Type type;
                while ((type = client.PopEventForConnection(local, out var reader)) != NetworkEvent.Type.Empty)
                {
                    if (type == NetworkEvent.Type.Connect) connected = true;
                    if (type == NetworkEvent.Type.Data) delivered = reader.ReadInt() == 271;
                }
                Thread.Sleep(1);
            }
            UnityEngine.Debug.Log($"[LostAccept] connected={connected} delivered={delivered} state={client.GetConnectionState(local)} remoteAccepted={remote.IsCreated}");
            if (!connected || !delivered) throw new InvalidOperationException("Lost transport accept prevented outgoing connection recovery.");
        }

        public static void VerifyLossTransport()
        {
            int failures = 0;
            for (int trial = 0; trial < 100; trial++)
            {
                var serverSettings = DefaultDriverBuilder.GetNetworkServerSettings();
                var clientSettings = DefaultDriverBuilder.GetNetworkClientSettings();
                clientSettings.WithNetworkConfigParameters(fixedFrameTimeMS: 16);
                serverSettings.WithNetworkConfigParameters(fixedFrameTimeMS: 16);
                clientSettings.WithNetworkSimulatorParameters(5, 5);
                var simulation = new SimulatorUtility.Parameters
                {
                    MaxPacketCount = 1000, MaxPacketSize = 1400, RandomSeed = 42,
                    Mode = ApplyMode.AllPackets
                };
                clientSettings.AddRawParameterStruct(ref simulation);
                using NetworkDriver server = NetworkDriver.Create(new UDPNetworkInterface(), serverSettings);
                using NetworkDriver client = NetworkDriver.Create(new UDPNetworkInterface(), clientSettings);
                serverSettings.Dispose();
                clientSettings.Dispose();
                server.CreatePipeline(typeof(UnreliableSequencedPipelineStage));
                server.CreatePipeline(typeof(ReliableSequencedPipelineStage));
                server.CreatePipeline(typeof(FragmentationPipelineStage));
                client.CreatePipeline(typeof(UnreliableSequencedPipelineStage), typeof(SimulatorPipelineStage));
                var reliable = client.CreatePipeline(typeof(ReliableSequencedPipelineStage), typeof(SimulatorPipelineStage));
                client.CreatePipeline(typeof(FragmentationPipelineStage), typeof(SimulatorPipelineStage));
                if (server.Bind(NetworkEndpoint.LoopbackIpv4) != 0 || server.Listen() != 0)
                    throw new InvalidOperationException("UTP loss test could not bind ephemeral port.");
                NetworkConnection connection = client.Connect(server.GetLocalEndpoint());
                NetworkConnection accepted = default;
                bool delivered = false;
                int disconnected = -1;
                int connectFrame = -1;
                for (int frame = 0; frame < 750 && !delivered && disconnected < 0; frame++)
                {
                    client.ScheduleUpdate().Complete();
                    server.ScheduleUpdate().Complete();
                    if (!accepted.IsCreated) accepted = server.Accept();
                    NetworkEvent.Type type;
                    while ((type = client.PopEventForConnection(connection, out var reader)) != NetworkEvent.Type.Empty)
                    {
                        if (type == NetworkEvent.Type.Connect)
                        {
                            connectFrame = frame;
                            if (client.BeginSend(reliable, connection, out var writer) != 0)
                                throw new InvalidOperationException("UTP loss test BeginSend failed.");
                            writer.WriteInt(271);
                            client.EndSend(writer);
                        }
                        if (type == NetworkEvent.Type.Disconnect) disconnected = reader.Length == 1 ? reader.ReadByte() : 255;
                    }
                    if (accepted.IsCreated)
                    {
                        while ((type = server.PopEventForConnection(accepted, out var reader)) != NetworkEvent.Type.Empty)
                        {
                            if (type == NetworkEvent.Type.Connect)
                                throw new InvalidOperationException("Incoming server connection emitted a local Connect event.");
                            if (type == NetworkEvent.Type.Data) delivered = reader.ReadInt() == 271;
                        }
                    }
                    Thread.Sleep(1);
                }
                if (!delivered) failures++;
                UnityEngine.Debug.Log($"[LossTransport] trial={trial} delivered={delivered} connectFrame={connectFrame} disconnectReason={disconnected}");
            }
            UnityEngine.Debug.Log($"[LossTransport] trials=100 failures={failures}");
            if (failures != 0) throw new InvalidOperationException("UTP loss transport failed.");
        }

        public static void Verify()
        {
            string certificate = File.ReadAllText("backend/.runtime/online-test/dtls-cert.pem");
            string key = File.ReadAllText("backend/.runtime/online-test/dtls-key.pem");
            Run(certificate, key, "localhost", true, 18179);
            Run(certificate, key, "wrong.example.test", false, 18180);
            UnityEngine.Debug.Log("[OnlineTransportVerification] PASS: DTLS payload delivered; wrong certificate name rejected.");
        }

        private static void Run(string certificate, string key, string hostname, bool shouldConnect, ushort port)
        {
            var serverSettings = new NetworkSettings();
            var clientSettings = new NetworkSettings();
            serverSettings.WithSecureServerParameters(certificate, key);
            clientSettings.WithSecureClientParameters(certificate, hostname);
            using NetworkDriver server = NetworkDriver.Create(new UDPNetworkInterface(), serverSettings);
            using NetworkDriver client = NetworkDriver.Create(new UDPNetworkInterface(), clientSettings);
            serverSettings.Dispose();
            clientSettings.Dispose();
            if (server.Bind(NetworkEndpoint.LoopbackIpv4.WithPort(port)) != 0 || server.Listen() != 0)
                throw new InvalidOperationException("Could not bind DTLS verification port.");
            NetworkConnection connection = client.Connect(NetworkEndpoint.LoopbackIpv4.WithPort(port));
            NetworkConnection accepted = default;
            bool delivered = false;
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed.TotalSeconds < (shouldConnect ? 10 : 3))
            {
                server.ScheduleUpdate().Complete();
                client.ScheduleUpdate().Complete();
                if (!accepted.IsCreated) accepted = server.Accept();
                NetworkEvent.Type type;
                while ((type = client.PopEvent(out _, out _)) != NetworkEvent.Type.Empty)
                {
                    if (type != NetworkEvent.Type.Connect) continue;
                    if (!shouldConnect) throw new InvalidOperationException("Wrong DTLS hostname was accepted.");
                    if (client.BeginSend(connection, out var writer) != 0)
                        throw new InvalidOperationException("Could not send DTLS verification payload.");
                    writer.WriteInt(271);
                    client.EndSend(writer);
                }
                if (accepted.IsCreated)
                {
                    while ((type = server.PopEventForConnection(accepted, out var reader)) != NetworkEvent.Type.Empty)
                    {
                        if (type == NetworkEvent.Type.Data)
                        {
                            if (reader.ReadInt() != 271) throw new InvalidOperationException("DTLS payload differs.");
                            delivered = true;
                        }
                    }
                }
                if (delivered) break;
                Thread.Sleep(10);
            }
            if (delivered != shouldConnect) throw new InvalidOperationException("Unexpected DTLS connection result.");
        }
    }
}
