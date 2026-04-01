#if SIMHUB_SDK
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using IRSDKSharper;
using Sentry;

namespace SimSteward.Plugin
{
    public partial class SimStewardPlugin
    {
        // ── Internal step enum ────────────────────────────────────────────────
        private enum SuiteInternalStep
        {
            T0_Rewind, T0_FrameZero, T0_ScanCooldown, T0_SeekCapture, T0_CaptureSettle,
            T1_Rewind, T1_FrameZero, T1_Sweep,
            T2, T3, T4,
            T5_Switch, T5_Settle,
            T5b_Seek, T5b_Cycle, T5b_Settle,
            T6,
            T7_Rewind, T7_FrameZero, T7_Cooldown,
            T8_Trigger, T8_Poll,
            TDISC_Seek, TDISC_Settle, TDISC_Capture,
            Done
        }

        // ── Suite fields ──────────────────────────────────────────────────────
        private DataCaptureSuitePhase _suitePhase = DataCaptureSuitePhase.Idle;
        private SuiteInternalStep     _suiteStep  = SuiteInternalStep.T0_Rewind;
        private string                _suiteTestRunId;
        private Stopwatch             _suiteStopwatch;
        private DateTime              _suiteEmitCompleteUtc;
        private volatile bool         _suiteCancelRequested;
        private volatile bool         _suiteStartRequested;
        private string                _lokiReadUrl;
        private DataCaptureSuiteTestResult[] _suiteResults;

        // ── Skip list ────────────────────────────────────────────────────────
        private HashSet<string> _suiteSkipList = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // ── Preflight ────────────────────────────────────────────────────────
        private enum PreflightStep
        {
            Idle,
            Level1_Check,
            Level2_SeekEnd, Level2_SettleEnd, Level2_SeekRestore, Level2_SettleRestore,
            Level3_EmitProbe, Level3_WaitProbe, Level3_QueryProbe,
            Complete
        }
        private volatile bool      _preflightRequested;
        private PreflightSnapshot  _preflightSnapshot = new PreflightSnapshot();
        private PreflightStep      _preflightStep = PreflightStep.Idle;
        private int                _preflightSavedFrame;
        private int                _preflightSettleTicks;
        private int                _preflightLevel;            // 0=not run, 1-3
        private string             _preflightCorrelationId;
        private string             _preflightReplayScope = "full";
        private string             _preflightProbeNonce;
        private int                _preflightProbeWaitTicks;
        private long               _preflightProbeEmitNs;
        private volatile int       _preflightLokiProbeResult = -2; // -2=not started, -1=error, 0+=count
        private string             _suitePreflightCorrelationId;

        // T0 scan/select/capture
        private List<(int frame, int lap, int carIdx)> _suiteScanCandidates;
        private int  _suiteFirstScanFrame;
        private int[] _suiteSelectedFrames;
        private int  _suiteCaptureIdx;
        private int  _suiteCaptureTicks;

        // T_60Hz: high-rate capture
        private bool _suite60HzEnabled;
        private HighRateTelemetryRecorder _suite60HzRecorder;

        // T_DISC: data discovery
        private int   _suiteDiscPositionIdx;
        private int[] _suiteDiscTargetFrames;
        private int   _suiteDiscSettleTicks;

        // T0 / T7 shared: ground truth + seek state
        private GroundTruthIncident[] _suiteGroundTruth;
        private int  _suiteGroundTruthIdx;
        private GroundTruthIncident[] _suiteReseekCapture;
        private int  _suiteReseekIdx;
        private int  _suiteSeekCooldownTicks;
        private int  _suiteFrameZeroConsecutive;
        private int  _suiteSeekTimeoutTicks;

        // T1: speed sweep
        private int   _suiteSpeedSweepIdx;
        private int   _suiteSpeedSweepTicks;
        private int   _suiteSpeedSweepFrameTarget;
        private int   _suiteSpeedSweepDetected;
        private int   _suiteSpeedSweepGtHits;
        private int[] _suiteSpeedSweepBaselineFlags;

        // T5b: camera cycle
        private List<(int groupNum, string groupName)> _suiteCameraGroups;
        private int  _suiteCameraGroupIdx;
        private int  _suiteCamSettleTicks;
        private int  _suiteCamConfirmedMatches;
        private readonly List<string> _suiteCamGroupsVisited = new List<string>();

        // T8: FF sweep
        private bool _suiteFfSweepTriggered;
        private int  _suiteT8PollTicks;
        private bool _suiteT8BuildWasRunning;

        // Sentry performance tracing
        private ITransactionTracer _sentryTx;
        private ISpan              _sentryCurrentSpan;

        // ── Public entry points (called from DataUpdate / DispatchAction) ──────

