using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Unity.Networking.Transport;
using Unity.Networking.Transport.TLS;
using UnityEngine;

namespace VaticanDefendor.Editor
{
    public static class OnlineTransportVerification
    {
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
