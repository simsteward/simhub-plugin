#if SIMHUB_SDK
using GameReaderCommon;
using SimHub.Plugins;
using System.Windows.Media;
using IRSDKSharper;
#endif

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace SimSteward.Plugin
{
#if SIMHUB_SDK
    [PluginName("Sim Steward")]
    [PluginDescription("Sim Steward: HTML dashboard bridge via WebSocket")]
    [PluginAuthor("Sim Steward")]
    public class SimStewardPlugin : IPlugin, IDataPlugin, IWPFSettingsV2
#else
#pragma warning disable CS0169, CS0649 // Fields only assigned when SIMHUB_SDK; unused when SDK absent
    public class SimStewardPlugin
#endif
    {
        private const int DefaultPort = 19847;
        private const double BroadcastThrottleMs = 200;
        private const double WaitingLogThrottleMs = 10000;
        private int _wsPort = DefaultPort;

#if SIMHUB_SDK
        public PluginManager PluginManager { get; set; }
#endif

        private PluginLogger _logger;
        private bool _debugMode;
        private SessionStats _sessionStats = new SessionStats();
        private DashboardBridge _bridge;
        private DateTime _lastBroadcastAt = DateTime.MinValue;
        private int _lastLoggedPlayerCarIdx = -2;
        private DateTime _lastWaitingLogAt = DateTime.MinValue;
        private string _pluginDataPath;
        private string _webApiPath;   // SimHub\Web\sim-steward-dash\api\ — served at /Web/sim-steward-dash/api/
        private string _settingsPath;
        private PluginUiSettings _settings = new PluginUiSettings();

        private sealed class PluginUiSettings
        {
            /// <summary>Log levels to omit at source (no file, structured log, or dashboard). E.g. ["DEBUG"].</summary>
            public List<string> OmitLogLevels { get; set; }
            /// <summary>Log events to omit at source (same as dashboard hidden events). E.g. ["state_broadcast_summary","tick_stats","ws_message_raw"].</summary>
            public List<string> OmitEvents { get; set; }
            /// <summary>Data API base URL for session-complete POST (e.g. http://localhost:8080). Empty = disabled.</summary>
            public string DataApiEndpoint { get; set; }
            /// <summary>When true, do not omit action_received and action_dispatched (full action traffic in logs).</summary>
            public bool LogAllActionTraffic { get; set; }
        }

        private static readonly HttpClient DataApiHttpClient = new HttpClient();
        private HashSet<string> _omittedLogLevels;
        private HashSet<string> _omittedEvents;
        private readonly object _omitLock = new object();
        private readonly object _broadcastErrorLock = new object();
        private DateTime _lastNoClientsLogAt = DateTime.MinValue;

        private PluginUiSettings LoadUiSettings()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_settingsPath) || !File.Exists(_settingsPath))
                    return new PluginUiSettings();
                var json = File.ReadAllText(_settingsPath);
                var parsed = JsonConvert.DeserializeObject<PluginUiSettings>(json);
                return parsed ?? new PluginUiSettings();
            }
            catch
            {
                return new PluginUiSettings();
            }
        }

        private bool GetLogAllActionTrafficEffective()
        {
            return _settings != null && _settings.LogAllActionTraffic ||
                   Environment.GetEnvironmentVariable("SIMSTEWARD_LOG_ALL_ACTIONS") == "1";
        }

        private void SaveUiSettings()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_settingsPath))
                    return;
                var dir = Path.GetDirectoryName(_settingsPath);
                if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                var json = JsonConvert.SerializeObject(_settings, Formatting.Indented);
                File.WriteAllText(_settingsPath, json);
            }
            catch
            {
                // Never fail plugin startup/runtime due to settings write.
            }
        }

#if SIMHUB_SDK
        private IRacingSdk _irsdk;
        private double _lastSessionTime;
        private string _pluginMode = "Unknown";
        private int _replayFrameNum;
        private int _replayFrameNumEnd;
        private int _replayPlaySpeed;
        private bool _replayPlaySlowMotion;
        private bool _replayIsPlaying;
        private int _replaySessionNum;
        private readonly IncidentTracker _tracker = new IncidentTracker();
        private int _scanBroadcastCounter;
        private bool _wasPostRace;
        private bool _wasReplay;
        private SessionSummaryCaptureStatus _lastSummaryCapture = new SessionSummaryCaptureStatus();
        private FinalizeThenCaptureJob _finalizeThenCaptureJob;
        private bool _autoSessionSnapshotsEnabled;
        private int _lastAutoSnapshotSiu = -1;
        private int _lastAutoSnapshotSessionState = -1;
        private bool _lastAutoSnapshotResultsReady;
        private DateTime _lastAutoSnapshotAt = DateTime.MinValue;
        private bool _sessionDigestEmitted;
        private int _dataUpdateTickCount;
        private int _lastLoggedSiu = -1;
        private double _runningAvgDataUpdateMs;
        private DateTime? _checkeredRetryAfter; // when set, retry session capture once after this time (ResultsPositions may lag checkered)
        private int _prevPlayerCarMyIncidentCount = -1;
        private int _lastFocusedCarIdx = -1;
        private readonly Dictionary<int, int> _prevFocusedCarIncidentCounts = new Dictionary<int, int>();
        private readonly object _incidentPersistLock = new object();
        private volatile bool _broadcastNoClientsPending;
        private DateTime _lastYamlInfoLogAt = DateTime.MinValue;
        private string _currentSessionId;
        private string _currentSessionSeq;
        private string _lastIncidentId;
        private DateTime _lastIncidentAt;
#endif

        /// <summary>Build the full state JSON for WebSocket push.</summary>
#if SIMHUB_SDK
        private string BuildStateJson(PluginSnapshot snapshot)
        {
            var state = new
            {
                type = "state",
                pluginMode = snapshot.PluginMode,
                currentSessionTime = snapshot.CurrentSessionTime,
                currentSessionTimeFormatted = snapshot.CurrentSessionTimeFormatted,
                replayIsPlaying = snapshot.ReplayIsPlaying,
                replayFrameNum = snapshot.ReplayFrameNum,
                replayFrameNumEnd = snapshot.ReplayFrameNumEnd,
                replayPlaySpeed = snapshot.ReplayPlaySpeed,
                replayPlaySlowMotion = snapshot.ReplayPlaySlowMotion,
                replaySessionNum = snapshot.ReplaySessionNum,
                playerCarIdx = snapshot.PlayerCarIdx,
                playerIncidentCount = snapshot.PlayerIncidentCount,
                hasLiveIncidentData = snapshot.HasLiveIncidentData,
                trackName = snapshot.TrackName,
                trackCategory = snapshot.TrackCategory,
                trackLengthM = snapshot.TrackLengthM,
                sessionId = snapshot.SessionId,
                drivers = snapshot.Drivers,
                incidents = snapshot.Incidents,
                metrics = snapshot.Metrics,
                diagnostics = snapshot.Diagnostics,
                sessionDiagnostics = snapshot.SessionDiagnostics
            };
            return JsonConvert.SerializeObject(state);
        }
#else
        private string BuildStateJson()
        {
            var drivers = new System.Collections.Generic.List<DriverRecord>();
            var incidents = new System.Collections.Generic.List<IncidentEvent>();
            var state = new
            {
                type = "state",
                pluginMode = "Unknown",
                currentSessionTime = 0.0,
                currentSessionTimeFormatted = "0:00",
                replayIsPlaying = false,
                replayFrameNum = 0,
                replayFrameNumEnd = 0,
                replayPlaySpeed = 0,
                replayPlaySlowMotion = false,
                playerCarIdx = -1,
                playerIncidentCount = 0,
                drivers,
                incidents
            };
            return JsonConvert.SerializeObject(state);
        }
#endif

#if SIMHUB_SDK
        private static string FormatSessionTime(double totalSeconds)
        {
            if (double.IsNaN(totalSeconds) || totalSeconds < 0 || double.IsInfinity(totalSeconds))
                return "0:00";
            int minutes = (int)(totalSeconds / 60);
            int seconds = (int)(totalSeconds % 60);
            return $"{minutes}:{seconds:D2}";
        }
#endif

        private string GetStateForNewClient()
        {
#if SIMHUB_SDK
            var snapshot = BuildPluginSnapshot();
            return BuildStateJson(snapshot);
#else
            return BuildStateJson();
#endif
        }

        private string GetLogTailForNewClient()
        {
            if (_logger == null) return null;
            var tail = _logger.GetTailIncludingIncidents(50, 20);
            if (tail == null || tail.Count == 0) return null;
            var msg = new { type = "logEvents", entries = tail };
            return JsonConvert.SerializeObject(msg);
        }

        private void OnLogWritten(LogEntry entry)
        {
            if (entry == null) return;
            if (_bridge == null) return;
            // incident_detected is pushed to the log stream from DataUpdate when we broadcast incidentEvents, so we avoid duplicate log lines
            if (string.Equals(entry.Event, "incident_detected", StringComparison.OrdinalIgnoreCase))
                return;
            try
            {
                var msg = new { type = "logEvents", entries = new[] { entry } };
                _bridge.Broadcast(JsonConvert.SerializeObject(msg), "logEvents");
            }
            catch (Exception ex)
            {
                WriteBroadcastError("OnLogWritten", ex);
            }
        }

        /// <summary>Write a line to broadcast-errors.log (no logger, no recursion). Thread-safe.</summary>
        private void WriteBroadcastError(string context, Exception ex)
        {
            if (string.IsNullOrEmpty(_pluginDataPath)) return;
            var path = System.IO.Path.Combine(_pluginDataPath, "broadcast-errors.log");
            var line = DateTime.UtcNow.ToString("o") + " " + context + (ex != null ? " " + ex.Message : "") + Environment.NewLine;
            lock (_broadcastErrorLock)
            {
                try
                {
                    var dir = System.IO.Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir) && !System.IO.Directory.Exists(dir))
                        System.IO.Directory.CreateDirectory(dir);
                    System.IO.File.AppendAllText(path, line, System.Text.Encoding.UTF8);
                }
                catch { }
            }
        }

#if SIMHUB_SDK
        /// <summary>Append one incident to incidents.jsonl for full history. Thread-safe. No logger.</summary>
        private void PersistIncident(IncidentEvent ev)
        {
            if (ev == null || string.IsNullOrEmpty(_pluginDataPath)) return;
            var path = System.IO.Path.Combine(_pluginDataPath, "incidents.jsonl");
            var line = JsonConvert.SerializeObject(ev) + Environment.NewLine;
            lock (_incidentPersistLock)
            {
                try
                {
                    var dir = System.IO.Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir) && !System.IO.Directory.Exists(dir))
                        System.IO.Directory.CreateDirectory(dir);
                    System.IO.File.AppendAllText(path, line, System.Text.Encoding.UTF8);
                }
                catch { }
            }
        }

        /// <summary>Write full scan result to a JSON file. Thread-safe.</summary>
        private void PersistScanResult(ReplayScanProgress progress)
        {
            if (progress == null || string.IsNullOrEmpty(_pluginDataPath)) return;
            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
            var fileName = $"replay_scan_{_tracker.SubSessionId}_{timestamp}.json";
            var path = System.IO.Path.Combine(_pluginDataPath, fileName);
            lock (_incidentPersistLock)
            {
                try
                {
                    var dir = System.IO.Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir) && !System.IO.Directory.Exists(dir))
                        System.IO.Directory.CreateDirectory(dir);
                    var json = JsonConvert.SerializeObject(progress, Formatting.Indented);
                    System.IO.File.WriteAllText(path, json, System.Text.Encoding.UTF8);
                }
                catch { }
            }
            _logger?.Structured("INFO", "simhub-plugin", "replay_scan_persisted",
                $"Scan result written to {fileName}",
                new Dictionary<string, object> { ["file"] = fileName, ["incidents"] = progress.IncidentsFound },
                "replay_scan", null);
            PersistScanSummary(progress);
        }

        /// <summary>
        /// Write session_meta_{subSessionId}.json once per session at baseline capture.
        /// Only includes WeekendInfo/WeekendOptions/DriverInfo — all available in replay
        /// without a completed session (no checkered flag dependency).
        /// </summary>
        private void PersistSessionMeta()
        {
            if (_tracker.SubSessionId == 0 || string.IsNullOrEmpty(_pluginDataPath)) return;
            var fileName = $"session_meta_{_tracker.SubSessionId}.json";
            var path = System.IO.Path.Combine(_pluginDataPath, fileName);
            if (System.IO.File.Exists(path)) return; // already written for this session

            try
            {
                var si = _irsdk?.Data?.SessionInfo;
                var w = si?.WeekendInfo;
                var opts = w?.WeekendOptions;
                var sessionPrefix = _tracker.GetSessionPrefix();
                var meta = new
                {
                    sessionPrefix    = sessionPrefix,
                    subSessionId     = _tracker.SubSessionId,
                    sessionId        = w?.SessionID ?? 0,
                    trackName        = _tracker.TrackName,
                    trackCategory    = _tracker.TrackCategory,
                    trackLength      = w?.TrackLength ?? "",
                    trackCity        = w?.TrackCity ?? "",
                    trackCountry     = w?.TrackCountry ?? "",
                    simMode          = w?.SimMode ?? "",
                    seriesId         = w?.SeriesID ?? 0,
                    seasonId         = w?.SeasonID ?? 0,
                    leagueId         = w?.LeagueID ?? 0,
                    incidentLimit    = opts?.IncidentLimit ?? "",
                    fastRepairsLimit = opts?.FastRepairsLimit ?? "",
                    sessions         = si?.SessionInfo?.Sessions?.Select(s => new {
                                           s.SessionNum, s.SessionType, s.SessionName
                                       }).ToList(),
                    drivers          = _tracker.GetDriverSnapshot(),
                    capturedAtUtc    = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                };
                var dir = System.IO.Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !System.IO.Directory.Exists(dir))
                    System.IO.Directory.CreateDirectory(dir);
                System.IO.File.WriteAllText(path, JsonConvert.SerializeObject(meta, Formatting.Indented), System.Text.Encoding.UTF8);
                _logger?.Structured("INFO", "simhub-plugin", "session_meta_persisted",
                    $"Session metadata written: {fileName}",
                    new Dictionary<string, object> { ["file"] = fileName, ["session_prefix"] = sessionPrefix },
                    "replay_scan", null);
            }
            catch { }
        }

        /// <summary>
        /// Write session_summary_{subSessionId}_{ts}.json after scan completes.
        /// Contains sessionPrefix, drivers, and full incidentFeed with fingerprints.
        /// No ResultsPositions — replay may not have a checkered flag.
        /// </summary>
        private void PersistScanSummary(ReplayScanProgress progress)
        {
            if (progress == null || string.IsNullOrEmpty(_pluginDataPath)) return;
            try
            {
                var fileName = $"session_summary_{_tracker.SubSessionId}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json";
                var path = System.IO.Path.Combine(_pluginDataPath, fileName);
                var sessionPrefix = _tracker.GetSessionPrefix();
                var summary = new
                {
                    sessionPrefix  = sessionPrefix,
                    subSessionId   = _tracker.SubSessionId,
                    trackName      = _tracker.TrackName,
                    capturedAtUtc  = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                    drivers        = _tracker.GetDriverSnapshot(),
                    incidentFeed   = _tracker.GetIncidentFeed(),
                    incidentsFound = progress.IncidentsFound,
                };
                var dir = System.IO.Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !System.IO.Directory.Exists(dir))
                    System.IO.Directory.CreateDirectory(dir);
                System.IO.File.WriteAllText(path, JsonConvert.SerializeObject(summary, Formatting.Indented), System.Text.Encoding.UTF8);
                _logger?.Structured("INFO", "simhub-plugin", "session_summary_persisted",
                    $"Session summary written: {fileName}",
                    new Dictionary<string, object> { ["file"] = fileName, ["incidents"] = progress.IncidentsFound, ["session_prefix"] = sessionPrefix },
                    "replay_scan", null);
            }
            catch { }
        }

        // ── Web API file writes ───────────────────────────────────────────────
        // SimHub's HTTP server (port 8888) serves Web\ as static files.
        // We write JSON here so the dashboard (and external tools) can GET them:
        //   /Web/sim-steward-dash/api/incidents.json
        //   /Web/sim-steward-dash/api/session.json
        //   /Web/sim-steward-dash/api/snapshots/{incidentId}.json

        /// <summary>Rewrite incidents.json with the full current feed. Called after each AddIncident.</summary>
        private void WriteWebApiIncidentFeed()
        {
            if (string.IsNullOrEmpty(_webApiPath)) return;
            try
            {
                var feed = _tracker.GetIncidentFeed();
                var payload = new
                {
                    sessionPrefix = _tracker.GetSessionPrefix(),
                    subSessionId  = _tracker.SubSessionId,
                    updatedAtUtc  = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                    incidents     = feed,
                };
                Directory.CreateDirectory(_webApiPath);
                File.WriteAllText(
                    Path.Combine(_webApiPath, "incidents.json"),
                    JsonConvert.SerializeObject(payload),
                    System.Text.Encoding.UTF8);
            }
            catch { }
        }

        /// <summary>Write session.json with meta + driver roster. Written once per session after baseline capture.</summary>
        private void WriteWebApiSession()
        {
            if (string.IsNullOrEmpty(_webApiPath) || _tracker.SubSessionId == 0) return;
            try
            {
                var si = _irsdk?.Data?.SessionInfo;
                var w  = si?.WeekendInfo;
                var payload = new
                {
                    sessionPrefix = _tracker.GetSessionPrefix(),
                    subSessionId  = _tracker.SubSessionId,
                    trackName     = _tracker.TrackName,
                    trackLength   = w?.TrackLength ?? "",
                    seriesId      = w?.SeriesID ?? 0,
                    simMode       = w?.SimMode ?? "",
                    drivers       = _tracker.GetDriverSnapshot(),
                    capturedAtUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                };
                Directory.CreateDirectory(_webApiPath);
                File.WriteAllText(
                    Path.Combine(_webApiPath, "session.json"),
                    JsonConvert.SerializeObject(payload),
                    System.Text.Encoding.UTF8);
            }
            catch { }
        }

        /// <summary>Write one file per snapshot: snapshots/{incidentId}.json. Called after scan completes.</summary>
        private void WriteWebApiSnapshotFiles(ReplayScanProgress progress)
        {
            if (string.IsNullOrEmpty(_webApiPath) || progress?.Snapshots == null) return;
            try
            {
                var snapshotsDir = Path.Combine(_webApiPath, "snapshots");
                Directory.CreateDirectory(snapshotsDir);
                foreach (var snap in progress.Snapshots)
                {
                    if (snap?.IncidentId == null) continue;
                    // Sanitize incidentId for use as filename (replace chars not safe on Windows)
                    var safeName = snap.IncidentId.Replace(':', '_').Replace('/', '_') + ".json";
                    File.WriteAllText(
                        Path.Combine(snapshotsDir, safeName),
                        JsonConvert.SerializeObject(snap),
                        System.Text.Encoding.UTF8);
                }
            }
            catch { }
        }