        private void TryStartDataCaptureSuite(string[] skipIds = null)
        {
            if (!_preflightSnapshot.AllPassed)
            {
                _logger?.Warn("DataCaptureSuite: cannot start — preflight not passed.");
                return;
            }
            if (_irsdk == null || !_irsdk.IsConnected)
            {
                _logger?.Warn("DataCaptureSuite: cannot start — iRacing not connected.");
                return;
            }
            string simMode = _irsdk.Data?.SessionInfo?.WeekendInfo?.SimMode ?? "";
            if (!string.Equals(simMode, "replay", StringComparison.OrdinalIgnoreCase))
            {
                _logger?.Warn("DataCaptureSuite: cannot start — not in replay mode.");
                return;
            }
            _suiteSkipList = new HashSet<string>(skipIds ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            // T7 depends on T0 ground truth — auto-skip if T0 is skipped
            if (_suiteSkipList.Contains("T0")) _suiteSkipList.Add("T7");
            _suiteStartRequested = true;
        }

        /// <summary>Called every telemetry tick from <c>OnIrsdkTelemetryDataForReplayIndex</c>.</summary>
        private void ProcessDataCaptureSuiteTick()
        {
            // ── Preflight (independent of suite phase) ──
            if (_preflightRequested)
            {
                _preflightRequested = false;
                // Force-restart even if a previous run got stuck in an intermediate step
                _preflightStep = PreflightStep.Idle;
                _preflightLevel = 0;
                _preflightCorrelationId = null;
                _preflightSnapshot = new PreflightSnapshot();
                try
                {
                    BeginPreflight();
                }
                catch (Exception ex)
                {
                    SentrySdk.CaptureException(ex);
                    _preflightSnapshot.Phase = "error";
                    _preflightSnapshot.Error = "BeginPreflight: " + ex.GetType().Name + ": " + ex.Message;
                    _preflightStep = PreflightStep.Complete;
                }
            }
            if (_preflightStep != PreflightStep.Idle && _preflightStep != PreflightStep.Complete)
            {
                try { TickPreflight(); }
                catch (Exception ex)
                {
                    SentrySdk.CaptureException(ex);
                    _preflightSnapshot.Phase = "error";
                    _preflightSnapshot.Error = "TickPreflight@" + _preflightStep + ": " + ex.GetType().Name + ": " + ex.Message;
                    _preflightStep = PreflightStep.Complete;
                }
            }

            if (_suitePhase == DataCaptureSuitePhase.Idle && !_suiteStartRequested && !_suiteCancelRequested)
                return;

            if (_suiteCancelRequested)
            {
                _suiteCancelRequested = false;
                if (_suitePhase != DataCaptureSuitePhase.Idle)
                {
                    try { _irsdk?.ReplaySetPlaySpeed(1, false); } catch { }
                    _suiteStopwatch?.Stop();
                    _suite60HzRecorder?.Dispose();
                    _suite60HzRecorder = null;
                    StopReplayIncidentIndexRecordModeLocked("suite_cancel");
                    EmitSuiteLifecycleEvent("sdk_capture_suite_cancelled", "Suite cancelled.", "T_cancel");

                    // Sentry: finish spans/transaction as cancelled
                    _sentryCurrentSpan?.Finish(SpanStatus.Cancelled);
                    _sentryCurrentSpan = null;
                    _sentryTx?.Finish(SpanStatus.Cancelled);
                    _sentryTx = null;

                    _suitePhase = DataCaptureSuitePhase.Cancelled;
                }
                return;
            }

            if (_suitePhase == DataCaptureSuitePhase.Idle && _suiteStartRequested)
            {
                _suiteStartRequested = false;
                BeginDataCaptureSuite();
                return;
            }

            if (_suitePhase == DataCaptureSuitePhase.Running)
            {
                TickSuiteRunning();
                return;
            }

            if (_suitePhase == DataCaptureSuitePhase.AwaitingLoki)
                TickAwaitingLoki();
        }

        public DataCaptureSuiteSnapshot BuildDataCaptureSuiteSnapshot()
        {
            var snap = new DataCaptureSuiteSnapshot
            {
                Phase           = _suitePhase.ToString().ToLower(),
                TestRunId       = _suiteTestRunId ?? "",
                ElapsedMs       = _suiteStopwatch?.ElapsedMilliseconds ?? 0,
                TestResults     = _suiteResults,
                CurrentStep     = (int)_suiteStep,
                CurrentStepName = _suitePhase == DataCaptureSuitePhase.Running
                    ? _suiteStep.ToString()
                    : _suitePhase.ToString().ToLower(),
            };

            if (!string.IsNullOrEmpty(_suiteTestRunId) && !string.IsNullOrEmpty(_grafanaBaseUrl))
            {
                snap.GrafanaExploreUrl = LokiQueryClient.BuildGrafanaExploreUrl(_grafanaBaseUrl, _suiteTestRunId);
                if (_suiteResults != null)
                {
                    foreach (var r in _suiteResults)
                    {
                        if (!string.IsNullOrEmpty(r.EventName))
                            r.GrafanaEventUrl = LokiQueryClient.BuildGrafanaExploreUrl(_grafanaBaseUrl, _suiteTestRunId, r.EventName);
                    }
                }
            }

            // Selected incidents summary for dashboard Test Cases panel
            if (_suiteGroundTruth != null)
            {
                var summaries = new List<SelectedIncidentSummary>();
                for (int i = 0; i < _suiteGroundTruth.Length; i++)
                {
                    var gt = _suiteGroundTruth[i];
                    if (gt == null) continue;
                    string reason = "first_available";
                    if (_suiteSelectedFrames != null && i < _suiteSelectedFrames.Length)
                    {
                        // Determine selection reason based on scan candidates
                        var usedLaps = new HashSet<int>();
                        for (int j = 0; j < i; j++)
                        {
                            if (_suiteGroundTruth[j] != null) usedLaps.Add(_suiteGroundTruth[j].LapNum);
                        }
                        reason = gt.LapNum > DataCaptureSuiteConstants.T0_MinLapForSelection && !usedLaps.Contains(gt.LapNum)
                            ? "different_lap" : "fallback";
                    }
                    summaries.Add(new SelectedIncidentSummary
                    {
                        Index      = i,
                        Frame      = gt.ReplayFrameNum,
                        Lap        = gt.LapNum,
                        DriverName = gt.DriverName,
                        CarNumber  = gt.CarNumber,
                        CustId     = gt.CustId,
                        Reason     = reason
                    });
                }
                if (summaries.Count > 0)
                    snap.SelectedIncidents = summaries.ToArray();
            }

            return snap;
        }

        // ── Skip helper ──────────────────────────────────────────────────────

        private bool TrySkipTest(string testId, SuiteInternalStep nextStep)
        {
            if (!_suiteSkipList.Contains(testId)) return false;
            var r = SuiteResult(testId);
            if (r != null) r.Status = "skip";
            _suiteStep = nextStep;
            return true;
        }

        // ── Preflight state machine ───────────────────────────────────────────

        private static PreflightMiniTest[] BuildPreflightMiniTests()
        {
            return new[]
            {
                new PreflightMiniTest { Id = "PC_WS",        Name = "WebSocket connected",    Level = 1 },
                new PreflightMiniTest { Id = "PC_PLUGIN",    Name = "Plugin responding",       Level = 1 },
                new PreflightMiniTest { Id = "PC_SIMHUB",    Name = "SimHub HTTP server",      Level = 1 },
                new PreflightMiniTest { Id = "PC_GRAFANA",   Name = "Grafana/Loki configured", Level = 1 },
                new PreflightMiniTest { Id = "PC_IRACING",   Name = "iRacing connected",       Level = 1 },
                new PreflightMiniTest { Id = "PC_REPLAY",    Name = "Replay mode active",      Level = 1 },
                new PreflightMiniTest { Id = "PC_SESSIONS",  Name = "Session map",              Level = 1 },
                new PreflightMiniTest { Id = "PC_CHECKERED", Name = "Session completed",       Level = 2 },
                new PreflightMiniTest { Id = "PC_RESULTS",   Name = "Results populated",       Level = 2 },
                new PreflightMiniTest { Id = "PC_LOKI_RT",   Name = "Loki roundtrip",          Level = 3 },
            };
        }

        private PreflightMiniTest PfTest(string id) =>
            Array.Find(_preflightSnapshot.MiniTests ?? Array.Empty<PreflightMiniTest>(), t => t.Id == id);

        private void BeginPreflight()
        {
            // Always run all levels in one pass
            int targetLevel = 3;

            // Generate correlation ID on first run or reset
            if (string.IsNullOrEmpty(_preflightCorrelationId))
                _preflightCorrelationId = Guid.NewGuid().ToString("D");

            // Build mini-tests (keep existing results for lower levels if re-running)
            if (_preflightSnapshot.MiniTests == null || _preflightLevel == 0)
                _preflightSnapshot.MiniTests = BuildPreflightMiniTests();

            // Auto-detect replay scope from session data
            _preflightReplayScope = IsReplaySessionCompleted() ? "full" : "partial";

            _preflightSnapshot.Phase = "running";
            _preflightSnapshot.CorrelationId = _preflightCorrelationId;
            _preflightSnapshot.ReplayScope = _preflightReplayScope;
            _preflightSavedFrame = SafeGetInt("ReplayFrameNum");
            _preflightSettleTicks = 0;
            _preflightLevel = targetLevel;
            _preflightSnapshot.Level = targetLevel;

            // Mark tests at current level as "running", deeper levels as "pending"
            foreach (var t in _preflightSnapshot.MiniTests)
            {
                if (t.Level == targetLevel) t.Status = "running";
                else if (t.Level > targetLevel) t.Status = "pending";
                // Keep lower-level results as-is
            }

            _preflightStep = PreflightStep.Level1_Check;
        }

        private void TickPreflight()
        {
            switch (_preflightStep)
            {
                // ── Level 1: passive checks ──────────────────────────────────────
                case PreflightStep.Level1_Check:
                {
                    bool irsdkOk = _irsdk != null && _irsdk.IsConnected;
                    string simMode = "";
                    try { simMode = _irsdk?.Data?.SessionInfo?.WeekendInfo?.SimMode ?? ""; } catch { }
                    bool replayOk = string.Equals(simMode, "replay", StringComparison.OrdinalIgnoreCase);

                    SetPfTest("PC_WS",      true,  "Plugin-side always true");  // WS is checked dashboard-side; plugin always passes
                    SetPfTest("PC_PLUGIN",   true,  "Plugin responding");
                    SetPfTest("PC_SIMHUB",   _simHubHttpListening, _simHubHttpListening ? "HTTP 8888 listening" : "HTTP 8888 not detected");
                    SetPfTest("PC_GRAFANA",  !string.IsNullOrEmpty(_lokiBaseUrl), string.IsNullOrEmpty(_lokiBaseUrl) ? "lokiBaseUrl not set" : _lokiBaseUrl);
                    SetPfTest("PC_IRACING",  irsdkOk, irsdkOk ? "SDK connected" : "SDK not connected");
                    SetPfTest("PC_REPLAY",   replayOk, replayOk ? "SimMode=replay" : "SimMode=" + simMode);

                    // Session map from YAML
                    var sessionList = ReadSessionListFromYaml();
                    _preflightSnapshot.Sessions = sessionList;
                    _preflightSnapshot.ReplayFrameTotal = _replayFrameTotal;
                    bool hasSessions = sessionList != null && sessionList.Length > 0;
                    SetPfTest("PC_SESSIONS", hasSessions,
                        hasSessions ? sessionList.Length + " session(s): " + string.Join(", ", sessionList.Select(s => s.SessionType))
                                    : "No sessions found in YAML");

                    // Legacy flat fields
                    _preflightSnapshot.SimHubOk = _simHubHttpListening;
                    _preflightSnapshot.GrafanaOk = !string.IsNullOrEmpty(_lokiBaseUrl);

                    if (_preflightLevel == 1)
                    {
                        CompletePreflight();
                        return;
                    }

                    // Check L1 pass — if any L1 test failed, stop here
                    if (!AllLevelPassed(1))
                    {
                        CompletePreflight();
                        return;
                    }

                    // Mark L2 tests as running
                    foreach (var t in _preflightSnapshot.MiniTests)
                        if (t.Level == 2) t.Status = "running";

                    // Handle partial replay scope — skip L2 seek checks
                    if (_preflightReplayScope == "partial")
                    {
                        SetPfTest("PC_CHECKERED", true, "skip");
                        PfTest("PC_CHECKERED").Status = "skip";
                        SetPfTest("PC_RESULTS", true, "skip");
                        PfTest("PC_RESULTS").Status = "skip";

                        if (_preflightLevel == 2)
                        {
                            CompletePreflight();
                            return;
                        }
                        // Jump to L3
                        foreach (var t in _preflightSnapshot.MiniTests)
                            if (t.Level == 3) t.Status = "running";
                        _preflightStep = PreflightStep.Level3_EmitProbe;
                        return;
                    }

                    // Seek to end of replay using ReplaySearch(ToEnd) — more reliable than
                    // frame-based seek (ReplayFrameNumEnd can be 0 or stale, which would
                    // seek to frame 0 and read SessionState at replay start instead of end).
                    _preflightSettleTicks = 0;
                    try
                    {
                        _irsdk.ReplaySearch(IRacingSdkEnum.RpySrchMode.ToEnd);
                    }
                    catch (Exception ex)
                    {
                        SentrySdk.CaptureException(ex);
                        SetPfTest("PC_CHECKERED", false, "seek_failed: " + ex.Message);
                        SetPfTest("PC_RESULTS", false, "seek_failed");
                        _preflightSnapshot.Error = "seek_failed: " + ex.Message;
                        CompletePreflight();
                        return;
                    }
                    _preflightStep = PreflightStep.Level2_SettleEnd;
                    break;
                }

                // ── Level 2: seek to end, read session state ─────────────────────
                case PreflightStep.Level2_SettleEnd:
                {
                    _preflightSettleTicks++;
                    int frame = SafeGetInt("ReplayFrameNum");
                    // ReplaySearch(ToEnd) is fire-and-forget; we don't have an exact target frame.
                    // Settle when: near ReplayFrameNumEnd (if valid) OR after 60 ticks (1s min wait).
                    bool nearEnd = _replayFrameTotal > 0 && frame >= _replayFrameTotal - 60;
                    if (nearEnd || _preflightSettleTicks >= 60 || _preflightSettleTicks > 300)
                    {
                        int sessionState = 0;
                        try { sessionState = _irsdk.Data.GetInt("SessionState"); } catch { }
                        bool checkeredOk = sessionState >= 6;
                        bool resultsOk = CheckResultsPositionsPopulated();

                        _preflightSnapshot.SessionStateAtEnd = sessionState;
                        _preflightSnapshot.CheckeredOk = checkeredOk;
                        _preflightSnapshot.ResultsPopulated = resultsOk;

                        SetPfTest("PC_CHECKERED", checkeredOk, checkeredOk ? "SessionState=" + sessionState : "SessionState=" + sessionState + " (need >=6)");
                        SetPfTest("PC_RESULTS", resultsOk, resultsOk ? "ResultsPositions found" : "No ResultsPositions");

                        // Restore saved frame
                        try { _irsdk.ReplaySetPlayPosition(IRacingSdkEnum.RpyPosMode.Begin, _preflightSavedFrame); }
                        catch { }
                        _preflightSettleTicks = 0;
                        _preflightStep = PreflightStep.Level2_SettleRestore;
                    }
                    break;
                }

                case PreflightStep.Level2_SettleRestore:
                {
                    _preflightSettleTicks++;
                    if (_preflightSettleTicks > 10)
                    {
                        if (_preflightLevel == 2 || !AllLevelPassed(2))
                        {
                            CompletePreflight();
                            return;
                        }
                        // Advance to L3
                        foreach (var t in _preflightSnapshot.MiniTests)
                            if (t.Level == 3) t.Status = "running";
                        _preflightStep = PreflightStep.Level3_EmitProbe;
                    }
                    break;
                }

                // ── Level 3: Loki roundtrip probe ────────────────────────────────
                case PreflightStep.Level3_EmitProbe:
                {
                    _preflightProbeNonce = Guid.NewGuid().ToString("N").Substring(0, 12);
                    _preflightProbeEmitNs = LokiQueryClient.NowNs();

                    // Emit probe event to Loki
                    var fields = new Dictionary<string, object>
                    {
                        ["preflight_correlation_id"] = _preflightCorrelationId,
                        ["probe_nonce"] = _preflightProbeNonce,
                        ["domain"] = "test",
                        ["testing"] = "true",
                    };
                    MergeSessionAndRoutingFields(fields);
                    _logger?.Structured("INFO", "simhub-plugin",
                        DataCaptureSuiteConstants.EventPreflightProbe,
                        "Preflight probe for Loki roundtrip check.", fields, "test", null);

                    _preflightProbeWaitTicks = 0;
                    _preflightStep = PreflightStep.Level3_WaitProbe;
                    break;
                }

                case PreflightStep.Level3_WaitProbe:
                {
                    _preflightProbeWaitTicks++;
                    // Wait ~3 seconds (180 ticks at 60Hz) for Loki ingestion
                    if (_preflightProbeWaitTicks >= 180)
                    {
                        _preflightLokiProbeResult = -2; // reset
                        string nonce = _preflightProbeNonce;
                        string lokiUrl = _lokiReadUrl ?? _lokiBaseUrl;
                        long startNs = _preflightProbeEmitNs;
                        long endNs = LokiQueryClient.NowNs();
                        string user = Environment.GetEnvironmentVariable("SIMSTEWARD_LOKI_USER")?.Trim() ?? "";
                        string pass = Environment.GetEnvironmentVariable("CURSOR_ELEVATED_GRAFANA_TOKEN")?.Trim() ?? "";

                        System.Threading.Tasks.Task.Run(async () =>
                        {
                            try
                            {
                                string logql = $"{{app=\"sim-steward\"}}|json|probe_nonce=\"{nonce}\"";
                                int count = await LokiQueryClient.CountMatchingAsync(lokiUrl, logql, startNs, endNs, user, pass).ConfigureAwait(false);
                                _preflightLokiProbeResult = count;
                            }
                            catch
                            {
                                _preflightLokiProbeResult = -1;
                            }
                        });
                        _preflightStep = PreflightStep.Level3_QueryProbe;
                    }
                    break;
                }

                case PreflightStep.Level3_QueryProbe:
                {
                    int result = _preflightLokiProbeResult;
                    if (result == -2) return; // still waiting for async Task

                    bool ok = result > 0;
                    string detail = result == -1 ? "Loki query error"
                                  : result == 0  ? "Probe not found in Loki"
                                  : $"Probe found ({result} match)";
                    SetPfTest("PC_LOKI_RT", ok, detail);
                    CompletePreflight();
                    break;
                }
            }
        }

        private void SetPfTest(string id, bool pass, string detail)
        {
            var t = PfTest(id);
            if (t == null) return;
            // Don't overwrite a "skip" status
            if (t.Status == "skip") return;
            t.Status = pass ? "pass" : "fail";
            t.Detail = detail;
        }

        private bool AllLevelPassed(int level)
        {
            if (_preflightSnapshot.MiniTests == null) return false;
            foreach (var t in _preflightSnapshot.MiniTests)
            {
                if (t.Level > level) continue;
                if (t.Status != "pass" && t.Status != "skip") return false;
            }
            return true;
        }

        private void CompletePreflight()
        {
            // Determine allPassed: all tests at completed levels must be pass or skip
            bool allPassed = true;
            foreach (var t in _preflightSnapshot.MiniTests)
            {
                if (t.Level > _preflightLevel) continue;
                if (t.Status != "pass" && t.Status != "skip") { allPassed = false; break; }
            }
            _preflightSnapshot.AllPassed = allPassed;
            _preflightSnapshot.Phase = "complete";
            _preflightStep = PreflightStep.Complete;

            // Emit structured log
            var fields = new Dictionary<string, object>
            {
                ["preflight_correlation_id"] = _preflightCorrelationId ?? "",
                ["level"] = _preflightLevel,
                ["replay_scope"] = _preflightReplayScope,
                ["all_passed"] = allPassed,
                ["domain"] = "test",
                ["testing"] = "true",
            };
            foreach (var t in _preflightSnapshot.MiniTests)
            {
                if (t.Level <= _preflightLevel)
                    fields["pc_" + t.Id.ToLower()] = t.Status;
            }
            MergeSessionAndRoutingFields(fields);
            _logger?.Structured("INFO", "simhub-plugin",
                DataCaptureSuiteConstants.EventPreflightCheck,
                $"Preflight L{_preflightLevel} complete. all_passed={allPassed}", fields, "test", null);
        }

        private bool CheckResultsPositionsPopulated()
        {
            try
            {
                var sessionInfo = _irsdk?.Data?.SessionInfo;
                if (!(sessionInfo?.SessionInfo?.Sessions is IList list)) return false;
                foreach (var o in list)
                {
                    if (o == null) continue;
                    var t = o.GetType();
                    var typeProp = t.GetProperty("SessionType");
                    if (!string.Equals(typeProp?.GetValue(o)?.ToString(), "Race", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var resultsProp = t.GetProperty("ResultsPositions");
                    var results = resultsProp?.GetValue(o);
                    if (results is IList resultsList && resultsList.Count > 0) return true;
                }
            }
            catch { }
            return false;
        }

        // ── Suite init ────────────────────────────────────────────────────────

        private void InitSuiteResults()
        {
            _suiteResults = new[]
            {
                new DataCaptureSuiteTestResult { TestId = "T0",  Name = "Ground Truth Capture",    EventName = DataCaptureSuiteConstants.EventGroundTruth },
                new DataCaptureSuiteTestResult { TestId = "T1",  Name = "Speed Sweep Detection",   EventName = DataCaptureSuiteConstants.EventSpeedSample },
                new DataCaptureSuiteTestResult { TestId = "T2",  Name = "Variable Inventory",      EventName = DataCaptureSuiteConstants.EventVariableInventory },
                new DataCaptureSuiteTestResult { TestId = "T3",  Name = "Player Data Snapshot",    EventName = DataCaptureSuiteConstants.EventPlayerSnapshot },
                new DataCaptureSuiteTestResult { TestId = "T4",  Name = "Driver Roster",           EventName = DataCaptureSuiteConstants.EventDriverRoster },
                new DataCaptureSuiteTestResult { TestId = "T5",  Name = "Camera Switch",           EventName = DataCaptureSuiteConstants.EventCameraSwitchDriver },
                new DataCaptureSuiteTestResult { TestId = "T5b", Name = "Camera View Cycle",       EventName = DataCaptureSuiteConstants.EventCameraViewSample },
                new DataCaptureSuiteTestResult { TestId = "T6",  Name = "Session Results",         EventName = DataCaptureSuiteConstants.EventSessionResults },
                new DataCaptureSuiteTestResult { TestId = "T7",  Name = "Incident Re-Seek",        EventName = DataCaptureSuiteConstants.EventIncidentReseek },
                new DataCaptureSuiteTestResult { TestId = "T8",    Name = "FF Sweep",                EventName = DataCaptureSuiteConstants.EventFfSweepResult },
                new DataCaptureSuiteTestResult { TestId = "T_DISC", Name = "Data Point Discovery",  EventName = DataCaptureSuiteConstants.EventDataDiscovery },
            };

            // Append T_60Hz only when feature flag is set
            if (_suite60HzEnabled)
            {
                var list = new List<DataCaptureSuiteTestResult>(_suiteResults);
                list.Add(new DataCaptureSuiteTestResult { TestId = "T_60Hz", Name = "60Hz Telemetry Dump", EventName = DataCaptureSuiteConstants.Event60HzSummary });
                _suiteResults = list.ToArray();
            }
        }

        private DataCaptureSuiteTestResult SuiteResult(string id)
            => Array.Find(_suiteResults, r => r.TestId == id);

        private void BeginDataCaptureSuite()
        {
            _suiteTestRunId           = Guid.NewGuid().ToString("D");
            _suitePreflightCorrelationId = _preflightCorrelationId ?? "";
            _suiteStopwatch           = Stopwatch.StartNew();
            _suiteGroundTruth         = new GroundTruthIncident[3];
            _suiteGroundTruthIdx      = 0;
            _suiteReseekCapture       = new GroundTruthIncident[3];
            _suiteReseekIdx           = 0;
            _suiteSpeedSweepIdx       = 0;
            _suiteFfSweepTriggered    = false;
            _suiteT8PollTicks         = 0;
            _suiteT8BuildWasRunning   = false;
            _suiteCamGroupsVisited.Clear();
            _lokiReadUrl = _lokiBaseUrl;
            _suiteDiscPositionIdx = 0;
            _suiteDiscTargetFrames = null;

            // 60Hz feature flag
            _suite60HzEnabled = string.Equals(
                Environment.GetEnvironmentVariable("SIMSTEWARD_60HZ_TEST_CAPTURE")?.Trim(), "1");
            _suite60HzRecorder?.Dispose();
            _suite60HzRecorder = null;
            if (_suite60HzEnabled)
                _suite60HzRecorder = new HighRateTelemetryRecorder(_suiteTestRunId, _pluginDataPath);

            InitSuiteResults();

            _suiteStep  = SuiteInternalStep.T0_Rewind;
            _suitePhase = DataCaptureSuitePhase.Running;

            // Sentry performance transaction for the entire suite run
            _sentryTx = SentrySdk.StartTransaction("data-capture-suite", "test.run");
            _sentryTx.SetExtra("test_run_id", _suiteTestRunId);
            SentrySdk.ConfigureScope(scope => scope.Transaction = _sentryTx);
            _sentryCurrentSpan = _sentryTx.StartChild("step", SuiteInternalStep.T0_Rewind.ToString());

            EmitSuiteLifecycleEvent(DataCaptureSuiteConstants.EventSuiteStarted,
                $"Data capture suite started. test_run_id={_suiteTestRunId}", "T_start");
            SentrySdk.AddBreadcrumb("Data capture suite started", "lifecycle",
                data: new Dictionary<string, string> { ["test_run_id"] = _suiteTestRunId });
            _logger?.Info($"DataCaptureSuite started. test_run_id={_suiteTestRunId}");
        }

        // ── Main tick dispatcher ──────────────────────────────────────────────

        private void TickSuiteRunning()
        {
            var stepBefore = _suiteStep;

            switch (_suiteStep)
            {
                case SuiteInternalStep.T0_Rewind:        TickT0_Rewind();        break;
                case SuiteInternalStep.T0_FrameZero:    TickT0_FrameZero();    break;
                case SuiteInternalStep.T0_ScanCooldown:  TickT0_ScanCooldown(); break;
                case SuiteInternalStep.T0_SeekCapture:   TickT0_SeekCapture();  break;
                case SuiteInternalStep.T0_CaptureSettle: TickT0_CaptureSettle(); break;
                case SuiteInternalStep.T1_Rewind:    TickT1_Rewind();    break;
                case SuiteInternalStep.T1_FrameZero: TickT1_FrameZero(); break;
                case SuiteInternalStep.T1_Sweep:     TickT1_Sweep();     break;
                case SuiteInternalStep.T2:           TickT2();           break;
                case SuiteInternalStep.T3:           TickT3();           break;
                case SuiteInternalStep.T4:           TickT4();           break;
                case SuiteInternalStep.T5_Switch:    TickT5_Switch();    break;
                case SuiteInternalStep.T5_Settle:    TickT5_Settle();    break;
                case SuiteInternalStep.T5b_Seek:     TickT5b_Seek();     break;
                case SuiteInternalStep.T5b_Cycle:    TickT5b_Cycle();    break;
                case SuiteInternalStep.T5b_Settle:   TickT5b_Settle();   break;
                case SuiteInternalStep.T6:           TickT6();           break;
                case SuiteInternalStep.T7_Rewind:    TickT7_Rewind();    break;
                case SuiteInternalStep.T7_FrameZero: TickT7_FrameZero(); break;
                case SuiteInternalStep.T7_Cooldown:  TickT7_Cooldown();  break;
                case SuiteInternalStep.T8_Trigger:     TickT8_Trigger();     break;
                case SuiteInternalStep.T8_Poll:        TickT8_Poll();        break;
                case SuiteInternalStep.TDISC_Seek:     TickTDISC_Seek();     break;
                case SuiteInternalStep.TDISC_Settle:   TickTDISC_Settle();   break;
                case SuiteInternalStep.TDISC_Capture:  TickTDISC_Capture();  break;
                case SuiteInternalStep.Done:           TransitionToLoki();   break;
            }

            // Sentry: finish previous span and start new one when step changes
            if (_suiteStep != stepBefore && _sentryTx != null)
            {
                _sentryCurrentSpan?.Finish(SpanStatus.Ok);
                if (_suiteStep != SuiteInternalStep.Done)
                    _sentryCurrentSpan = _sentryTx.StartChild("step", _suiteStep.ToString());
                else
                    _sentryCurrentSpan = null;
            }

            // 60Hz recording: every tick while running
            _suite60HzRecorder?.RecordTick(_irsdk);
        }

        // ── T0: Ground Truth Capture — two-pass scan/select/capture ──────────

        private void TickT0_Rewind()
        {
            if (TrySkipTest("T0", SuiteInternalStep.T1_Rewind)) return;
            SuiteResult("T0").Status = "pending";
            try
            {
                _irsdk.ReplaySetPlaySpeed(1, false);
                _irsdk.ReplaySearch(IRacingSdkEnum.RpySrchMode.ToStart);
            }
            catch (Exception ex)
            {
                SentrySdk.CaptureException(ex);
                _logger?.Warn("DataCaptureSuite T0 rewind: " + ex.Message);
            }

            StartReplayIncidentIndexRecordModeLocked("suite_t0");
            _suiteScanCandidates = new List<(int, int, int)>();
            _suiteFirstScanFrame = -1;
            _suiteFrameZeroConsecutive = 0;
            _suiteSeekTimeoutTicks     = 0;
            _suiteStep = SuiteInternalStep.T0_FrameZero;
        }

        private void TickT0_FrameZero()
        {
            _suiteSeekTimeoutTicks++;
            if (_suiteSeekTimeoutTicks > DataCaptureSuiteConstants.SeekTimeoutTicks)
            {
                SuiteResult("T0").Status = "fail";
                SuiteResult("T0").Error  = "frame_zero_timeout";
                StopReplayIncidentIndexRecordModeLocked("suite_t0_timeout");
                StartT1Rewind(0);
                return;
            }

            int frame = SafeGetInt("ReplayFrameNum");
            if (frame <= 2) _suiteFrameZeroConsecutive++;
            else            _suiteFrameZeroConsecutive = 0;

            if (_suiteFrameZeroConsecutive < DataCaptureSuiteConstants.FrameZeroStableTicks) return;

            // Frame zero stable — begin incident scan
            _suiteSeekCooldownTicks = DataCaptureSuiteConstants.NextIncidentCooldownTicks;
            try { _irsdk.ReplaySearch(IRacingSdkEnum.RpySrchMode.NextIncident); } catch { }
            _suiteStep = SuiteInternalStep.T0_ScanCooldown;
        }

        private void TickT0_ScanCooldown()
        {
            if (--_suiteSeekCooldownTicks > 0) return;

            int frame = SafeGetInt("ReplayFrameNum");
            int camCarIdx = SafeGetInt("CamCarIdx");
            int lap = -1;
            try { lap = _irsdk.Data.GetInt("CarIdxLap", camCarIdx); } catch { }

            // Detect wraparound: if we've looped back near the first scanned frame
            if (_suiteFirstScanFrame < 0) _suiteFirstScanFrame = frame;
            bool wrapped = _suiteScanCandidates.Count > 0 && frame <= _suiteFirstScanFrame + DataCaptureSuiteConstants.T0_SeekSettleTolerance;

            if (!wrapped)
                _suiteScanCandidates.Add((frame, lap, camCarIdx));

            // Stop scanning if wrapped or hit max
            if (wrapped || _suiteScanCandidates.Count >= DataCaptureSuiteConstants.T0_ScanMaxIncidents)
            {
                // Select best 3 incidents
                _suiteSelectedFrames = SelectGroundTruthFrames(_suiteScanCandidates);
                if (_suiteSelectedFrames.Length == 0)
                {
                    SuiteResult("T0").Status = "fail";
                    SuiteResult("T0").Error  = "no_incidents_found";
                    StopReplayIncidentIndexRecordModeLocked("suite_t0_no_incidents");
                    StartT1Rewind(0);
                    return;
                }
                _suiteGroundTruthIdx = 0;
                _suiteCaptureIdx = 0;
                _suiteStep = SuiteInternalStep.T0_SeekCapture;
                return;
            }

            // Scan next incident
            _suiteSeekCooldownTicks = DataCaptureSuiteConstants.NextIncidentCooldownTicks;
            try { _irsdk.ReplaySearch(IRacingSdkEnum.RpySrchMode.NextIncident); } catch { }
        }

        private void TickT0_SeekCapture()
        {
            if (_suiteCaptureIdx >= _suiteSelectedFrames.Length)
            {
                FinishT0Capture();
                return;
            }
            try { _irsdk.ReplaySetPlayPosition(IRacingSdkEnum.RpyPosMode.Begin, _suiteSelectedFrames[_suiteCaptureIdx]); }
            catch { }
            _suiteCaptureTicks = 0;
            _suiteStep = SuiteInternalStep.T0_CaptureSettle;
        }

        private void TickT0_CaptureSettle()
        {
            _suiteCaptureTicks++;
            int frame = SafeGetInt("ReplayFrameNum");
            int target = _suiteSelectedFrames[_suiteCaptureIdx];

            if (Math.Abs(frame - target) <= DataCaptureSuiteConstants.T0_SeekSettleTolerance || _suiteCaptureTicks > DataCaptureSuiteConstants.SeekTimeoutTicks)
            {
                CaptureGroundTruthIncident(_suiteCaptureIdx);
                _suiteCaptureIdx++;
                _suiteStep = SuiteInternalStep.T0_SeekCapture;
            }
        }

        private void FinishT0Capture()
        {
            int captured = Math.Min(_suiteCaptureIdx, _suiteGroundTruth.Length);
            SuiteResult("T0").Status   = "emitted";
            SuiteResult("T0").KpiLabel = "incidents_captured";
            SuiteResult("T0").KpiValue = captured.ToString();
            StopReplayIncidentIndexRecordModeLocked("suite_t0_done");
            StartT1Rewind(0);
        }

        private static int[] SelectGroundTruthFrames(List<(int frame, int lap, int carIdx)> candidates)
            => DataCaptureSuiteSelection.SelectGroundTruthFrames(candidates);

        private void CaptureGroundTruthIncident(int idx)
        {
            int camCarIdx = SafeGetInt("CamCarIdx");
            int frame     = SafeGetInt("ReplayFrameNum");
            double rst    = 0;
            try { rst = _irsdk.Data.GetDouble("ReplaySessionTime"); } catch { }

            var flags = new int[ReplayIncidentIndexBuild.CarSlotCount];
            for (int i = 0; i < flags.Length; i++)
            {
                try { flags[i] = _irsdk.Data.GetInt("CarIdxSessionFlags", i); } catch { flags[i] = 0; }
            }

            int   lap        = -1;
            float lapDistPct = 0f;
            try { lap        = _irsdk.Data.GetInt("CarIdxLap", camCarIdx); }   catch { }
            try { lapDistPct = _irsdk.Data.GetFloat("CarIdxLapDistPct", camCarIdx); } catch { }

            ResolveDriverFromCarIdx(camCarIdx, out string driverName, out string carNumber, out string custId);

            _suiteGroundTruth[idx] = new GroundTruthIncident
            {
                IncidentIndex           = idx,
                CarIdx                  = camCarIdx,
                ReplayFrameNum          = frame,
                ReplaySessionTimeSec    = rst,
                CarIdxSessionFlagsSnapshot = flags,
                DriverName              = driverName,
                CarNumber               = carNumber,
                CustId                  = custId,
                LapDistPct              = lapDistPct,
                LapNum                  = lap
            };

            var fields = BuildTestFields("T0");
            fields["incident_index"]           = idx;
            fields["car_idx"]                  = camCarIdx;
            fields["replay_frame"]             = frame;
            fields["replay_session_time_sec"]  = rst;
            fields["driver_name"]              = driverName;
            fields["car_number"]               = carNumber;
            fields["unique_user_id"]           = custId;
            fields["lap_dist_pct"]             = lapDistPct;
            fields["lap_num"]                  = lap;
            MergeSessionAndRoutingFields(fields);
            _logger?.Structured("INFO", "simhub-plugin", DataCaptureSuiteConstants.EventGroundTruth,
                $"Ground truth {idx}: car_idx={camCarIdx} frame={frame}", fields, "test", null);
        }

        // ── T1: Speed Sweep (per speed in [1,4,8,16]) ────────────────────────

        private void StartT1Rewind(int speedIdx)
        {
            if (speedIdx == 0 && TrySkipTest("T1", SuiteInternalStep.T2)) return;
            _suiteSpeedSweepIdx = speedIdx;
            if (speedIdx >= DataCaptureSuiteConstants.SpeedSweepSpeeds.Length)
            {
                SuiteResult("T1").Status = "emitted";
                _suiteStep = SuiteInternalStep.T2;
                return;
            }
            _suiteStep = SuiteInternalStep.T1_Rewind;
        }

        private void TickT1_Rewind()
        {
            try
            {
                _irsdk.ReplaySetPlaySpeed(1, false);
                _irsdk.ReplaySearch(IRacingSdkEnum.RpySrchMode.ToStart);
            }
            catch { }

            int speed = DataCaptureSuiteConstants.SpeedSweepSpeeds[_suiteSpeedSweepIdx];
            StartReplayIncidentIndexRecordModeLocked("suite_t1_speed_" + speed);
            _suiteSpeedSweepBaselineFlags = new int[ReplayIncidentIndexBuild.CarSlotCount];
            _suiteSpeedSweepDetected      = 0;
            _suiteSpeedSweepGtHits        = 0;
            _suiteSpeedSweepTicks         = 0;
            _suiteFrameZeroConsecutive    = 0;
            _suiteSeekTimeoutTicks        = 0;
            _suiteStep = SuiteInternalStep.T1_FrameZero;
        }

        private void TickT1_FrameZero()
        {
            _suiteSeekTimeoutTicks++;
            if (_suiteSeekTimeoutTicks > DataCaptureSuiteConstants.SeekTimeoutTicks)
            {
                StopReplayIncidentIndexRecordModeLocked("suite_t1_timeout");
                StartT1Rewind(_suiteSpeedSweepIdx + 1);
                return;
            }

            int frame = SafeGetInt("ReplayFrameNum");
            if (frame <= 2) _suiteFrameZeroConsecutive++;
            else            _suiteFrameZeroConsecutive = 0;

            if (_suiteFrameZeroConsecutive < DataCaptureSuiteConstants.FrameZeroStableTicks) return;

            // Capture baseline flags
            for (int i = 0; i < _suiteSpeedSweepBaselineFlags.Length; i++)
            {
                try { _suiteSpeedSweepBaselineFlags[i] = _irsdk.Data.GetInt("CarIdxSessionFlags", i); }
                catch { _suiteSpeedSweepBaselineFlags[i] = 0; }
            }

            int lastGtFrame = _suiteGroundTruth.Where(g => g != null)
                                               .Select(g => g.ReplayFrameNum)
                                               .DefaultIfEmpty(0).Max();
            _suiteSpeedSweepFrameTarget = lastGtFrame + DataCaptureSuiteConstants.SpeedSweepAdvanceFrames;

            int speed = DataCaptureSuiteConstants.SpeedSweepSpeeds[_suiteSpeedSweepIdx];
            try { _irsdk.ReplaySetPlaySpeed(speed, false); } catch { }
            _suiteStep = SuiteInternalStep.T1_Sweep;
        }

        private void TickT1_Sweep()
        {
            _suiteSpeedSweepTicks++;
            int frame = SafeGetInt("ReplayFrameNum");

            // Detect rising edges on CarIdxSessionFlags (furled or repair flag)
            for (int i = 0; i < ReplayIncidentIndexBuild.CarSlotCount; i++)
            {
                int cur;
                try { cur = _irsdk.Data.GetInt("CarIdxSessionFlags", i); }
                catch { cur = _suiteSpeedSweepBaselineFlags[i]; }

                bool prevHad = (_suiteSpeedSweepBaselineFlags[i] & DataCaptureSuiteConstants.IncidentFlagMask) != 0;
                bool curHas  = (cur & DataCaptureSuiteConstants.IncidentFlagMask) != 0;
                if (!prevHad && curHas)
                {
                    _suiteSpeedSweepDetected++;
                    if (_suiteGroundTruth.Any(g => g != null && g.CarIdx == i))
                        _suiteSpeedSweepGtHits++;
                }
                _suiteSpeedSweepBaselineFlags[i] = cur;
            }

            if (frame < _suiteSpeedSweepFrameTarget) return;

            // Speed window done
            int reqSpeed     = DataCaptureSuiteConstants.SpeedSweepSpeeds[_suiteSpeedSweepIdx];
            double effectHz  = 60.0 / reqSpeed;
            int gtCount      = _suiteGroundTruth.Count(g => g != null);
            double detRate   = gtCount > 0 ? _suiteSpeedSweepGtHits * 100.0 / gtCount : 0;

            var fields = BuildTestFields("T1");
            fields["requested_speed"]          = reqSpeed;
            fields["actual_play_speed"]        = SafeGetInt("ReplayPlaySpeed");
            fields["effective_session_hz"]     = Math.Round(effectHz, 4);
            fields["tick_count"]               = _suiteSpeedSweepTicks;
            fields["incidents_detected"]       = _suiteSpeedSweepDetected;
            fields["ground_truth_hit_count"]   = _suiteSpeedSweepGtHits;
            fields["ground_truth_miss_count"]  = Math.Max(0, gtCount - _suiteSpeedSweepGtHits);
            fields["detection_rate_pct"]       = Math.Round(detRate, 1);
            MergeSessionAndRoutingFields(fields);
            _logger?.Structured("INFO", "simhub-plugin", DataCaptureSuiteConstants.EventSpeedSample,
                $"Speed sweep {reqSpeed}x: det_rate={detRate:F1}% eff_hz={effectHz:F2}", fields, "test", null);

            SuiteResult("T1").KpiLabel = $"det_rate@{reqSpeed}x";
            SuiteResult("T1").KpiValue = $"{detRate:F1}%";

            StopReplayIncidentIndexRecordModeLocked("suite_t1_speed_done");
            StartT1Rewind(_suiteSpeedSweepIdx + 1);
        }

        // ── T2: Variable Inventory ────────────────────────────────────────────

        private void TickT2()
        {
            if (TrySkipTest("T2", SuiteInternalStep.T3)) return;
            int varCount = 0;
            try
            {
                var props = _irsdk?.Data?.GetType().GetProperty("TelemetryDataProperties")?.GetValue(_irsdk.Data);
                if (props is IEnumerable en)
                    foreach (var _ in en) varCount++;
            }
            catch { }

            var fields = BuildTestFields("T2");
            fields["variable_count"] = varCount;
            MergeSessionAndRoutingFields(fields);
            _logger?.Structured("INFO", "simhub-plugin", DataCaptureSuiteConstants.EventVariableInventory,
                $"Variable inventory: {varCount} variables.", fields, "test", null);

            SuiteResult("T2").Status   = "emitted";
            SuiteResult("T2").KpiLabel = "variable_count";
            SuiteResult("T2").KpiValue = varCount.ToString();
            _suiteStep = SuiteInternalStep.T3;
        }

        // ── T3: Player Data Snapshot ──────────────────────────────────────────

        private void TickT3()
        {
            if (TrySkipTest("T3", SuiteInternalStep.T4)) return;
            double speed = 0, rpm = 0; int gear = 0; float lapDistPct = 0;
            try { speed      = _irsdk.Data.GetDouble("Speed"); }         catch { }
            try { rpm        = _irsdk.Data.GetDouble("RPM"); }           catch { }
            try { gear       = _irsdk.Data.GetInt("Gear"); }             catch { }
            try { lapDistPct = _irsdk.Data.GetFloat("LapDistPct"); }     catch { }

            var fields = BuildTestFields("T3");
            fields["speed_mps"]    = speed;
            fields["rpm"]          = rpm;
            fields["gear"]         = gear;
            fields["lap_dist_pct"] = lapDistPct;
            fields["note"]         = "player_car_only";
            MergeSessionAndRoutingFields(fields);
            _logger?.Structured("INFO", "simhub-plugin", DataCaptureSuiteConstants.EventPlayerSnapshot,
                $"Player snapshot: speed={speed:F1}m/s gear={gear}", fields, "test", null);

            SuiteResult("T3").Status = "emitted";
            _suiteStep = SuiteInternalStep.T4;
        }

        // ── T4: Driver Roster ─────────────────────────────────────────────────

        private void TickT4()
        {
            if (TrySkipTest("T4", SuiteInternalStep.T5_Switch)) return;
            var driverList = _irsdk?.Data?.SessionInfo?.DriverInfo?.Drivers as IList;
            int driverCount = driverList?.Count ?? 0;
            int gtCarsFound = 0;
            if (driverList != null)
            {
                foreach (var d in driverList)
                {
                    if (d == null) continue;
                    var t      = d.GetType();
                    var idxObj = t.GetProperty("CarIdx")?.GetValue(d);
                    int carIdx = idxObj is int ci ? ci : Convert.ToInt32(idxObj ?? -1);
                    if (_suiteGroundTruth.Any(g => g != null && g.CarIdx == carIdx))
                        gtCarsFound++;
                }
            }

            var fields = BuildTestFields("T4");
            fields["driver_count"]  = driverCount;
            fields["gt_cars_found"] = gtCarsFound;
            MergeSessionAndRoutingFields(fields);
            _logger?.Structured("INFO", "simhub-plugin", DataCaptureSuiteConstants.EventDriverRoster,
                $"Driver roster: {driverCount} drivers, {gtCarsFound} GT cars.", fields, "test", null);

            SuiteResult("T4").Status   = "emitted";
            SuiteResult("T4").KpiLabel = "driver_count";
            SuiteResult("T4").KpiValue = driverCount.ToString();

            // Seek to GT0 position for T5 camera tests
            if (_suiteGroundTruth[0] != null)
            {
                int sessionNum     = SafeGetInt("SessionNum");
                int sessionTimeMs  = (int)(_suiteGroundTruth[0].ReplaySessionTimeSec * 1000);
                try { _irsdk.ReplaySearchSessionTime(sessionNum, sessionTimeMs); } catch { }
            }
            _suiteCamSettleTicks = DataCaptureSuiteConstants.CamSettleTicks;
            _suiteStep = SuiteInternalStep.T5_Switch;
        }

        // ── T5: Camera Switch ─────────────────────────────────────────────────

        private void TickT5_Switch()
        {
            if (TrySkipTest("T5", SuiteInternalStep.T5b_Seek)) return;
            if (_suiteGroundTruth[0] == null)
            {
                SuiteResult("T5").Status = "skip";
                SuiteResult("T5").Error  = "no_ground_truth";
                _suiteStep = SuiteInternalStep.T5b_Seek;
                return;
            }

            try
            {
                _irsdk.CamSwitchPos(IRacingSdkEnum.CamSwitchMode.FocusAtDriver,
                    _suiteGroundTruth[0].CarIdx, 0, 0);
            }
            catch { }

            _suiteCamSettleTicks = DataCaptureSuiteConstants.CamSettleTicks;
            _suiteStep = SuiteInternalStep.T5_Settle;
        }

        private void TickT5_Settle()
        {
            if (--_suiteCamSettleTicks > 0) return;

            int camCarIdx      = SafeGetInt("CamCarIdx");
            int camGroup       = SafeGetInt("CamGroupNumber");
            if (camGroup == 0) camGroup = SafeGetInt("CameraGroupNumber");
            string camGroupName = ResolveCameraGroupNumToName(camGroup);
            bool confirmed     = _suiteGroundTruth[0] != null && camCarIdx == _suiteGroundTruth[0].CarIdx;

            var fields = BuildTestFields("T5");
            fields["cam_car_idx"]      = camCarIdx;
            fields["expected_car_idx"] = _suiteGroundTruth[0]?.CarIdx ?? -1;
            fields["confirmed_match"]  = confirmed;
            fields["cam_group_num"]    = camGroup;
            fields["cam_group_name"]   = camGroupName;
            MergeSessionAndRoutingFields(fields);
            _logger?.Structured("INFO", "simhub-plugin", DataCaptureSuiteConstants.EventCameraSwitchDriver,
                $"Camera switch: cam_car_idx={camCarIdx} confirmed={confirmed}", fields, "test", null);

            SuiteResult("T5").Status   = "emitted";
            SuiteResult("T5").KpiLabel = "confirmed";
            SuiteResult("T5").KpiValue = confirmed.ToString().ToLower();
            _suiteStep = SuiteInternalStep.T5b_Seek;
        }

        // ── T5b: Camera View Cycle ────────────────────────────────────────────

        private void TickT5b_Seek()
        {
            if (TrySkipTest("T5b", SuiteInternalStep.T6)) return;
            if (_suiteGroundTruth[0] != null)
            {
                int sessionNum    = SafeGetInt("SessionNum");
                int sessionTimeMs = (int)(_suiteGroundTruth[0].ReplaySessionTimeSec * 1000);
                try
                {
                    _irsdk.ReplaySearchSessionTime(sessionNum, sessionTimeMs);
                    _irsdk.ReplaySetPlaySpeed(0, false);
                }
                catch { }
            }

            _suiteCameraGroups = GetAllCameraGroups();
            _suiteCameraGroupIdx    = 0;
            _suiteCamConfirmedMatches = 0;
            _suiteCamGroupsVisited.Clear();

            if (_suiteCameraGroups.Count == 0)
            {
                SuiteResult("T5b").Status = "skip";
                SuiteResult("T5b").Error  = "no_camera_groups";
                _suiteStep = SuiteInternalStep.T6;
                return;
            }

            StartReplayIncidentIndexRecordModeLocked("suite_t5b");
            _suiteStep = SuiteInternalStep.T5b_Cycle;
        }

        private void TickT5b_Cycle()
        {
            if (_suiteCameraGroupIdx >= _suiteCameraGroups.Count)
            {
                StopReplayIncidentIndexRecordModeLocked("suite_t5b_done");

                var sf = BuildTestFields("T5b");
                sf["groups_tested"]       = _suiteCameraGroups.Count;
                sf["confirmed_matches"]   = _suiteCamConfirmedMatches;
                sf["group_names"]         = _suiteCamGroupsVisited.ToArray();
                MergeSessionAndRoutingFields(sf);
                _logger?.Structured("INFO", "simhub-plugin", DataCaptureSuiteConstants.EventCameraViewSummary,
                    $"Camera view cycle: {_suiteCameraGroups.Count} groups, {_suiteCamConfirmedMatches} confirmed.", sf, "test", null);

                SuiteResult("T5b").Status   = "emitted";
                SuiteResult("T5b").KpiLabel = "groups_tested";
                SuiteResult("T5b").KpiValue = _suiteCameraGroups.Count.ToString();
                _suiteStep = SuiteInternalStep.T6;
                return;
            }

            var (groupNum, groupName) = _suiteCameraGroups[_suiteCameraGroupIdx];
            int carIdx = _suiteGroundTruth[0]?.CarIdx ?? 0;
            try { _irsdk.CamSwitchPos(IRacingSdkEnum.CamSwitchMode.FocusAtDriver, carIdx, groupNum, 0); }
            catch { }

            _suiteCamSettleTicks = DataCaptureSuiteConstants.CamSettleTicks;
            _suiteStep = SuiteInternalStep.T5b_Settle;
        }

        private void TickT5b_Settle()
        {
            if (--_suiteCamSettleTicks > 0) return;

            int camCarIdx  = SafeGetInt("CamCarIdx");
            int camGroupNum = SafeGetInt("CamGroupNumber");
            if (camGroupNum == 0) camGroupNum = SafeGetInt("CameraGroupNumber");
            int camCamNum  = SafeGetInt("CamCameraNumber");

            var (expectedGroup, expectedGroupName) = _suiteCameraGroups[_suiteCameraGroupIdx];
            int expectedCar = _suiteGroundTruth[0]?.CarIdx ?? -1;
            bool confirmed  = camCarIdx == expectedCar;
            if (confirmed) _suiteCamConfirmedMatches++;
            _suiteCamGroupsVisited.Add(expectedGroupName);

            // Per-car arrays for GT0 car
            int ci = expectedCar >= 0 ? expectedCar : 0;
            int carLap = -1, carPos = -1, carGear = -1; float carRpm = 0, carLdp = 0; int carFlags = 0, trackSurf = -1;
            try { carLap   = _irsdk.Data.GetInt("CarIdxLap", ci); }              catch { }
            try { carPos   = _irsdk.Data.GetInt("CarIdxPosition", ci); }         catch { }
            try { carGear  = _irsdk.Data.GetInt("CarIdxGear", ci); }             catch { }
            try { carRpm   = _irsdk.Data.GetFloat("CarIdxRPM", ci); }            catch { }
            try { carLdp   = _irsdk.Data.GetFloat("CarIdxLapDistPct", ci); }     catch { }
            try { carFlags = _irsdk.Data.GetInt("CarIdxSessionFlags", ci); }     catch { }
            try { trackSurf= _irsdk.Data.GetInt("CarIdxTrackSurface", ci); }     catch { }

            var fields = BuildTestFields("T5b");
            fields["cam_group_num"]               = expectedGroup;
            fields["cam_group_name"]              = expectedGroupName;
            fields["cam_car_idx"]                 = camCarIdx;
            fields["cam_camera_number"]           = camCamNum;
            fields["confirmed_match"]             = confirmed;
            fields["ground_truth_incident_index"] = 0;
            fields["car_idx_lap"]                 = carLap;
            fields["car_idx_position"]            = carPos;
            fields["car_idx_gear"]                = carGear;
            fields["car_idx_rpm"]                 = carRpm;
            fields["car_idx_lap_dist_pct"]        = carLdp;
            fields["car_idx_session_flags"]       = carFlags;
            fields["car_idx_track_surface"]       = trackSurf;
            MergeSessionAndRoutingFields(fields);
            _logger?.Structured("INFO", "simhub-plugin", DataCaptureSuiteConstants.EventCameraViewSample,
                $"Camera view sample: group={expectedGroupName} car_idx={camCarIdx}", fields, "test", null);

            _suiteCameraGroupIdx++;
            _suiteStep = SuiteInternalStep.T5b_Cycle;
        }

        private List<(int groupNum, string groupName)> GetAllCameraGroups()
        {
            var result = new List<(int, string)>();
            try
            {
                if (!(_irsdk?.Data?.SessionInfo?.CameraInfo?.Groups is IList groups)) return result;
                foreach (var g in groups)
                {
                    if (g == null) continue;
                    var gt      = g.GetType();
                    var numProp = gt.GetProperty("GroupNum");
                    var nameProp = gt.GetProperty("GroupName");
                    if (numProp == null || nameProp == null) continue;
                    result.Add((Convert.ToInt32(numProp.GetValue(g)), nameProp.GetValue(g)?.ToString() ?? ""));
                }
            }
            catch { }
            return result;
        }

        // ── T6: Session Results ───────────────────────────────────────────────

        private void TickT6()
        {
            if (TrySkipTest("T6", SuiteInternalStep.T7_Rewind)) return;
            int subId      = _irsdk?.Data?.SessionInfo?.WeekendInfo?.SubSessionID ?? 0;
            int sessionNum = SafeGetInt("SessionNum");
            string yaml    = _irsdk?.Data?.SessionInfoYaml ?? "";

            ReplayIncidentIndexResultsYaml.TryParseOfficialIncidentsByCarIdx(
                yaml, sessionNum,
                out Dictionary<int, int> byCarIdx,
                out int _,
                out string _);

            int gtCarsInResults = _suiteGroundTruth
                .Where(g => g != null && byCarIdx != null && byCarIdx.ContainsKey(g.CarIdx))
                .Count();

            var fields = BuildTestFields("T6");
            fields["result_entries"]      = byCarIdx?.Count ?? 0;
            fields["gt_cars_in_results"]  = gtCarsInResults;
            fields["subsession_id"]       = subId;
            MergeSessionAndRoutingFields(fields);
            _logger?.Structured("INFO", "simhub-plugin", DataCaptureSuiteConstants.EventSessionResults,
                $"Session results: {byCarIdx?.Count ?? 0} entries, {gtCarsInResults} GT cars.", fields, "test", null);

            SuiteResult("T6").Status   = "emitted";
            SuiteResult("T6").KpiLabel = "gt_cars_in_results";
            SuiteResult("T6").KpiValue = gtCarsInResults.ToString();

            // Rewind for T7
            try
            {
                _irsdk.ReplaySetPlaySpeed(1, false);
                _irsdk.ReplaySearch(IRacingSdkEnum.RpySrchMode.ToStart);
            }
            catch { }
            _suiteFrameZeroConsecutive = 0;
            _suiteSeekTimeoutTicks     = 0;
            _suiteReseekIdx            = 0;
            _suiteStep = SuiteInternalStep.T7_Rewind;
        }

        // ── T7: Incident Re-Seek Validation ──────────────────────────────────

        private void TickT7_Rewind()
        {
            if (TrySkipTest("T7", SuiteInternalStep.T8_Trigger)) return;
            // Rewind was issued in TickT6; just reset counters and wait for frame zero
            _suiteFrameZeroConsecutive = 0;
            _suiteSeekTimeoutTicks     = 0;
            _suiteStep = SuiteInternalStep.T7_FrameZero;
        }

        private void TickT7_FrameZero()
        {
            _suiteSeekTimeoutTicks++;
            if (_suiteSeekTimeoutTicks > DataCaptureSuiteConstants.SeekTimeoutTicks)
            {
                SuiteResult("T7").Status = "fail";
                SuiteResult("T7").Error  = "frame_zero_timeout";
                _suiteStep = SuiteInternalStep.T8_Trigger;
                return;
            }

            int frame = SafeGetInt("ReplayFrameNum");
            if (frame <= 2) _suiteFrameZeroConsecutive++;
            else            _suiteFrameZeroConsecutive = 0;

            if (_suiteFrameZeroConsecutive < DataCaptureSuiteConstants.FrameZeroStableTicks) return;

            _suiteReseekIdx         = 0;
            _suiteSeekCooldownTicks = DataCaptureSuiteConstants.NextIncidentCooldownTicks;
            try { _irsdk.ReplaySearch(IRacingSdkEnum.RpySrchMode.NextIncident); } catch { }
            _suiteStep = SuiteInternalStep.T7_Cooldown;
        }

        private void TickT7_Cooldown()
        {
            if (--_suiteSeekCooldownTicks > 0) return;

            int frame     = SafeGetInt("ReplayFrameNum");
            int camCarIdx = SafeGetInt("CamCarIdx");
            _suiteReseekCapture[_suiteReseekIdx] = new GroundTruthIncident
            {
                IncidentIndex  = _suiteReseekIdx,
                CarIdx         = camCarIdx,
                ReplayFrameNum = frame,
            };
            _suiteReseekIdx++;

            if (_suiteReseekIdx < 3)
            {
                _suiteSeekCooldownTicks = DataCaptureSuiteConstants.NextIncidentCooldownTicks;
                try { _irsdk.ReplaySearch(IRacingSdkEnum.RpySrchMode.NextIncident); } catch { }
                return;
            }

            // All 3 reseeks done — compare against ground truth
            int matches = 0;
            for (int i = 0; i < 3; i++)
            {
                var gt = _suiteGroundTruth[i];
                var rs = _suiteReseekCapture[i];
                if (gt != null && rs != null && Math.Abs(rs.ReplayFrameNum - gt.ReplayFrameNum) <= 60)
                    matches++;
            }

            var fields = BuildTestFields("T7");
            fields["matches_within_60_frames"] = matches;
            fields["total_reseeks"]            = 3;
            fields["reseek_frames"]            = new[] { _suiteReseekCapture[0]?.ReplayFrameNum ?? 0, _suiteReseekCapture[1]?.ReplayFrameNum ?? 0, _suiteReseekCapture[2]?.ReplayFrameNum ?? 0 };
            fields["gt_frames"]                = new[] { _suiteGroundTruth[0]?.ReplayFrameNum ?? 0,  _suiteGroundTruth[1]?.ReplayFrameNum ?? 0,  _suiteGroundTruth[2]?.ReplayFrameNum ?? 0 };
            MergeSessionAndRoutingFields(fields);
            _logger?.Structured("INFO", "simhub-plugin", DataCaptureSuiteConstants.EventIncidentReseek,
                $"Incident re-seek: {matches}/3 within ±60 frames.", fields, "test", null);

            SuiteResult("T7").Status   = "emitted";
            SuiteResult("T7").KpiLabel = "matches";
            SuiteResult("T7").KpiValue = matches + "/3";
            _suiteStep = SuiteInternalStep.T8_Trigger;
        }

        // ── T8: FF Sweep (trigger existing replay index build) ────────────────

        private void TickT8_Trigger()
        {
            if (TrySkipTest("T8", SuiteInternalStep.Done)) return;
            if (_suiteFfSweepTriggered) { _suiteStep = SuiteInternalStep.T8_Poll; return; }
            _suiteFfSweepTriggered  = true;
            _suiteT8PollTicks       = 0;
            _suiteT8BuildWasRunning = false;

            var (success, _, err) = DispatchReplayIncidentIndexBuild("start", _suiteTestRunId);
            if (!success)
            {
                SuiteResult("T8").Status = "fail";
                SuiteResult("T8").Error  = err ?? "trigger_failed";
                _suiteStep = SuiteInternalStep.TDISC_Seek;
                return;
            }
            _suiteStep = SuiteInternalStep.T8_Poll;
        }

        private void TickT8_Poll()
        {
            _suiteT8PollTicks++;

            ReplayIndexBuildPhase buildPhase;
            lock (_replayIndexBuildLock) { buildPhase = _replayIndexBuildPhase; }

            if (buildPhase != ReplayIndexBuildPhase.Idle) { _suiteT8BuildWasRunning = true; return; }

            // Timeout at 60s (3600 ticks at 60Hz)
            if (_suiteT8PollTicks > 3600)
            {
                SuiteResult("T8").Status = "fail";
                SuiteResult("T8").Error  = "timeout";
                _suiteStep = SuiteInternalStep.TDISC_Seek;
                return;
            }

            // Haven't started yet
            if (!_suiteT8BuildWasRunning && _suiteT8PollTicks < 30) return;

            // Build completed — cross-ref GT cars
            int gtCarsInIndex = 0;
            var indexRoot = _replayIndexDashboardCachedRoot;
            if (indexRoot?.Incidents != null)
            {
                foreach (var gt in _suiteGroundTruth)
                {
                    if (gt == null) continue;
                    if (indexRoot.Incidents.Exists(inc => inc.CarIdx == gt.CarIdx))
                        gtCarsInIndex++;
                }
            }

            var fields = BuildTestFields("T8");
            fields["gt_cars_in_index"]          = gtCarsInIndex;
            fields["total_incidents_in_index"]  = indexRoot?.Incidents?.Count ?? 0;
            fields["poll_ticks"]                = _suiteT8PollTicks;
            MergeSessionAndRoutingFields(fields);
            _logger?.Structured("INFO", "simhub-plugin", DataCaptureSuiteConstants.EventFfSweepResult,
                $"FF sweep: {gtCarsInIndex} GT cars in index.", fields, "test", null);

            SuiteResult("T8").Status   = "emitted";
            SuiteResult("T8").KpiLabel = "gt_cars_in_index";
            SuiteResult("T8").KpiValue = gtCarsInIndex.ToString();
            _suiteStep = SuiteInternalStep.TDISC_Seek;
        }

        // ── T_DISC: Data Point Discovery ─────────────────────────────────────

        private static readonly string[] DiscPositionNames = { "frame_zero", "mid_race", "at_incident", "end_of_replay" };

        private void TickTDISC_Seek()
        {
            if (TrySkipTest("T_DISC", SuiteInternalStep.Done)) return;

            // Compute target frames on first entry
            if (_suiteDiscTargetFrames == null)
            {
                int incidentFrame = _suiteGroundTruth?[0]?.ReplayFrameNum ?? (_replayFrameTotal * 3 / 4);
                _suiteDiscTargetFrames = new[]
                {
                    0,
                    Math.Max(1, _replayFrameTotal / 2),
                    incidentFrame,
                    Math.Max(0, _replayFrameTotal - 10)
                };
            }

            if (_suiteDiscPositionIdx >= _suiteDiscTargetFrames.Length)
            {
                // All positions captured
                int captured = _suiteDiscPositionIdx;
                SuiteResult("T_DISC").Status   = "emitted";
                SuiteResult("T_DISC").KpiLabel = "positions_captured";
                SuiteResult("T_DISC").KpiValue = captured.ToString();
                _suiteStep = SuiteInternalStep.Done;
                return;
            }

            int target = _suiteDiscTargetFrames[_suiteDiscPositionIdx];
            try { _irsdk.ReplaySetPlayPosition(IRacingSdkEnum.RpyPosMode.Begin, target); } catch { }
            _suiteDiscSettleTicks = 0;
            _suiteStep = SuiteInternalStep.TDISC_Settle;
        }

        private void TickTDISC_Settle()
        {
            _suiteDiscSettleTicks++;
            int frame = SafeGetInt("ReplayFrameNum");
            int target = _suiteDiscTargetFrames[_suiteDiscPositionIdx];

            if (Math.Abs(frame - target) <= DataCaptureSuiteConstants.T0_SeekSettleTolerance || _suiteDiscSettleTicks > 300)
            {
                _suiteStep = SuiteInternalStep.TDISC_Capture;
            }
        }

        private void TickTDISC_Capture()
        {
            string posName = _suiteDiscPositionIdx < DiscPositionNames.Length
                ? DiscPositionNames[_suiteDiscPositionIdx] : "unknown";
            int frame = SafeGetInt("ReplayFrameNum");

            var fields = BuildTestFields("T_DISC");
            fields["position"]      = posName;
            fields["position_idx"]  = _suiteDiscPositionIdx;
            fields["frame"]         = frame;

            // Read SessionState
            int sessionState = 0;
            try { sessionState = _irsdk.Data.GetInt("SessionState"); } catch { }
            fields["session_state"] = sessionState;

            // Read Tier 1 + 2 variables: report populated counts for CarIdx arrays
            fields["CarIdxTrackSurface_populated"]    = CountPopulated("CarIdxTrackSurface");
            fields["CarIdxPosition_populated"]        = CountPopulated("CarIdxPosition");
            fields["CarIdxLap_populated"]             = CountPopulated("CarIdxLap");
            fields["CarIdxSessionFlags_populated"]    = CountPopulated("CarIdxSessionFlags");
            fields["CarIdxOnPitRoad_populated"]       = CountPopulatedBool("CarIdxOnPitRoad");
            fields["CarIdxTrackSurfaceMaterial_populated"] = CountPopulated("CarIdxTrackSurfaceMaterial");
            fields["CarIdxClassPosition_populated"]   = CountPopulated("CarIdxClassPosition");

            // Focused-car telemetry
            float latAccel = 0f, lonAccel = 0f, yawRate = 0f;
            try { latAccel = _irsdk.Data.GetFloat("LatAccel"); }  catch { }
            try { lonAccel = _irsdk.Data.GetFloat("LonAccel"); }  catch { }
            try { yawRate  = _irsdk.Data.GetFloat("YawRate"); }   catch { }
            fields["LatAccel_available"]  = latAccel != 0f;
            fields["LonAccel_available"]  = lonAccel != 0f;
            fields["YawRate_available"]   = yawRate != 0f;

            // YAML: ResultsPositions
            fields["ResultsPositions_populated"] = CheckResultsPositionsPopulated();

            MergeSessionAndRoutingFields(fields);
            _logger?.Structured("INFO", "simhub-plugin", DataCaptureSuiteConstants.EventDataDiscovery,
                $"Data discovery at {posName} (frame={frame}, state={sessionState})", fields, "test", null);

            _suiteDiscPositionIdx++;
            _suiteStep = SuiteInternalStep.TDISC_Seek;
        }

        private int CountPopulated(string carIdxVar)
        {
            int count = 0;
            for (int i = 0; i < ReplayIncidentIndexBuild.CarSlotCount; i++)
            {
                try { if (_irsdk.Data.GetInt(carIdxVar, i) != 0) count++; } catch { }
            }
            return count;
        }

        private int CountPopulatedBool(string carIdxVar)
        {
            int count = 0;
            for (int i = 0; i < ReplayIncidentIndexBuild.CarSlotCount; i++)
            {
                try { if (_irsdk.Data.GetBool(carIdxVar, i)) count++; } catch { }
            }
            return count;
        }

        // ── Loki verification ─────────────────────────────────────────────────

        private void TransitionToLoki()
        {
            // Finalize 60Hz recorder
            if (_suite60HzRecorder != null)
            {
                var stats = _suite60HzRecorder.Finish();
                var r60 = SuiteResult("T_60Hz");
                if (r60 != null)
                {
                    r60.Status   = "emitted";
                    r60.KpiLabel = "ticks_recorded";
                    r60.KpiValue = stats.ticksRecorded.ToString();
                }
                var f60 = BuildTestFields("T_60Hz");
                f60["ticks_recorded"]   = stats.ticksRecorded;
                f60["file_size_bytes"]  = stats.fileSizeBytes;
                f60["duration_sec"]     = stats.durationSec;
                f60["file_path"]        = _suite60HzRecorder.FilePath;
                MergeSessionAndRoutingFields(f60);
                _logger?.Structured("INFO", "simhub-plugin", DataCaptureSuiteConstants.Event60HzSummary,
                    $"60Hz capture: {stats.ticksRecorded} ticks, {stats.fileSizeBytes / 1024}KB.", f60, "test", null);
                _suite60HzRecorder.Dispose();
                _suite60HzRecorder = null;
            }

            _suiteEmitCompleteUtc = DateTime.UtcNow;
            _suitePhase = DataCaptureSuitePhase.AwaitingLoki;

            // Sentry: finish any remaining span and the transaction
            _sentryCurrentSpan?.Finish(SpanStatus.Ok);
            _sentryCurrentSpan = null;
            _sentryTx?.Finish(SpanStatus.Ok);
            _sentryTx = null;

            var fields = BuildTestFields("T_done");
            fields["loki_wait_ms"] = DataCaptureSuiteConstants.LokiVerifyDelayMs;
            MergeSessionAndRoutingFields(fields);
            _logger?.Structured("INFO", "simhub-plugin", DataCaptureSuiteConstants.EventSuiteComplete,
                "Suite complete — awaiting Loki ingestion.", fields, "test", null);
        }

        private void TickAwaitingLoki()
        {
            if ((DateTime.UtcNow - _suiteEmitCompleteUtc).TotalMilliseconds < DataCaptureSuiteConstants.LokiVerifyDelayMs)
                return;
            RunLokiVerificationAsync();
        }

        private void RunLokiVerificationAsync()
        {
            if (string.IsNullOrEmpty(_lokiReadUrl))
            {
                foreach (var r in _suiteResults)
                    if (r.Status == "emitted") r.Status = "pass";
                _suitePhase = DataCaptureSuitePhase.Complete;
                return;
            }

            long startNs  = LokiQueryClient.NowMinusMs(3_600_000L);
            long endNs    = LokiQueryClient.NowNs();
            string user   = Environment.GetEnvironmentVariable("SIMSTEWARD_LOKI_USER")?.Trim() ?? "";
            string pass   = Environment.GetEnvironmentVariable("CURSOR_ELEVATED_GRAFANA_TOKEN")?.Trim() ?? "";
            string runId  = _suiteTestRunId;
            var results   = _suiteResults;

            System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    foreach (var r in results)
                    {
                        if (r.Status != "emitted") continue;
                        var q     = LokiQueryClient.BuildTestRunQuery(runId, r.EventName);
                        var lines = await LokiQueryClient.QueryLinesAsync(_lokiReadUrl, q, startNs, endNs, user, pass).ConfigureAwait(false);
                        r.LokiCount = lines.Count;
                        if (lines.Count == 0)
                        {
                            r.Status = "fail";
                            r.Error  = "not_found_in_loki";
                        }
                        else
                        {
                            r.Status = "found";
                            var (ok, failReason) = ValidateTestContent(r.TestId, lines);
                            r.Status = ok ? "pass" : "fail";
                            if (!ok) r.Error = failReason;
                        }
                    }
                }
                catch { }
                _suitePhase = DataCaptureSuitePhase.Complete;
            });
        }

