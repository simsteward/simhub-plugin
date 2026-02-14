using System;
using System.ComponentModel;

namespace SimStewardPlugin
{
    public enum PluginRuntimeState
    {
        Starting,
        Running,
        Shutdown,
        Error
    }

    public enum ConnectionState
    {
        Disconnected,
        Connected,
        Error
    }

    public enum FeatureState
    {
        NotConfigured,
        Waiting,
        Active,
        Warning,
        Error,
        Disabled
    }

    public sealed class StatusManager : INotifyPropertyChanged
    {
        private PluginRuntimeState _pluginState = PluginRuntimeState.Starting;
        private ConnectionState _iRacingConnection = ConnectionState.Disconnected;
        private long _telemetryUpdateCount;
        private double _lastSessionTime;
        private int _lastSessionNum;
        private int _lastIncidentCount;
        private string _lastGameName = "None";
        private FeatureState _monitoringState = FeatureState.NotConfigured;
        private FeatureState _obsState = FeatureState.NotConfigured;
        private FeatureState _incidentDetectionState = FeatureState.NotConfigured;
        private FeatureState _recordingState = FeatureState.NotConfigured;
        private FeatureState _replayState = FeatureState.NotConfigured;
        private string _lastErrorMessage = string.Empty;
        private DateTime _lastStatusChangeUtc = DateTime.UtcNow;
        private DateTime _lastHeartbeatUtc = DateTime.UtcNow;

        public event PropertyChangedEventHandler PropertyChanged;
        public event Action<string> StatusTransition;

        public PluginRuntimeState PluginState
        {
            get { return _pluginState; }
            set
            {
                if (_pluginState == value)
                {
                    return;
                }

                PluginRuntimeState previous = _pluginState;
                _pluginState = value;
                TouchStatusTimestamp();
                OnPropertyChanged(nameof(PluginState));
                OnPropertyChanged(nameof(PluginStateText));
                EmitTransition("PLUGIN", previous.ToString(), value.ToString());
            }
        }

        public ConnectionState IRacingConnection
        {
            get { return _iRacingConnection; }
            set
            {
                if (_iRacingConnection == value)
                {
                    return;
                }

                ConnectionState previous = _iRacingConnection;
                _iRacingConnection = value;
                TouchStatusTimestamp();
                OnPropertyChanged(nameof(IRacingConnection));
                OnPropertyChanged(nameof(IRacingConnectionText));
                OnPropertyChanged(nameof(IsIRacingConnected));
                EmitTransition("IRACING", previous.ToString(), value.ToString());
            }
        }

        public long TelemetryUpdateCount
        {
            get { return _telemetryUpdateCount; }
            set
            {
                if (_telemetryUpdateCount == value)
                {
                    return;
                }

                _telemetryUpdateCount = value;
                OnPropertyChanged(nameof(TelemetryUpdateCount));
            }
        }

        public double LastSessionTime
        {
            get { return _lastSessionTime; }
            set
            {
                if (Math.Abs(_lastSessionTime - value) < 0.0001)
                {
                    return;
                }

                _lastSessionTime = value;
                OnPropertyChanged(nameof(LastSessionTime));
                OnPropertyChanged(nameof(LastSessionTimeFormatted));
            }
        }

        public int LastSessionNum
        {
            get { return _lastSessionNum; }
            set
            {
                if (_lastSessionNum == value)
                {
                    return;
                }

                _lastSessionNum = value;
                OnPropertyChanged(nameof(LastSessionNum));
            }
        }

        public int LastIncidentCount
        {
            get { return _lastIncidentCount; }
            set
            {
                if (_lastIncidentCount == value)
                {
                    return;
                }

                _lastIncidentCount = value;
                OnPropertyChanged(nameof(LastIncidentCount));
            }
        }

        public string LastGameName
        {
            get { return _lastGameName; }
            set
            {
                string normalized = string.IsNullOrWhiteSpace(value) ? "None" : value;
                if (string.Equals(_lastGameName, normalized, StringComparison.Ordinal))
                {
                    return;
                }

                _lastGameName = normalized;
                OnPropertyChanged(nameof(LastGameName));
            }
        }

        public FeatureState MonitoringState
        {
            get { return _monitoringState; }
            set
            {
                if (_monitoringState == value)
                {
                    return;
                }

                FeatureState previous = _monitoringState;
                _monitoringState = value;
                TouchStatusTimestamp();
                OnPropertyChanged(nameof(MonitoringState));
                OnPropertyChanged(nameof(MonitoringStateText));
                EmitTransition("MONITORING", previous.ToString(), value.ToString());
            }
        }