#endif

#if SIMHUB_SDK
        private static ProjectMarkers BuildProjectMarkers()
        {
            return new ProjectMarkers();
        }

        /// <summary>
        /// Builds human-readable session sequence: "{trackName}_{yyyyMMdd}". Used as session_seq and as session_id fallback until SubSessionID is available.
        /// </summary>
        private static string BuildSessionSeq(string trackName)
        {
            if (string.IsNullOrEmpty(trackName)) return "";
            var safe = new System.Text.StringBuilder();
            foreach (var c in trackName)
                safe.Append(char.IsLetterOrDigit(c) ? c : '_');
            return $"{safe}_{DateTime.UtcNow:yyyyMMdd}";
        }

#if SIMHUB_SDK
        /// <summary>Current session id for spine (SubSessionID when available, else session_seq). Updated in DataUpdate.</summary>
        private string GetCurrentSessionId() => _currentSessionId ?? "";
        /// <summary>Incident id to attach to log lines within 2s of an incident (for trace-style LogQL).</summary>
        private string GetIncidentIdForSpine()
        {
            if (string.IsNullOrEmpty(_lastIncidentId) || _lastIncidentAt == default) return null;
            if ((DateTime.UtcNow - _lastIncidentAt).TotalSeconds > 2) return null;
            return _lastIncidentId;
        }
#endif

        private static string GetString(Dictionary<string, object> f, string key)
        {
            if (f == null || !f.TryGetValue(key, out var o) || o == null) return "";
            return o.ToString();
        }

        private static int GetIntFromFields(Dictionary<string, object> f, string key)
        {
            if (f == null || !f.TryGetValue(key, out var o)) return 0;
            if (o is int i) return i;
            if (o is long l) return (int)l;
            if (o != null && int.TryParse(o.ToString(), out var parsed)) return parsed;
            return 0;
        }

        private static double GetDoubleFromFields(Dictionary<string, object> f, string key)
        {
            if (f == null || !f.TryGetValue(key, out var o)) return 0;
            if (o is double d) return d;
            if (o is float fl) return fl;
            if (o is int i) return i;
            if (o is long l) return l;
            if (o != null && double.TryParse(o.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed)) return parsed;
            return 0;
        }

        private PluginSnapshot BuildPluginSnapshot()
        {
            var irConnected = _irsdk?.IsConnected ?? false;
            var clientCount = _bridge?.ClientCount ?? 0;
            return new PluginSnapshot
            {
                PluginMode = _pluginMode,
                CurrentSessionTime = _lastSessionTime,
                CurrentSessionTimeFormatted = FormatSessionTime(_lastSessionTime),
                ReplayIsPlaying = _replayIsPlaying,
                ReplayFrameNum = _replayFrameNum,
                ReplayFrameNumEnd = _replayFrameNumEnd,
                ReplayPlaySpeed = _replayPlaySpeed,
                ReplayPlaySlowMotion = _replayPlaySlowMotion,
                ReplaySessionNum = _replaySessionNum,
                PlayerCarIdx = _tracker.PlayerCarIdx,
                PlayerIncidentCount = _tracker.PlayerIncidentCount,
                HasLiveIncidentData = irConnected && _tracker.BaselineEstablished,
                TrackName = _tracker.TrackName,
                TrackCategory = _tracker.TrackCategory,
                TrackLengthM = _tracker.TrackLengthM,
                SessionId = GetCurrentSessionId(),
                Drivers = _tracker.GetDriverSnapshot(),
                Incidents = _tracker.GetIncidentFeed(),
                Metrics = _tracker.GetMetricsSnapshot(),
                Diagnostics = new PluginDiagnostics
                {
                    IrsdkStarted   = _irsdk != null,
                    IrsdkConnected = irConnected,
                    WsRunning      = _bridge != null,
                    WsPort         = _wsPort,
                    WsClients      = clientCount,
                    PlayerCarIdx   = _tracker.PlayerCarIdx,
                },
                SessionDiagnostics = BuildSessionDataDiagnostics(),
                ProjectMarkers = BuildProjectMarkers(),
                ReplayScan = _tracker.CurrentScanState != IncidentTracker.ScanState.Idle ? _tracker.ScanProgress : null
            };
        }

        private SessionDataDiagnostics BuildSessionDataDiagnostics()
        {
            var diag = new SessionDataDiagnostics
            {
                SimMode = _irsdk?.Data?.SessionInfo?.WeekendInfo?.SimMode ?? _pluginMode ?? "Unknown",
                SessionState = SafeGetInt("SessionState"),
                SessionNum = SafeGetInt("SessionNum"),
                SessionInfoUpdate = SafeGetInt("SessionInfoUpdate"),
                SessionFlags = SafeGetInt("SessionFlags"),
                HasSessionInfo = _irsdk?.Data?.SessionInfo != null,
                LastSummaryCapture = _lastSummaryCapture ?? new SessionSummaryCaptureStatus(),
            };
            var w = _irsdk?.Data?.SessionInfo?.WeekendInfo;
            diag.IrSessionId = w?.SessionID ?? 0;
            diag.IrSubSessionId = w?.SubSessionID ?? 0;

            try
            {
                var si = _irsdk?.Data?.SessionInfo;
                var sessions = si?.SessionInfo?.Sessions;
                int wantedSessionNum = _replaySessionNum >= 0 ? _replaySessionNum : SafeGetInt("SessionNum");

                if (sessions != null)
                {
                    diag.Sessions = sessions.Select(s => new SessionInfoEntry
                    {
                        SessionNum = s.SessionNum,
                        SessionType = s.SessionType ?? "",
                        SessionName = s.SessionName ?? "",
                    }).ToList();
                }

                var session =
                    sessions?.FirstOrDefault(s => s.SessionNum == wantedSessionNum && s.ResultsPositions != null && s.ResultsPositions.Count > 0)
                    ?? sessions?.FirstOrDefault(s => s.SessionNum == wantedSessionNum)
                    ?? sessions?.LastOrDefault(s => s.ResultsPositions != null && s.ResultsPositions.Count > 0);

                if (session != null)
                {
                    diag.SelectedResultsSessionNum = session.SessionNum;
                    diag.SelectedResultsSessionType = session.SessionType ?? "";
                    diag.ResultsPositionsCount = session.ResultsPositions?.Count ?? 0;
                    diag.ResultsLapsComplete = session.ResultsLapsComplete;
                    diag.ResultsOfficial = session.ResultsOfficial;
                }
                diag.ResultsReady = diag.ResultsPositionsCount > 0;
            }
            catch
            {
                // best-effort diagnostics; never break state push
            }

            try
            {
                var drivers = _tracker.GetDriverSnapshot();
                int active = 0, nonZero = 0, max = 0;
                foreach (var d in drivers)
                {
                    if (d.IsSpectator) continue;
                    active++;
                    if (d.IncidentCount > 0) nonZero++;
                    if (d.IncidentCount > max) max = d.IncidentCount;
                }
                diag.ActiveDriverCount = active;
                diag.DriversWithNonZeroIncidents = nonZero;
                diag.MaxDriverIncidents = max;
                diag.AllNonSpectatorIncidentsZero = active > 0 && nonZero == 0;
            }
            catch
            {
                // best-effort
            }

            return diag;
        }
#endif

        /// <summary>Dispatch an action from the dashboard. Returns (success, result, error). correlationId optional; generated if null.</summary>
        private (bool success, string result, string error) DispatchAction(string action, string arg, string correlationId = null)
        {
            if (string.IsNullOrEmpty(action))
                return (false, null, "missing_action");

            correlationId = correlationId ?? Guid.NewGuid().ToString("N").Substring(0, 8);
            var sw = Stopwatch.StartNew();

            var dispatchFields = new Dictionary<string, object> { ["action"] = action, ["arg"] = arg, ["correlation_id"] = correlationId };
#if SIMHUB_SDK
            if (_tracker != null) dispatchFields["session_id"] = GetCurrentSessionId();
            dispatchFields["session_num"] = _replaySessionNum;
#endif
            _logger?.Structured("INFO", "simhub-plugin", "action_dispatched", action, dispatchFields, "action", GetIncidentIdForSpine());

#if SIMHUB_SDK
            if (TryHandleReplayAction(action, arg, out var replayResult))
            {
                sw.Stop();
                _sessionStats.Record(action, replayResult.success, sw.ElapsedMilliseconds);
                _logger?.Structured("INFO", "simhub-plugin", "action_result", $"{action} -> {(replayResult.success ? "ok" : replayResult.error)}", new Dictionary<string, object>
                {
                    ["action"] = action, ["arg"] = arg, ["correlation_id"] = correlationId,
                    ["success"] = replayResult.success, ["result"] = replayResult.result, ["error"] = replayResult.error, ["duration_ms"] = sw.ElapsedMilliseconds
                }, "action", GetIncidentIdForSpine());
                RecordDashboardAction(action);
                return replayResult;
            }
#endif

            (bool success, string result, string error) response;
            switch (action)
            {
                case "CaptureSessionSummaryNow":
#if SIMHUB_SDK
                    response = TryCaptureAndEmitSessionSummary("dashboard", logNotReady: true);
#else
                    response = (false, null, "not_supported");
#endif
                    break;
                case "FinalizeThenCaptureSessionSummary":
#if SIMHUB_SDK
                    response = StartFinalizeThenCaptureJob();
#else
                    response = (false, null, "not_supported");
#endif
                    break;
                case "RecordSessionSnapshot":
#if SIMHUB_SDK
                    response = RecordSessionSnapshot(string.IsNullOrWhiteSpace(arg) ? "dashboard" : ("dashboard:" + arg.Trim()));
#else
                    response = (false, null, "not_supported");
#endif
                    break;
                case "ToggleIntentionalCapture":
                case "SetReplayCaptureSpeed":
                case "SetSecondsBefore":
                case "SetSecondsAfter":
                case "SetCaptureDriver1":
                case "SetCaptureDriver2":
                case "SetCaptureCamera1":
                case "SetCaptureCamera2":
                case "SetAutoRotateAndCapture":
                case "ToggleAutoRotateAndCapture":
                case "SetAutoRotateDwellSeconds":
                    response = (true, "stub", null);
                    break;
                default:
                    response = (false, null, "unknown_action");
                    break;
            }
            sw.Stop();
            _sessionStats.Record(action, response.success, sw.ElapsedMilliseconds);
            _logger?.Structured("INFO", "simhub-plugin", "action_result", $"{action} -> {(response.success ? "ok" : response.error)}", new Dictionary<string, object>
            {
                ["action"] = action, ["arg"] = arg, ["correlation_id"] = correlationId,
                ["success"] = response.success, ["result"] = response.result, ["error"] = response.error, ["duration_ms"] = sw.ElapsedMilliseconds
            }, "action", GetIncidentIdForSpine());
            RecordDashboardAction(action);
            return response;
        }

        private void RecordDashboardAction(string action)
        {
            // No-op: previously used for memory-bank markers (personal dev tool, not a project feature).
        }

