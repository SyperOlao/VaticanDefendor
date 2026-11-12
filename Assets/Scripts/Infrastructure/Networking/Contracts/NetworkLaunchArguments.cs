using System;
using System.Globalization;
using System.Net;
using Unity.Mathematics;

namespace VaticanDefendor.Network
{
    public sealed class NetworkLaunchArguments
    {
        public string Role { get; private set; }
        public string BindAddress { get; private set; }
        public bool SecureTransport { get; private set; }
        public string CertificateName { get; private set; }
        public string RunId { get; private set; }
        public ushort Port { get; private set; }
        public string Host { get; private set; }
        public bool DevelopmentIdentity { get; private set; }
        public string DevelopmentTicket { get; private set; }
        public string DevelopmentAccountId { get; private set; }
        public string ConnectionCredential { get; private set; }
        public string BackendUrl { get; private set; }
        public string ServerId { get; private set; }
        public string ServerSecret { get; private set; }
        public string GuestToken { get; private set; }
        public string ProfileDisplayName { get; private set; }
        public int ProfileVersion { get; private set; }
        public string ExpectedContentOverride { get; private set; }
        public bool HasScriptedInput { get; private set; }
        public float2 ScriptedDirection { get; private set; }
        public byte ScriptedRun { get; private set; }
        public int LoadEnemyCount { get; private set; }
        public bool HasScriptedBuild { get; private set; }
        public string ScriptedBuildDefinition { get; private set; }
        public int ScriptedBuildStartX { get; private set; }
        public int ScriptedBuildStartZ { get; private set; }
        public int ScriptedBuildEndX { get; private set; }
        public int ScriptedBuildEndZ { get; private set; }
        public bool ScriptedBuildRepeat { get; private set; }
        public uint ScriptedBuildOperationId { get; private set; }
        public bool HasScriptedPause { get; private set; }
        public float ScriptedPauseAfterSeconds { get; private set; }
        public byte ScriptedPauseValue { get; private set; }

