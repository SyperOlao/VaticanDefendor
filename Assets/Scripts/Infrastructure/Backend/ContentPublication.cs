using System;

namespace VaticanDefendor.Backend
{
    [Serializable]
    public sealed class ContentPublication
    {
        public string releaseId;
        public int schemaVersion;
        public string payloadJson;
        public string payloadSha256;
    }
}