#if SIMHUB_SDK
        private bool TryHandleReplayAction(string action, string arg, out (bool success, string result, string error) result)
        {
            switch (action)
            {
                case "ReplayPlayPause":
                    result = HandleReplayPlayPause();
                    return true;
                case "ReplaySetSpeed":
                    result = HandleReplaySetSpeed(arg);
                    return true;
                case "NextIncident":
                    result = HandleReplaySearch(IRacingSdkEnum.RpySrchMode.NextIncident);
                    return true;
                case "PrevIncident":
                    result = HandleReplaySearch(IRacingSdkEnum.RpySrchMode.PrevIncident);
                    return true;
                case "ReplayStepFrame":
                    result = HandleReplayStepFrame(arg);
                    return true;
                case "ReplaySeekFrame":
                    result = HandleReplaySeekFrame(arg);
                    return true;
                case "SelectIncidentAndSeek":
                    result = HandleSelectIncidentAndSeek(arg);
                    return true;
                case "ReplaySeekSessionStart":
                    result = HandleReplaySeekSessionStart(arg);
                    return true;
                case "ReplaySeekToSessionEnd":
                    result = HandleReplaySeekToSessionEnd(arg);
                    return true;
                case "StartReplayScan":
                    result = HandleStartReplayScan(arg);
                    return true;
                case "StopReplayScan":
                    result = HandleStopReplayScan();
                    return true;
                case "GetReplayScanProgress":
                    result = HandleGetReplayScanProgress();
                    return true;
                default:
                    result = default;
                    return false;
            }
        }

        /// <summary>Parse SessionTime string (e.g. "30 min", "2 hrs") to milliseconds. Best effort; returns 0 if unparseable.</summary>
        private static int ParseSessionTimeToMs(string sessionTime)
        {
            if (string.IsNullOrWhiteSpace(sessionTime)) return 0;
            var s = sessionTime.Trim();
            if (s.IndexOf("hr", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                if (double.TryParse(System.Text.RegularExpressions.Regex.Match(s, @"[\d.]+").Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double hrs))
                    return (int)Math.Min(hrs * 3600 * 1000, int.MaxValue);
            }
            if (s.IndexOf("min", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                if (double.TryParse(System.Text.RegularExpressions.Regex.Match(s, @"[\d.]+").Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double min))
                    return (int)Math.Min(min * 60 * 1000, int.MaxValue);
            }
            return 0;
        }

        private (bool success, string result, string error) HandleReplaySeekToSessionEnd(string arg)
        {
            if (!EnsureIrsdkConnected(out var error))
                return (false, null, error);
            if (string.IsNullOrWhiteSpace(arg) || !int.TryParse(arg.Trim(), out int sessionNum))
                return (false, null, "invalid_session_num");
            var si = _irsdk?.Data?.SessionInfo;
            var sessions = si?.SessionInfo?.Sessions;
            var session = sessions?.FirstOrDefault(s => s.SessionNum == sessionNum);
            if (session == null)
                return (false, null, "session_not_found");
            int sessionEndTimeMs = ParseSessionTimeToMs(session.SessionTime);
            _logger?.Structured("INFO", "simhub-plugin", "replay_control", "Replay seek to session end", new Dictionary<string, object> { ["mode"] = "seek_session_end", ["session_num"] = sessionNum, ["session_time_ms"] = sessionEndTimeMs }, "replay", GetIncidentIdForSpine());
            _irsdk.ReplaySearchSessionTime(sessionNum, sessionEndTimeMs);
            return (true, "ok", null);
        }

        private (bool success, string result, string error) HandleReplayPlayPause()
        {
            if (!EnsureIrsdkConnected(out var error))
                return (false, null, error);
            var nextSpeed = _replayPlaySpeed == 0 ? 1 : 0;
            _irsdk.ReplaySetPlaySpeed(nextSpeed, _replayPlaySlowMotion);
            _replayPlaySpeed = nextSpeed;
            _replayIsPlaying = nextSpeed != 0;
            var modeStr = nextSpeed == 0 ? "pause" : "play";
            _logger?.Structured("INFO", "simhub-plugin", "replay_control", $"Replay: {modeStr}", new Dictionary<string, object> { ["mode"] = modeStr }, "replay", GetIncidentIdForSpine());
            return (true, "ok", null);
        }

        private (bool success, string result, string error) HandleReplaySetSpeed(string arg)
        {
            if (!EnsureIrsdkConnected(out var error))
                return (false, null, error);
            if (!int.TryParse(arg ?? string.Empty, out var speed))
                return (false, null, "invalid_speed");
            _irsdk.ReplaySetPlaySpeed(speed, _replayPlaySlowMotion);
            _replayPlaySpeed = speed;
            _replayIsPlaying = speed != 0;
            _logger?.Structured("INFO", "simhub-plugin", "replay_control", $"Replay speed -> {speed}x", new Dictionary<string, object> { ["mode"] = "speed", ["speed"] = speed }, "replay", GetIncidentIdForSpine());
            return (true, "ok", null);
        }

        private (bool success, string result, string error) HandleReplaySearch(IRacingSdkEnum.RpySrchMode mode)
        {
            if (!EnsureIrsdkConnected(out var error))
                return (false, null, error);
            _irsdk.ReplaySearch(mode);
            _logger?.Structured("INFO", "simhub-plugin", "replay_control", $"Replay search: {mode}", new Dictionary<string, object> { ["mode"] = "search", ["search_mode"] = mode.ToString() }, "replay", GetIncidentIdForSpine());
            return (true, "ok", null);
        }

        private (bool success, string result, string error) HandleReplayStepFrame(string arg)
        {
            if (!EnsureIrsdkConnected(out var error))
                return (false, null, error);
            if (!int.TryParse(arg ?? string.Empty, out var step) || (step != -1 && step != 1))
                return (false, null, "invalid_step");
            _irsdk.ReplaySetPlayPosition(IRacingSdkEnum.RpyPosMode.Current, step);
            _logger?.Structured("INFO", "simhub-plugin", "replay_control", $"Replay step frame: {step:+0;-0}", new Dictionary<string, object> { ["mode"] = "step", ["step"] = step }, "replay", GetIncidentIdForSpine());
            return (true, "ok", null);
        }

        private (bool success, string result, string error) HandleReplaySeekFrame(string arg)
        {
            if (!EnsureIrsdkConnected(out var error))
                return (false, null, error);
            if (!int.TryParse(arg ?? string.Empty, out var frame))
                return (false, null, "invalid_frame");
            if (frame < 0 || (_replayFrameNumEnd > 0 && frame > _replayFrameNumEnd))
                return (false, null, "frame_out_of_range");
            _irsdk.ReplaySetPlayPosition(IRacingSdkEnum.RpyPosMode.Begin, frame);
            _logger?.Structured("INFO", "simhub-plugin", "replay_control", $"Replay seek to frame {frame}", new Dictionary<string, object> { ["mode"] = "seek_frame", ["frame"] = frame }, "replay", GetIncidentIdForSpine());
            return (true, "ok", null);
        }

        private (bool success, string result, string error) HandleSelectIncidentAndSeek(string arg)
        {
            if (!EnsureIrsdkConnected(out var error))
                return (false, null, error);
            if (string.IsNullOrEmpty(arg))
                return (false, null, "missing_incident_id");
            var incidents = _tracker.GetIncidentFeed();
            IncidentEvent target = null;
            foreach (var ev in incidents)
            {
                if (string.Equals(ev.Id, arg, StringComparison.Ordinal))
                {
                    target = ev;
                    break;
                }
            }
            if (target == null)
                return (false, null, "incident_not_found");
            int safeSession = _replaySessionNum >= 0 ? _replaySessionNum : 0;
            int safeTimeMs  = (int)Math.Min(target.SessionTime * 1000.0, int.MaxValue);
            _logger?.Structured("INFO", "simhub-plugin", "replay_control", $"Seeking to incident {arg} at {target.SessionTimeFormatted}", new Dictionary<string, object> { ["mode"] = "select_incident", ["session_num"] = safeSession, ["session_time_ms"] = safeTimeMs }, "replay", GetIncidentIdForSpine());
            _irsdk.ReplaySearchSessionTime(safeSession, safeTimeMs);
            return (true, "ok", null);
        }

        private (bool success, string result, string error) HandleReplaySeekSessionStart(string arg)
        {
            if (!EnsureIrsdkConnected(out var error))
                return (false, null, error);
            int sessionNum = _replaySessionNum >= 0 ? _replaySessionNum : 0;
            if (!string.IsNullOrWhiteSpace(arg) && int.TryParse(arg.Trim(), out int parsed))
                sessionNum = parsed;
            _logger?.Structured("INFO", "simhub-plugin", "replay_control", "Replay seek to session start", new Dictionary<string, object> { ["mode"] = "seek_session_start", ["session_num"] = sessionNum }, "replay", GetIncidentIdForSpine());
            _irsdk.ReplaySearchSessionTime(sessionNum, 0);
            return (true, "ok", null);
        }

        private (bool success, string result, string error) HandleStartReplayScan(string arg)
        {
            if (!EnsureIrsdkConnected(out var error))
                return (false, null, error);

            int sessionNum = _replaySessionNum >= 0 ? _replaySessionNum : 0;
            if (!string.IsNullOrWhiteSpace(arg) && int.TryParse(arg.Trim(), out int parsed))
                sessionNum = parsed;

            bool started = _tracker.StartReplayScan(_irsdk, sessionNum);
            if (!started)
                return (false, null, "scan_already_running_or_failed");

            _logger?.Structured("INFO", "simhub-plugin", "replay_scan_started",
                $"Replay incident scan started for session {sessionNum}",
                new Dictionary<string, object> { ["session_num"] = sessionNum },
                "replay_scan", GetIncidentIdForSpine());
            return (true, "scan_started", null);
        }

        private (bool success, string result, string error) HandleStopReplayScan()
        {
            _tracker.StopReplayScan();
            _logger?.Structured("INFO", "simhub-plugin", "replay_scan_stopped", "Replay scan stopped.",
                null, "replay_scan", GetIncidentIdForSpine());
            return (true, "scan_stopped", null);
        }

        private (bool success, string result, string error) HandleGetReplayScanProgress()
        {
            var progress = _tracker.ScanProgress;
            var json = JsonConvert.SerializeObject(progress);
            return (true, json, null);
        }

        private bool EnsureIrsdkConnected(out string error)
        {
            if (_irsdk == null)
            {
                error = "irsdk_not_started";
                return false;
            }
            if (!_irsdk.IsConnected)
            {
                error = "irsdk_not_connected";
                return false;
            }
            error = null;
            return true;
        }

        private int SafeGetInt(string name)
        {
            try
            {
                return _irsdk.Data.GetInt(name);
            }
            catch
            {
                return 0;
            }
        }

        private bool SafeGetBool(string name)
        {
            try
            {
                return _irsdk.Data.GetBool(name);
            }
            catch
            {
                return false;
            }
        }

        private (bool success, string result, string error) TryCaptureAndEmitSessionSummary(string trigger, bool logNotReady)
        {
            var status = new SessionSummaryCaptureStatus
            {
                AttemptedAtUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                Trigger = trigger ?? "unknown"
            };

            if (!EnsureIrsdkConnected(out var error))
            {
                status.Success = false;
                status.Error = error;
                status.Details = "IRSDKSharper is not connected.";
                _lastSummaryCapture = status;
                if (logNotReady) _logger?.Warn($"Session summary capture skipped ({status.Trigger}): {error}");
                return (false, null, error);
            }

            if (_bridge == null)
            {
                status.Success = false;
                status.Error = "ws_not_running";
                status.Details = "WebSocket bridge is not running.";
                _lastSummaryCapture = status;
                if (logNotReady) _logger?.Warn($"Session summary capture skipped ({status.Trigger}): ws_not_running");
                return (false, null, "ws_not_running");
            }

            var si = _irsdk.Data.SessionInfo;
            if (si == null)
            {
                status.Success = false;
                status.Error = "sessioninfo_null";
                status.Details = "SessionInfo YAML is null (not yet available).";
                _lastSummaryCapture = status;
                if (logNotReady) _logger?.Warn($"Session summary capture skipped ({status.Trigger}): sessioninfo_null");
                return (false, null, "sessioninfo_null");
            }

            var w = si.WeekendInfo;
            var sessions = si.SessionInfo?.Sessions;
            int wantedSessionNum = _replaySessionNum >= 0 ? _replaySessionNum : SafeGetInt("SessionNum");

            var session =
                sessions?.FirstOrDefault(s => s.SessionNum == wantedSessionNum && s.ResultsPositions != null && s.ResultsPositions.Count > 0)
                ?? sessions?.FirstOrDefault(s => s.SessionNum == wantedSessionNum)
                ?? sessions?.LastOrDefault(s => s.ResultsPositions != null && s.ResultsPositions.Count > 0);

            var positions = session?.ResultsPositions;
            if (positions == null || positions.Count == 0)
            {
                status.Success = false;
                status.Error = "results_not_ready";
                status.Details = $"ResultsPositions empty (wantedSessionNum={wantedSessionNum}, selectedSessionNum={(session?.SessionNum ?? -1)}, simMode={w?.SimMode ?? _pluginMode ?? "Unknown"}).";
                _lastSummaryCapture = status;
                if (logNotReady) _logger?.Warn($"Session summary capture skipped ({status.Trigger}): {status.Details}");
                _logger?.Structured("INFO", "simhub-plugin", "session_capture_skipped", $"Session capture skipped ({trigger}): results not ready.", new Dictionary<string, object>
                {
                    ["trigger"] = trigger,
                    ["error"] = "results_not_ready",
                    ["details"] = status.Details,
                    ["will_retry"] = string.Equals(trigger, "checkered", StringComparison.OrdinalIgnoreCase)
                }, "session", GetIncidentIdForSpine());
                // At checkered, iRacing may populate ResultsPositions shortly after; schedule one retry at 2s
                if (string.Equals(trigger, "checkered", StringComparison.OrdinalIgnoreCase))
                    _checkeredRetryAfter = DateTime.UtcNow.AddSeconds(2);
                return (false, null, "results_not_ready");
            }
            _checkeredRetryAfter = null; // success or non-checkered; clear any pending retry

            var opts = w?.WeekendOptions;
            var telemetryAtCapture = new TelemetryAtCapture
            {
                SessionState = SafeGetInt("SessionState"),
                SessionNum = SafeGetInt("SessionNum"),
                SessionInfoUpdate = SafeGetInt("SessionInfoUpdate"),
                SessionFlags = SafeGetInt("SessionFlags"),
                SessionTime = _lastSessionTime,
                ReplayFrameNum = _replayFrameNum,
                ReplayFrameNumEnd = _replayFrameNumEnd,
                ReplayPlaySpeed = _replayPlaySpeed,
                ReplaySessionNum = _replaySessionNum
            };

            var summary = new SessionSummary
            {
                SessionId = GetCurrentSessionId(),
                SubSessionID = w?.SubSessionID ?? 0,
                SessionID = w?.SessionID ?? 0,
                SeriesID = w?.SeriesID ?? 0,
                SeasonID = w?.SeasonID ?? 0,
                LeagueID = w?.LeagueID ?? 0,
                SessionNum = session?.SessionNum ?? wantedSessionNum,
                SessionType = session?.SessionType ?? "",
                EventType = w?.EventType ?? "",
                TrackName = w?.TrackName ?? _tracker.TrackName ?? "",
                TrackID = w?.TrackID ?? 0,
                TrackConfigName = w?.TrackConfigName ?? "",
                TrackCity = w?.TrackCity ?? "",
                TrackCountry = w?.TrackCountry ?? "",
                Category = w?.Category ?? _tracker.TrackCategory ?? "Road",
                NumCautionFlags = session?.ResultsNumCautionFlags ?? 0,
                NumCautionLaps = session?.ResultsNumCautionLaps ?? 0,
                NumLeadChanges = session?.ResultsNumLeadChanges ?? 0,
                TotalLapsComplete = session?.ResultsLapsComplete ?? 0,
                AverageLapTime = (float)(session?.ResultsAverageLapTime ?? 0),
                IsOfficial = (session?.ResultsOfficial ?? 0) == 1,
                SimMode = w?.SimMode ?? _pluginMode ?? "Unknown",
                CapturedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                SessionTimeSec = _lastSessionTime,
                SessionName = session?.SessionName ?? "",
                SessionLaps = session?.SessionLaps ?? "",
                SessionTimeStr = session?.SessionTime ?? "",
                TrackLength = w?.TrackLength ?? "",
                IncidentLimit = opts?.IncidentLimit ?? "",
                FastRepairsLimit = opts?.FastRepairsLimit ?? "",
                GreenWhiteCheckeredLimit = opts?.GreenWhiteCheckeredLimit ?? "",
                TelemetryAtCapture = telemetryAtCapture,
                Results = new List<DriverResult>(),
                IncidentFeed = _tracker.GetIncidentFeed()
            };

            var driverList = si.DriverInfo?.Drivers;
            foreach (var pos in positions)
            {
                if (pos.CarIdx < 0) continue;
                string driverName = "Unknown";
                string carNumber = "?";
                string carClass = "";
                string abbrevName = "";
                int userID = 0, iRating = 0, curDriverIncidentCount = 0, teamIncidentCount = 0;
                string teamName = "";
                if (driverList != null)
                {
                    var dr = driverList.FirstOrDefault(d => d.CarIdx == pos.CarIdx);
                    if (dr != null)
                    {
                        driverName = dr.UserName ?? driverName;
                        carNumber = dr.CarNumber ?? carNumber;
                        carClass = dr.CarClassShortName ?? "";
                        abbrevName = dr.AbbrevName ?? "";
                        userID = dr.UserID;
                        teamName = dr.TeamName ?? "";
                        iRating = dr.IRating;
                        curDriverIncidentCount = dr.CurDriverIncidentCount;
                        teamIncidentCount = dr.TeamIncidentCount;
                    }
                }
                // pos.* from IRSDKSharper SessionInfo.Sessions[].ResultsPositions (iRacing YAML).
                summary.Results.Add(new DriverResult
                {
                    CarIdx = pos.CarIdx,
                    DriverName = driverName,
                    CarNumber = carNumber,
                    CarClass = carClass,
                    AbbrevName = abbrevName,
                    UserID = userID,
                    TeamName = teamName,
                    IRating = iRating,
                    CurDriverIncidentCount = curDriverIncidentCount,
                    TeamIncidentCount = teamIncidentCount,
                    Position = pos.Position,
                    ClassPosition = pos.ClassPosition,
                    LapsComplete = pos.LapsComplete,
                    LapsLed = pos.LapsLed,
                    FastestTime = pos.FastestTime,
                    FastestLap = pos.FastestLap,
                    LastTime = pos.LastTime,
                    Incidents = pos.Incidents,
                    ReasonOut = pos.ReasonOutStr ?? "Running",
                    ReasonOutId = pos.ReasonOutId,
                    Lap = pos.Lap,
                    Time = pos.Time,
                    JokerLapsComplete = pos.JokerLapsComplete,
                    LapsDriven = pos.LapsDriven
                });
            }

            // Cross-check: player's Results row incidents vs tracker (surfaces wrong-session or SDK mapping issues)
            int playerCarIdx = _tracker.PlayerCarIdx;
            if (playerCarIdx >= 0)
            {
                var playerResult = summary.Results.FirstOrDefault(r => r.CarIdx == playerCarIdx);
                if (playerResult != null)
                {
                    int trackerCount = _tracker.PlayerIncidentCount;
                    if (playerResult.Incidents != trackerCount)
                        _logger?.Structured("WARN", "simhub-plugin", "session_capture_incident_mismatch", $"Player incident count mismatch: ResultsPositions={playerResult.Incidents}, tracker={trackerCount}", new Dictionary<string, object> { ["results_incidents"] = playerResult.Incidents, ["tracker_incidents"] = trackerCount, ["player_car_idx"] = playerCarIdx }, "session", GetIncidentIdForSpine());
                }
            }

            try
            {
                var summaryForBroadcast = new SessionSummary
                {
                    SessionId = summary.SessionId,
                    SubSessionID = summary.SubSessionID,
                    SessionID = summary.SessionID,
                    SeriesID = summary.SeriesID,
                    SeasonID = summary.SeasonID,
                    LeagueID = summary.LeagueID,
                    SessionNum = summary.SessionNum,
                    SessionType = summary.SessionType,
                    EventType = summary.EventType,
                    TrackName = summary.TrackName,
                    TrackID = summary.TrackID,
                    TrackConfigName = summary.TrackConfigName,
                    TrackCity = summary.TrackCity,
                    TrackCountry = summary.TrackCountry,
                    Category = summary.Category,
                    NumCautionFlags = summary.NumCautionFlags,
                    NumCautionLaps = summary.NumCautionLaps,
                    NumLeadChanges = summary.NumLeadChanges,
                    TotalLapsComplete = summary.TotalLapsComplete,
                    AverageLapTime = summary.AverageLapTime,
                    IsOfficial = summary.IsOfficial,
                    SimMode = summary.SimMode,
                    CapturedAt = summary.CapturedAt,
                    SessionTimeSec = summary.SessionTimeSec,
                    SessionName = summary.SessionName,
                    SessionLaps = summary.SessionLaps,
                    SessionTimeStr = summary.SessionTimeStr,
                    TrackLength = summary.TrackLength,
                    IncidentLimit = summary.IncidentLimit,
                    FastRepairsLimit = summary.FastRepairsLimit,
                    GreenWhiteCheckeredLimit = summary.GreenWhiteCheckeredLimit,
                    TelemetryAtCapture = summary.TelemetryAtCapture,
                    Results = summary.Results,
                    IncidentFeed = summary.IncidentFeed
                };

                var msg = new { type = "sessionComplete", summary = summaryForBroadcast };
                _bridge.Broadcast(JsonConvert.SerializeObject(msg), "sessionComplete");

                int resultsIncidentSum = summary.Results?.Sum(r => r.Incidents) ?? 0;
                int incidentCount = resultsIncidentSum > 0 ? resultsIncidentSum : (summary.IncidentFeed?.Count ?? 0);
                string bannerMessage = $"Session complete · {incidentCount} incident{(incidentCount != 1 ? "s" : "")} captured · Results available";
                _logger?.Structured("INFO", "simhub-plugin", "session_complete_broadcast", bannerMessage, new Dictionary<string, object>
                {
                    ["trigger"] = status.Trigger,
                    ["incident_count"] = incidentCount,
                    ["results_count"] = summary.Results?.Count ?? 0,
                    ["session_num"] = summary.SessionNum
                }, "session", GetIncidentIdForSpine());

                status.Success = true;
                status.Error = null;
                status.Details = $"Captured ResultsPositions={positions.Count} (sessionNum={summary.SessionNum}, simMode={summary.SimMode}).";
                _lastSummaryCapture = status;
                PostSessionSummaryToDataApiFireAndForget(summary);
                bool sessionMatchExact = session != null && session.SessionNum == wantedSessionNum;
                const int incidentSampleSize = 3;
                var resultsIncidentSample = summary.Results.Take(incidentSampleSize).Select(r => new Dictionary<string, object> { ["car_idx"] = r.CarIdx, ["position"] = r.Position, ["incidents"] = r.Incidents }).ToList();
                _logger?.Structured("INFO", "simhub-plugin", "session_summary_captured", $"Session summary captured ({status.Trigger})", new Dictionary<string, object>
                {
                    ["trigger"] = status.Trigger,
                    ["session_num"] = summary.SessionNum,
                    ["driver_count"] = positions?.Count ?? 0,
                    ["wanted_session_num"] = wantedSessionNum,
                    ["selected_session_num"] = session?.SessionNum ?? -1,
                    ["session_match_exact"] = sessionMatchExact,
                    ["results_incident_sample"] = resultsIncidentSample
                }, "session", GetIncidentIdForSpine());

                if (!_sessionDigestEmitted)
                {
                    var incidents = _sessionStats.Incidents;
                    const int maxIncidents = 20;
                    var incidentSummary = incidents.Take(maxIncidents).Select(i => new { type = i.Type, driver = i.Driver, car = i.Car, lap = i.Lap, session_time = i.SessionTime }).ToList();
                    // Authoritative session-end results from iRacing ResultsPositions (incident counts per driver). total_incidents = event count; results_table[].incidents = per-driver points.
                    const int maxResultsForDigest = 64;
                    var resultsTable = summary.Results.Take(maxResultsForDigest).Select(r => new Dictionary<string, object>
                    {
                        ["pos"] = r.Position,
                        ["car"] = r.CarNumber ?? "?",
                        ["driver"] = r.DriverName ?? "Unknown",
                        ["incidents"] = r.Incidents,
                        ["laps"] = r.LapsComplete,
                        ["class"] = r.CarClass ?? "",
                        ["reason_out"] = r.ReasonOut ?? "Running"
                    }).ToList();
                    int digestResultsIncidentSum = summary.Results.Sum(r => r.Incidents);
                    var digestFields = new Dictionary<string, object>
                    {
                        ["session_id"] = summary.SessionId,
                        ["session_num"] = summary.SessionNum,
                        ["track"] = summary.TrackName,
                        ["duration_minutes"] = (int)(summary.SessionTimeSec / 60.0),
                        ["total_incidents"] = incidents.Count,
                        ["results_incident_sum"] = digestResultsIncidentSum,
                        ["incident_summary"] = incidentSummary,
                        ["incident_summary_truncated"] = incidents.Count > maxIncidents,
                        ["results_table"] = resultsTable,
                        ["results_driver_count"] = summary.Results.Count,
                        ["actions_dispatched"] = _sessionStats.ActionsDispatched,
                        ["action_failures"] = _sessionStats.ActionFailures,
                        ["failed_actions"] = _sessionStats.FailedActions.ToList(),
                        ["p50_action_latency_ms"] = (long)_sessionStats.P50LatencyMs,
                        ["p95_action_latency_ms"] = (long)_sessionStats.P95LatencyMs,
                        ["ws_peak_clients"] = _sessionStats.WsPeakClients,
                        ["plugin_errors"] = _sessionStats.PluginErrors,
                        ["plugin_warns"] = _sessionStats.PluginWarns
                    };
                    _logger?.Structured("INFO", "simhub-plugin", "session_digest", "Session digest", digestFields, "session", GetIncidentIdForSpine());
                    _sessionDigestEmitted = true;
                }

                // Log end-of-session as session metadata + chunked results (scale to hundreds of drivers); each line under 8 KB.
                // ~220 bytes per driver row; 35 * 220 + overhead keeps under 8 KB.
                const int driversPerChunk = 35;
                int totalDrivers = summary.Results.Count;

                static Dictionary<string, object> DriverResultToLogFields(DriverResult r)
                {
                    return new Dictionary<string, object>
                    {
                        ["pos"] = r.Position,
                        ["car_idx"] = r.CarIdx,
                        ["driver"] = r.DriverName ?? "",
                        ["abbrev"] = r.AbbrevName ?? "",
                        ["car"] = r.CarNumber ?? "",
                        ["class"] = r.CarClass ?? "",
                        ["class_pos"] = r.ClassPosition,
                        ["laps"] = r.LapsComplete,
                        ["laps_led"] = r.LapsLed,
                        ["fastest_time"] = r.FastestTime,
                        ["fastest_lap"] = r.FastestLap,
                        ["last_time"] = r.LastTime,
                        ["incidents"] = r.Incidents,
                        ["reason_out"] = r.ReasonOut ?? "",
                        ["reason_out_id"] = r.ReasonOutId,
                        ["lap"] = r.Lap,
                        ["time"] = r.Time,
                        ["joker_laps"] = r.JokerLapsComplete,
                        ["laps_driven"] = r.LapsDriven,
                        ["user_id"] = r.UserID,
                        ["team"] = r.TeamName ?? "",
                        ["irating"] = r.IRating,
                        ["cur_incidents"] = r.CurDriverIncidentCount,
                        ["team_incidents"] = r.TeamIncidentCount
                    };
                }

                // 1) session_end_datapoints_session: metadata + telemetry only (no results array).
                var sessionFields = new Dictionary<string, object>
                {
                    ["trigger"] = status.Trigger,
                    ["session_id"] = summary.SessionId,
                    ["session_num"] = summary.SessionNum,
                    ["sub_session_id"] = summary.SubSessionID,
                    ["session_id_ir"] = summary.SessionID,
                    ["series_id"] = summary.SeriesID,
                    ["season_id"] = summary.SeasonID,
                    ["track"] = summary.TrackName,
                    ["track_length"] = summary.TrackLength ?? "",
                    ["session_name"] = summary.SessionName ?? "",
                    ["session_laps"] = summary.SessionLaps ?? "",
                    ["session_time_str"] = summary.SessionTimeStr ?? "",
                    ["incident_limit"] = summary.IncidentLimit ?? "",
                    ["fast_repairs_limit"] = summary.FastRepairsLimit ?? "",
                    ["green_white_checkered_limit"] = summary.GreenWhiteCheckeredLimit ?? "",
                    ["num_caution_flags"] = summary.NumCautionFlags,
                    ["num_caution_laps"] = summary.NumCautionLaps,
                    ["num_lead_changes"] = summary.NumLeadChanges,
                    ["total_laps_complete"] = summary.TotalLapsComplete,
                    ["average_lap_time"] = summary.AverageLapTime,
                    ["is_official"] = summary.IsOfficial,
                    ["sim_mode"] = summary.SimMode,
                    ["captured_at"] = summary.CapturedAt,
                    ["session_time_sec"] = summary.SessionTimeSec,
                    ["telemetry_session_state"] = telemetryAtCapture.SessionState,
                    ["telemetry_session_num"] = telemetryAtCapture.SessionNum,
                    ["telemetry_session_info_update"] = telemetryAtCapture.SessionInfoUpdate,
                    ["telemetry_session_flags"] = telemetryAtCapture.SessionFlags,
                    ["telemetry_session_time"] = telemetryAtCapture.SessionTime,
                    ["telemetry_replay_frame_num"] = telemetryAtCapture.ReplayFrameNum,
                    ["telemetry_replay_frame_num_end"] = telemetryAtCapture.ReplayFrameNumEnd,
                    ["telemetry_replay_play_speed"] = telemetryAtCapture.ReplayPlaySpeed,
                    ["telemetry_replay_session_num"] = telemetryAtCapture.ReplaySessionNum,
                    ["results_driver_count"] = totalDrivers
                };
                _logger?.Structured("INFO", "simhub-plugin", "session_end_datapoints_session", "End-of-session metadata and telemetry.", sessionFields, "session", GetIncidentIdForSpine());

                // 2) session_end_datapoints_results: one log line per chunk (up to driversPerChunk drivers); chunk_index 0-based, chunk_total = number of chunks.
                int chunkTotal = totalDrivers == 0 ? 0 : (int)Math.Ceiling((double)totalDrivers / driversPerChunk);
                for (int chunkIndex = 0; chunkIndex < chunkTotal; chunkIndex++)
                {
                    var chunkResults = summary.Results
                        .Skip(chunkIndex * driversPerChunk)
                        .Take(driversPerChunk)
                        .Select(DriverResultToLogFields)
                        .ToList();
                    var resultFields = new Dictionary<string, object>
                    {
                        ["session_id"] = summary.SessionId,
                        ["session_num"] = summary.SessionNum,
                        ["chunk_index"] = chunkIndex,
                        ["chunk_total"] = chunkTotal,
                        ["results_driver_count"] = totalDrivers,
                        ["results"] = chunkResults
                    };
                    _logger?.Structured("INFO", "simhub-plugin", "session_end_datapoints_results", "End-of-session results chunk.", resultFields, "session", GetIncidentIdForSpine());
                }

                return (true, "ok", null);
            }
            catch (Exception ex)
            {
                status.Success = false;
                status.Error = "broadcast_failed";
                status.Details = ex.Message;
                _lastSummaryCapture = status;
                _logger?.Warn($"Session complete broadcast failed ({status.Trigger}): {ex.Message}");
                return (false, null, "broadcast_failed");
            }
        }

        private (bool success, string result, string error) StartFinalizeThenCaptureJob()
        {
            if (!EnsureIrsdkConnected(out var error))
                return (false, null, error);

            var simMode = _irsdk.Data.SessionInfo?.WeekendInfo?.SimMode;
            bool isReplay = string.Equals(simMode, "replay", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(_pluginMode, "Replay", StringComparison.OrdinalIgnoreCase);
            if (!isReplay)
                return (false, null, "not_in_replay");

            if (_finalizeThenCaptureJob != null && !_finalizeThenCaptureJob.IsDone)
                return (false, null, "capture_busy");

            _finalizeThenCaptureJob = new FinalizeThenCaptureJob(
                originalFrame: _replayFrameNum,
                originalSpeed: _replayPlaySpeed,
                originalSlowMotion: _replayPlaySlowMotion,
                startSessionInfoUpdate: SafeGetInt("SessionInfoUpdate"),
                startedAtUtc: DateTime.UtcNow);

                _logger?.Structured("INFO", "simhub-plugin", "finalize_capture_started", "FinalizeThenCapture started", new Dictionary<string, object> { ["target_frame"] = _replayFrameNum, ["end_frame"] = _replayFrameNumEnd }, "session", GetIncidentIdForSpine());
            return (true, "started", null);
        }

#if SIMHUB_SDK
        /// <summary>Build replay metadata (WeekendInfo, driverRoster, sessions, incidentFeed) for snapshot when SessionInfo is available. Null otherwise.</summary>
        private object BuildReplayMetadata()
        {
            var si = _irsdk?.Data?.SessionInfo;
            if (si == null) return null;
            var w = si.WeekendInfo;
            int sessionID = w?.SessionID ?? 0;
            int subSessionID = w?.SubSessionID ?? 0;
            string trackDisplayName = w?.TrackName ?? _tracker?.TrackName ?? "";
            string category = w?.Category ?? _tracker?.TrackCategory ?? "Road";
            string simMode = w?.SimMode ?? _pluginMode ?? "Unknown";
            var drivers = si.DriverInfo?.Drivers;
            var driverRoster = drivers?.Select(d => new { carIdx = d.CarIdx, userName = d.UserName, carNumber = d.CarNumber, abbrevName = d.AbbrevName, curDriverIncidentCount = d.CurDriverIncidentCount, isSpectator = d.IsSpectator }).ToList();
            var sessionsList = si.SessionInfo?.Sessions?.Select(s => new { sessionNum = s.SessionNum, sessionType = s.SessionType ?? "", sessionName = s.SessionName ?? "" }).ToList();
            var feed = _tracker.GetIncidentFeed();
            const int maxIncidentFeedEntries = 30;
            var incidentFeed = feed != null ? feed.Take(maxIncidentFeedEntries).ToList() : new List<IncidentEvent>();
            return new { sessionID, subSessionID, trackDisplayName, category, simMode, driverRoster, sessions = sessionsList, incidentFeed };
        }
#endif

        private (bool success, string result, string error) RecordSessionSnapshot(string trigger)
        {
            if (!EnsureIrsdkConnected(out var error))
                return (false, null, error);

            try
            {
                if (!string.IsNullOrEmpty(_pluginDataPath) && !Directory.Exists(_pluginDataPath))
                    Directory.CreateDirectory(_pluginDataPath);
                if (!string.IsNullOrEmpty(_webApiPath) && !Directory.Exists(_webApiPath))
                    Directory.CreateDirectory(_webApiPath);

#if SIMHUB_SDK
                object replayMetadata = BuildReplayMetadata();
#else
                object replayMetadata = null;
#endif
                var payload = new
                {
                    type = "sessionSnapshot",
                    capturedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                    trigger = trigger ?? "unknown",
                    pluginMode = _pluginMode,
                    playerCarIdx = _tracker.PlayerCarIdx,
                    replayFrameNum = _replayFrameNum,
                    replayFrameNumEnd = _replayFrameNumEnd,
                    replayPlaySpeed = _replayPlaySpeed,
                    replaySessionNum = _replaySessionNum,
                    sessionDiagnostics = BuildSessionDataDiagnostics(),
                    replayMetadata = replayMetadata
                };

                var path = System.IO.Path.Combine(_pluginDataPath ?? ".", "session-discovery.jsonl");
                File.AppendAllText(path, JsonConvert.SerializeObject(payload) + Environment.NewLine);
                _logger?.Structured("INFO", "simhub-plugin", "session_snapshot_recorded", $"Session snapshot recorded: {path}", new Dictionary<string, object> { ["path"] = path }, "session", GetIncidentIdForSpine());

                if (trigger != null && trigger.IndexOf("session_end", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    int fingerprintSessionNum = _replaySessionNum >= 0 ? _replaySessionNum : SafeGetInt("SessionNum");
                    var parts = trigger.Split(':');
                    for (int i = 0; i < parts.Length; i++)
                        if (string.Equals(parts[i], "session_end", StringComparison.OrdinalIgnoreCase) && i + 1 < parts.Length && int.TryParse(parts[i + 1].Trim(), out int parsed))
                        { fingerprintSessionNum = parsed; break; }
                    var diag = BuildSessionDataDiagnostics();
                    _logger?.Structured("INFO", "simhub-plugin", "session_end_fingerprint", "Session end fingerprint", new Dictionary<string, object>
                    {
                        ["session_num"] = fingerprintSessionNum,
                        ["results_ready"] = diag.ResultsReady,
                        ["results_positions_count"] = diag.ResultsPositionsCount,
                        ["replay_frame_num"] = _replayFrameNum,
                        ["session_time"] = _lastSessionTime
                    }, "session", GetIncidentIdForSpine());
                }
                return (true, "ok", null);
            }
            catch (Exception ex)
            {
                _logger?.Warn($"Session snapshot record failed: {ex.Message}");
                return (false, null, "snapshot_write_failed");
            }
        }

        private void MaybeAutoRecordSessionSnapshot(SessionDataDiagnostics sd)
        {
            if (!_autoSessionSnapshotsEnabled) return;
            if (sd == null) return;
            if (string.IsNullOrEmpty(_pluginDataPath)) return;

            var now = DateTime.UtcNow;
            if ((now - _lastAutoSnapshotAt).TotalMilliseconds < 500)
                return;

            bool changed = false;
            string reason = "";

            if (sd.SessionInfoUpdate != _lastAutoSnapshotSiu)
            {
                changed = true;
                reason = "siu";
            }
            else if (sd.SessionState != _lastAutoSnapshotSessionState)
            {
                changed = true;
                reason = "sessionState";
            }
            else if (sd.ResultsReady != _lastAutoSnapshotResultsReady)
            {
                changed = true;
                reason = "resultsReady";
            }

            if (!changed) return;

            _lastAutoSnapshotAt = now;
            _lastAutoSnapshotSiu = sd.SessionInfoUpdate;
            _lastAutoSnapshotSessionState = sd.SessionState;
            _lastAutoSnapshotResultsReady = sd.ResultsReady;

            try
            {
                if (!Directory.Exists(_pluginDataPath))
                    Directory.CreateDirectory(_pluginDataPath);

                var payload = new
                {
                    type = "sessionSnapshot",
                    capturedAt = now.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                    trigger = "auto:" + reason,
                    pluginMode = _pluginMode,
                    replayFrameNum = _replayFrameNum,
                    replayFrameNumEnd = _replayFrameNumEnd,
                    replayPlaySpeed = _replayPlaySpeed,
                    replaySessionNum = _replaySessionNum,
                    sessionDiagnostics = sd
                };

                var path = System.IO.Path.Combine(_pluginDataPath, "session-discovery.jsonl");
                File.AppendAllText(path, JsonConvert.SerializeObject(payload) + Environment.NewLine);
            }
            catch
            {
                // Never disrupt DataUpdate for discovery snapshots.
            }
        }

        private sealed class FinalizeThenCaptureJob
        {
            public bool IsDone { get; private set; }
            public int StartSessionInfoUpdate { get; }

            private readonly int _originalFrame;
            private readonly int _originalSpeed;
            private readonly bool _originalSlowMotion;
            private readonly DateTime _startedAtUtc;
            private int _phase; // 0=seekEnd,1=settle,2=waitAndCapture,3=restore,4=done
            private int _ticks;
            private int _lastAttemptSiu = -1;
            private int _settleTicks;

            public FinalizeThenCaptureJob(int originalFrame, int originalSpeed, bool originalSlowMotion, int startSessionInfoUpdate, DateTime startedAtUtc)
            {
                _originalFrame = originalFrame;
                _originalSpeed = originalSpeed;
                _originalSlowMotion = originalSlowMotion;
                StartSessionInfoUpdate = startSessionInfoUpdate;
                _startedAtUtc = startedAtUtc;
                _lastAttemptSiu = startSessionInfoUpdate;
            }

            public void Tick(SimStewardPlugin plugin)
            {
                if (IsDone) return;
                _ticks++;

                // ~15s safety timeout at 60Hz
                if (_ticks > 900)
                {
                    var elapsedSec = (DateTime.UtcNow - _startedAtUtc).TotalSeconds;
                    plugin._lastSummaryCapture = new SessionSummaryCaptureStatus
                    {
                        AttemptedAtUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                        Trigger = "finalizeThenCapture",
                        Success = false,
                        Error = "timeout",
                        Details = $"Timed out waiting for ResultsPositions to become available ({elapsedSec:F1}s)."
                    };
                    plugin._logger?.Structured("WARN", "simhub-plugin", "finalize_capture_timeout", "FinalizeThenCapture timed out waiting for results readiness.", null, "session", plugin.GetIncidentIdForSpine());
                    IsDone = true;
                    return;
                }

                if (_phase == 0)
                {
                    // Pause and jump to end to trigger final-session YAML population, if iRacing requires it.
                    plugin._irsdk.ReplaySetPlaySpeed(0, plugin._replayPlaySlowMotion);
                    plugin._replayPlaySpeed = 0;
                    plugin._replayIsPlaying = false;

                    int endFrame = Math.Max(plugin._replayFrameNumEnd, plugin._replayFrameNum);
                    plugin._irsdk.ReplaySetPlayPosition(IRacingSdkEnum.RpyPosMode.Begin, endFrame);
                    plugin._logger?.Structured("INFO", "simhub-plugin", "finalize_capture_started", "FinalizeThenCapture seek to final frame",
                        new Dictionary<string, object> { ["target_frame"] = endFrame }, "session", plugin.GetIncidentIdForSpine());
                    _settleTicks = 0;
                    _phase = 1;
                    return;
                }

                if (_phase == 1)
                {
                    // Let the seek settle for a few ticks.
                    _settleTicks++;
                    if (_settleTicks < 10) return;
                    _phase = 2;
                    return;
                }

                if (_phase == 2)
                {
                    int curSiu = plugin.SafeGetInt("SessionInfoUpdate");
                    bool shouldAttempt = (curSiu != _lastAttemptSiu) || (_ticks % 30 == 0); // attempt on SIU change or ~2x/sec
                    if (!shouldAttempt) return;

                    _lastAttemptSiu = curSiu;
                    var capture = plugin.TryCaptureAndEmitSessionSummary("finalizeThenCapture", logNotReady: false);
                    if (capture.success)
                    {
                        _phase = 3;
                        return;
                    }
                    return;
                }

                if (_phase == 3)
                {
                    // Restore user's prior position and speed.
                    if (_originalFrame > 0)
                    {
                        plugin._irsdk.ReplaySetPlayPosition(IRacingSdkEnum.RpyPosMode.Begin, _originalFrame);
                    }
                    plugin._irsdk.ReplaySetPlaySpeed(_originalSpeed, _originalSlowMotion);
                    plugin._replayPlaySpeed = _originalSpeed;
                    plugin._replayPlaySlowMotion = _originalSlowMotion;
                    plugin._replayIsPlaying = _originalSpeed != 0;
                    var durationMs = (long)(DateTime.UtcNow - _startedAtUtc).TotalMilliseconds;
                    plugin._logger?.Structured("INFO", "simhub-plugin", "finalize_capture_complete", "FinalizeThenCapture complete", new Dictionary<string, object> { ["duration_ms"] = durationMs }, "session", plugin.GetIncidentIdForSpine());
                    _phase = 4;
                    return;
                }

                IsDone = true;
            }
        }
#endif

        private void OnLog(string level, string message, string source)
        {
            var prefix = "[OVERLAY]";
            if (!string.IsNullOrEmpty(source)) prefix += " [" + source + "]";
            var line = prefix + " " + message;
            if (string.Equals(level, "warn", StringComparison.OrdinalIgnoreCase))
                _logger?.Warn(line);
            else if (string.Equals(level, "error", StringComparison.OrdinalIgnoreCase))
                _logger?.Error(line);
            else
                _logger?.Info(line);
        }

        /// <summary>Called by bridge when dashboard sends a structured UI event (e.g. dashboard_ui_event).</summary>
        private void OnDashboardStructuredLog(string eventType, string message, Dictionary<string, object> fields)
        {
            if (string.IsNullOrEmpty(eventType) || fields == null) return;
            _logger?.Structured("INFO", "bridge", eventType, message, fields, "action", null);
        }

#if SIMHUB_SDK
        public void Init(PluginManager pluginManager)
        {
            PluginManager = pluginManager;
            _pluginDataPath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SimHubWpf", "PluginsData", "SimSteward");
            _webApiPath = System.IO.Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "Web", "sim-steward-dash", "api");
            _settingsPath = Path.Combine(_pluginDataPath, "ui-settings.json");
            _settings = LoadUiSettings();
            var omitList = _settings.OmitLogLevels;
            if (omitList == null || omitList.Count == 0)
                omitList = new List<string>(); // log everything by default; user can add omits in settings
            _omittedLogLevels = new HashSet<string>(omitList, StringComparer.OrdinalIgnoreCase);
            var omitEventsList = _settings.OmitEvents;
            if (omitEventsList == null)
                omitEventsList = new List<string>(); // log everything by default
            _omittedEvents = new HashSet<string>(omitEventsList, StringComparer.OrdinalIgnoreCase);
            // Never omit action_result, incident_detected, or dashboard_ui_event so button clicks and UI events always appear in logs
            _omittedEvents.Remove("action_result");
            _omittedEvents.Remove("incident_detected");
            _omittedEvents.Remove("dashboard_ui_event");

            // Prod only: limit frequency by omitting high-volume events (file + dashboard). Same pipeline as local; config reduces volume.
            var logEnv = Environment.GetEnvironmentVariable("SIMSTEWARD_LOG_ENV") ?? "production";
            if (string.Equals(logEnv, "production", StringComparison.OrdinalIgnoreCase))
            {
                _omittedEvents.Add("state_broadcast_summary");
                _omittedEvents.Add("tick_stats");
                _omittedEvents.Add("ws_message_raw");
                if (!GetLogAllActionTrafficEffective())
                {
                    _omittedEvents.Add("action_received");
                    _omittedEvents.Add("action_dispatched");
                }
            }

            _debugMode = Environment.GetEnvironmentVariable("SIMSTEWARD_LOG_DEBUG") == "1";
            _logger = new PluginLogger(_pluginDataPath, isDebugMode: _debugMode,
                getOmittedLevels: GetOmittedLevelsSnapshot,
                getOmittedEvents: GetOmittedEventsSnapshot);
#if SIMHUB_SDK
            _logger.SetSpineProvider(() => (_currentSessionId ?? "", _currentSessionSeq ?? "", _replayFrameNumEnd));
#endif
            _logger.Structured("INFO", "simhub-plugin", "logging_ready", "Logging pipeline ready; init continuing.", null, "lifecycle", null);

            SaveUiSettings();
            _logger.Structured("INFO", "simhub-plugin", "settings_saved", "UI settings persisted.", null, "lifecycle", null);

            var structuredPath = _logger.StructuredLogPath;
            _logger.Structured("INFO", "simhub-plugin", "file_tail_ready", "Structured log file ready for Alloy/Loki file-tail.",
                new Dictionary<string, object> { ["path"] = structuredPath ?? "(none)" }, "lifecycle", null);

            _tracker.LogStructured = entry =>
            {
                if (entry?.Event == "incident_detected")
                {
                    // #region agent log
                    AgentDebugLog.WriteB0C27E("H2", "SimStewardPlugin.LogStructured", "incident_callback_entered", new { incidentId = entry.IncidentId, loggerNull = _logger == null });
                    // #endregion
                    _lastIncidentId = entry.IncidentId ?? (entry.Fields != null && entry.Fields.TryGetValue("incident_id", out var o) ? o?.ToString() : null);
                    _lastIncidentAt = DateTime.UtcNow;
                    if (_logger?.IsDebugMode == true && entry.Fields != null)
                    {
                        try { entry.Fields["snapshot"] = BuildPluginSnapshot(); } catch { }
                    }
                }
                _logger?.Emit(entry);
                // #region agent log
                if (entry?.Event == "incident_detected")
                    AgentDebugLog.WriteB0C27E("H2", "SimStewardPlugin.LogStructured", "incident_emit_done", new { incidentId = entry.IncidentId, loggerWasNull = _logger == null, structuredLogPath = _logger?.StructuredLogPath ?? "(null)" });
                // #endregion
                if (entry?.Event == "incident_detected" && entry.Fields != null)
                {
                    var f = entry.Fields;
                    _sessionStats.AddIncident(new IncidentSummaryEntry
                    {
                        Type = GetString(f, "incident_type"),
                        Driver = GetString(f, "driver_name"),
                        Car = GetString(f, "car_number"),
                        Lap = GetIntFromFields(f, "lap"),
                        SessionTime = GetDoubleFromFields(f, "session_time")
                    });
                }
            };
            _tracker.OnIncidentPersist = ev =>
            {
                PersistIncident(ev);
                WriteWebApiIncidentFeed();
                if (ev != null && _logger != null)
                    _logger.Structured("INFO", "simhub-plugin", "incident_persisted", "Incident persisted to file.",
                        new Dictionary<string, object>
                        {
                            ["id"] = ev.Id ?? "",
                            ["session_time"] = ev.SessionTime,
                            ["car_number"] = ev.CarNumber ?? "?",
                            ["delta"] = ev.Delta,
                            ["type"] = ev.Type ?? "?",
                            ["source"] = ev.Source ?? "?"
                        }, "incident", ev?.Id);
            };
            _tracker.OnBaselineCaptured = () => { PersistSessionMeta(); WriteWebApiSession(); };
            _tracker.OnScanComplete = progress =>
            {
                PersistScanResult(progress);
                WriteWebApiSnapshotFiles(progress);
                WriteWebApiIncidentFeed(); // final feed after all scan incidents merged
            };
            _logger.Structured("INFO", "simhub-plugin", "plugin_started", "SimSteward plugin starting.", null, "lifecycle", null);

            pluginManager.AddProperty("SimSteward.PluginMode", GetType(), "Unknown");
            pluginManager.AddProperty("SimSteward.IncidentCount", GetType(), 0);
            pluginManager.AddProperty("SimSteward.HasLiveIncidentData", GetType(), false);
            pluginManager.AddProperty("SimSteward.ClientCount", GetType(), 0);

            pluginManager.AddAction("SimSteward.ToggleIntentionalCapture", GetType(), (pm, arg) => DispatchAction("ToggleIntentionalCapture", arg?.ToString()));
            pluginManager.AddAction("SimSteward.SelectIncidentAndSeek", GetType(), (pm, arg) => DispatchAction("SelectIncidentAndSeek", arg?.ToString()));
            pluginManager.AddAction("SimSteward.SetReplayCaptureSpeed", GetType(), (pm, arg) => DispatchAction("SetReplayCaptureSpeed", arg?.ToString()));
            pluginManager.AddAction("SimSteward.SetSecondsBefore", GetType(), (pm, arg) => DispatchAction("SetSecondsBefore", arg?.ToString()));
            pluginManager.AddAction("SimSteward.SetSecondsAfter", GetType(), (pm, arg) => DispatchAction("SetSecondsAfter", arg?.ToString()));
            pluginManager.AddAction("SimSteward.SetCaptureDriver1", GetType(), (pm, arg) => DispatchAction("SetCaptureDriver1", arg?.ToString()));
            pluginManager.AddAction("SimSteward.SetCaptureDriver2", GetType(), (pm, arg) => DispatchAction("SetCaptureDriver2", arg?.ToString()));
            pluginManager.AddAction("SimSteward.SetCaptureCamera1", GetType(), (pm, arg) => DispatchAction("SetCaptureCamera1", arg?.ToString()));
            pluginManager.AddAction("SimSteward.SetCaptureCamera2", GetType(), (pm, arg) => DispatchAction("SetCaptureCamera2", arg?.ToString()));
            pluginManager.AddAction("SimSteward.SetAutoRotateAndCapture", GetType(), (pm, arg) => DispatchAction("SetAutoRotateAndCapture", arg?.ToString()));
            pluginManager.AddAction("SimSteward.ToggleAutoRotateAndCapture", GetType(), (pm, arg) => DispatchAction("ToggleAutoRotateAndCapture", arg?.ToString()));
            pluginManager.AddAction("SimSteward.SetAutoRotateDwellSeconds", GetType(), (pm, arg) => DispatchAction("SetAutoRotateDwellSeconds", arg?.ToString()));
            pluginManager.AddAction("SimSteward.NextIncident", GetType(), (pm, arg) => DispatchAction("NextIncident", arg?.ToString()));
            pluginManager.AddAction("SimSteward.PrevIncident", GetType(), (pm, arg) => DispatchAction("PrevIncident", arg?.ToString()));
            pluginManager.AddAction("SimSteward.ReplayPlayPause", GetType(), (pm, arg) => DispatchAction("ReplayPlayPause", arg?.ToString()));
            pluginManager.AddAction("SimSteward.ReplaySetSpeed", GetType(), (pm, arg) => DispatchAction("ReplaySetSpeed", arg?.ToString()));
            pluginManager.AddAction("SimSteward.ReplayStepFrame", GetType(), (pm, arg) => DispatchAction("ReplayStepFrame", arg?.ToString()));
            pluginManager.AddAction("SimSteward.ReplaySeekFrame", GetType(), (pm, arg) => DispatchAction("ReplaySeekFrame", arg?.ToString()));
            pluginManager.AddAction("SimSteward.ReplaySeekSessionStart", GetType(), (pm, arg) => DispatchAction("ReplaySeekSessionStart", arg?.ToString()));
            pluginManager.AddAction("SimSteward.ReplaySeekToSessionEnd", GetType(), (pm, arg) => DispatchAction("ReplaySeekToSessionEnd", arg?.ToString()));
            pluginManager.AddAction("SimSteward.CaptureSessionSummaryNow", GetType(), (pm, arg) => DispatchAction("CaptureSessionSummaryNow", arg?.ToString()));
            pluginManager.AddAction("SimSteward.FinalizeThenCaptureSessionSummary", GetType(), (pm, arg) => DispatchAction("FinalizeThenCaptureSessionSummary", arg?.ToString()));
            pluginManager.AddAction("SimSteward.RecordSessionSnapshot", GetType(), (pm, arg) => DispatchAction("RecordSessionSnapshot", arg?.ToString()));
            _logger.Structured("INFO", "simhub-plugin", "actions_registered", "SimHub properties and actions registered.", null, "lifecycle", null);

            var wsPort = DefaultPort;
            var wsPortEnv = Environment.GetEnvironmentVariable("SIMSTEWARD_WS_PORT");
            if (!string.IsNullOrWhiteSpace(wsPortEnv) &&
                int.TryParse(wsPortEnv, out var parsedPort) &&
                parsedPort > 0 && parsedPort < 65536)
            {
                wsPort = parsedPort;
            }
            _wsPort = wsPort;
            var wsBind = Environment.GetEnvironmentVariable("SIMSTEWARD_WS_BIND");
            if (string.IsNullOrWhiteSpace(wsBind))
                wsBind = "0.0.0.0";
            var wsToken = Environment.GetEnvironmentVariable("SIMSTEWARD_WS_TOKEN");
            if (string.IsNullOrWhiteSpace(wsToken))
                wsToken = null;

            var autoSnapEnv = Environment.GetEnvironmentVariable("SIMSTEWARD_SESSION_SNAPSHOT_AUTO");
            _autoSessionSnapshotsEnabled =
                string.Equals(autoSnapEnv, "1", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(autoSnapEnv, "true", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(autoSnapEnv, "yes", StringComparison.OrdinalIgnoreCase);
            if (_autoSessionSnapshotsEnabled)
                _logger.Info("Auto session snapshots enabled (SIMSTEWARD_SESSION_SNAPSHOT_AUTO).");

            _logger.Structured("INFO", "simhub-plugin", "bridge_starting", "WebSocket bridge starting.",
                new Dictionary<string, object> { ["bind"] = wsBind, ["port"] = _wsPort }, "lifecycle", null);
            _bridge = new DashboardBridge(
                GetStateForNewClient,
                GetLogTailForNewClient,
                DispatchAction,
                OnLog,
                _logger,
                OnDashboardStructuredLog,
                onSendError: (ex, payloadType) =>
                {
                    AgentDebugLog.WriteB0C27E("H2", "SimStewardPlugin.onSendError", "send_failed", new { payloadType, error = ex?.Message });
                    WriteBroadcastError("Send:" + payloadType, ex);
                },
                onNoClients: () =>
                {
                    var now = DateTime.UtcNow;
                    lock (_broadcastErrorLock)
                    {
                        if ((now - _lastNoClientsLogAt).TotalSeconds < 10) return;
                        _lastNoClientsLogAt = now;
                    }
                    AgentDebugLog.WriteB0C27E("H1", "SimStewardPlugin.onNoClients", "broadcast_skipped_0_clients_throttled", null);
                    WriteBroadcastError("Broadcast skipped: 0 clients", null);
                    _broadcastNoClientsPending = true;
                });
            try
            {
                _bridge.Start(wsBind, wsPort, wsToken);
                var logEnvReady = Environment.GetEnvironmentVariable("SIMSTEWARD_LOG_ENV") ?? "production";
                _logger.Structured("INFO", "simhub-plugin", "plugin_ready", "SimSteward ready.", new Dictionary<string, object> { ["ws_port"] = _wsPort, ["env"] = logEnvReady }, "lifecycle", null);
            }
            catch (Exception ex)
            {
                _logger.Structured("WARN", "simhub-plugin", "bridge_start_failed", $"WebSocket server could not start: {ex.Message}",
                    new Dictionary<string, object> { ["bind"] = wsBind, ["port"] = _wsPort, ["error"] = ex.Message }, "lifecycle", null);
                _logger.Error($"WebSocket server could not start on {wsBind}:{wsPort}. Is it already in use?", ex);
                _bridge = null;
            }

            // Subscribe log streaming after bridge is up so we only forward lines once a client can receive them
            _logger.LogWritten += OnLogWritten;
            _logger.Structured("INFO", "simhub-plugin", "log_streaming_subscribed", "Dashboard log streaming attached.", null, "lifecycle", null);

            try
            {
                _irsdk = new IRacingSdk();
                _irsdk.UpdateInterval = 1;
                _irsdk.OnConnected += () =>
                {
                    _sessionStats.Reset();
                    _sessionDigestEmitted = false;
                    AgentDebugLog.WriteB0C27E("IR", "SimStewardPlugin.OnConnected", "iracing_connected", null);
                    _logger?.Structured("INFO", "simhub-plugin", "iracing_connected", "iRacing connected.", null, "lifecycle", GetIncidentIdForSpine());
                };
                _irsdk.OnDisconnected += () =>
                {
                    AgentDebugLog.WriteB0C27E("IR", "SimStewardPlugin.OnDisconnected", "iracing_disconnected", null);
                    _logger?.Structured("INFO", "simhub-plugin", "iracing_disconnected", "iRacing disconnected.", null, "lifecycle", GetIncidentIdForSpine());
                };
                _irsdk.Start();
                AgentDebugLog.WriteB0C27E("IR", "SimStewardPlugin.Init", "irsdk_started", new { irsdkNull = _irsdk == null });
                _logger.Structured("INFO", "simhub-plugin", "irsdk_started", "iRacing SDK started.", null, "lifecycle", null);
            }
            catch (Exception ex)
            {
                AgentDebugLog.WriteB0C27E("IR", "SimStewardPlugin.Init", "irsdk_start_failed", new { error = ex?.Message });
                _logger.Error("iRacing SDK (IRSDKSharper) failed to start. Plugin will run without iRacing data.", ex);
                _irsdk = null;
            }

            _logger?.Info($"SimSteward ready. WebSocket on :{_wsPort}. Waiting for iRacing...");
        }

        // #region agent log — iRacing broadcast debug (session b0c27e)
        private DateTime _irNotConnectedLogAt = DateTime.MinValue;
        private int _irConnectedTickLogged;
        // #endregion

        public void DataUpdate(PluginManager pluginManager, ref GameData data)
        {
            var sw = Stopwatch.StartNew();
            int clientCount = _bridge != null ? _bridge.ClientCount : 0;
            _sessionStats.SetWsPeakClients(clientCount);

            if (_irsdk != null && _irsdk.IsConnected)
            {
                // #region agent log
                if (_irConnectedTickLogged < 3)
                {
                    _irConnectedTickLogged++;
                    try
                    {
                        var st = _irsdk.Data.GetDouble("SessionTime");
                        var siu = SafeGetInt("SessionInfoUpdate");
                        AgentDebugLog.WriteB0C27E("IR", "SimStewardPlugin.DataUpdate", "iracing_data_tick", new { sessionTime = st, sessionInfoUpdate = siu });
                    }
                    catch (Exception ex)
                    {
                        AgentDebugLog.WriteB0C27E("IR", "SimStewardPlugin.DataUpdate", "iracing_data_tick_error", new { error = ex?.Message });
                    }
                }
                // #endregion
                try
                {
                    _lastSessionTime = _irsdk.Data.GetDouble("SessionTime");
                }
                catch
                {
                    _lastSessionTime = 0;
                }
                try
                {
                    string simMode = _irsdk.Data.SessionInfo?.WeekendInfo?.SimMode;
                    if (string.Equals(simMode, "replay", StringComparison.OrdinalIgnoreCase))
                        _pluginMode = "Replay";
                    else if (!string.IsNullOrEmpty(simMode))
                        _pluginMode = "Live";
                    else
                        _pluginMode = "Unknown";
                }
                catch
                {
                    _pluginMode = "Unknown";
                }
                bool isReplayNow = string.Equals(_pluginMode, "Replay", StringComparison.OrdinalIgnoreCase);
                _wasReplay = isReplayNow;
                // iRacing SDK: ReplayFrameNumEnd = current playback position, ReplayFrameNum = last frame of session.
                _replayFrameNum = SafeGetInt("ReplayFrameNumEnd");
                _replayFrameNumEnd = SafeGetInt("ReplayFrameNum");
                _replayPlaySpeed = SafeGetInt("ReplayPlaySpeed");
                _replayPlaySlowMotion = SafeGetBool("ReplayPlaySlowMotion");
                _replaySessionNum = SafeGetInt("ReplaySessionNum");
                _replayIsPlaying = _replayPlaySpeed != 0;

                int subId = _irsdk?.Data?.SessionInfo?.WeekendInfo?.SubSessionID ?? 0;
                string trackName = _tracker?.TrackName ?? "";
                _currentSessionSeq = BuildSessionSeq(trackName);
                _currentSessionId = subId > 0 ? subId.ToString() : _currentSessionSeq;

                bool scanActive = _tracker.CurrentScanState != IncidentTracker.ScanState.Idle &&
                                  _tracker.CurrentScanState != IncidentTracker.ScanState.Complete &&
                                  _tracker.CurrentScanState != IncidentTracker.ScanState.Error;

                // Skip live YAML tracking while scan is running — scan owns YAML reads during its seek loop.
                // Prevents _prevYamlIncidents from being corrupted by historical frame values seen during scan.
                if (!scanActive)
                    _tracker.Update(_irsdk, _lastSessionTime);

                // Advance replay scan state machine (no-op when idle)
                if (scanActive)
                {
                    _tracker.TickReplayScan(_irsdk);

                    // Broadcast scan progress periodically (every ~30 ticks = ~0.5s)
                    if (_scanBroadcastCounter++ % 30 == 0 && _bridge != null)
                    {
                        try
                        {
                            var progressJson = JsonConvert.SerializeObject(new { type = "replayScanProgress", data = _tracker.ScanProgress });
                            _bridge.Broadcast(progressJson);
                        }
                        catch { }
                    }

                    // On completion, broadcast final result and reset live tracking baseline
                    if (_tracker.CurrentScanState == IncidentTracker.ScanState.Complete ||
                        _tracker.CurrentScanState == IncidentTracker.ScanState.Error)
                    {
                        if (_bridge != null)
                        {
                            try
                            {
                                var resultJson = JsonConvert.SerializeObject(new { type = "replayScanComplete", data = _tracker.ScanProgress });
                                _bridge.Broadcast(resultJson);
                            }
                            catch { }
                        }
                        _scanBroadcastCounter = 0;
                        // Reset live tracker baseline so _prevYamlIncidents reflects current YAML state
                        // after scan, not stale mid-scan values.
                        _tracker.ResetLiveBaseline(_irsdk);
                    }
                }

                int rawPlayerIncidentCount = SafeGetInt("PlayerCarMyIncidentCount");
                // iRacing incident count is 0-999; SDK can return garbage if shared memory not ready or var missing — reject out-of-range to avoid bogus incidents and corrupted prev state
                const int MaxValidIncidentCount = 999;
                int playerIncidentCount = rawPlayerIncidentCount < 0 ? 0 : (rawPlayerIncidentCount > MaxValidIncidentCount ? MaxValidIncidentCount : rawPlayerIncidentCount);
                if (rawPlayerIncidentCount != playerIncidentCount)
                    AgentDebugLog.WriteB0C27E("IR", "SimStewardPlugin.DataUpdate", "player_incident_count_clamped", new { raw = rawPlayerIncidentCount, clamped = playerIncidentCount });
                int focusedCarIdx = _tracker.PlayerCarIdx;
                int prevFocusedIncidentCount = -1;
                bool hasFocusedHistory = focusedCarIdx >= 0 && _prevFocusedCarIncidentCounts.TryGetValue(focusedCarIdx, out prevFocusedIncidentCount);
                // Bootstrap per-car prev from global when first seeing this car and we didn't switch (same car as last tick).
                if (focusedCarIdx >= 0 && !hasFocusedHistory && _lastFocusedCarIdx == focusedCarIdx && _prevPlayerCarMyIncidentCount >= 0)
                {
                    _prevFocusedCarIncidentCounts[focusedCarIdx] = _prevPlayerCarMyIncidentCount;
                    hasFocusedHistory = true;
                    prevFocusedIncidentCount = _prevPlayerCarMyIncidentCount;
                    AgentDebugLog.WriteB0C27E("H2", "SimStewardPlugin.DataUpdate", "telemetry_bootstrap", new { focusedCarIdx, prevFocusedIncidentCount });
                }
                bool validPlayerIncidentCount = playerIncidentCount >= 0;
                bool countWasClamped = rawPlayerIncidentCount != playerIncidentCount;
                // #region agent log
                bool telemetryWouldAdd = !countWasClamped && _tracker.BaselineEstablished && focusedCarIdx >= 0 && validPlayerIncidentCount &&
                                         playerIncidentCount > prevFocusedIncidentCount && hasFocusedHistory;
                bool countChanged = focusedCarIdx >= 0 && validPlayerIncidentCount && playerIncidentCount != prevFocusedIncidentCount;
                if (countChanged || telemetryWouldAdd)
                    AgentDebugLog.WriteB0C27E("H2", "SimStewardPlugin.DataUpdate", "telemetry_check",
                        new
                        {
                            BaselineEstablished = _tracker.BaselineEstablished,
                            playerIncidentCount,
                            prevFocusedIncidentCount,
                            hasFocusedHistory,
                            focusedCarIdx,
                            telemetryWouldAdd,
                            validPlayerIncidentCount
                        });
                // #endregion
                if (telemetryWouldAdd)
                    _tracker.AddPlayerIncidentFromTelemetry(_lastSessionTime, _replayFrameNum, playerIncidentCount,
                        _irsdk?.Data?.SessionInfo?.WeekendInfo?.SubSessionID ?? 0, SafeGetInt("SessionNum"));
                if (focusedCarIdx >= 0 && validPlayerIncidentCount)
                    _prevFocusedCarIncidentCounts[focusedCarIdx] = playerIncidentCount;
                _lastFocusedCarIdx = focusedCarIdx;
                _prevPlayerCarMyIncidentCount = playerIncidentCount;

                if (_logger.IsDebugMode)
                {
                    int currentSiu = SafeGetInt("SessionInfoUpdate");
                    if (currentSiu != _lastLoggedSiu)
                    {
                        _lastLoggedSiu = currentSiu;
                        int driverCount = _tracker.GetDriverSnapshot().Count;
                        int camCarIdx = SafeGetInt("CamCarIdx");
                        int driverCarIdx = _irsdk?.Data?.SessionInfo?.DriverInfo?.DriverCarIdx ?? -1;
                        AgentDebugLog.WriteB0C27E("H6", "SimStewardPlugin.DataUpdate", "siu_telemetry_snapshot",
                            new
                            {
                                siu = currentSiu,
                                focusedCarIdx = _tracker.PlayerCarIdx,
                                camCarIdx,
                                driverCarIdx,
                                playerIncidentCount = SafeGetInt("PlayerCarMyIncidentCount"),
                                replayPlaySpeed = _replayPlaySpeed
                            });
                        _logger.Debug($"YAML update (SIU={currentSiu})", "simhub-plugin", "yaml_update", new Dictionary<string, object> { ["driver_count"] = driverCount, ["update_index"] = currentSiu });
                    }
                }

                int sessionState = SafeGetInt("SessionState");
                bool isPostRace = sessionState >= 5; // irsdk_StateCheckered = 5, StateCoolDown = 6
                bool justHitCheckered = !_wasPostRace && isPostRace;
                _wasPostRace = isPostRace;
                if (justHitCheckered)
                {
                    // Manual-only behavior: never auto-seek or auto-capture at checkered.
                    _logger?.Structured("INFO", "simhub-plugin", "checkered_detected", "Crossed the line — waiting for explicit user action to capture.", new Dictionary<string, object> { ["session_state"] = sessionState }, "session", GetIncidentIdForSpine());
                }

                if (_finalizeThenCaptureJob != null && !_finalizeThenCaptureJob.IsDone)
                    _finalizeThenCaptureJob.Tick(this);

                // Drain and broadcast any new incidents as real-time push events
                var newIncidents = _tracker.DrainNewIncidents();
                // #region agent log
                if (newIncidents.Count > 0)
                    AgentDebugLog.WriteB0C27E("H5", "SimStewardPlugin.DataUpdate", "incidents_drained", new { count = newIncidents.Count, bridgeNull = _bridge == null, firstId = newIncidents[0]?.Id });
                // #endregion
                if (newIncidents.Count > 0)
                {
                    if (_bridge != null)
                    {
                        try
                        {
                            // Push incidents into the log stream (source of truth for dashboard log = Grafana-style stream)
                            var logEntries = new List<LogEntry>();
                            var dashboardLogFieldKeys = new[] { "incident_id", "incident_type", "car_number", "driver_name", "delta", "session_time", "lap", "source" };
                            foreach (var ev in newIncidents)
                            {
                                logEntries.Add(new LogEntry
                                {
                                    Level = "INFO",
                                    Event = "incident_detected",
                                    Message = $"Incident detected: {ev.Type ?? "?"} #{ev.CarNumber ?? "?"} {ev.DriverName ?? "?"}",
                                    Timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                                    SessionId = ev.SubSessionId > 0 ? ev.SubSessionId.ToString() : null,
                                    ReplayFrame = ev.ReplayFrameNum > 0 ? (int?)ev.ReplayFrameNum : null,
                                    IncidentId = ev.Id,
                                    Fields = new Dictionary<string, object>
                                    {
                                        ["incident_id"] = ev.Id ?? "",
                                        ["incident_type"] = ev.Type ?? "?",
                                        ["car_number"] = ev.CarNumber ?? "?",
                                        ["driver_name"] = ev.DriverName ?? "?",
                                        ["delta"] = ev.Delta,
                                        ["session_time"] = ev.SessionTime,
                                        ["lap"] = ev.Lap,
                                        ["source"] = ev.Source ?? "?"
                                    }
                                });
                            }
                            AgentDebugLog.WriteB0C27E("ID", "SimStewardPlugin.DataUpdate", "incident_dashboard_log_fields", new { count = newIncidents.Count, fieldKeys = dashboardLogFieldKeys });
                            int clientCountAtBroadcast = _bridge.ClientCount;
                            AgentDebugLog.WriteB0C27E("ID", "SimStewardPlugin.DataUpdate", "incident_logEvents_broadcast", new { entryCount = logEntries.Count, clientCount = clientCountAtBroadcast });
                            var logMsg = new { type = "logEvents", entries = logEntries };
                            _bridge.Broadcast(JsonConvert.SerializeObject(logMsg), "logEvents");
                            var incidentMsg = new { type = "incidentEvents", events = newIncidents };
                            _bridge.Broadcast(JsonConvert.SerializeObject(incidentMsg), "incidentEvents");
                            AgentDebugLog.WriteB0C27E("H5", "SimStewardPlugin.DataUpdate", "incident_broadcast_sent", new { count = newIncidents.Count });
                        }
                        catch (Exception ex)
                        {
                            AgentDebugLog.WriteB0C27E("H5", "SimStewardPlugin.DataUpdate", "incident_broadcast_error", new { count = newIncidents.Count, error = ex.Message });
                        }
                    }
                    else
                        AgentDebugLog.WriteB0C27E("H5", "SimStewardPlugin.DataUpdate", "incident_broadcast_skipped_bridge_null", new { count = newIncidents.Count });
                }

                if (_tracker.BaselineJustEstablished && _bridge != null)
                {
                    try
                    {
                        var entry = new LogEntry
                        {
                            Level = "INFO",
                            Message = "Incident baseline established — tracking is active.",
                            Timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
                        };
                        var msg = new { type = "logEvents", entries = new[] { entry } };
                        _bridge.Broadcast(JsonConvert.SerializeObject(msg), "logEvents");
                    }
                    catch { }
                }

            }
            else
            {
                // #region agent log — throttle: log once per 5s when not receiving iRacing
                var nowUtc = DateTime.UtcNow;
                if ((nowUtc - _irNotConnectedLogAt).TotalSeconds >= 5)
                {
                    _irNotConnectedLogAt = nowUtc;
                    AgentDebugLog.WriteB0C27E("IR", "SimStewardPlugin.DataUpdate", "iracing_not_connected", new { irsdkNull = _irsdk == null, isConnected = _irsdk?.IsConnected ?? false });
                }
                // #endregion
                _lastSessionTime = 0;
                _pluginMode = "Unknown";
                _replayFrameNum = 0;
                _replayFrameNumEnd = 0;
                _replayPlaySpeed = 0;
                _replayPlaySlowMotion = false;
                _replaySessionNum = 0;
                _replayIsPlaying = false;
                _wasPostRace = false;
                _wasReplay = false;
                _prevPlayerCarMyIncidentCount = -1;
                _lastFocusedCarIdx = -1;
                _prevFocusedCarIncidentCounts.Clear();
                _currentSessionId = null;
                _currentSessionSeq = null;
                _lastLoggedPlayerCarIdx = -2;
                _tracker.Reset();
                if (_irsdk != null && (DateTime.UtcNow - _lastWaitingLogAt).TotalMilliseconds >= WaitingLogThrottleMs)
                {
                    _lastWaitingLogAt = DateTime.UtcNow;
                    _logger?.Info("Waiting for iRacing to connect...");
                }
            }

            pluginManager.SetPropertyValue("SimSteward.PluginMode", GetType(), _pluginMode);
            pluginManager.SetPropertyValue("SimSteward.IncidentCount", GetType(), _tracker.PlayerIncidentCount);
            pluginManager.SetPropertyValue("SimSteward.HasLiveIncidentData", GetType(), _irsdk != null && _irsdk.IsConnected && _tracker.BaselineEstablished);
            pluginManager.SetPropertyValue("SimSteward.ClientCount", GetType(), clientCount);

            var snapshot = BuildPluginSnapshot();
            MaybeAutoRecordSessionSnapshot(snapshot?.SessionDiagnostics);

            if (_bridge == null) return;
            var now = DateTime.UtcNow;

            if (_broadcastNoClientsPending)
            {
                _broadcastNoClientsPending = false;
                _logger?.Structured("WARN", "simhub-plugin", "broadcast_no_clients", "Broadcast skipped: 0 connected clients. Check broadcast-errors.log.",
                    new Dictionary<string, object> { ["hint"] = "Open dashboard at http://localhost:8888/Web/sim-steward-dash/index.html" }, "lifecycle", GetIncidentIdForSpine());
            }

            if (_irsdk != null && _irsdk.IsConnected && (now - _lastYamlInfoLogAt).TotalSeconds >= 10)
            {
                _lastYamlInfoLogAt = now;
                int siu = SafeGetInt("SessionInfoUpdate");
                int driverCount = _tracker.GetDriverSnapshot().Count;
                _logger?.Structured("INFO", "simhub-plugin", "yaml_processed", "Session YAML processed (evidence in log).",
                    new Dictionary<string, object> { ["session_info_update"] = siu, ["driver_count"] = driverCount, ["session_time"] = _lastSessionTime }, "lifecycle", GetIncidentIdForSpine());
            }

            if ((now - _lastBroadcastAt).TotalMilliseconds >= BroadcastThrottleMs)
            {
                _lastBroadcastAt = now;
                int playerIdx = snapshot?.PlayerCarIdx ?? -1;
                if (playerIdx != _lastLoggedPlayerCarIdx)
                {
                    _lastLoggedPlayerCarIdx = playerIdx;
                    string carNum = "?";
                    if (playerIdx >= 0 && snapshot?.Drivers != null)
                    {
                        var dr = snapshot.Drivers.FirstOrDefault(d => d.CarIdx == playerIdx);
                        carNum = dr?.CarNumber ?? "?";
                    }
                    _logger?.Structured("INFO", "simhub-plugin", "viewing_driver", $"Viewing driver #{carNum} (CarIdx {playerIdx})",
                        new Dictionary<string, object> { ["player_car_idx"] = playerIdx, ["player_car_number"] = carNum }, "lifecycle", null);
                }
                _bridge.BroadcastState(BuildStateJson(snapshot));
                if (_logger.IsDebugMode && snapshot != null)
                    _logger.Debug("state broadcast", "simhub-plugin", "state_broadcast_summary", new Dictionary<string, object>
                    {
                        ["mode"] = snapshot.PluginMode,
                        ["session_id"] = snapshot.SessionId,
                        ["incident_count"] = snapshot.PlayerIncidentCount,
                        ["client_count"] = clientCount,
                        ["replay_frame"] = snapshot.ReplayFrameNum
                    });
            }

            if (_logger.IsDebugMode && _irsdk != null && _irsdk.IsConnected)
            {
                sw.Stop();
                _dataUpdateTickCount++;
                _runningAvgDataUpdateMs = (_runningAvgDataUpdateMs * 59 + sw.ElapsedMilliseconds) / 60.0;
                if (_dataUpdateTickCount % 60 == 0)
                {
                    int framesDropped = 0;
                    try { framesDropped = _irsdk.Data?.FramesDropped ?? 0; } catch { }
                    _logger.Debug("tick_stats", "simhub-plugin", "tick_stats", new Dictionary<string, object>
                    {
                        ["data_update_ms"] = Math.Round(_runningAvgDataUpdateMs, 2),
                        ["frames_dropped"] = framesDropped,
                        ["session_time"] = _lastSessionTime
                    });
                }
            }
        }

        private void CaptureAndEmitSessionSummary()
        {
            // Legacy wrapper: keep method for compatibility but route to unified capture logic
            TryCaptureAndEmitSessionSummary("legacy", logNotReady: true);
        }

        public void End(PluginManager pluginManager)
        {
            _logger?.Structured("INFO", "simhub-plugin", "plugin_stopped", "SimSteward plugin End.", null, "lifecycle", null);

            // Unsubscribe before stopping the bridge to prevent callbacks on a dead bridge
            if (_logger != null)
                _logger.LogWritten -= OnLogWritten;

            if (_irsdk != null)
            {
                try
                {
                    _irsdk.Stop();
                }
                catch (Exception ex)
                {
                    _logger?.Warn($"iRacing SDK Stop failed: {ex.Message}");
                }
                _irsdk = null;
            }
            if (_bridge != null)
            {
                _bridge.Stop();
                _bridge = null;
            }
        }

        /// <summary>For settings panel: number of connected WebSocket clients.</summary>
        public int ClientCountForSettings => _bridge?.ClientCount ?? 0;

        /// <summary>For settings panel: WebSocket server is running.</summary>
        public bool WsRunningForSettings => _bridge != null;

        /// <summary>For settings panel: WebSocket port.</summary>
        public int WsPortForSettings => _wsPort;

        /// <summary>For settings panel: iRacing SDK was started successfully.</summary>
        public bool IrsdkStartedForSettings => _irsdk != null;

        /// <summary>For settings panel: iRacing SDK connection status.</summary>
        public string IracingConnectionStatus =>
            _irsdk == null ? "SDK not started" : (_irsdk.IsConnected ? "Connected" : "Not connected");

        /// <summary>For settings panel: path to plugin-structured.jsonl (for Alloy file-tail).</summary>
        public string StructuredLogPathForSettings => _logger?.StructuredLogPath ?? "";

        /// <summary>Thread-safe snapshot for PluginLogger so omit changes take effect immediately.</summary>
        private HashSet<string> GetOmittedLevelsSnapshot()
        {
            lock (_omitLock)
            {
                return _omittedLogLevels == null
                    ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    : new HashSet<string>(_omittedLogLevels, StringComparer.OrdinalIgnoreCase);
            }
        }

        /// <summary>Thread-safe snapshot for PluginLogger so omit changes take effect immediately.</summary>
        private HashSet<string> GetOmittedEventsSnapshot()
        {
            lock (_omitLock)
            {
                return _omittedEvents == null
                    ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    : new HashSet<string>(_omittedEvents, StringComparer.OrdinalIgnoreCase);
            }
        }

        /// <summary>For settings panel: whether a log level is omitted at source.</summary>
        public bool OmitLevelForSettings(string level)
        {
            if (string.IsNullOrEmpty(level)) return false;
            lock (_omitLock)
                return _omittedLogLevels != null && _omittedLogLevels.Contains(level.Trim());
        }

        /// <summary>For settings panel: whether DEBUG level is omitted at source.</summary>
        public bool OmitDebugLogsForSettings => OmitLevelForSettings("DEBUG");

        /// <summary>Include or omit a log level at source (no file, Loki, or dashboard).</summary>
        public void SetOmitLogLevel(string level, bool omit)
        {
            if (string.IsNullOrWhiteSpace(level)) return;
            lock (_omitLock)
            {
                if (_omittedLogLevels == null)
                    _omittedLogLevels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (omit)
                    _omittedLogLevels.Add(level.Trim());
                else
                    _omittedLogLevels.Remove(level.Trim());
                _settings.OmitLogLevels = new List<string>(_omittedLogLevels);
            }
            SaveUiSettings();
        }

        /// <summary>For settings panel: whether a log event is omitted at source (same event ids as dashboard).</summary>
        public bool OmitEventForSettings(string eventId)
        {
            if (string.IsNullOrEmpty(eventId)) return false;
            lock (_omitLock)
                return _omittedEvents != null && _omittedEvents.Contains(eventId.Trim());
        }

        /// <summary>Include or omit a log event at source (no file, Loki, or dashboard). Same events as dashboard hide-by-event.</summary>
        public void SetOmitEvent(string eventId, bool omit)
        {
            if (string.IsNullOrWhiteSpace(eventId)) return;
                if (omit && (string.Equals(eventId.Trim(), "action_result", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(eventId.Trim(), "incident_detected", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(eventId.Trim(), "dashboard_ui_event", StringComparison.OrdinalIgnoreCase)))
                return; // Never omit action_result, incident_detected, or dashboard_ui_event so they always appear in logs
            lock (_omitLock)
            {
                if (_omittedEvents == null)
                    _omittedEvents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (omit)
                    _omittedEvents.Add(eventId.Trim());
                else
                    _omittedEvents.Remove(eventId.Trim());
                _settings.OmitEvents = new List<string>(_omittedEvents);
            }
            SaveUiSettings();
        }

        /// <summary>For settings panel: whether "Log all action traffic" is enabled (action_received and action_dispatched not omitted).</summary>
        public bool LogAllActionTrafficForSettings => _settings != null && _settings.LogAllActionTraffic;

        /// <summary>Enable or disable logging of all action traffic (action_received, action_dispatched). When enabled, these events are not omitted in production.</summary>
        public void SetLogAllActionTraffic(bool value)
        {
            if (_settings == null) return;
            _settings.LogAllActionTraffic = value;
            SaveUiSettings();
            var logEnv = Environment.GetEnvironmentVariable("SIMSTEWARD_LOG_ENV") ?? "production";
            var isProduction = string.Equals(logEnv, "production", StringComparison.OrdinalIgnoreCase);
            lock (_omitLock)
            {
                if (_omittedEvents == null) return;
                if (value)
                {
                    _omittedEvents.Remove("action_received");
                    _omittedEvents.Remove("action_dispatched");
                }
                else if (isProduction)
                {
                    _omittedEvents.Add("action_received");
                    _omittedEvents.Add("action_dispatched");
                }
            }
        }

        /// <summary>For settings panel: current Data API endpoint (e.g. http://localhost:8080). Empty = disabled.</summary>
        public string GetDataApiEndpointForSettings()
        {
            var s = _settings?.DataApiEndpoint;
            return string.IsNullOrWhiteSpace(s) ? "" : s.Trim();
        }

        /// <summary>Set Data API base URL; session-complete is POSTed here after a successful capture. Persisted in ui-settings.</summary>
        public void SetDataApiEndpoint(string value)
        {
            _settings.DataApiEndpoint = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            SaveUiSettings();
        }

        /// <summary>Emit plugin_ui_changed structured log for settings-panel interactions (called from PluginControl).</summary>
        public void LogPluginUiChanged(string element, object value)
        {
            if (string.IsNullOrEmpty(element)) return;
            var fields = new Dictionary<string, object> { ["element"] = element };
            if (value != null)
                fields["value"] = value;
            _logger?.Structured("INFO", "simhub-plugin", "plugin_ui_changed", "Plugin UI changed", fields, "action", null);
        }

        private void PostSessionSummaryToDataApiFireAndForget(SessionSummary summary)
        {
            if (summary == null) return;
            var endpoint = _settings?.DataApiEndpoint;
            if (string.IsNullOrWhiteSpace(endpoint)) return;
            var url = endpoint.Trim().TrimEnd('/') + "/session-complete";
            var json = JsonConvert.SerializeObject(summary);
            Task.Run(() =>
            {
                try
                {
                    using (var content = new StringContent(json, Encoding.UTF8, "application/json"))
                    {
                        var response = DataApiHttpClient.PostAsync(url, content).ConfigureAwait(false).GetAwaiter().GetResult();
                        if (response.IsSuccessStatusCode)
                            _logger?.Structured("INFO", "simhub-plugin", "data_api_post_ok", "Session summary posted to Data API", new Dictionary<string, object> { ["sub_session_id"] = summary.SubSessionID, ["status_code"] = (int)response.StatusCode }, "session", GetIncidentIdForSpine());
                        else
                            _logger?.Structured("WARN", "simhub-plugin", "data_api_post_failed", "Data API session-complete returned non-success", new Dictionary<string, object> { ["sub_session_id"] = summary.SubSessionID, ["status_code"] = (int)response.StatusCode, ["reason"] = response.ReasonPhrase }, "session", GetIncidentIdForSpine());
                    }
                }
                catch (Exception ex)
                {
                    _logger?.Structured("WARN", "simhub-plugin", "data_api_post_error", "Data API session-complete failed", new Dictionary<string, object> { ["sub_session_id"] = summary.SubSessionID, ["error"] = ex.Message }, "session", GetIncidentIdForSpine());
                }
            });
        }

        public string LeftMenuTitle => "Sim Steward";

        private static ImageSource _cachedMenuIcon;
        public ImageSource PictureIcon => _cachedMenuIcon ?? (_cachedMenuIcon = CreateMenuIcon());

        public System.Windows.Controls.Control GetWPFSettingsControl(PluginManager pluginManager)
        {
            return new PluginControl(this);
        }

        private static ImageSource CreateMenuIcon()
        {
            const int size = 24;
            var drawing = new DrawingGroup();
            drawing.Children.Add(new GeometryDrawing(
                Brushes.Black,
                new Pen(Brushes.Black, 1),
                new RectangleGeometry(new System.Windows.Rect(2, 2, size - 4, size - 4))));
            var image = new DrawingImage(drawing);
            image.Freeze();
            return image;
        }
#endif
    }
}