        public static NetworkLaunchArguments Parse(string[] args)
        {
            var result = new NetworkLaunchArguments
            {
                Role = GetValue(args, "-vdRole") ?? DefaultRole(),
                BindAddress = GetValue(args, "-vdBind") ?? "127.0.0.1",
                SecureTransport = HasFlag(args, "-vdDtls"),
                Host = GetValue(args, "-vdHost") ?? "127.0.0.1",
                DevelopmentIdentity = HasFlag(args, "-vdDevelopmentIdentity"),
                DevelopmentTicket = Environment.GetEnvironmentVariable("VD_DEV_TICKET"),
                DevelopmentAccountId = Environment.GetEnvironmentVariable("VD_DEV_ACCOUNT_ID"),
                BackendUrl = GetValue(args, "-vdBackendUrl") ?? Environment.GetEnvironmentVariable("VD_BACKEND_URL") ??
                    UnityEngine.Resources.Load<NetworkDeploymentSettings>("NetworkDeployment")?.BackendUrl,
                ExpectedContentOverride = GetValue(args, "-vdExpectedContent")
            };

            if (result.Role != "server" && result.Role != "client")
            {
                throw new ArgumentException("-vdRole must be 'server' or 'client'.");
            }
            if (result.Role == "server")
            {
                result.ServerSecret = Environment.GetEnvironmentVariable("VD_SERVER_SECRET");
                if (!IPAddress.TryParse(result.BindAddress, out IPAddress bind) ||
                    bind.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork ||
                    (!IPAddress.IsLoopback(bind) && !result.SecureTransport && !result.DevelopmentIdentity))
                    throw new ArgumentException("Non-loopback server bind requires -vdDtls and an IPv4 address.");
            }
            else
            {
                result.GuestToken = Environment.GetEnvironmentVariable("VD_GUEST_TOKEN");
            }

            if (result.DevelopmentIdentity)
            {
                if (!UnityEngine.Debug.isDebugBuild)
                {
                    throw new InvalidOperationException("Development identity requires a Development Build.");
                }
                if (!Guid.TryParse(GetValue(args, "-vdRunId"), out Guid runId) || runId == Guid.Empty)
                {
                    throw new ArgumentException("-vdRunId must be a non-empty GUID in development identity mode.");
                }
                result.RunId = runId.ToString("D");
                if (string.IsNullOrWhiteSpace(result.DevelopmentTicket) || result.DevelopmentTicket.Length > 110)
                {
                    throw new ArgumentException("VD_DEV_TICKET must contain a non-empty ticket of at most 110 characters.");
                }
                result.ConnectionCredential = result.DevelopmentTicket;
                if (result.Role == "server" && (string.IsNullOrWhiteSpace(result.DevelopmentAccountId) ||
                    result.DevelopmentAccountId.Length > 60))
                {
                    throw new ArgumentException("Server requires VD_DEV_ACCOUNT_ID (at most 60 characters).");
                }
            }
            else
            {
                if (!Uri.TryCreate(result.BackendUrl, UriKind.Absolute, out Uri backend) ||
                    (backend.Scheme != Uri.UriSchemeHttps &&
                     !(backend.Scheme == Uri.UriSchemeHttp && HasFlag(args, "-vdInsecureLocalBackend") &&
                       backend.IsLoopback)))
                {
                    throw new InvalidOperationException(
                        "Backend requires HTTPS; local loopback HTTP requires explicit -vdInsecureLocalBackend.");
                }
                result.BackendUrl = backend.GetLeftPart(UriPartial.Authority).TrimEnd('/');
                if (result.ExpectedContentOverride != null)
                {
                    throw new ArgumentException("-vdExpectedContent is only available in development identity mode.");
                }
                if (result.Role == "server")
                {
                    if (!Guid.TryParse(GetValue(args, "-vdServerId"), out Guid serverId) || serverId == Guid.Empty ||
                        string.IsNullOrEmpty(result.ServerSecret) || result.ServerSecret.Length < 32 ||
                        result.ServerSecret.Length > 120)
                    {
                        throw new ArgumentException("Backend server requires -vdServerId and VD_SERVER_SECRET.");
                    }
                    result.ServerId = serverId.ToString("D");
                }
            }

            if (result.Role == "server" || result.DevelopmentIdentity)
            {
                if (!ushort.TryParse(GetValue(args, "-vdPort"), NumberStyles.None,
                    CultureInfo.InvariantCulture, out ushort port) || port == 0)
                {
                    throw new ArgumentException("-vdPort must be between 1 and 65535.");
                }
                result.Port = port;
            }

            string move = GetValue(args, "-vdMove");
            if (move != null)
            {
                string[] parts = move.Split(',');
                if (parts.Length != 2 ||
                    !float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x) ||
                    !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float z))
                {
                    throw new ArgumentException("-vdMove must be 'x,z' using invariant decimal values.");
                }

                result.HasScriptedInput = true;
                result.ScriptedDirection = new float2(x, z);
                result.ScriptedRun = (byte)(HasFlag(args, "-vdRun") ? 1 : 0);
            }

            string loadLevel = GetValue(args, "-vdLoadLevel");
            if (loadLevel != null)
            {
                if (result.Role != "server" || !result.DevelopmentIdentity ||
                    !UnityEngine.Debug.isDebugBuild)
                    throw new ArgumentException("-vdLoadLevel is only allowed on a Development dedicated server with explicit development identity.");
                result.LoadEnemyCount = loadLevel switch
                {
                    "small" => 32,
                    "medium" => 128,
                    "large" => 512,
                    _ => throw new ArgumentException("-vdLoadLevel must be small, medium or large.")
                };
            }

            string pauseAfter = GetValue(args, "-vdPauseAfter");
            if (pauseAfter != null)
            {
                string pauseValue = GetValue(args, "-vdPauseValue") ?? "1";
                if (result.Role != "client" || !UnityEngine.Debug.isDebugBuild ||
                    !float.TryParse(pauseAfter, NumberStyles.Float, CultureInfo.InvariantCulture,
                        out float seconds) || !float.IsFinite(seconds) || seconds < 0f || seconds > 60f ||
                    (pauseValue != "0" && pauseValue != "1"))
                    throw new ArgumentException("-vdPauseAfter needs 0..60 seconds and -vdPauseValue 0 or 1 in a Development client.");
                result.HasScriptedPause = true;
                result.ScriptedPauseAfterSeconds = seconds;
                result.ScriptedPauseValue = (byte)(pauseValue == "1" ? 1 : 0);
            }

