using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using SimStewardPlugin.Telemetry;
using System.Windows.Threading;

namespace SimStewardPlugin.Settings
{
    public partial class SettingsControl : UserControl
    {
        private readonly StatusManager _statusManager;
        private readonly SimStewardSettings _settings;
        private readonly TelemetryManager _telemetryManager;
        private readonly Action _saveSettings;
        private readonly DispatcherTimer _telemetryUiTimer;
        private DateTime _lastHeartbeatSuccessUtc = DateTime.MinValue;
        private DateTime _heartbeatAnimStartUtc = DateTime.MinValue;
        private static readonly TimeSpan HeartbeatAnimCooldown = TimeSpan.FromMilliseconds(400);
        private static readonly TimeSpan HeartbeatOpacityAnimDuration = TimeSpan.FromMilliseconds(200);
        private const double HeartbeatIdleOpacity = 0.18;
        private const double HeartbeatPeakOpacity = 1.0;
        private bool _heartbeatFailedState;

        public SettingsControl(StatusManager statusManager, SimStewardSettings settings, TelemetryManager telemetryManager, Action saveSettings)
        {
            _statusManager = statusManager ?? throw new ArgumentNullException(nameof(statusManager));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _telemetryManager = telemetryManager;
            _saveSettings = saveSettings;
            InitializeComponent();
            DataContext = _statusManager;

            ThemeModeComboBox.SelectedIndex = ThemeModeToIndex(_settings.ThemeMode);
            ApplyThemeResources(_settings.ThemeMode);

            // Migrate legacy plaintext API key to DPAPI-protected storage.
            if (!string.IsNullOrWhiteSpace(_settings.GrafanaLokiApiKey) && string.IsNullOrWhiteSpace(_settings.GrafanaLokiApiKeyProtected))
            {
                _settings.GrafanaLokiApiKeyProtected = SecretStore.ProtectToBase64(_settings.GrafanaLokiApiKey);
                _settings.GrafanaLokiApiKey = string.Empty;
                _saveSettings?.Invoke();
            }

            TelemetryEnabledCheckBox.IsChecked = _settings.TelemetryRequired || _settings.TelemetryEnabled;
            TelemetryEnabledCheckBox.IsEnabled = !_settings.TelemetryRequired;
            GrafanaLokiUrlTextBox.Text = _settings.GrafanaLokiUrl ?? string.Empty;
            GrafanaLokiUsernameTextBox.Text = _settings.GrafanaLokiUsername ?? string.Empty;

            // Never populate PasswordBox from stored secret.
            GrafanaLokiApiKeyPasswordBox.Password = string.Empty;

            TelemetryFlushIntervalTextBox.Text = (_settings.TelemetryFlushIntervalSeconds <= 0 ? 5 : _settings.TelemetryFlushIntervalSeconds).ToString(CultureInfo.InvariantCulture);
            TelemetryHeartbeatIntervalTextBox.Text = (_settings.TelemetryHeartbeatIntervalSeconds < 1 ? 2 : _settings.TelemetryHeartbeatIntervalSeconds > 60 ? 60 : _settings.TelemetryHeartbeatIntervalSeconds).ToString(CultureInfo.InvariantCulture);
            TelemetryLogToDiskCheckBox.IsChecked = _settings.TelemetryLogToDisk;
            TelemetryLogDirectoryTextBox.Text = _settings.TelemetryLogDirectory ?? string.Empty;

            UpdateTelemetryKeyStatus();

            _telemetryUiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _telemetryUiTimer.Tick += (_, __) => RefreshTelemetryConnectionUi();
            _telemetryUiTimer.Start();

            RefreshTelemetryConnectionUi();
        }