        public FeatureState ObsState
        {
            get { return _obsState; }
            set
            {
                if (_obsState == value)
                {
                    return;
                }

                FeatureState previous = _obsState;
                _obsState = value;
                TouchStatusTimestamp();
                OnPropertyChanged(nameof(ObsState));
                OnPropertyChanged(nameof(ObsStateText));
                EmitTransition("OBS", previous.ToString(), value.ToString());
            }
        }

        public FeatureState IncidentDetectionState
        {
            get { return _incidentDetectionState; }
            set
            {
                if (_incidentDetectionState == value)
                {
                    return;
                }

                FeatureState previous = _incidentDetectionState;
                _incidentDetectionState = value;
                TouchStatusTimestamp();
                OnPropertyChanged(nameof(IncidentDetectionState));
                OnPropertyChanged(nameof(IncidentDetectionStateText));
                EmitTransition("INCIDENT", previous.ToString(), value.ToString());
            }
        }

        public FeatureState RecordingState
        {
            get { return _recordingState; }
            set
            {
                if (_recordingState == value)
                {
                    return;
                }

                FeatureState previous = _recordingState;
                _recordingState = value;
                TouchStatusTimestamp();
                OnPropertyChanged(nameof(RecordingState));
                OnPropertyChanged(nameof(RecordingStateText));
                EmitTransition("RECORDING", previous.ToString(), value.ToString());
            }
        }

        public FeatureState ReplayState
        {
            get { return _replayState; }
            set
            {
                if (_replayState == value)
                {
                    return;
                }

                FeatureState previous = _replayState;
                _replayState = value;
                TouchStatusTimestamp();
                OnPropertyChanged(nameof(ReplayState));
                OnPropertyChanged(nameof(ReplayStateText));
                EmitTransition("REPLAY", previous.ToString(), value.ToString());
            }
        }

        public string LastErrorMessage
        {
            get { return _lastErrorMessage; }
            set
            {
                string normalized = value ?? string.Empty;
                if (string.Equals(_lastErrorMessage, normalized, StringComparison.Ordinal))
                {
                    return;
                }

                _lastErrorMessage = normalized;
                TouchStatusTimestamp();
                OnPropertyChanged(nameof(LastErrorMessage));
                OnPropertyChanged(nameof(HasError));
            }
        }

        public DateTime LastStatusChangeUtc
        {
            get { return _lastStatusChangeUtc; }
        }

        public DateTime LastHeartbeatUtc
        {
            get { return _lastHeartbeatUtc; }
        }

        public string PluginStateText => PluginState.ToString();
        public string IRacingConnectionText => IRacingConnection.ToString();
        public bool IsIRacingConnected => IRacingConnection == ConnectionState.Connected;
        public string MonitoringStateText => MonitoringState.ToString();
        public string ObsStateText => ObsState.ToString();
        public string IncidentDetectionStateText => IncidentDetectionState.ToString();
        public string RecordingStateText => RecordingState.ToString();
        public string ReplayStateText => ReplayState.ToString();
        public bool HasError => !string.IsNullOrWhiteSpace(LastErrorMessage);
        public string LastSessionTimeFormatted => LastSessionTime.ToString("F3");
        public string LastStatusChangeUtcFormatted => LastStatusChangeUtc.ToString("u");
        public string LastHeartbeatUtcFormatted => LastHeartbeatUtc.ToString("u");

        public void ResetTelemetry()
        {
            TelemetryUpdateCount = 0;
            LastSessionTime = 0;
            LastSessionNum = 0;
            LastIncidentCount = 0;
        }

        public void SetError(string message)
        {
            LastErrorMessage = message;
            PluginState = PluginRuntimeState.Error;
        }

        public void RecordHeartbeat()
        {
            _lastHeartbeatUtc = DateTime.UtcNow;
            OnPropertyChanged(nameof(LastHeartbeatUtc));
            OnPropertyChanged(nameof(LastHeartbeatUtcFormatted));
        }

        private void TouchStatusTimestamp()
        {
            _lastStatusChangeUtc = DateTime.UtcNow;
            OnPropertyChanged(nameof(LastStatusChangeUtc));
            OnPropertyChanged(nameof(LastStatusChangeUtcFormatted));
        }

        private void EmitTransition(string component, string previous, string next)
        {
            StatusTransition?.Invoke($"[{component}] Status: {previous} -> {next}");
        }

        private void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}