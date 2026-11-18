using System;
using System.IO;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using VaticanDefendor.Network;
using VaticanDefendor.Server;

namespace VaticanDefendor.Editor
{
    public static class BackendGrantVerification
    {
        [Serializable]
        private sealed class Cases
        {
            public string accountId;
            public string runId;
            public string serverId;
            public string secret;
            public string valid;
            public string expired;
            public string foreignAccount;
            public string foreignServer;
            public string staleGeneration;
        }

        public static void Verify()
        {
            string path = Environment.GetEnvironmentVariable("VD_TEST_GRANT_FILE");
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                throw new InvalidOperationException("VD_TEST_GRANT_FILE must name an existing local fixture.");
            }
            Cases cases = JsonUtility.FromJson<Cases>(File.ReadAllText(path));
            var session = new RunSession
            {
                AccountId = new FixedString64Bytes(cases.accountId),
                RunId = new FixedString64Bytes(cases.runId)
            };
            var grant = new BackendIdentityGrant
            {
                ServerId = new FixedString64Bytes(cases.serverId),
                Secret = new FixedString128Bytes(cases.secret),
                Generation = 1,
                Active = 1
            };
            Check(cases.valid, session, grant, JoinRejectionCode.None);
            Check(cases.expired, session, grant, JoinRejectionCode.ExpiredGrant);
            Check(cases.foreignAccount, session, grant, JoinRejectionCode.StaleAssignment);
            Check(cases.foreignServer, session, grant, JoinRejectionCode.StaleAssignment);
            Check(cases.staleGeneration, session, grant, JoinRejectionCode.StaleAssignment);
            grant.Active = 0;
            Check(cases.valid, session, grant, JoinRejectionCode.StaleAssignment);
            Debug.Log("[BackendGrantVerification] Python-issued valid, expired, foreign, stale and revoked grants verified in C#.");
        }

        private static void Check(string token, RunSession session, BackendIdentityGrant grant,
            JoinRejectionCode expected)
        {
            JoinRejectionCode actual = BackendGrantVerifier.Validate(token, session, grant);
            if (actual != expected)
            {
                throw new InvalidOperationException($"Grant result {actual}; expected {expected}.");
            }
        }
    }
}
