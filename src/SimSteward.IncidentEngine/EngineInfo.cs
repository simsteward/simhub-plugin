namespace SimSteward.IncidentEngine
{
    /// <summary>
    /// Marker for the pure Incident Engine library (detection, index build, stable identity,
    /// jump-to-next + misfire). Built greenfield from docs/superpowers/specs/incident-engine/.
    /// Real types (TelemetrySample, detectors, ReplayIncidentIndex, IncidentFingerprint, jump engine)
    /// are added test-first by the Ralph loop. This placeholder only proves the project builds green.
    /// </summary>
    public static class EngineInfo
    {
        public const string Name = "SimSteward.IncidentEngine";
        public const string Version = "0.1.0";
    }
}