        private void RefreshTelemetryConnectionUi()
        {
            if (_telemetryManager == null)
            {
                GrafanaTelemetryConnectionTextBlock.Text = "Unavailable";
                GrafanaTelemetryConnectButton.IsEnabled = false;
                GrafanaTelemetryLastErrorTextBlock.Text = "";
                if (HeartbeatGlyph != null)
                {
                    HeartbeatGlyph.Text = "♥";
                    HeartbeatGlyph.FontFamily = new FontFamily("Segoe UI Symbol");
                    HeartbeatGlyph.Foreground = FindResource("OkBrush") as Brush ?? Brushes.LimeGreen;
                    HeartbeatGlyph.Opacity = HeartbeatIdleOpacity;
                }
                if (HeartbeatAgoText != null) HeartbeatAgoText.Text = "";
                _statusManager.MonitoringState = FeatureState.NotConfigured;
                return;
            }

            TelemetryStatusSnapshot snapshot = _telemetryManager.GetLokiStatusSnapshot();
            _statusManager.MonitoringState = MapTelemetryStateToFeatureState(snapshot.State);

            // Detect new successful Grafana round-trip and trigger beat animation.
            bool isNewBeat = snapshot.LastSuccessUtc > _lastHeartbeatSuccessUtc
                             && snapshot.LastSuccessUtc > DateTime.MinValue;
            if (isNewBeat)
            {
                _lastHeartbeatSuccessUtc = snapshot.LastSuccessUtc;
                TriggerHeartbeatAnimation();
            }
            UpdateHeartbeatVisual(snapshot);

            GrafanaTelemetryConnectionTextBlock.Text = snapshot.StateText;

            if (snapshot.State == TelemetryConnectionState.Connected || snapshot.State == TelemetryConnectionState.Connecting)
            {
                GrafanaTelemetryConnectButton.Content = "Click to disconnect";
            }
            else
            {
                GrafanaTelemetryConnectButton.Content = "Connect telemetry";
            }

            if (!string.IsNullOrWhiteSpace(snapshot.LastError))
            {
                GrafanaTelemetryLastErrorTextBlock.Text = snapshot.LastError;
            }
            else if (snapshot.LastSuccessUtc > DateTime.MinValue)
            {
                GrafanaTelemetryLastErrorTextBlock.Text = $"Last success (UTC): {snapshot.LastSuccessUtc:u}";
            }
            else
            {
                GrafanaTelemetryLastErrorTextBlock.Text = "";
            }
        }

        private static FeatureState MapTelemetryStateToFeatureState(TelemetryConnectionState state)
        {
            switch (state)
            {
                case TelemetryConnectionState.Connected:
                    return FeatureState.Active;
                case TelemetryConnectionState.Connecting:
                    return FeatureState.Waiting;
                case TelemetryConnectionState.Error:
                    return FeatureState.Error;
                case TelemetryConnectionState.NotConfigured:
                case TelemetryConnectionState.Disconnected:
                default:
                    return FeatureState.NotConfigured;
            }
        }

        private void TriggerHeartbeatAnimation()
        {
            if (HeartbeatGlyph == null) return;

            _heartbeatAnimStartUtc = DateTime.UtcNow;
            HeartbeatGlyph.Text = "♥";
            HeartbeatGlyph.FontFamily = new FontFamily("Segoe UI Symbol");
            HeartbeatGlyph.Foreground = FindResource("OkBrush") as Brush ?? Brushes.LimeGreen;
            _heartbeatFailedState = false;

            var opacityUp = new DoubleAnimation(HeartbeatIdleOpacity, HeartbeatPeakOpacity, HeartbeatOpacityAnimDuration)
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            var opacityDown = new DoubleAnimation(HeartbeatPeakOpacity, HeartbeatIdleOpacity, HeartbeatOpacityAnimDuration)
            {
                BeginTime = HeartbeatOpacityAnimDuration,
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
            };

            var storyboard = new Storyboard();
            storyboard.Children.Add(opacityUp);
            storyboard.Children.Add(opacityDown);
            Storyboard.SetTarget(opacityUp, HeartbeatGlyph);
            Storyboard.SetTargetProperty(opacityUp, new PropertyPath(UIElement.OpacityProperty));
            Storyboard.SetTarget(opacityDown, HeartbeatGlyph);
            Storyboard.SetTargetProperty(opacityDown, new PropertyPath(UIElement.OpacityProperty));
            storyboard.Begin();

            var scale = HeartbeatGlyph.RenderTransform as ScaleTransform;
            if (scale != null)
            {
                var beat = new DoubleAnimation(1.0, 1.15, TimeSpan.FromMilliseconds(100))
                {
                    AutoReverse = true,
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                };
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, beat);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, beat);
            }

