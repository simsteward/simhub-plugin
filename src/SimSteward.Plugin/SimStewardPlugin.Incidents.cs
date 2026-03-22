#if SIMHUB_SDK
using System;
using System.Collections;
using System.Collections.Generic;

namespace SimSteward.Plugin
{
    public partial class SimStewardPlugin
    {
        private readonly object _incidentBaselineLock = new object();
        private readonly int[] _incidentBaselineByCar = CreateFreshBaseline();
        private string _incidentBaselineSessionKey = "";
        private volatile int _yamlIncidentCount;

        private static int[] CreateFreshBaseline()
        {
            var a = new int[64];
            for (int i = 0; i < 64; i++)
                a[i] = -1;
            return a;
        }

        private void OnIrsdkSessionInfo()
        {
            if (_logger == null || _irsdk == null || !_irsdk.IsConnected)
                return;

            MaybeLogReplayIncidentIndexSessionContext();

            try
            {
                int subId = _irsdk.Data?.SessionInfo?.WeekendInfo?.SubSessionID ?? 0;
                if (subId <= 0)
                    return;

                int sessionNum = SafeGetInt("SessionNum");
                string sessionKey = subId + "_" + sessionNum;
                int replayFrame = SafeGetInt("ReplayFrameNum");
                double sessionTime;
                try
                {
                    sessionTime = _irsdk.Data.GetDouble("SessionTime");
                }
                catch
                {
                    sessionTime = 0;
                }

                int camCarIdx = SafeGetInt("CamCarIdx");
                int camGroupNum = SafeGetInt("CamGroupNumber");
                if (camGroupNum == 0)
                    camGroupNum = SafeGetInt("CameraGroupNumber");
                string cameraGroupName = ResolveCameraGroupNumToName(camGroupNum);

                var drivers = _irsdk.Data?.SessionInfo?.DriverInfo?.Drivers as IList;
                if (drivers == null)
                    return;

                lock (_incidentBaselineLock)
                {
                    if (sessionKey != _incidentBaselineSessionKey)
                    {
                        _incidentBaselineSessionKey = sessionKey;
                        _yamlIncidentCount = 0;
                        for (int i = 0; i < _incidentBaselineByCar.Length; i++)
                            _incidentBaselineByCar[i] = -1;
                    }

                    foreach (var d in drivers)
                    {
                        if (d == null)
                            continue;
                        var t = d.GetType();
                        var carIdxObj = t.GetProperty("CarIdx")?.GetValue(d);
                        int carIdx = carIdxObj is int ci ? ci : Convert.ToInt32(carIdxObj ?? -1);
                        if (carIdx < 0 || carIdx >= _incidentBaselineByCar.Length)
                            continue;

                        int cur = ReflectGetDriverIncidentCount(d);
                        int prev = _incidentBaselineByCar[carIdx];
                        if (prev < 0)
                        {
                            _incidentBaselineByCar[carIdx] = cur;
                            continue;
                        }

                        if (cur <= prev)
                        {
                            _incidentBaselineByCar[carIdx] = cur;
                            continue;
                        }

                        int delta = cur - prev;
                        _incidentBaselineByCar[carIdx] = cur;

                        string carNum = t.GetProperty("CarNumber")?.GetValue(d)?.ToString() ?? "";
                        string name = t.GetProperty("UserName")?.GetValue(d)?.ToString() ?? "";
                        object uidObj = t.GetProperty("UserID")?.GetValue(d) ?? t.GetProperty("CustID")?.GetValue(d);

                        int lap = SafeGetCarIdxLap(carIdx);
                        string cause = MapDeltaToCause(delta);
                        string incidentType = delta + "x";
                        int frameEnd = replayFrame;

                        LogIncidentDetected(
                            incidentType,
                            carNum,
                            name,
                            uidObj,
                            delta,
                            cause,
                            sessionTime,
                            replayFrame,
                            frameEnd,
                            lap,
                            camCarIdx,
                            cameraGroupName);
                    }
                }
            }
            catch
            {
                // ignored — session YAML can be transiently inconsistent
            }
        }

        private void LogIncidentDetected(
            string incidentType,
            string carNumber,
            string driverName,
            object uniqueUserId,
            int delta,
            string cause,
            double sessionTime,
            int replayFrame,
            int replayFrameEnd,
            int incidentCarLap,
            int camCarIdx,
            string cameraGroupName)
        {
            var fields = new Dictionary<string, object>
            {
                ["incident_type"] = incidentType,
                ["car_number"] = carNumber ?? "",
                ["driver_name"] = driverName ?? "",
                ["delta"] = delta,
                ["cause"] = cause,
                ["session_time"] = sessionTime,
                ["replay_frame"] = replayFrame,
                ["replay_frame_end"] = replayFrameEnd,
                ["start_frame"] = replayFrame,
                ["end_frame"] = replayFrameEnd,
                ["cam_car_idx"] = camCarIdx,
                ["camera_group"] = cameraGroupName ?? "",
                ["camera_view"] = "cam_car_idx=" + camCarIdx + ";group=" + (cameraGroupName ?? "")
            };
            if (uniqueUserId != null)
                fields["unique_user_id"] = uniqueUserId;

            MergeSessionAndRoutingFields(fields);
            fields["lap"] = incidentCarLap;

            _yamlIncidentCount++;
            string msg = $"Incident detected: {incidentType} #{carNumber} {driverName}".Trim();
            _logger?.Structured("INFO", "tracker", "incident_detected", msg, fields, "iracing", null);
        }

        private static int ReflectGetDriverIncidentCount(object driver)
        {
            if (driver == null)
                return 0;
            var t = driver.GetType();
            var v = t.GetProperty("CurDriverIncidentCount")?.GetValue(driver)
                ?? t.GetProperty("IncidentCount")?.GetValue(driver);
            if (v == null)
                return 0;
            try
            {
                return Convert.ToInt32(v);
            }
            catch
            {
                return 0;
            }
        }

        private static string MapDeltaToCause(int delta)
        {
            return delta switch
            {
                1 => "off_track",
                2 => "wall_spin",
                4 => "heavy_contact",
                _ => "incident"
            };
        }

        private string ResolveCameraGroupNumToName(int groupNum)
        {
            try
            {
                var groups = _irsdk?.Data?.SessionInfo?.CameraInfo?.Groups as IList;
                if (groups == null)
                    return "";
                foreach (var g in groups)
                {
                    if (g == null)
                        continue;
                    var gt = g.GetType();
                    var numProp = gt.GetProperty("GroupNum");
                    if (numProp == null)
                        continue;
                    int n = Convert.ToInt32(numProp.GetValue(g));
                    if (n == groupNum)
                        return gt.GetProperty("GroupName")?.GetValue(g)?.ToString() ?? "";
                }
            }
            catch
            {
                // ignored
            }

            return "";
        }
    }
}
#endif