        /// <summary>
        /// Two-stage content validation per test. Returns (pass, failReason).
        /// Stage 1 (found) already confirmed count > 0 before this is called.
        /// </summary>
        private static (bool pass, string failReason) ValidateTestContent(string testId, List<Newtonsoft.Json.Linq.JObject> lines)
        {
            switch (testId)
            {
                case "T0":
                    return lines.Count >= 3
                        ? (true, null)
                        : (false, $"expected>=3_got_{lines.Count}");
                case "T1":
                    return lines.Count >= 4
                        ? (true, null)
                        : (false, $"expected>=4_speeds_got_{lines.Count}");
                case "T2":
                    return lines.Any(j => j["variable_count"] != null)
                        ? (true, null)
                        : (false, "missing_variable_count");
                case "T3":
                    return lines.Any(j => !string.IsNullOrEmpty(j["driver_name"]?.ToString()))
                        ? (true, null)
                        : (false, "missing_driver_name");
                case "T4":
                {
                    bool ok = lines.Any(j => int.TryParse(j["driver_count"]?.ToString(), out int dc) && dc > 0);
                    return ok ? (true, null) : (false, "driver_count_zero_or_missing");
                }
                case "T5":
                    return lines.Any(j => j["cam_group_num"] != null)
                        ? (true, null)
                        : (false, "missing_cam_group_num");
                case "T5b":
                    return lines.Any(j => j["camera_group_name"] != null)
                        ? (true, null)
                        : (false, "missing_camera_group_name");
                case "T6":
                    return (true, null); // existence is sufficient
                case "T7":
                    return lines.Count >= 3
                        ? (true, null)
                        : (false, $"expected>=3_reseeks_got_{lines.Count}");
                case "T8":
                {
                    bool ok = lines.Any(j => int.TryParse(j["gt_cars_in_index"]?.ToString(), out int g) && g >= 1);
                    return ok ? (true, null) : (false, "gt_cars_in_index<1");
                }
                case "T_DISC":
                    return lines.Count >= 4
                        ? (true, null)
                        : (false, $"expected>=4_positions_got_{lines.Count}");
                case "T_60Hz":
                {
                    bool ok = lines.Any(j => int.TryParse(j["ticks_recorded"]?.ToString(), out int t) && t > 0);
                    return ok ? (true, null) : (false, "ticks_recorded_zero");
                }
                default:
                    return (true, null);
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private Dictionary<string, object> BuildTestFields(string testTag)
        {
            return new Dictionary<string, object>
            {
                ["test_run_id"] = _suiteTestRunId ?? "",
                ["preflight_correlation_id"] = _suitePreflightCorrelationId ?? "",
                ["test_tag"]    = testTag,
                ["domain"]      = "test",
                ["testing"]     = "true",
            };
        }

        private void EmitSuiteLifecycleEvent(string eventName, string message, string testTag)
        {
            var fields = BuildTestFields(testTag);
            MergeSessionAndRoutingFields(fields);
            _logger?.Structured("INFO", "simhub-plugin", eventName, message, fields, "test", null);
        }

        private void ResolveDriverFromCarIdx(int carIdx, out string driverName, out string carNumber, out string custId)
        {
            driverName = ""; carNumber = ""; custId = "";
            try
            {
                if (!(_irsdk?.Data?.SessionInfo?.DriverInfo?.Drivers is IList list)) return;
                foreach (var d in list)
                {
                    if (d == null) continue;
                    var t      = d.GetType();
                    var idxObj = t.GetProperty("CarIdx")?.GetValue(d);
                    int idx    = idxObj is int ci ? ci : Convert.ToInt32(idxObj ?? -1);
                    if (idx != carIdx) continue;
                    driverName = t.GetProperty("UserName")?.GetValue(d)?.ToString() ?? "";
                    carNumber  = t.GetProperty("CarNumber")?.GetValue(d)?.ToString() ?? "";
                    var uid    = t.GetProperty("UserID")?.GetValue(d) ?? t.GetProperty("CustID")?.GetValue(d);
                    custId     = uid?.ToString() ?? "";
                    return;
                }
            }
            catch { }
        }
    }
}
#endif
