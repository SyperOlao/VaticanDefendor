using System;
using System.IO;
using Unity.Entities;
using Unity.NetCode;
using Unity.Networking.Transport;
using Unity.Networking.Transport.TLS;

namespace VaticanDefendor.Bootstrap
{
    internal sealed class SecureGameDriver : INetworkStreamDriverConstructor
    {
        private readonly string _certificate;
        private readonly string _key;
        private readonly string _ca;
        private readonly string _name;

        public SecureGameDriver(bool server, string certificateName)
        {
            if (server)
            {
                _certificate = ReadPem("VD_DTLS_CERT_FILE");
                _key = ReadPem("VD_DTLS_KEY_FILE");
            }
            else
            {
                _ca = string.IsNullOrEmpty(Environment.GetEnvironmentVariable("VD_DTLS_CA_FILE"))
                    ? UnityEngine.Resources.Load<VaticanDefendor.Network.NetworkDeploymentSettings>("NetworkDeployment")?.TrustedGameServerCaPem
                    : ReadPem("VD_DTLS_CA_FILE");
                if (string.IsNullOrWhiteSpace(_ca))
                    throw new InvalidOperationException("A trusted DTLS CA is required in NetworkDeployment or VD_DTLS_CA_FILE.");
                _name = certificateName;
                if (string.IsNullOrWhiteSpace(_name) || _name.Length > 253)
                    throw new InvalidOperationException("DTLS server certificate name is missing.");
            }
        }

        public void CreateClientDriver(World world, ref NetworkDriverStore store, NetDebug netDebug)
        {
            NetworkSettings settings = DefaultDriverBuilder.GetNetworkClientSettings();
            try
            {
                settings.WithSecureClientParameters(_ca, _name);
                DefaultDriverBuilder.RegisterClientUdpDriver(world, ref store, netDebug, settings);
            }
            finally { settings.Dispose(); }
        }

        public void CreateServerDriver(World world, ref NetworkDriverStore store, NetDebug netDebug)
        {
            NetworkSettings settings = DefaultDriverBuilder.GetNetworkServerSettings();
            try
            {
                settings.WithSecureServerParameters(_certificate, _key);
                DefaultDriverBuilder.RegisterServerUdpDriver(world, ref store, netDebug, settings);
            }
            finally { settings.Dispose(); }
        }

        private static string ReadPem(string variable)
        {
            string path = Environment.GetEnvironmentVariable(variable);
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                throw new InvalidOperationException(variable + " must point to a PEM file.");
            string value = File.ReadAllText(path);
            if (value.Length > 16000 || !value.Contains("-----BEGIN "))
                throw new InvalidOperationException(variable + " is not a supported PEM file.");
            return value;
        }
    }
}
