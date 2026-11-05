using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;
using VaticanDefendor.Backend;
using VaticanDefendor.Content;

namespace VaticanDefendor.Editor
{
    public static class ContentReleasePublisher
    {
        [MenuItem("VaticanDefendor/Content/Export Selected Release JSON")]
        public static void ExportSelected()
        {
            ContentRelease release = Selection.activeObject as ContentRelease;
            if (release == null) throw new InvalidOperationException("Select a built ContentRelease asset.");
            string path = EditorUtility.SaveFilePanel("Export content release", "", release.ReleaseId + ".json", "json");
            if (!string.IsNullOrEmpty(path)) Export(release, path);
        }

        public static void Export(ContentRelease release, string path)
        {
            ContentReleaseHash.Validate(release);
            string payload = JsonUtility.ToJson(release);
            using var sha = SHA256.Create();
            var publication = new ContentPublication
            {
                releaseId = release.ReleaseId,
                schemaVersion = release.ContentSchemaVersion,
                payloadJson = payload,
                payloadSha256 = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(payload)))
                    .Replace("-", "").ToLowerInvariant()
            };
            File.WriteAllText(path, JsonUtility.ToJson(publication, true), new UTF8Encoding(false));
        }
    }
}
