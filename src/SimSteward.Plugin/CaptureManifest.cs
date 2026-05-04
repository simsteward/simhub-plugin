using Newtonsoft.Json;
using System.Collections.Generic;

namespace SimSteward.Plugin
{
    public sealed class CaptureManifest
    {
        [JsonProperty("version")] public int Version { get; set; } = 1;
        [JsonProperty("subSessionId")] public long SubSessionId { get; set; }
        [JsonProperty("generatedAt")] public string GeneratedAt { get; set; }
        [JsonProperty("entries")] public List<CaptureManifestEntry> Entries { get; set; } = new List<CaptureManifestEntry>();
    }

    public sealed class CaptureManifestEntry
    {
        [JsonProperty("fingerprint")] public string Fingerprint { get; set; }
        [JsonProperty("carIdx")] public int CarIdx { get; set; }
        [JsonProperty("sessionNum")] public int SessionNum { get; set; }
        [JsonProperty("sessionTimeMs")] public long SessionTimeMs { get; set; }
        [JsonProperty("lap")] public int Lap { get; set; } = -1;
        [JsonProperty("incidentPoints")] public int IncidentPoints { get; set; }
        [JsonProperty("detectionSource")] public string DetectionSource { get; set; }
        [JsonProperty("clips")] public List<CaptureClipEntry> Clips { get; set; } = new List<CaptureClipEntry>();
        [JsonProperty("pushedToQueue")] public bool PushedToQueue { get; set; }
        [JsonProperty("capturedAt")] public string CapturedAt { get; set; }
    }

    public sealed class CaptureClipEntry
    {
        [JsonProperty("cameraView")] public string CameraView { get; set; }
        [JsonProperty("capturedAt")] public string CapturedAt { get; set; }
        [JsonProperty("clipPath")] public string ClipPath { get; set; }
    }
}
