#if UNITY_SERVER || UNITY_EDITOR
using System;
using System.Security.Cryptography;
using System.Text;
using VaticanDefendor.Network;

namespace VaticanDefendor.Server
{
    public static class BackendGrantVerifier
    {
        public static JoinRejectionCode Validate(string token, RunSession session, BackendIdentityGrant grant)
        {
            if (grant.Active == 0)
            {
                return JoinRejectionCode.StaleAssignment;
            }
            if (string.IsNullOrEmpty(token) || token.Length > 120)
            {
                return JoinRejectionCode.Unauthorized;
            }
            byte[] bytes;
            try
            {
                string base64 = token.Replace('-', '+').Replace('_', '/');
                bytes = Convert.FromBase64String(base64.PadRight((base64.Length + 3) & ~3, '='));
            }
            catch (FormatException)
            {
                return JoinRejectionCode.Unauthorized;
            }
            if (bytes.Length != 89 || bytes[0] != 1)
            {
                return JoinRejectionCode.Unauthorized;
            }
            byte[] payload = new byte[57];
            Buffer.BlockCopy(bytes, 0, payload, 0, payload.Length);
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(grant.Secret.ToString()));
            byte[] signature = hmac.ComputeHash(payload);
            int difference = 0;
            for (int index = 0; index < signature.Length; index++)
            {
                difference |= signature[index] ^ bytes[payload.Length + index];
            }
            if (difference != 0)
            {
                return JoinRejectionCode.Unauthorized;
            }
            string accountId = ReadGuid(bytes, 1);
            string runId = ReadGuid(bytes, 17);
            string serverId = ReadGuid(bytes, 33);
            uint expiresAt = ReadUInt32(bytes, 49);
            uint generation = ReadUInt32(bytes, 53);
            if (expiresAt <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            {
                return JoinRejectionCode.ExpiredGrant;
            }
            if (accountId != session.AccountId.ToString() || runId != session.RunId.ToString() ||
                serverId != grant.ServerId.ToString() || generation != grant.Generation)
            {
                return JoinRejectionCode.StaleAssignment;
            }
            return JoinRejectionCode.None;
        }

        private static string ReadGuid(byte[] bytes, int offset)
        {
            byte[] value = new byte[16];
            Buffer.BlockCopy(bytes, offset, value, 0, 16);
            return new Guid(value).ToString("D");
        }

        private static uint ReadUInt32(byte[] bytes, int offset)
        {
            return ((uint)bytes[offset] << 24) | ((uint)bytes[offset + 1] << 16) |
                   ((uint)bytes[offset + 2] << 8) | bytes[offset + 3];
        }
    }
}
#endif
