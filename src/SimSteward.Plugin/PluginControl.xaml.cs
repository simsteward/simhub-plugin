#if SIMHUB_SDK
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
#endif

namespace SimSteward.Plugin
{
#if SIMHUB_SDK
    public partial class PluginControl : UserControl
    {
        private readonly SimStewardPlugin _plugin;
        private readonly DispatcherTimer _refreshTimer;
        private bool _suppressEvents;

        public PluginControl(SimStewardPlugin plugin)
        {
            InitializeComponent();
            _plugin = plugin;
            _refreshTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = new System.TimeSpan(0, 0, 1)
            };
            _refreshTimer.Tick += RefreshStatus;
            Loaded += (s, e) => { _refreshTimer.Start(); RefreshStatus(s, e); };
            Unloaded += (s, e) => _refreshTimer.Stop();
        }

        private void RefreshStatus(object sender, System.EventArgs e)
        {
            if (_plugin == null) return;

            WsRunningText.Text      = _plugin.WsRunningForSettings ? "Yes" : "No";
            WsPortText.Text         = _plugin.WsPortForSettings.ToString();
            ClientCountText.Text    = _plugin.ClientCountForSettings.ToString();

            _suppressEvents = true;
            OmitDebugCheckBox.IsChecked = _plugin.OmitLevelForSettings("DEBUG");
            OmitInfoCheckBox.IsChecked = _plugin.OmitLevelForSettings("INFO");
            OmitWarnCheckBox.IsChecked = _plugin.OmitLevelForSettings("WARN");
            OmitErrorCheckBox.IsChecked = _plugin.OmitLevelForSettings("ERROR");
            OmitStateBroadcastCheckBox.IsChecked = _plugin.OmitEventForSettings("state_broadcast_summary");
            OmitTickStatsCheckBox.IsChecked = _plugin.OmitEventForSettings("tick_stats");
            OmitWsMessageRawCheckBox.IsChecked = _plugin.OmitEventForSettings("ws_message_raw");
            OmitActionReceivedCheckBox.IsChecked = _plugin.OmitEventForSettings("action_received");
            OmitActionDispatchedCheckBox.IsChecked = _plugin.OmitEventForSettings("action_dispatched");
            if (LogAllActionTrafficCheckBox != null)
                LogAllActionTrafficCheckBox.IsChecked = _plugin.LogAllActionTrafficForSettings;
            _suppressEvents = false;

            if (DataApiEndpointBox != null)
                DataApiEndpointBox.Text = _plugin.GetDataApiEndpointForSettings();

            var structuredPath = _plugin.StructuredLogPathForSettings;
            StructuredLogPathText.Text = string.IsNullOrWhiteSpace(structuredPath) ? "Structured log: (none)" : "Structured log: " + structuredPath;

            IrsdkStartedText.Text   = _plugin.IrsdkStartedForSettings ? "Started" : "Not started";
            IracingStatusText.Text  = _plugin.IracingConnectionStatus;
            bool connected = _plugin.IracingConnectionStatus == "Connected";
            IracingHelpText.Visibility = connected ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
            IracingHelpText.Text = "If iRacing is running but status is Not connected: enable shared memory in Documents\\iRacing\\app.ini (irsdkEnableMem=1), save, then restart iRacing. You must be in a session or replay, not just the launcher.";
        }

        private void OmitLevel_Changed(object sender, RoutedEventArgs e)
        {
            if (_plugin == null || _suppressEvents) return;
            var cb = sender as CheckBox;
            if (cb?.Tag is string level)
            {
                var @checked = cb.IsChecked == true;
                _plugin.SetOmitLogLevel(level, @checked);
                _plugin.LogPluginUiChanged("omit_level_" + level, @checked);
            }
            RefreshStatus(sender, e);
        }

        private void OmitEvent_Changed(object sender, RoutedEventArgs e)
        {
            if (_plugin == null || _suppressEvents) return;
            var cb = sender as CheckBox;
            if (cb?.Tag is string eventId)
            {
                var @checked = cb.IsChecked == true;
                _plugin.SetOmitEvent(eventId, @checked);
                _plugin.LogPluginUiChanged("omit_event_" + eventId, @checked);
            }
            RefreshStatus(sender, e);
        }

        private void DataApiEndpoint_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_plugin == null || DataApiEndpointBox == null) return;
            var value = DataApiEndpointBox?.Text ?? "";
            _plugin.SetDataApiEndpoint(value);
            _plugin.LogPluginUiChanged("data_api_endpoint", string.IsNullOrWhiteSpace(value) ? "(empty)" : value);
        }

        private void LogAllActionTraffic_Changed(object sender, RoutedEventArgs e)
        {
            if (_plugin == null || _suppressEvents || LogAllActionTrafficCheckBox == null) return;
            var @checked = LogAllActionTrafficCheckBox.IsChecked == true;
            _plugin.SetLogAllActionTraffic(@checked);
            _plugin.LogPluginUiChanged("log_all_action_traffic", @checked);
        }

    }
#endif
}
