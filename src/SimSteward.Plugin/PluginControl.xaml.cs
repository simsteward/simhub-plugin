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

        public PluginControl(SimStewardPlugin plugin)
        {
            InitializeComponent();
            _plugin = plugin;
            _refreshTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = new System.TimeSpan(0, 0, 1)
            };
            _refreshTimer.Tick += RefreshStatus;
            Loaded += (s, e) =>
            {
                PluginVersionText.Text = "Version: " + PluginVersionInfo.Display;
                _refreshTimer.Start();
                RefreshStatus(s, e);
            };
            Unloaded += (s, e) => _refreshTimer.Stop();
        }

        private void RefreshStatus(object sender, System.EventArgs e)
        {
            if (_plugin == null) return;

            WsRunningText.Text = _plugin.WsRunningForSettings ? "Yes" : "No";
            WsPortText.Text = _plugin.WsPortForSettings.ToString();
            ClientCountText.Text = _plugin.ClientCountForSettings.ToString();

            SteamRunningText.Text = _plugin.SteamRunningForSettings ? "Yes" : "No";
            IrsdkStartedText.Text = _plugin.IrsdkStartedForSettings ? "Started" : "Not started";
            IracingStatusText.Text = _plugin.IracingConnectionStatus;
            SimHubHttpText.Text = _plugin.SimHubWebServerForSettings ? "Listening" : "Not listening";
            DashboardPingText.Text = _plugin.DashboardPingForSettings ?? "—";

            var structuredPath = _plugin.StructuredLogPathForSettings;
            StructuredLogPathText.Text = string.IsNullOrWhiteSpace(structuredPath)
                ? "Structured log: (none)"
                : "Structured log: " + structuredPath;
        }
    }
#endif
}
