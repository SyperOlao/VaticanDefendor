using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using VaticanDefendor.Backend;
using VaticanDefendor.Content;

namespace VaticanDefendor.Bootstrap
{
    internal static class ContentReleaseDownload
    {
        public static async Task<string> FetchAsync(string baseUrl, string releaseId, CancellationToken cancel)
        {
            if (releaseId == null || !System.Text.RegularExpressions.Regex.IsMatch(releaseId, "^sha256-[0-9a-f]{64}$"))
                throw new InvalidDataException("Invalid content release ID.");
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30), MaxResponseContentBufferSize = 4_000_000 };
            using HttpResponseMessage response = await client.GetAsync(baseUrl + "/v1/content/" + releaseId, cancel);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync();
        }

        public static ContentRelease Decode(string json, string expectedId)
        {
            ContentPublication publication = JsonUtility.FromJson<ContentPublication>(json);
            if (publication == null || publication.releaseId != expectedId ||
                publication.schemaVersion != ContentRelease.CurrentContentSchemaVersion ||
                string.IsNullOrEmpty(publication.payloadJson))
                throw new InvalidDataException("Incompatible content publication.");
            using var sha = SHA256.Create();
            string digest = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(publication.payloadJson)))
                .Replace("-", "").ToLowerInvariant();
            if (digest != publication.payloadSha256)
                throw new InvalidDataException("Content download checksum mismatch.");
            ContentRelease release = ScriptableObject.CreateInstance<ContentRelease>();
            try
            {
                JsonUtility.FromJsonOverwrite(publication.payloadJson, release);
                if (release.ReleaseId != expectedId) throw new InvalidDataException("Wrong content release.");
                ContentReleaseHash.Validate(release);
                return release;
            }
            catch
            {
                UnityEngine.Object.Destroy(release);
                throw;
            }
        }
    }
}