            var glow = HeartbeatGlyph.Effect as DropShadowEffect;
            if (glow != null)
            {
                var okBrush = FindResource("OkBrush") as SolidColorBrush;
                if (okBrush != null) glow.Color = okBrush.Color;

                var blur = new DoubleAnimation(0, 12, TimeSpan.FromMilliseconds(100)) { AutoReverse = true };
                glow.BeginAnimation(DropShadowEffect.BlurRadiusProperty, blur);

                var opac = new DoubleAnimation(0, 0.6, TimeSpan.FromMilliseconds(100)) { AutoReverse = true };
                glow.BeginAnimation(DropShadowEffect.OpacityProperty, opac);
            }
        }

        private void UpdateHeartbeatVisual(TelemetryStatusSnapshot snapshot)
        {
            if (HeartbeatGlyph == null) return;

            // Keep the green flash visible during animation cooldown.
            bool inAnim = (DateTime.UtcNow - _heartbeatAnimStartUtc) < HeartbeatAnimCooldown;
            if (!inAnim)
            {
                bool isFailed = snapshot.State == TelemetryConnectionState.Error;
                if (isFailed)
                {
                    _heartbeatFailedState = true;
                    HeartbeatGlyph.Text = "\uD83D\uDC94";
                    HeartbeatGlyph.FontFamily = new FontFamily("Segoe UI Emoji");
                    HeartbeatGlyph.Foreground = FindResource("ErrorBrush") as Brush ?? Brushes.Red;
                    HeartbeatGlyph.Opacity = 1.0;
                }
                else
                {
                    if (_heartbeatFailedState)
                    {
                        _heartbeatFailedState = false;
                    }

                    HeartbeatGlyph.Text = "♥";
                    HeartbeatGlyph.FontFamily = new FontFamily("Segoe UI Symbol");
                    HeartbeatGlyph.Foreground = FindResource("OkBrush") as Brush ?? Brushes.LimeGreen;
                    HeartbeatGlyph.Opacity = HeartbeatIdleOpacity;
                }
            }

            // Update "ago" text beside the heart.
            if (HeartbeatAgoText == null) return;
            if (snapshot.LastSuccessUtc > DateTime.MinValue)
            {
                TimeSpan ago = DateTime.UtcNow - snapshot.LastSuccessUtc;
                if (ago.TotalSeconds < 2)
                    HeartbeatAgoText.Text = "just now";
                else if (ago.TotalMinutes < 1)
                    HeartbeatAgoText.Text = $"{(int)ago.TotalSeconds}s ago";
                else if (ago.TotalHours < 1)
                    HeartbeatAgoText.Text = $"{(int)ago.TotalMinutes}m ago";
                else
                    HeartbeatAgoText.Text = $"{(int)ago.TotalHours}h ago";
            }
            else
            {
                HeartbeatAgoText.Text = "";
            }
        }

        private void UpdateTelemetryKeyStatus()
        {
            if (_settings == null)
            {
                return;
            }

            bool hasKey = !string.IsNullOrWhiteSpace(_settings.GrafanaLokiApiKeyProtected);
            GrafanaLokiKeyStatusTextBlock.Text = hasKey ? "Stored" : "Not set";
        }

        private void TelemetryEnabledCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (_settings == null)
            {
                return;
            }

            if (_settings.TelemetryRequired)
            {
                TelemetryEnabledCheckBox.IsChecked = true;
                return;
            }

            _settings.TelemetryEnabled = TelemetryEnabledCheckBox.IsChecked == true;
            _saveSettings?.Invoke();
        }

        private void GrafanaTelemetryField_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_settings == null)
            {
                return;
            }

            _settings.GrafanaLokiUrl = GrafanaLokiUrlTextBox.Text?.Trim() ?? string.Empty;
            _settings.GrafanaLokiUsername = GrafanaLokiUsernameTextBox.Text?.Trim() ?? string.Empty;

            if (int.TryParse(TelemetryFlushIntervalTextBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int flushSeconds))
            {
                _settings.TelemetryFlushIntervalSeconds = flushSeconds;
            }

            if (int.TryParse(TelemetryHeartbeatIntervalTextBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int heartbeatSeconds))
            {
                _settings.TelemetryHeartbeatIntervalSeconds = heartbeatSeconds;
            }

            _settings.TelemetryLogDirectory = TelemetryLogDirectoryTextBox.Text?.Trim() ?? string.Empty;

            _saveSettings?.Invoke();
        }

        private void TelemetryLogToDisk_Changed(object sender, RoutedEventArgs e)
        {
            if (_settings == null)
            {
                return;
            }
            _settings.TelemetryLogToDisk = TelemetryLogToDiskCheckBox.IsChecked == true;
            _saveSettings?.Invoke();
        }

        private void GrafanaLokiApiKeyPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (_settings == null)
            {
                return;
            }

            string plaintext = GrafanaLokiApiKeyPasswordBox.Password ?? string.Empty;
            _settings.GrafanaLokiApiKeyProtected = SecretStore.ProtectToBase64(plaintext);
            _settings.GrafanaLokiApiKey = string.Empty;
            _saveSettings?.Invoke();

            UpdateTelemetryKeyStatus();
        }

        private void ClearLokiApiKey_Click(object sender, RoutedEventArgs e)
        {
            if (_settings == null)
            {
                return;
            }

            _settings.GrafanaLokiApiKeyProtected = string.Empty;
            _settings.GrafanaLokiApiKey = string.Empty;
            GrafanaLokiApiKeyPasswordBox.Password = string.Empty;
            _saveSettings?.Invoke();

            UpdateTelemetryKeyStatus();
            RefreshTelemetryConnectionUi();
        }

        private async void GrafanaTelemetryConnectButton_Click(object sender, RoutedEventArgs e)
        {
            if (_telemetryManager == null)
            {
                return;
            }

            TelemetryStatusSnapshot current = _telemetryManager.GetLokiStatusSnapshot();
            if (current.State == TelemetryConnectionState.Connected || current.State == TelemetryConnectionState.Connecting)
            {
                _telemetryManager.Disconnect();
                RefreshTelemetryConnectionUi();
                return;
            }

            // Persist any current fields before attempting connect.
            GrafanaTelemetryField_LostFocus(null, null);

            try
            {
                GrafanaTelemetryConnectButton.IsEnabled = false;
                GrafanaTelemetryConnectionTextBlock.Text = "Connecting";
                GrafanaTelemetryLastErrorTextBlock.Text = "";

                TelemetryStatusSnapshot result = await _telemetryManager.ConnectAndTestAsync(_statusManager);
                GrafanaTelemetryConnectionTextBlock.Text = result.StateText;

                if (!string.IsNullOrWhiteSpace(result.LastError))
                {
                    GrafanaTelemetryLastErrorTextBlock.Text = result.LastError;
                }
                else if (result.LastSuccessUtc > DateTime.MinValue)
                {
                    GrafanaTelemetryLastErrorTextBlock.Text = $"Connected. Last success (UTC): {result.LastSuccessUtc:u}";
                }
            }
            finally
            {
                GrafanaTelemetryConnectButton.IsEnabled = true;
                RefreshTelemetryConnectionUi();
            }
        }

        private static int ThemeModeToIndex(ThemeMode mode)
        {
            switch (mode)
            {
                case ThemeMode.Dark:
                    return 0;
                case ThemeMode.Light:
                    return 1;
                default:
                    return 0;
            }
        }

        private static ThemeMode IndexToThemeMode(int index)
        {
            switch (index)
            {
                case 0:
                    return ThemeMode.Dark;
                case 1:
                    return ThemeMode.Light;
                default:
                    return ThemeMode.Dark;
            }
        }

        private void ThemeModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_settings == null)
            {
                return;
            }

            ThemeMode selected = IndexToThemeMode(ThemeModeComboBox.SelectedIndex);
            if (selected == _settings.ThemeMode)
            {
                return;
            }

            _settings.ThemeMode = selected;
            _saveSettings?.Invoke();
            ApplyThemeResources(selected);
        }

        private void ApplyThemeResources(ThemeMode mode)
        {
            if (SystemParameters.HighContrast)
            {
                Resources["PanelBackgroundBrush"] = SystemColors.WindowBrush;
                Resources["SurfaceBrush"] = SystemColors.WindowBrush;
                Resources["SectionBackgroundBrush"] = SystemColors.WindowBrush;
                Resources["CardBackgroundBrush"] = SystemColors.WindowBrush;
                Resources["BorderBrush"] = SystemColors.ActiveBorderBrush;
                Resources["AccentBrush"] = SystemColors.HighlightBrush;
                Resources["HeaderBrush"] = SystemColors.WindowTextBrush;
                Resources["MutedBrush"] = SystemColors.GrayTextBrush;
                Resources["ValueBrush"] = SystemColors.WindowTextBrush;
                Resources["OkBrush"] = SystemColors.HighlightBrush;
                Resources["WarningBrush"] = SystemColors.HighlightBrush;
                Resources["ErrorBrush"] = SystemColors.HighlightBrush;
                Resources["InactiveBrush"] = SystemColors.GrayTextBrush;
                Resources["NetworkBrush"] = SystemColors.HighlightBrush;
                Resources["SuccessBrush"] = SystemColors.HighlightBrush;
                return;
            }

            if (mode == ThemeMode.Dark)
            {
                ApplyPitwallDarkPalette();
            }
            else
            {
                ApplyPitwallLightPalette();
            }
        }

        private void ApplyPitwallDarkPalette()
        {
            // PITWALL NOC (Dark): deep navy + cyan accent
            Resources["PanelBackgroundBrush"] = new SolidColorBrush(Color.FromRgb(8, 12, 20));          // #080C14
            Resources["SurfaceBrush"] = new SolidColorBrush(Color.FromRgb(15, 25, 35));                 // #0F1923
            Resources["SectionBackgroundBrush"] = new SolidColorBrush(Color.FromRgb(15, 25, 35));      // #0F1923
            Resources["CardBackgroundBrush"] = new SolidColorBrush(Color.FromRgb(20, 30, 43));         // #141E2B
            Resources["BorderBrush"] = new SolidColorBrush(Color.FromRgb(43, 62, 85));                 // #2B3E55 (stronger framing)
            Resources["AccentBrush"] = new SolidColorBrush(Color.FromRgb(0, 191, 255));                // #00BFFF
            Resources["HeaderBrush"] = new SolidColorBrush(Color.FromRgb(0, 191, 255));                // #00BFFF
            Resources["MutedBrush"] = new SolidColorBrush(Color.FromRgb(123, 140, 160));               // #7B8CA0
            Resources["ValueBrush"] = new SolidColorBrush(Color.FromRgb(224, 232, 240));               // #E0E8F0
            Resources["OkBrush"] = new SolidColorBrush(Color.FromRgb(0, 230, 118));                    // #00E676
            Resources["WarningBrush"] = new SolidColorBrush(Color.FromRgb(255, 179, 0));               // #FFB300
            Resources["ErrorBrush"] = new SolidColorBrush(Color.FromRgb(255, 61, 61));                 // #FF3D3D
            Resources["InactiveBrush"] = new SolidColorBrush(Color.FromRgb(58, 74, 90));               // #3A4A5A
            Resources["NetworkBrush"] = new SolidColorBrush(Color.FromRgb(33, 150, 243));               // #2196F3
            Resources["SuccessBrush"] = Resources["OkBrush"];
        }

        private void ApplyPitwallLightPalette()
        {
            // PITWALL NOC (Light): soft off-white base + translucent surface layer + darker (non-harsh) text
            Resources["PanelBackgroundBrush"] = new SolidColorBrush(Color.FromRgb(241, 244, 248));     // #F1F4F8

            // Surface layer provides depth over the base background (slightly translucent white)
            Resources["SurfaceBrush"] = new SolidColorBrush(Color.FromArgb(230, 255, 255, 255));        // ~90% white

            Resources["SectionBackgroundBrush"] = new SolidColorBrush(Color.FromRgb(250, 252, 255));    // #FAFCFF
            Resources["CardBackgroundBrush"] = new SolidColorBrush(Color.FromRgb(255, 255, 255));       // #FFFFFF
            Resources["BorderBrush"] = new SolidColorBrush(Color.FromRgb(184, 201, 219));               // #B8C9DB (stronger framing)

            // Accent: slightly desaturated racing blue (not neon)
            Resources["AccentBrush"] = new SolidColorBrush(Color.FromRgb(0, 106, 153));                // #006A99
            Resources["HeaderBrush"] = new SolidColorBrush(Color.FromRgb(0, 106, 153));                // #006A99

            // Text hierarchy: darker values + softer labels for crisp contrast without jarring black
            Resources["ValueBrush"] = new SolidColorBrush(Color.FromRgb(30, 45, 61));                  // #1E2D3D
            Resources["MutedBrush"] = new SolidColorBrush(Color.FromRgb(70, 90, 110));                 // #465A6E

            // Status colors: professional tones (less saturated than overlay greens/reds)
            Resources["OkBrush"] = new SolidColorBrush(Color.FromRgb(15, 138, 95));                     // #0F8A5F
            Resources["WarningBrush"] = new SolidColorBrush(Color.FromRgb(183, 121, 31));              // #B7791F
            Resources["ErrorBrush"] = new SolidColorBrush(Color.FromRgb(197, 48, 48));                 // #C53030
            Resources["InactiveBrush"] = new SolidColorBrush(Color.FromRgb(120, 137, 155));            // #78899B
            Resources["NetworkBrush"] = new SolidColorBrush(Color.FromRgb(25, 118, 210));              // #1976D2
            Resources["SuccessBrush"] = Resources["OkBrush"];
        }
    }

    public sealed class BooleanToVisibilityInverseConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool boolValue)
            {
                return boolValue ? Visibility.Collapsed : Visibility.Visible;
            }

            return Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}