            string build = GetValue(args, "-vdBuild");
            if (build != null)
            {
                string[] parts = build.Split(',');
                if (result.Role != "client" || !UnityEngine.Debug.isDebugBuild || parts.Length != 5 ||
                    (string.IsNullOrWhiteSpace(parts[0]) || parts[0].Length > 60) ||
                    !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture,
                        out int startX) ||
                    !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture,
                        out int startZ) ||
                    !int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture,
                        out int endX) ||
                    !int.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture,
                        out int endZ))
                    throw new ArgumentException("-vdBuild requires a Development client and definition,x,z,endX,endZ.");
                result.HasScriptedBuild = true;
                result.ScriptedBuildDefinition = parts[0];
                result.ScriptedBuildStartX = startX;
                result.ScriptedBuildStartZ = startZ;
                result.ScriptedBuildEndX = endX;
                result.ScriptedBuildEndZ = endZ;
                result.ScriptedBuildRepeat = HasFlag(args, "-vdBuildRepeat");
                string operation = GetValue(args, "-vdBuildOperationId");
                uint requestedOperation = 0;
                if (operation != null &&
                    (!uint.TryParse(operation, NumberStyles.None, CultureInfo.InvariantCulture,
                        out requestedOperation) || requestedOperation == 0))
                    throw new ArgumentException("-vdBuildOperationId must be a positive uint.");
                result.ScriptedBuildOperationId = operation == null ? 0u : requestedOperation;
            }

            return result;
        }

#if UNITY_EDITOR
        public static NetworkLaunchArguments CreateEditorLocal(string Role, string RunIdentifier,
            ushort Port, string Ticket)
        {
            return new NetworkLaunchArguments
            {
                Role = Role,
                RunId = RunIdentifier,
                BindAddress = "127.0.0.1",
                Host = "127.0.0.1",
                Port = Port,
                DevelopmentIdentity = true,
                DevelopmentTicket = Ticket,
                DevelopmentAccountId = "editor-local-player",
                ConnectionCredential = Ticket,
                ProfileDisplayName = "Local Player"
            };
        }
#endif

        public void ApplyBackendAssignment(string runId, string serverId, string host, ushort port, string credential,
            string profileDisplayName, int profileVersion, string transport = "udp-loopback",
            string certificateName = "")
        {
            if (DevelopmentIdentity || Role != "client" || !Guid.TryParse(runId, out Guid parsed) ||
                !Guid.TryParse(serverId, out Guid parsedServer) || parsedServer == Guid.Empty ||
                parsed == Guid.Empty || string.IsNullOrWhiteSpace(host) || host.Length > 60 || port == 0 ||
                string.IsNullOrWhiteSpace(credential) || credential.Length > 120 ||
                profileVersion <= 0 || string.IsNullOrWhiteSpace(profileDisplayName) ||
                !IPAddress.TryParse(host, out IPAddress address) ||
                address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork ||
                (transport != "dtls" && transport != "udp-loopback") ||
                (transport == "udp-loopback" && !IPAddress.IsLoopback(address)) ||
                (transport == "dtls" && string.IsNullOrWhiteSpace(certificateName)))
            {
                throw new ArgumentException("Invalid backend client assignment.");
            }
            SecureTransport = transport == "dtls";
            CertificateName = certificateName;
            RunId = parsed.ToString("D");
            ServerId = parsedServer.ToString("D");
            Host = host;
            Port = port;
            ConnectionCredential = credential;
            ProfileDisplayName = profileDisplayName;
            ProfileVersion = profileVersion;
        }

        public void RetainGuestToken(string token)
        {
            if (DevelopmentIdentity || Role != "client" || string.IsNullOrEmpty(token) ||
                token.Length > 120)
                throw new ArgumentException("Invalid backend guest credential.");
            GuestToken = token;
        }

        private static string DefaultRole()
        {
#if UNITY_SERVER
            return "server";
#else
            return "client";
#endif
        }

        private static bool HasFlag(string[] args, string name)
        {
            foreach (string arg in args)
            {
                if (string.Equals(arg, name, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static string GetValue(string[] args, string name)
        {
            for (int index = 0; index + 1 < args.Length; index++)
            {
                if (string.Equals(args[index], name, StringComparison.Ordinal))
                {
                    return args[index + 1];
                }
            }

            return null;
        }
    }
}
