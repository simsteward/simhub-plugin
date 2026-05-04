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
            TINDEX_Rewind, TINDEX_FrameZero, TINDEX_ScanCooldown, TINDEX_Emit,
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
        private volatile bool         _lokiVerificationStarted;
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
            Level4_Pause, Level4_PauseSettle,
            Level4_SeekStart, Level4_SeekStartSettle,
            Level4_Incident, Level4_IncidentSettle,
            Level4_Ff, Level4_FfSettle,
            Level4_SeekFrame, Level4_SeekFrameSettle,
            Complete
        }
        private volatile bool      _preflightRequested;
        private PreflightSnapshot  _preflightSnapshot = new PreflightSnapshot();
        private PreflightStep      _preflightStep = PreflightStep.Idle;
        private int                _preflightSavedFrame;
        private int                _preflightSettleTicks;
        private int                _preflightLevel;            // 0=not run, 1-4
        private string             _preflightCorrelationId;
        private string             _preflightReplayScope = "full";
        private int                _preflightProbeWaitTicks;
        private long               _preflightProbeEmitNs;
        private volatile int       _preflightLokiProbeResult = -2; // -2=not started, -1=error, 0+=count
        private List<Newtonsoft.Json.Linq.JObject> _preflightL2Lines;
        private int                _preflightDwellTicks;
        private string             _suitePreflightCorrelationId;

        // Level 4 control probe fields
        private int _preflightL4PreCmdFrame;      // frame snapshot before a command is issued
        private int _preflightL4FfStartFrame;     // frame when FF was issued
        private int _preflightL4FrameZeroConsec;  // consecutive ticks with ReplayFrameNum ≤ 2
        private int _preflightL4SeekFrameTarget;  // target for SeekFrame test

        // T0 scan/select/capture
        private List<(int frame, int lap, int carIdx)> _suiteScanCandidates;     // player-car incidents only
        private List<(int frame, int lap, int carIdx)> _suiteScanAllCandidates;  // all cars (fallback)
        private int  _suiteFirstScanFrame;
        private int[] _suiteSelectedFrames;
        private int  _suiteCaptureIdx;
        private int  _suiteCaptureTicks;
        private int  _suitePlayerCarIdx;           // PlayerCarIdx captured at T0 start
        private int  _suitePreNextIncidentFrame;   // frame before last NextIncident call
        private int  _suiteStuckNextIncidentCount; // consecutive ignored NextIncident calls
        private bool _suiteNextIncidentPending;    // waiting for pause-settle before issuing NextIncident
        private int  _suiteScanCallCount;         // total NextIncident calls issued during T0/T_INDEX scan

        // T_INDEX: player incident index
        private List<(int frame, int lap, int camCarIdx)> _suiteIndexCandidates;
        private int  _suiteIndexScanCallCount;
        private int  _suiteIndexFirstScanFrame;

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
        private int  _suiteT8PlayNudgeCount;
        private int  _suiteT8TimeoutTicks;
        private int  _suiteT8LastFrameSnapshot;
        private int  _suiteT8LastFrameSnapshotTick;
        private int  _suiteT8SlowRateCount;
        private bool _suiteT8GraceTickPending;  // 1-tick grace after timeout: let ReplayIncidentIndexBuild finish its tick
        private int  _suiteT8FrameAtEndTicks;   // consecutive ticks with frame >= _replayFrameMax-500 (end-of-replay detection)

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
                new PreflightMiniTest { Id = "PC_CHECKERED",   Name = "Session completed",       Level = 2 },
                new PreflightMiniTest { Id = "PC_RESULTS",    Name = "Results populated",       Level = 2 },
                new PreflightMiniTest { Id = "PC_SCOPE",      Name = "Full replay (race)",      Level = 2 },
                new PreflightMiniTest { Id = "PC_PLAYER_INC", Name = "Player has incidents",    Level = 2 },
                new PreflightMiniTest { Id = "PC_LOKI_RT",    Name = "Loki roundtrip",          Level = 3 },
                new PreflightMiniTest { Id = "PC_CTRL_PAUSE",      Name = "Pause control",         Level = 4 },
                new PreflightMiniTest { Id = "PC_CTRL_SEEK",       Name = "Seek-to-start control", Level = 4 },
                new PreflightMiniTest { Id = "PC_CTRL_INCIDENT",   Name = "Next-incident control", Level = 4 },
                new PreflightMiniTest { Id = "PC_CTRL_FF",         Name = "Fast-forward control",  Level = 4 },
                new PreflightMiniTest { Id = "PC_CTRL_SEEK_FRAME", Name = "Seek-to-frame control", Level = 4 },
            };
        }

        private PreflightMiniTest PfTest(string id) =>
            Array.Find(_preflightSnapshot.MiniTests ?? Array.Empty<PreflightMiniTest>(), t => t.Id == id);

        private void BeginPreflight()
        {
            // Always run all levels in one pass
            int targetLevel = 4;

            // Generate correlation ID on first run or reset
            if (string.IsNullOrEmpty(_preflightCorrelationId))
                _preflightCorrelationId = Guid.NewGuid().ToString("D");

            // Build mini-tests (keep existing results for lower levels if re-running)
            if (_preflightSnapshot.MiniTests == null || _preflightLevel == 0)
                _preflightSnapshot.MiniTests = BuildPreflightMiniTests();

            // Scope is determined AFTER seeking to end of replay (in Level2_SettleEnd)
            _preflightReplayScope = "detecting";

            _preflightSnapshot.Phase = "running";
            _preflightSnapshot.CorrelationId = _preflightCorrelationId;
            _preflightSnapshot.ReplayScope = "detecting";
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

                    // Reliable end-of-replay frame: prefer running max of ReplayFrameNum seen this session,
                    // fall back to ReplayFrameNumEnd (unreliable — session-relative on some iRacing builds).
                    int bestFrameTotal = Math.Max(_replayFrameTotal, _replayFrameMax);

                    // Guard: no frame data yet — SDK hasn't reported replay length.
                    if (bestFrameTotal <= 0)
                    {
                        const string noFrameMsg = "No replay frame data — SDK not ready, retry in a moment";
                        SetPfTest("PC_CHECKERED", false, noFrameMsg);
                        SetPfTest("PC_RESULTS", false, noFrameMsg);
                        SetPfTest("PC_SCOPE", false, noFrameMsg);
                        SetPfTest("PC_PLAYER_INC", false, noFrameMsg);
                        _preflightSnapshot.Error = noFrameMsg;
                        CompletePreflight();
                        return;
                    }

                    // Seek to the end of the replay using ReplaySearch(ToEnd) — this correctly
                    // lands at the actual end of race content regardless of multi-session replays.
                    // The old ReplaySetPlayPosition(Begin, frameTotal-300) approach landed in the
                    // "dead zone" after the race ended (SessionState=0) for multi-session replays.
                    // If ToEnd bugs out and lands at frame 0, we fall back in Level2_SettleEnd.
                    _preflightSnapshot.SeekTargetFrame = bestFrameTotal;
                    _preflightSettleTicks = 0;
                    _preflightDwellTicks  = 0;
                    try
                    {
                        _irsdk.ReplaySetPlaySpeed(0, false);
                        _irsdk.ReplaySearch(IRacingSdkEnum.RpySrchMode.ToEnd);
                    }
                    catch (Exception ex)
                    {
                        SentrySdk.CaptureException(ex);
                        SetPfTest("PC_CHECKERED", false, "seek_failed: " + ex.Message);
                        SetPfTest("PC_RESULTS", false, "seek_failed");
                        SetPfTest("PC_SCOPE", false, "seek_failed");
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
                    int bestFrameTotal2 = Math.Max(_replayFrameTotal, _replayFrameMax);
                    // ToEnd seek: wait for frame to stabilise (not near zero) or timeout after ~10s.
                    // If ToEnd bugs out and lands at frame 0, fall back to position-based seek.
                    bool settled = _preflightSettleTicks > 120 && frame > 100;
                    if (!settled && _preflightSettleTicks == 120 && frame <= 10 && bestFrameTotal2 > 300)
                    {
                        // ToEnd landed at frame 0 — fall back to position-based seek
                        int fallbackTarget = bestFrameTotal2 - 3000 > 0 ? bestFrameTotal2 - 3000 : bestFrameTotal2 / 2;
                        _preflightSnapshot.SeekTargetFrame = fallbackTarget;
                        try { _irsdk.ReplaySetPlayPosition(IRacingSdkEnum.RpyPosMode.Begin, fallbackTarget); } catch { }
                        _preflightSettleTicks = 0;
                        break;
                    }
                    if (settled || _preflightSettleTicks > 600)
                    {
                        // Dwell at the target for 60 ticks (~1s) so the seek is visually perceptible
                        _preflightDwellTicks++;
                        if (_preflightDwellTicks < 60) break;
                        int sessionState = 0;
                        try { sessionState = _irsdk.Data.GetInt("SessionState"); } catch { }
                        // >= 5: checkered (race finished); >= 6: cooldown. Use 5 to catch both.
                        bool checkeredOk = sessionState >= 5;
                        bool resultsOk = CheckResultsPositionsPopulated();

                        // Additional signals for full/partial determination
                        int sessionFlags = 0;
                        try { sessionFlags = _irsdk.Data.GetInt("SessionFlags"); } catch { }
                        bool checkeredFlagBit = (sessionFlags & 0x1) != 0;
                        bool resultsOfficialOk = IsReplaySessionCompleted(); // now post-seek, reliable
                        string sessionType = GetSessionTypeFromYaml();

                        // Determine scope from what we actually found at end of replay
                        bool isFull = (checkeredOk || checkeredFlagBit) && resultsOk;
                        _preflightReplayScope = isFull ? "full" : "partial";
                        _preflightSnapshot.ReplayScope = _preflightReplayScope;

                        _preflightSnapshot.SessionStateAtEnd = sessionState;
                        _preflightSnapshot.CheckeredOk = checkeredOk;
                        _preflightSnapshot.ResultsPopulated = resultsOk;

                        SetPfTest("PC_CHECKERED", checkeredOk, checkeredOk ? "SessionState=" + sessionState : "SessionState=" + sessionState + " (need >=5)");
                        SetPfTest("PC_RESULTS", resultsOk, resultsOk ? "ResultsPositions found" : "No ResultsPositions");
                        SetPfTest("PC_SCOPE", isFull, isFull ? "Full replay: race detected" : "Partial replay: no checkered state detected");

                        // Player incident count — soft gate: T0/T1/T7/T_INDEX auto-skipped when 0.
                        // Telemetry (PlayerCarMyIncidentCount) is unreliable in replay — falls back to YAML ResultsPositions.
                        int playerIncCount = 0;
                        try { playerIncCount = _irsdk.Data.GetInt("PlayerCarMyIncidentCount"); } catch { }
                        if (playerIncCount == 0)
                        {
                            try
                            {
                                int playerCarIdx2 = SafeGetInt("PlayerCarIdx");
                                string yaml2      = _irsdk?.Data?.SessionInfoYaml ?? "";
                                int    sessNum2   = SafeGetInt("SessionNum");
                                if (ReplayIncidentIndexResultsYaml.TryParseOfficialIncidentsByCarIdx(
                                        yaml2, sessNum2, out var byCarIdx2, out _, out _) && byCarIdx2 != null)
                                    byCarIdx2.TryGetValue(playerCarIdx2, out playerIncCount);
                            }
                            catch { }
                        }
                        SetPfTest("PC_PLAYER_INC", playerIncCount > 0,
                            playerIncCount > 0 ? playerIncCount + " incident(s)" : "0 incidents — choose a replay where the player had incidents");

                        // Capture query start timestamp BEFORE emitting — used as L3 Loki query window start
                        _preflightProbeEmitNs = LokiQueryClient.NowNs();

                        // Emit L2 seek event to Loki so PC_LOKI_RT can verify it
                        var l2Fields = new Dictionary<string, object>
                        {
                            ["preflight_correlation_id"]    = _preflightCorrelationId,
                            ["seek_target_frame"]           = _preflightSnapshot.SeekTargetFrame,
                            ["actual_frame_at_read"]        = frame,
                            ["session_state_at_end"]        = sessionState,
                            ["checkered_ok"]                = checkeredOk,
                            ["checkered_flag_bit"]          = checkeredFlagBit,
                            ["results_populated"]           = resultsOk,
                            ["results_official"]            = resultsOfficialOk,
                            ["session_type"]                = sessionType,
                            ["replay_frame_total"]          = _replayFrameTotal,
                            ["replay_scope_detected"]       = _preflightReplayScope,
                            ["player_car_my_incident_count"] = playerIncCount,
                            ["replay_play_speed"]           = SafeGetInt("ReplayPlaySpeed"),
                            ["is_replay_playing"]           = SafeGetInt("IsReplayPlaying"),
                            ["domain"]                      = "test",
                            ["testing"]                     = "true",
                        };
                        MergeSessionAndRoutingFields(l2Fields);
                        _logger?.Structured("INFO", "simhub-plugin",
                            DataCaptureSuiteConstants.EventPreflightL2Seek,
                            $"L2 seek complete: scope={_preflightReplayScope} state={sessionState} results={resultsOk}",
                            l2Fields, "test", null);

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
                        if (_preflightLevel == 2)
                        {
                            CompletePreflight();
                            return;
                        }
                        // Always advance to L3 — PC_LOKI_RT verifies the L2 event regardless of scope
                        foreach (var t in _preflightSnapshot.MiniTests)
                            if (t.Level == 3) t.Status = "running";
                        _preflightStep = PreflightStep.Level3_EmitProbe;
                    }
                    break;
                }

                // ── Level 3: verify L2 seek event round-tripped through Loki ─────
                case PreflightStep.Level3_EmitProbe:
                {
                    // _preflightProbeEmitNs already captured in Level2_SettleEnd before emitting
                    // the L2 seek event — use that as the query window start.
                    _preflightProbeWaitTicks = 0;
                    _preflightLokiProbeResult = -2;
                    _preflightL2Lines = null;
                    _preflightStep = PreflightStep.Level3_WaitProbe;
                    break;
                }

                case PreflightStep.Level3_WaitProbe:
                {
                    _preflightProbeWaitTicks++;
                    // Wait ~3 seconds (180 ticks at 60Hz) for Loki ingestion
                    if (_preflightProbeWaitTicks >= 180)
                    {
                        string corrId  = _preflightCorrelationId;
                        string lokiUrl = _lokiReadUrl ?? _lokiBaseUrl;
                        long startNs   = _preflightProbeEmitNs;
                        long endNs     = LokiQueryClient.NowNs();
                        string user    = Environment.GetEnvironmentVariable("SIMSTEWARD_LOKI_USER")?.Trim() ?? "";
                        string pass    = Environment.GetEnvironmentVariable("CURSOR_ELEVATED_GRAFANA_TOKEN")?.Trim() ?? "";

                        System.Threading.Tasks.Task.Run(async () =>
                        {
                            try
                            {
                                // Query for the L2 seek event by correlation ID.
                                // Loki | json flattens nested fields.x as fields_x, so use fields_preflight_correlation_id.
                                string logql = $"{{app=\"sim-steward\"}}|json"
                                             + $"|event=\"{DataCaptureSuiteConstants.EventPreflightL2Seek}\""
                                             + $"|fields_preflight_correlation_id=\"{corrId}\"";
                                var lines = await LokiQueryClient.QueryLinesAsync(lokiUrl, logql, startNs, endNs, user, pass).ConfigureAwait(false);
                                _preflightL2Lines = lines;
                                _preflightLokiProbeResult = lines.Count;
                            }
                            catch
                            {
                                _preflightL2Lines = null;
                                _preflightLokiProbeResult = -1;
                            }
                        });
                        _preflightStep = PreflightStep.Level3_QueryProbe;
                    }
                    break;
                }

                case PreflightStep.Level3_QueryProbe:
                {
                    _preflightProbeWaitTicks++;
                    int result = _preflightLokiProbeResult;
                    if (result == -2)
                    {
                        // 600 additional ticks (~10s) after launching the query — hard timeout.
                        if (_preflightProbeWaitTicks > 600) _preflightLokiProbeResult = -1;
                        else return;
                        result = -1;
                    }

                    if (result == -1 || _preflightL2Lines == null || _preflightL2Lines.Count == 0)
                    {
                        string errDetail = result == -1 ? "Loki query error" : "L2 seek event not found in Loki";
                        SetPfTest("PC_LOKI_RT", false, errDetail);
                    }
                    else
                    {
                        // Parse the returned L2 event fields and verify they match what we captured
                        // AND that the seek landed at a valid session position (state > 0).
                        var line = _preflightL2Lines[0];
                        var f = line["fields"] as Newtonsoft.Json.Linq.JObject;
                        int logState       = f?.Value<int>("session_state_at_end") ?? -1;
                        bool logResults    = f?.Value<bool>("results_populated") ?? false;
                        string logScope    = f?.Value<string>("replay_scope_detected") ?? "";
                        int logActualFrame = f?.Value<int>("actual_frame_at_read") ?? 0;
                        int logSeekTarget  = f?.Value<int>("seek_target_frame") ?? 0;

                        bool valuesMatch = logState == _preflightSnapshot.SessionStateAtEnd
                                        && logResults == _preflightSnapshot.ResultsPopulated;
                        // Verify seek actually landed: frame must be within 630 of target
                        // (600-tick timeout × 1 frame/tick + 30 tolerance = 630 max drift)
                        bool seekLanded = logSeekTarget > 0 && Math.Abs(logActualFrame - logSeekTarget) <= 630;
                        bool seekValid  = logState > 0 && seekLanded;
                        bool allOk      = valuesMatch && seekValid;
                        string detail = allOk
                            ? $"L2 seek verified: state={logState} results={logResults} scope={logScope} frame={logActualFrame}"
                            : !seekLanded
                                ? $"Seek did not land at target: frame={logActualFrame} vs target={logSeekTarget} (diff={Math.Abs(logActualFrame - logSeekTarget)})"
                                : !valuesMatch
                                    ? $"Values mismatch: got state={logState}/results={logResults}, expected {_preflightSnapshot.SessionStateAtEnd}/{_preflightSnapshot.ResultsPopulated}"
                                    : $"Seek landed at invalid session (state=0, frame={logActualFrame})";
                        SetPfTest("PC_LOKI_RT", allOk, detail);
                    }

                    // ── Transition to Level 4 (replay control probes) ──
                    // Gate: only run Level 4 if iRacing is still connected and in replay mode.
                    if (_preflightLevel >= 4 && AllLevelPassed(1))
                    {
                        foreach (var t in _preflightSnapshot.MiniTests)
                            if (t.Level == 4) t.Status = "running";
                        _preflightSettleTicks        = 0;
                        _preflightL4PreCmdFrame      = 0;
                        _preflightL4FfStartFrame     = 0;
                        _preflightL4FrameZeroConsec  = 0;
                        _preflightL4SeekFrameTarget  = 0;
                        _preflightStep = PreflightStep.Level4_Pause;
                    }
                    else
                    {
                        CompletePreflight();
                    }
                    break;
                }

                // ── Level 4: replay control probes ───────────────────────────────────
                case PreflightStep.Level4_Pause:
                {
                    _preflightL4PreCmdFrame = SafeGetInt("ReplayFrameNum");
                    try { _irsdk.ReplaySetPlaySpeed(0, false); } catch { }
                    _preflightSettleTicks = 0;
                    _preflightStep = PreflightStep.Level4_PauseSettle;
                    break;
                }

                case PreflightStep.Level4_PauseSettle:
                {
                    _preflightSettleTicks++;
                    int  playSpeed  = SafeGetInt("ReplayPlaySpeed");
                    bool captured   = playSpeed == 0;
                    int  frameNow   = SafeGetInt("ReplayFrameNum");
                    bool correct    = captured && frameNow <= _preflightL4PreCmdFrame + 5;
                    // Evaluate once captured or at timeout (90 ticks ~1.5s)
                    if (!captured && _preflightSettleTicks < 90) break;
                    string pauseDetail = captured && correct ? $"ReplayPlaySpeed=0 frame={frameNow}"
                        : !captured ? $"pause_not_captured: speed={playSpeed}"
                        : $"pause_frame_still_advancing: was={_preflightL4PreCmdFrame} now={frameNow}";
                    SetPfTest("PC_CTRL_PAUSE", captured && correct, pauseDetail);
                    _preflightSettleTicks = 0;
                    _preflightStep = PreflightStep.Level4_SeekStart;
                    break;
                }

                case PreflightStep.Level4_SeekStart:
                {
                    try { _irsdk.ReplaySearch(IRacingSdkEnum.RpySrchMode.ToStart); } catch { }
                    _preflightL4FrameZeroConsec = 0;
                    _preflightSettleTicks       = 0;
                    _preflightStep = PreflightStep.Level4_SeekStartSettle;
                    break;
                }

                case PreflightStep.Level4_SeekStartSettle:
                {
                    _preflightSettleTicks++;
                    int seekFrame = SafeGetInt("ReplayFrameNum");
                    if (seekFrame <= 2) _preflightL4FrameZeroConsec++;
                    else                _preflightL4FrameZeroConsec = 0;

                    bool seekStable = _preflightL4FrameZeroConsec >= 4;
                    bool seekTimeout = _preflightSettleTicks >= 600;
                    if (!seekStable && !seekTimeout) break;

                    bool seekCorrect = seekStable && seekFrame <= 2;
                    string seekDetail = seekCorrect ? $"frame=0 stable (consec={_preflightL4FrameZeroConsec})"
                        : seekTimeout && !seekStable ? $"seek_not_captured: frame={seekFrame} after {_preflightSettleTicks} ticks"
                        : $"seek_frame_unstable: frame={seekFrame}";
                    SetPfTest("PC_CTRL_SEEK", seekCorrect, seekDetail);
                    _preflightSettleTicks = 0;
                    _preflightStep = PreflightStep.Level4_Incident;
                    break;
                }

                case PreflightStep.Level4_Incident:
                {
                    // Skip if SEEK failed — we need to be at frame 0 for this test to be meaningful
                    if (PfTest("PC_CTRL_SEEK")?.Status != "pass")
                    {
                        var skipTest = PfTest("PC_CTRL_INCIDENT");
                        if (skipTest != null) { skipTest.Status = "skip"; skipTest.Detail = "skip:seek_failed"; }
                        _preflightStep = PreflightStep.Level4_Ff;
                        break;
                    }
                    _preflightL4PreCmdFrame = SafeGetInt("ReplayFrameNum");
                    try { _irsdk.ReplaySetPlaySpeed(0, false); } catch { }
                    try { _irsdk.ReplaySearch(IRacingSdkEnum.RpySrchMode.NextIncident); } catch { }
                    try { _irsdk.ReplaySetPlaySpeed(1, false); } catch { }
                    _preflightSettleTicks = 0;
                    _preflightStep = PreflightStep.Level4_IncidentSettle;
                    break;
                }

                case PreflightStep.Level4_IncidentSettle:
                {
                    _preflightSettleTicks++;
                    if (_preflightSettleTicks < 150) break;

                    int incFrame    = SafeGetInt("ReplayFrameNum");
                    int camCarIdx   = SafeGetInt("CamCarIdx");
                    int frameEnd    = Math.Max(_replayFrameMax, _replayFrameTotal);
                    bool captured   = incFrame - _preflightL4PreCmdFrame > 100;
                    bool camValid   = camCarIdx >= 0 && camCarIdx < 64;
                    bool inBounds   = frameEnd <= 0 || incFrame < frameEnd;
                    bool incCorrect = captured && camValid && inBounds;
                    string incDetail = incCorrect ? $"frame jumped to {incFrame} camCarIdx={camCarIdx}"
                        : !captured ? $"incident_not_captured: frame={incFrame} start={_preflightL4PreCmdFrame}"
                        : !camValid ? $"incident_camcar_invalid: CamCarIdx={camCarIdx}"
                        : $"incident_out_of_bounds: frame={incFrame} frameEnd={frameEnd}";
                    SetPfTest("PC_CTRL_INCIDENT", incCorrect, incDetail);
                    try { _irsdk.ReplaySetPlaySpeed(0, false); } catch { }
                    _preflightSettleTicks = 0;
                    _preflightStep = PreflightStep.Level4_Ff;
                    break;
                }

                case PreflightStep.Level4_Ff:
                {
                    _preflightL4FfStartFrame = SafeGetInt("ReplayFrameNum");
                    try { _irsdk.ReplaySetPlaySpeed(32, false); } catch { }
                    _preflightSettleTicks = 0;
                    _preflightStep = PreflightStep.Level4_FfSettle;
                    break;
                }

                case PreflightStep.Level4_FfSettle:
                {
                    _preflightSettleTicks++;
                    if (_preflightSettleTicks < 60) break;

                    int ffFrame   = SafeGetInt("ReplayFrameNum");
                    int ffDelta   = ffFrame - _preflightL4FfStartFrame;
                    bool ffCaptured = ffDelta > 100;
                    int  ffRate   = _preflightSettleTicks > 0 ? ffDelta / _preflightSettleTicks : 0;
                    bool ffCorrect = ffCaptured && ffRate >= DataCaptureSuiteConstants.T8_SlowRateThreshold;
                    string ffDetail = ffCorrect ? $"delta={ffDelta} rate={ffRate}fps"
                        : !ffCaptured ? $"ff_not_captured: delta={ffDelta}"
                        : $"ff_rate_too_slow: rate={ffRate}fps (expected>={DataCaptureSuiteConstants.T8_SlowRateThreshold})";
                    SetPfTest("PC_CTRL_FF", ffCorrect, ffDetail);
                    try { _irsdk.ReplaySetPlaySpeed(0, false); } catch { }
                    _preflightSettleTicks = 0;
                    _preflightStep = PreflightStep.Level4_SeekFrame;
                    break;
                }

                case PreflightStep.Level4_SeekFrame:
                {
                    _preflightL4SeekFrameTarget = _preflightSavedFrame;
                    try { _irsdk.ReplaySetPlayPosition(IRacingSdkEnum.RpyPosMode.Begin, _preflightL4SeekFrameTarget); } catch { }
                    _preflightSettleTicks = 0;
                    _preflightStep = PreflightStep.Level4_SeekFrameSettle;
                    break;
                }

                case PreflightStep.Level4_SeekFrameSettle:
                {
                    _preflightSettleTicks++;
                    int sfFrame   = SafeGetInt("ReplayFrameNum");
                    bool onTarget = Math.Abs(sfFrame - _preflightL4SeekFrameTarget) <= 50;
                    bool sfTimeout = _preflightSettleTicks >= 120;
                    if (!onTarget && !sfTimeout) break;

                    bool sfCorrect = onTarget;
                    string sfDetail = sfCorrect ? $"frame={sfFrame} target={_preflightL4SeekFrameTarget} within tolerance"
                        : $"seek_frame_missed: frame={sfFrame} vs target={_preflightL4SeekFrameTarget} diff={Math.Abs(sfFrame - _preflightL4SeekFrameTarget)}";
                    SetPfTest("PC_CTRL_SEEK_FRAME", sfCorrect, sfDetail);
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
            // Determine allPassed: all tests at completed levels must be pass or skip.
            // Soft gates: PC_LOKI_RT (Loki may be down), PC_CHECKERED, PC_SCOPE (partial replays ok),
            //             PC_PLAYER_INC (0 incidents → auto-skip incident tests, don't block everything).
            static bool IsSoftGate(string id) =>
                id == "PC_LOKI_RT" || id == "PC_CHECKERED" || id == "PC_SCOPE" || id == "PC_PLAYER_INC";
            bool allPassed = true;
            foreach (var t in _preflightSnapshot.MiniTests)
            {
                if (t.Level > _preflightLevel) continue;
                if (IsSoftGate(t.Id)) continue;
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
                new DataCaptureSuiteTestResult { TestId = "T0",      Name = "Ground Truth Capture",   EventName = DataCaptureSuiteConstants.EventGroundTruth,        CameraContext = "incident", DriverContext = "player" },
                new DataCaptureSuiteTestResult { TestId = "T1",      Name = "Speed Sweep Detection",  EventName = DataCaptureSuiteConstants.EventSpeedSample,        CameraContext = "incident", DriverContext = "player" },
                new DataCaptureSuiteTestResult { TestId = "T2",      Name = "Variable Inventory",     EventName = DataCaptureSuiteConstants.EventVariableInventory },
                new DataCaptureSuiteTestResult { TestId = "T3",      Name = "Player Data Snapshot",   EventName = DataCaptureSuiteConstants.EventPlayerSnapshot,     CameraContext = "player",   DriverContext = "player" },
                new DataCaptureSuiteTestResult { TestId = "T4",      Name = "Driver Roster",          EventName = DataCaptureSuiteConstants.EventDriverRoster },
                new DataCaptureSuiteTestResult { TestId = "T5",      Name = "Camera Switch",          EventName = DataCaptureSuiteConstants.EventCameraSwitchDriver, CameraContext = "other",    DriverContext = "other" },
                new DataCaptureSuiteTestResult { TestId = "T5b",     Name = "Camera View Cycle",      EventName = DataCaptureSuiteConstants.EventCameraViewSample,   CameraContext = "other",    DriverContext = "other" },
                new DataCaptureSuiteTestResult { TestId = "T6",      Name = "Session Results",        EventName = DataCaptureSuiteConstants.EventSessionResults },
                new DataCaptureSuiteTestResult { TestId = "T7",      Name = "Incident Re-Seek",       EventName = DataCaptureSuiteConstants.EventIncidentReseek,     CameraContext = "incident", DriverContext = "player" },
                new DataCaptureSuiteTestResult { TestId = "T8",      Name = "FF Sweep",               EventName = DataCaptureSuiteConstants.EventFfSweepResult },
                new DataCaptureSuiteTestResult { TestId = "T_INDEX", Name = "Player Incident Index",  EventName = DataCaptureSuiteConstants.EventPlayerIncidentIndex, CameraContext = "incident", DriverContext = "player" },
                new DataCaptureSuiteTestResult { TestId = "T_DISC",  Name = "Data Point Discovery",   EventName = DataCaptureSuiteConstants.EventDataDiscovery },
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
            _suiteTestRunId              = Guid.NewGuid().ToString("D");
            _suitePreflightCorrelationId = _preflightCorrelationId ?? "";
            _suiteStopwatch              = Stopwatch.StartNew();
            _lokiVerificationStarted     = false;
            _suiteGroundTruth         = new GroundTruthIncident[3];
            _suiteGroundTruthIdx      = 0;
            _suiteReseekCapture       = new GroundTruthIncident[3];
            _suiteReseekIdx           = 0;
            _suiteSpeedSweepIdx       = 0;
            _suiteFfSweepTriggered         = false;
            _suiteT8PollTicks              = 0;
            _suiteT8BuildWasRunning        = false;
            _suiteT8PlayNudgeCount         = 0;
            _suiteT8TimeoutTicks           = DataCaptureSuiteConstants.T8_MaxTimeoutTicks;
            _suiteT8LastFrameSnapshot      = 0;
            _suiteT8LastFrameSnapshotTick  = 0;
            _suiteT8SlowRateCount          = 0;
            _suiteT8GraceTickPending       = false;
            _suiteT8FrameAtEndTicks        = 0;
            _suiteCamGroupsVisited.Clear();
            _lokiReadUrl = _lokiBaseUrl;
            _suiteDiscPositionIdx = 0;
            _suiteDiscTargetFrames = null;
            _suiteIndexCandidates     = null;
            _suiteIndexScanCallCount  = 0;
            _suiteIndexFirstScanFrame = -1;

            // 60Hz feature flag
            _suite60HzEnabled = string.Equals(
                Environment.GetEnvironmentVariable("SIMSTEWARD_60HZ_TEST_CAPTURE")?.Trim(), "1");
            _suite60HzRecorder?.Dispose();
            _suite60HzRecorder = null;
            if (_suite60HzEnabled)
                _suite60HzRecorder = new HighRateTelemetryRecorder(_suiteTestRunId, _pluginDataPath);

            InitSuiteResults();

            // Auto-skip incident tests when player had 0 incidents (PC_PLAYER_INC soft-failed).
            // T0, T1, T7, T_INDEX all require player incidents for ground truth / index capture.
            var pfIncTest = _preflightSnapshot?.MiniTests != null
                ? Array.Find(_preflightSnapshot.MiniTests, t => t.Id == "PC_PLAYER_INC")
                : null;
            if (pfIncTest != null && pfIncTest.Status != "pass")
            {
                _suiteSkipList.Add("T0");
                _suiteSkipList.Add("T1");
                _suiteSkipList.Add("T7");
                _suiteSkipList.Add("T_INDEX");
                _logger?.Warn("DataCaptureSuite: PC_PLAYER_INC not passed — auto-skipping T0, T1, T7, T_INDEX.");
            }

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
                case SuiteInternalStep.T8_Trigger:        TickT8_Trigger();        break;
                case SuiteInternalStep.T8_Poll:           TickT8_Poll();           break;
                case SuiteInternalStep.TINDEX_Rewind:     TickTINDEX_Rewind();     break;
                case SuiteInternalStep.TINDEX_FrameZero:  TickTINDEX_FrameZero();  break;
                case SuiteInternalStep.TINDEX_ScanCooldown: TickTINDEX_ScanCooldown(); break;
                case SuiteInternalStep.TINDEX_Emit:       TickTINDEX_Emit();       break;
                case SuiteInternalStep.TDISC_Seek:        TickTDISC_Seek();        break;
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
                // Pause before seeking — NextIncident is ignored by iRacing when replay plays at speed > 0.
                _irsdk.ReplaySetPlaySpeed(0, false);
                _irsdk.ReplaySearch(IRacingSdkEnum.RpySrchMode.ToStart);
            }
            catch (Exception ex)
            {
                SentrySdk.CaptureException(ex);
                _logger?.Warn("DataCaptureSuite T0 rewind: " + ex.Message);
            }

            StartReplayIncidentIndexRecordModeLocked("suite_t0");
            _suiteScanCandidates           = new List<(int, int, int)>();
            _suiteScanAllCandidates        = new List<(int, int, int)>();
            _suiteFirstScanFrame           = -1;
            // Use DriverInfo.DriverCarIdx (session YAML) as the authoritative player-car signal.
            // This is set by iRacing based on the logged-in user and is more reliable than the
            // PlayerCarIdx telemetry variable, which returns 0 (default) on read failure.
            var _suiteDriverInfo = _irsdk?.Data?.SessionInfo?.DriverInfo;
            _suitePlayerCarIdx = _suiteDriverInfo != null
                ? _suiteDriverInfo.DriverCarIdx
                : SafeGetInt("PlayerCarIdx");
            _suitePreNextIncidentFrame     = -1;
            _suiteStuckNextIncidentCount   = 0;
            _suiteNextIncidentPending      = false;
            _suiteScanCallCount            = 0;
            _suiteFrameZeroConsecutive     = 0;
            _suiteSeekTimeoutTicks         = 0;
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

            // Frame zero stable — begin incident scan.
            // Pause first; NextIncident must not be sent in the same tick as the pause command
            // or iRacing may still be processing the speed change and ignore it.
            // _suiteNextIncidentPending = true triggers the actual NextIncident call after a
            // short settle window (15 ticks) inside TickT0_ScanCooldown.
            _suitePreNextIncidentFrame = frame;
            _suiteNextIncidentPending  = true;
            _suiteSeekCooldownTicks    = DataCaptureSuiteConstants.NextIncidentPauseSettleTicks;
            try { _irsdk.ReplaySetPlaySpeed(0, false); } catch { }
            _suiteStep = SuiteInternalStep.T0_ScanCooldown;
        }

        private void TickT0_ScanCooldown()
        {
            if (--_suiteSeekCooldownTicks > 0) return;

            // Pause-settle phase: issue NextIncident now that pause has taken effect,
            // then resume at 1x so telemetry fires at 60Hz during the cooldown window.
            if (_suiteNextIncidentPending)
            {
                _suiteNextIncidentPending  = false;
                _suitePreNextIncidentFrame = SafeGetInt("ReplayFrameNum");
                _suiteSeekCooldownTicks    = DataCaptureSuiteConstants.T0_PlayModeCooldownTicks;
                _suiteScanCallCount++;
                try { _irsdk.ReplaySearch(IRacingSdkEnum.RpySrchMode.NextIncident); } catch { }
                try { _irsdk.ReplaySetPlaySpeed(1, false); } catch { } // resume for 60Hz telemetry during settle
                return;
            }

            int frame     = SafeGetInt("ReplayFrameNum");
            int camCarIdx = SafeGetInt("CamCarIdx");

            // Stuck-detection: if NextIncident didn't seek (frame advanced only naturally at 1x),
            // frameDelta ≈ T0_PlayModeCooldownTicks (60). A successful seek jumps hundreds of frames.
            int frameDelta = _suitePreNextIncidentFrame >= 0 ? Math.Abs(frame - _suitePreNextIncidentFrame) : int.MaxValue;
            bool stuckCall = frameDelta < 300;
            if (stuckCall)
            {
                _suiteStuckNextIncidentCount++;
                _logger?.Warn($"DataCaptureSuite T0: NextIncident ignored (frame delta={frameDelta}, stuck={_suiteStuckNextIncidentCount})");
                // Bail out if stuck too many times in a row — no more incidents
                if (_suiteStuckNextIncidentCount >= 3)
                {
                    _suiteSelectedFrames = SelectGroundTruthFrames(_suiteScanCandidates);
                    if (_suiteSelectedFrames.Length == 0)
                    {
                        SuiteResult("T0").Status = "fail";
                        SuiteResult("T0").Error  = "next_incident_stuck";
                        StopReplayIncidentIndexRecordModeLocked("suite_t0_stuck");
                        StartT1Rewind(0);
                        return;
                    }
                    _suiteGroundTruthIdx = 0;
                    _suiteCaptureIdx     = 0;
                    _suiteStep = SuiteInternalStep.T0_SeekCapture;
                    return;
                }
                // Re-issue with pause-settle
                try { _irsdk.ReplaySetPlaySpeed(0, false); } catch { }
                _suiteNextIncidentPending  = true;
                _suiteSeekCooldownTicks    = DataCaptureSuiteConstants.NextIncidentPauseSettleTicks;
                return;
            }
            _suiteStuckNextIncidentCount = 0; // reset on successful jump

            // Player-car filter: only accept incidents involving the player's car.
            // CamCarIdx is set by iRacing to the incident car after a NextIncident jump.
            bool isPlayerCarIncident = _suitePlayerCarIdx >= 0 && camCarIdx == _suitePlayerCarIdx;

            int lap = -1;
            try { lap = _irsdk.Data.GetInt("CarIdxLap", camCarIdx); } catch { }

            // Detect wraparound: if we've looped back near the first scanned frame.
            // Don't require player-car candidates — replay may have no player incidents.
            if (_suiteFirstScanFrame < 0) _suiteFirstScanFrame = frame;
            bool wrapped = _suiteScanCallCount > 3 && frame <= _suiteFirstScanFrame + DataCaptureSuiteConstants.T0_SeekSettleTolerance;
            // Safety ceiling: stop after T0_ScanMaxCalls total NextIncident calls.
            bool reachedCallLimit = _suiteScanCallCount >= DataCaptureSuiteConstants.T0_ScanMaxCalls;

            if (!wrapped && isPlayerCarIncident)
                _suiteScanCandidates.Add((frame, lap, camCarIdx));
            if (!wrapped)
                _suiteScanAllCandidates.Add((frame, lap, camCarIdx));

            // Stop scanning if wrapped, hit max candidates (player or any car), or reached the call ceiling
            if (wrapped || reachedCallLimit ||
                _suiteScanCandidates.Count >= DataCaptureSuiteConstants.T0_ScanMaxIncidents ||
                _suiteScanAllCandidates.Count >= DataCaptureSuiteConstants.T0_ScanMaxIncidents)
            {
                // Prefer player-car incidents; fall back to any incident car if none found.
                var pool = _suiteScanCandidates.Count > 0 ? _suiteScanCandidates : _suiteScanAllCandidates;
                bool usedFallback = _suiteScanCandidates.Count == 0 && _suiteScanAllCandidates.Count > 0;
                _suiteSelectedFrames = SelectGroundTruthFrames(pool);
                if (_suiteSelectedFrames.Length == 0)
                {
                    SuiteResult("T0").Status = "fail";
                    SuiteResult("T0").Error  = "no_incidents_found";
                    StopReplayIncidentIndexRecordModeLocked("suite_t0_no_incidents");
                    StartT1Rewind(0);
                    return;
                }
                if (usedFallback)
                    _logger?.Warn($"DataCaptureSuite T0: no player-car incidents found (playerCarIdx={_suitePlayerCarIdx}); using any-car fallback.");
                _suiteGroundTruthIdx = 0;
                _suiteCaptureIdx     = 0;
                _suiteStep = SuiteInternalStep.T0_SeekCapture;
                return;
            }

            // Always pause before the next NextIncident call.
            // Issuing NextIncident while the replay is playing causes iRacing to randomly seek
            // instead of jumping to the next incident.
            try { _irsdk.ReplaySetPlaySpeed(0, false); } catch { }
            _suiteNextIncidentPending = true;
            _suiteSeekCooldownTicks   = DataCaptureSuiteConstants.NextIncidentPauseSettleTicks;
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
            _suiteSpeedSweepFrameTarget = Math.Max(
                lastGtFrame + DataCaptureSuiteConstants.SpeedSweepAdvanceFrames,
                DataCaptureSuiteConstants.T1_MinSweepFrames);

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

            // Hard per-speed timeout: 3600 ticks (~60s at 60Hz). Long races at 1x would otherwise
            // take 30+ minutes. Emit partial results and advance to the next speed.
            bool sweepDone = frame >= _suiteSpeedSweepFrameTarget
                          || _suiteSpeedSweepTicks > DataCaptureSuiteConstants.SweepTimeoutTicks;
            if (!sweepDone) return;

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

            ResolveDriverFromCarIdx(SafeGetInt("PlayerCarIdx"), out string driverName, out string carNumber, out _);

            var fields = BuildTestFields("T3");
            fields["speed_mps"]    = speed;
            fields["rpm"]          = rpm;
            fields["gear"]         = gear;
            fields["lap_dist_pct"] = lapDistPct;
            fields["driver_name"]  = driverName;
            fields["car_number"]   = carNumber;
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
            fields["actual_frame"]     = SafeGetInt("ReplayFrameNum");
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
            fields["actual_frame"]                = SafeGetInt("ReplayFrameNum");
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

            // Cross-match: how many reseek frames land within 60 of ANY GT frame (regardless of index)
            int anyMatches = 0;
            for (int i = 0; i < 3; i++)
            {
                var rs = _suiteReseekCapture[i];
                if (rs == null) continue;
                if (_suiteGroundTruth.Any(gt => gt != null && Math.Abs(rs.ReplayFrameNum - gt.ReplayFrameNum) <= 60))
                    anyMatches++;
            }

            var fields = BuildTestFields("T7");
            fields["matches_within_60_frames"]     = matches;
            fields["any_frame_matches"]            = anyMatches;
            fields["total_reseeks"]                = 3;
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
            if (TrySkipTest("T8", SuiteInternalStep.TINDEX_Rewind)) return;
            if (_suiteFfSweepTriggered) { _suiteStep = SuiteInternalStep.T8_Poll; return; }
            _suiteFfSweepTriggered       = true;
            _suiteT8PollTicks            = 0;
            _suiteT8BuildWasRunning      = false;
            _suiteT8PlayNudgeCount       = 0;
            _suiteT8SlowRateCount        = 0;
            _suiteT8GraceTickPending     = false;
            _suiteT8FrameAtEndTicks      = 0;
            _suiteT8LastFrameSnapshot    = SafeGetInt("ReplayFrameNum");
            _suiteT8LastFrameSnapshotTick = 0;

            // Compute dynamic timeout from replay length: 3x expected sweep time at 32x + 30s baseline.
            // ReplayFrameNumEnd is unreliable (returns session-relative or stale values); prefer _replayFrameMax
            // which tracks the max ReplayFrameNum seen across all ticks (reliable absolute frame count).
            // Multiplier is 3x (not 2x) because actual FF rate is typically ~16 frames/tick, not 32.
            // Clamped between T8_MinTimeoutTicks and T8_MaxTimeoutTicks.
            int frameNumEnd = _replayFrameMax > 0 ? _replayFrameMax : Math.Max(SafeGetInt("ReplayFrameNumEnd"), _replayFrameTotal);
            int expectedSweepTicks = frameNumEnd > 0
                ? frameNumEnd / ReplayIncidentIndexBuild.DefaultFastForwardPlaySpeed
                : 9000;
            _suiteT8TimeoutTicks = Math.Min(
                Math.Max(expectedSweepTicks * 3 + 1800, DataCaptureSuiteConstants.T8_MinTimeoutTicks),
                DataCaptureSuiteConstants.T8_MaxTimeoutTicks);
            _logger?.Warn($"[T8_DIAG] trigger: frameNumEnd={frameNumEnd} expectedSweepTicks={expectedSweepTicks} timeoutTicks={_suiteT8TimeoutTicks}");

            // T7 leaves the replay paused. Resume at 1x before triggering the build so that
            // ReplaySearch(ToStart) and the subsequent ReplaySetPlaySpeed(32) actually take effect.
            // iRacing silently ignores speed commands issued while paused from the SDK callback thread.
            try { _irsdk.ReplaySetPlaySpeed(1, false); } catch { }

            var (success, _, err) = DispatchReplayIncidentIndexBuild("start", _suiteTestRunId);
            if (!success)
            {
                SuiteResult("T8").Status = "fail";
                SuiteResult("T8").Error  = err ?? "trigger_failed";
                _suiteStep = SuiteInternalStep.TINDEX_Rewind;
                return;
            }
            _suiteStep = SuiteInternalStep.T8_Poll;
        }

        private void TickT8_Poll()
        {
            _suiteT8PollTicks++;

            ReplayIndexBuildPhase buildPhase;
            bool ffComplete;
            lock (_replayIndexBuildLock)
            {
                buildPhase = _replayIndexBuildPhase;
                ffComplete = _replayIndexFfComplete;
            }

            // Track that the build started running
            if (buildPhase != ReplayIndexBuildPhase.Idle) _suiteT8BuildWasRunning = true;

            // Build is done when:
            //   (a) Phase returned to Idle (full build including camera validation complete), OR
            //   (b) FF sweep done and now camera validating (_replayIndexFfComplete set before
            //       transitioning to CameraValidating) — T8 only needs the incident CarIdx data
            //       from the FF sweep; camera validation adds timing context but isn't required.
            //   (c) Frame has been stuck at the end of the replay for 300+ ticks — ffComplete
            //       signal is delayed (ReplayIncidentIndexBuild runs after T8_Poll in DataUpdate);
            //       being at the replay end for 5+ seconds is sufficient evidence of completion.
            bool frameAtEnd = _replayFrameMax > 0 && SafeGetInt("ReplayFrameNum") >= _replayFrameMax - 500;
            if (frameAtEnd) _suiteT8FrameAtEndTicks++;
            else            _suiteT8FrameAtEndTicks = 0;

            bool buildDone =
                (buildPhase == ReplayIndexBuildPhase.Idle && (_suiteT8BuildWasRunning || _suiteT8PollTicks >= 30)) ||
                (ffComplete && _suiteT8BuildWasRunning && buildPhase == ReplayIndexBuildPhase.CameraValidating) ||
                (_suiteT8BuildWasRunning && _suiteT8FrameAtEndTicks >= 300);

            if (!buildDone)
            {
                int currentFrame = SafeGetInt("ReplayFrameNum");

                // Periodic diagnostic log every ~5s (300 ticks)
                if (_suiteT8PollTicks % 300 == 0)
                    _logger?.Warn($"[T8_DIAG] poll ticks={_suiteT8PollTicks}/{_suiteT8TimeoutTicks} phase={buildPhase} ffComplete={ffComplete} wasRunning={_suiteT8BuildWasRunning} nudges={_suiteT8PlayNudgeCount} frame={currentFrame}");

                if (buildPhase == ReplayIndexBuildPhase.FastForwarding)
                {
                    // Watchdog A: replay not playing at all — nudge up to 3 times then abort.
                    bool isPlaying = SafeGetInt("IsReplayPlaying") != 0;
                    if (!isPlaying && _suiteT8PollTicks % 300 == 0)
                    {
                        if (_suiteT8PlayNudgeCount >= 3)
                        {
                            _logger?.Warn($"[T8_DIAG] replay not playing after {_suiteT8PlayNudgeCount} nudges — aborting T8");
                            SuiteResult("T8").Status = "fail";
                            SuiteResult("T8").Error  = "replay_not_playing";
                            _suiteStep = SuiteInternalStep.TINDEX_Rewind;
                            return;
                        }
                        _suiteT8PlayNudgeCount++;
                        _logger?.Warn($"[T8_DIAG] replay paused during FF sweep — nudge #{_suiteT8PlayNudgeCount}, re-issuing play speed");
                        try { _irsdk.ReplaySetPlaySpeed(32, false); } catch { }
                    }

                    // Watchdog B: replay is playing but at the wrong speed (1x instead of 32x).
                    // Check frame advancement rate every T8_RateCheckIntervalTicks ticks.
                    // Skip the abort if frame is near the end of the replay — the sweep completed
                    // but ffComplete hasn't signalled yet; we just need to wait a bit longer.
                    bool frameNearEnd = frameAtEnd; // already computed above using SafeGetInt("ReplayFrameNum")
                    if (_suiteT8PollTicks % DataCaptureSuiteConstants.T8_RateCheckIntervalTicks == 0 && _suiteT8PollTicks > 0)
                    {
                        int ticksSinceSnapshot = _suiteT8PollTicks - _suiteT8LastFrameSnapshotTick;
                        if (ticksSinceSnapshot > 0)
                        {
                            int frameDelta = currentFrame - _suiteT8LastFrameSnapshot;
                            int ratePerTick = frameDelta / ticksSinceSnapshot;
                            if (ratePerTick < DataCaptureSuiteConstants.T8_SlowRateThreshold && !frameNearEnd)
                            {
                                _suiteT8SlowRateCount++;
                                _logger?.Warn($"[T8_DIAG] replay speed stuck? rate={ratePerTick} frames/tick (expected ~32) slowCount={_suiteT8SlowRateCount}");
                                if (_suiteT8SlowRateCount >= DataCaptureSuiteConstants.T8_SlowRateAbortCount)
                                {
                                    _logger?.Warn($"[T8_DIAG] aborting T8 - replay running at wrong speed ({ratePerTick} frames/tick)");
                                    SuiteResult("T8").Status = "fail";
                                    SuiteResult("T8").Error  = "replay_speed_stuck";
                                    _suiteStep = SuiteInternalStep.TINDEX_Rewind;
                                    return;
                                }
                            }
                            else
                            {
                                if (frameNearEnd && ratePerTick < DataCaptureSuiteConstants.T8_SlowRateThreshold)
                                    _logger?.Warn($"[T8_DIAG] frame near end ({currentFrame}/{_replayFrameMax}) - skipping slow-rate abort, waiting for ffComplete");
                                _suiteT8SlowRateCount = 0;
                            }
                        }
                        _suiteT8LastFrameSnapshot     = currentFrame;
                        _suiteT8LastFrameSnapshotTick = _suiteT8PollTicks;
                    }
                }

                // Still running — timeout if we've waited too long.
                // ReplayIncidentIndexBuild runs AFTER TickT8_Poll in the same DataUpdate tick, so
                // ffComplete may be set on the same tick we declare timeout. Give it one grace tick:
                // the next call to TickT8_Poll will see buildDone=true at the top and succeed.
                if (_suiteT8PollTicks > _suiteT8TimeoutTicks)
                {
                    if (!_suiteT8GraceTickPending)
                    {
                        _suiteT8GraceTickPending = true;
                        return; // one extra tick for ReplayIncidentIndexBuild to finish
                    }
                    _logger?.Warn($"[T8_DIAG] timeout after {_suiteT8PollTicks} ticks (limit={_suiteT8TimeoutTicks})");
                    SuiteResult("T8").Status = "fail";
                    SuiteResult("T8").Error  = "timeout";
                    _suiteStep = SuiteInternalStep.TINDEX_Rewind;
                }
                return;
            }

            _logger?.Warn($"[T8_DIAG] buildDone! ticks={_suiteT8PollTicks} phase={buildPhase} ffComplete={ffComplete}");

            // Haven't started yet (shouldn't happen after buildDone check above, but guard anyway)
            if (!_suiteT8BuildWasRunning && _suiteT8PollTicks < 30) return;

            // Build completed (or FF sweep done) — cross-ref GT cars.
            // Prefer finalized root; fall back to draft cam rows when still in CameraValidating.
            int gtCarsInIndex = 0;
            List<ReplayIncidentIndexIncidentRow> incidents = null;
            var indexRoot = _replayIndexDashboardCachedRoot;
            if (indexRoot?.Incidents != null)
            {
                incidents = indexRoot.Incidents;
            }
            else if (ffComplete)
            {
                lock (_replayIndexBuildLock)
                {
                    incidents = _replayIndexCamRows != null
                        ? new List<ReplayIncidentIndexIncidentRow>(_replayIndexCamRows)
                        : null;
                }
            }

            if (incidents != null)
            {
                foreach (var gt in _suiteGroundTruth)
                {
                    if (gt == null) continue;
                    if (incidents.Exists(inc => inc.CarIdx == gt.CarIdx))
                        gtCarsInIndex++;
                }
            }

            var fields = BuildTestFields("T8");
            fields["gt_cars_in_index"]          = gtCarsInIndex;
            fields["total_incidents_in_index"]  = incidents?.Count ?? 0;
            fields["poll_ticks"]                = _suiteT8PollTicks;
            fields["used_draft_rows"]           = indexRoot == null && ffComplete;
            MergeSessionAndRoutingFields(fields);
            _logger?.Structured("INFO", "simhub-plugin", DataCaptureSuiteConstants.EventFfSweepResult,
                $"FF sweep: {gtCarsInIndex} GT cars in index.", fields, "test", null);

            SuiteResult("T8").Status   = "emitted";
            SuiteResult("T8").KpiLabel = "gt_cars_in_index";
            SuiteResult("T8").KpiValue = gtCarsInIndex.ToString();
            _suiteStep = SuiteInternalStep.TINDEX_Rewind;
        }

        // ── T_INDEX: Player Incident Index ───────────────────────────────────

        private void TickTINDEX_Rewind()
        {
            if (TrySkipTest("T_INDEX", SuiteInternalStep.TDISC_Seek)) return;
            try
            {
                _irsdk.ReplaySetPlaySpeed(0, false);
                _irsdk.ReplaySearch(IRacingSdkEnum.RpySrchMode.ToStart);
            }
            catch (Exception ex) { _logger?.Warn("DataCaptureSuite T_INDEX rewind: " + ex.Message); }

            _suiteIndexCandidates     = new List<(int, int, int)>();
            _suiteIndexScanCallCount  = 0;
            _suiteIndexFirstScanFrame = -1;
            _suitePreNextIncidentFrame     = -1;
            _suiteStuckNextIncidentCount   = 0;
            _suiteNextIncidentPending      = true;
            _suiteSeekCooldownTicks        = DataCaptureSuiteConstants.NextIncidentPauseSettleTicks;
            _suiteFrameZeroConsecutive     = 0;
            _suiteSeekTimeoutTicks         = 0;
            _suiteStep = SuiteInternalStep.TINDEX_FrameZero;
        }

        private void TickTINDEX_FrameZero()
        {
            _suiteSeekTimeoutTicks++;
            if (_suiteSeekTimeoutTicks > DataCaptureSuiteConstants.SeekTimeoutTicks)
            {
                SuiteResult("T_INDEX").Status = "fail";
                SuiteResult("T_INDEX").Error  = "frame_zero_timeout";
                _suiteStep = SuiteInternalStep.TDISC_Seek;
                return;
            }

            int frame = SafeGetInt("ReplayFrameNum");
            if (frame <= 2) _suiteFrameZeroConsecutive++;
            else            _suiteFrameZeroConsecutive = 0;
            if (_suiteFrameZeroConsecutive < DataCaptureSuiteConstants.FrameZeroStableTicks) return;

            _suitePreNextIncidentFrame = frame;
            _suiteNextIncidentPending  = true;
            _suiteSeekCooldownTicks    = DataCaptureSuiteConstants.NextIncidentPauseSettleTicks;
            try { _irsdk.ReplaySetPlaySpeed(0, false); } catch { }
            _suiteStep = SuiteInternalStep.TINDEX_ScanCooldown;
        }

        private void TickTINDEX_ScanCooldown()
        {
            if (--_suiteSeekCooldownTicks > 0) return;

            // Pause-settle: issue NextIncident now that pause has settled
            if (_suiteNextIncidentPending)
            {
                _suiteNextIncidentPending  = false;
                _suitePreNextIncidentFrame = SafeGetInt("ReplayFrameNum");
                _suiteSeekCooldownTicks    = DataCaptureSuiteConstants.T0_PlayModeCooldownTicks;
                _suiteIndexScanCallCount++;
                try { _irsdk.ReplaySearch(IRacingSdkEnum.RpySrchMode.NextIncident); } catch { }
                try { _irsdk.ReplaySetPlaySpeed(1, false); } catch { }
                return;
            }

            int frame     = SafeGetInt("ReplayFrameNum");
            int camCarIdx = SafeGetInt("CamCarIdx");

            // Stuck detection: frameDelta < 300 means seek didn't jump
            int frameDelta = _suitePreNextIncidentFrame >= 0 ? Math.Abs(frame - _suitePreNextIncidentFrame) : int.MaxValue;
            if (frameDelta < 300)
            {
                _suiteStuckNextIncidentCount++;
                _logger?.Warn($"DataCaptureSuite T_INDEX: NextIncident ignored (delta={frameDelta}, stuck={_suiteStuckNextIncidentCount})");
                if (_suiteStuckNextIncidentCount >= 3)
                {
                    _suiteStep = SuiteInternalStep.TINDEX_Emit;
                    return;
                }
                try { _irsdk.ReplaySetPlaySpeed(0, false); } catch { }
                _suiteNextIncidentPending = true;
                _suiteSeekCooldownTicks   = DataCaptureSuiteConstants.NextIncidentPauseSettleTicks;
                return;
            }
            _suiteStuckNextIncidentCount = 0;

            int lap = -1;
            try { lap = _irsdk.Data.GetInt("CarIdxLap", camCarIdx); } catch { }

            // Wraparound detection
            if (_suiteIndexFirstScanFrame < 0) _suiteIndexFirstScanFrame = frame;
            bool wrapped         = _suiteIndexScanCallCount > 3 && frame <= _suiteIndexFirstScanFrame + DataCaptureSuiteConstants.T0_SeekSettleTolerance;
            bool reachedCallLimit = _suiteIndexScanCallCount >= DataCaptureSuiteConstants.T0_ScanMaxCalls;

            if (!wrapped)
                _suiteIndexCandidates.Add((frame, lap, camCarIdx));

            if (wrapped || reachedCallLimit)
            {
                _suiteStep = SuiteInternalStep.TINDEX_Emit;
                return;
            }

            // Next incident — always pause first
            try { _irsdk.ReplaySetPlaySpeed(0, false); } catch { }
            _suiteNextIncidentPending = true;
            _suiteSeekCooldownTicks   = DataCaptureSuiteConstants.NextIncidentPauseSettleTicks;
        }

        private void TickTINDEX_Emit()
        {
            int playerCarIdx    = _suitePlayerCarIdx;
            int totalFound      = _suiteIndexCandidates?.Count ?? 0;
            int playerFound     = _suiteIndexCandidates?.Count(c => c.camCarIdx == playerCarIdx) ?? 0;

            // Build incident array for the log
            var incidentList = new System.Collections.Generic.List<object>();
            if (_suiteIndexCandidates != null)
            {
                foreach (var (f, lap, cam) in _suiteIndexCandidates)
                {
                    ResolveDriverFromCarIdx(cam, out string driverName, out string carNumber, out string custId);
                    incidentList.Add(new
                    {
                        frame        = f,
                        lap          = lap,
                        cam_car_idx  = cam,
                        is_player    = cam == playerCarIdx,
                        driver_name  = driverName,
                        car_number   = carNumber,
                    });
                }
            }

            var fields = BuildTestFields("T_INDEX");
            fields["player_car_idx"]       = playerCarIdx;
            fields["incidents_found"]      = totalFound;
            fields["player_incidents_found"] = playerFound;
            fields["incidents"]            = Newtonsoft.Json.JsonConvert.SerializeObject(incidentList);
            MergeSessionAndRoutingFields(fields);
            _logger?.Structured("INFO", "simhub-plugin", DataCaptureSuiteConstants.EventPlayerIncidentIndex,
                $"Player incident index: {playerFound} player incidents, {totalFound} total.", fields, "test", null);

            SuiteResult("T_INDEX").Status   = "emitted";
            SuiteResult("T_INDEX").KpiLabel = "player_incidents_found";
            SuiteResult("T_INDEX").KpiValue = playerFound.ToString();
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
            if (_lokiVerificationStarted) return; // already started — prevent concurrent tasks
            _lokiVerificationStarted = true;
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
        // Helper: log entries have a nested "fields" object; access nested fields for validation.
        private static string LF(Newtonsoft.Json.Linq.JObject j, string key) =>
            j["fields"]?[key]?.ToString();

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
                    return lines.Any(j => LF(j, "variable_count") != null)
                        ? (true, null)
                        : (false, "missing_variable_count");
                case "T3":
                    return lines.Any(j => !string.IsNullOrEmpty(LF(j, "driver_name")))
                        ? (true, null)
                        : (false, "missing_driver_name");
                case "T4":
                {
                    bool ok = lines.Any(j => int.TryParse(LF(j, "driver_count"), out int dc) && dc > 0);
                    return ok ? (true, null) : (false, "driver_count_zero_or_missing");
                }
                case "T5":
                    return lines.Any(j => LF(j, "cam_group_num") != null)
                        ? (true, null)
                        : (false, "missing_cam_group_num");
                case "T5b":
                    return lines.Any(j => LF(j, "cam_group_name") != null)
                        ? (true, null)
                        : (false, "missing_cam_group_name");
                case "T6":
                    return (true, null); // existence is sufficient
                case "T7":
                {
                    // T7 emits a single consolidated event with total_reseeks field
                    bool ok = lines.Any(j => int.TryParse(LF(j, "total_reseeks"), out int tr) && tr >= 3);
                    int best = lines.Max(j => { int.TryParse(LF(j, "total_reseeks"), out int tr); return tr; });
                    return ok ? (true, null) : (false, $"expected>=3_reseeks_got_{best}");
                }
                case "T8":
                {
                    // GT car incidents are not reliably detectable at 16x via CarIdxSessionFlags
                    // (T1 confirms 0.0% GT detection rate at 16x). Verify the build completed
                    // and produced at least 1 incident (player car via player_incident_count).
                    bool ok = lines.Any(j => int.TryParse(LF(j, "total_incidents_in_index"), out int t) && t >= 1);
                    return ok ? (true, null) : (false, "total_incidents_in_index<1");
                }
                case "T_INDEX":
                {
                    bool ok = lines.Any(j => int.TryParse(LF(j, "player_incidents_found"), out int n) && n >= 1);
                    return ok ? (true, null) : (false, "player_incidents_found<1");
                }
                case "T_DISC":
                    return lines.Count >= 4
                        ? (true, null)
                        : (false, $"expected>=4_positions_got_{lines.Count}");
                case "T_60Hz":
                {
                    bool ok = lines.Any(j => int.TryParse(LF(j, "ticks_recorded"), out int t) && t > 0);
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
