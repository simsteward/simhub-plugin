using System;
using System.Collections.Generic;
using Fleck;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Sentry;

namespace SimSteward.Plugin
{
    /// <summary>Fleck WebSocket server for dashboard communication.</summary>
    public class DashboardBridge
    {
        private WebSocketServer _server;
        private readonly List<IWebSocketConnection> _clients = new List<IWebSocketConnection>();
        private readonly object _clientLock = new object();
        private readonly Func<string> _getStateForNewClient;
        private readonly Func<string> _getLogTailForNewClient;
        private readonly Func<string, string, string, (bool success, string result, string error)> _dispatchAction;
        private readonly Action<string, string, string> _onLog;
        private readonly Action<string, string, Dictionary<string, object>> _onStructuredLog;
        private readonly PluginLogger _logger;
        private readonly Action<Exception, string> _onSendError;
        private readonly Action _onNoClients;
        private string _authToken;

        public DashboardBridge(
            Func<string> getStateForNewClient,
            Func<string> getLogTailForNewClient,
            Func<string, string, string, (bool success, string result, string error)> dispatchAction,
            Action<string, string, string> onLog,
            PluginLogger logger = null,
            Action<string, string, Dictionary<string, object>> onStructuredLog = null,
            Action<Exception, string> onSendError = null,
            Action onNoClients = null)
        {
            _getStateForNewClient = getStateForNewClient ?? (() => "{}");
            _getLogTailForNewClient = getLogTailForNewClient;
            _dispatchAction = dispatchAction ?? ((_, __, ___) => (false, null, "missing_dispatch"));
            _onLog = onLog ?? ((_, __, ___) => { });
            _onStructuredLog = onStructuredLog;
            _logger = logger;
            _onSendError = onSendError;
            _onNoClients = onNoClients;
        }

        public void Start(string bindAddress, int port, string authToken)
        {
            if (_server != null) return;
            _authToken = string.IsNullOrEmpty(authToken) ? null : authToken;
            var address = string.IsNullOrWhiteSpace(bindAddress) ? "127.0.0.1" : bindAddress;
            try
            {
                _server = new WebSocketServer($"ws://{address}:{port}");
                _server.Start(socket =>
                {
                    socket.OnOpen = () =>
                    {
                        if (!Authenticate(socket)) return;
                        int clientCount;
                        lock (_clientLock)
                        {
                            _clients.Add(socket);
                            clientCount = _clients.Count;
                        }
                        var clientIp = socket.ConnectionInfo?.ClientIpAddress ?? "unknown";
                        _logger?.Structured("INFO", "bridge", "ws_client_connected", "client connected",
                            new Dictionary<string, object> { ["client_ip"] = clientIp, ["client_count"] = clientCount });
                        try
                        {
                            var stateJson = _getStateForNewClient();
                            if (!string.IsNullOrEmpty(stateJson))
                                socket.Send(stateJson);
                        }
                        catch (Exception ex)
                        {
                            SentrySdk.CaptureException(ex);
                            _logger?.Warn($"DashboardBridge: getStateForNewClient failed: {ex.Message}");
                        }
                        try
                        {
                            var tailJson = _getLogTailForNewClient?.Invoke();
                            if (!string.IsNullOrEmpty(tailJson))
                                socket.Send(tailJson);
                        }
                        catch (Exception ex)
                        {
                            SentrySdk.CaptureException(ex);
                            _logger?.Warn($"DashboardBridge: getLogTailForNewClient failed: {ex.Message}");
                        }
                    };

                    socket.OnClose = () =>
                    {
                        int clientCount;
                        lock (_clientLock)
                        {
                            _clients.Remove(socket);
                            clientCount = _clients.Count;
                        }
                        var clientIp = socket.ConnectionInfo?.ClientIpAddress ?? "unknown";
                        _logger?.Structured("INFO", "bridge", "ws_client_disconnected", "client disconnected",
                            new Dictionary<string, object> { ["client_ip"] = clientIp, ["client_count"] = clientCount });
                    };

                    socket.OnMessage = msg => HandleMessage(socket, msg);
                });
                var suffix = _authToken == null ? " (token not required)" : " (token required)";
                _logger?.Info($"DashboardBridge: WebSocket listening on {address}:{port}{suffix}");
            }
            catch (Exception ex)
            {
                SentrySdk.CaptureException(ex);
                _logger?.Error($"DashboardBridge: failed to start: {ex.Message}", ex);
                throw;
            }
        }

        public void Stop()
        {
            if (_server == null) return;
            try { _server.Dispose(); }
            catch (Exception ex)
            {
                SentrySdk.CaptureException(ex);
                _logger?.Warn($"DashboardBridge: dispose error: {ex.Message}");
            }
            _server = null;
            lock (_clientLock) { _clients.Clear(); }
            _logger?.Info("DashboardBridge: WebSocket server stopped");
        }

        public int ClientCount
        {
            get { lock (_clientLock) { return _clients.Count; } }
        }

        public void Broadcast(string json, string payloadType = "logEvents")
        {
            if (string.IsNullOrEmpty(json)) return;
            List<IWebSocketConnection> snapshot;
            int clientCount;
            lock (_clientLock)
            {
                clientCount = _clients.Count;
                snapshot = new List<IWebSocketConnection>(_clients);
            }
            foreach (var client in snapshot)
            {
                try { client.Send(json); }
                catch (Exception ex) { _onSendError?.Invoke(ex, payloadType); }
            }
            if (clientCount == 0)
                _onNoClients?.Invoke();
        }

        public void BroadcastState(string json)
        {
            if (string.IsNullOrEmpty(json)) return;
            List<IWebSocketConnection> snapshot;
            int clientCount;
            lock (_clientLock)
            {
                clientCount = _clients.Count;
                snapshot = new List<IWebSocketConnection>(_clients);
            }
            foreach (var client in snapshot)
            {
                try { client.Send(json); }
                catch (Exception ex) { _onSendError?.Invoke(ex, "state"); }
            }
            if (clientCount == 0)
                _onNoClients?.Invoke();
        }

        private bool Authenticate(IWebSocketConnection socket)
        {
            if (string.IsNullOrEmpty(_authToken))
                return true;

            var path = socket.ConnectionInfo?.Path ?? string.Empty;
            var token = ExtractQueryValue(path, "token");
            if (string.Equals(token, _authToken, StringComparison.Ordinal))
                return true;

            var clientIp = socket.ConnectionInfo?.ClientIpAddress ?? "unknown";
            var reason = string.IsNullOrEmpty(token) ? "missing_token" : "invalid_token";
            _logger?.Structured("WARN", "bridge", "ws_client_rejected", "rejecting connection (missing/invalid token)",
                new Dictionary<string, object> { ["client_ip"] = clientIp, ["reason"] = reason });
            try { socket.Close(); } catch { }
            return false;
        }

        private static string ExtractQueryValue(string path, string key)
        {
            if (string.IsNullOrEmpty(path))
                return null;
            var idx = path.IndexOf('?');
            if (idx < 0 || idx == path.Length - 1)
                return null;

            var query = path.Substring(idx + 1);
            foreach (var part in query.Split('&'))
            {
                if (string.IsNullOrEmpty(part)) continue;
                var pair = part.Split(new[] { '=' }, 2);
                var name = Uri.UnescapeDataString(pair[0]);
                if (!string.Equals(name, key, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (pair.Length == 1)
                    return string.Empty;
                return Uri.UnescapeDataString(pair[1]);
            }
            return null;
        }

        private void HandleMessage(IWebSocketConnection socket, string msg)
        {
            var clientIp = socket.ConnectionInfo?.ClientIpAddress ?? "unknown";
            if (_logger?.IsDebugMode == true)
            {
                _logger.Debug("ws message raw", "bridge", "ws_message_raw",
                    new Dictionary<string, object> { ["raw_json"] = msg, ["client_ip"] = clientIp });
            }

            string action = null;
            string arg = null;

            try
            {
                var jo = JObject.Parse(msg);
                action = jo["action"]?.ToString();
                arg = jo["arg"]?.ToString();
            }
            catch
            {
                SendResponse(socket, type: "error", error: "invalid_json");
                return;
            }

            if (string.IsNullOrEmpty(action))
            {
                SendResponse(socket, type: "error", error: "missing_action");
                return;
            }

            if (string.Equals(action, "ping", StringComparison.OrdinalIgnoreCase))
            {
                socket.Send(JsonConvert.SerializeObject(new { type = "pong" }));
                return;
            }

            if (string.Equals(action, "log", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var jo = JObject.Parse(msg);
                    var eventType = jo["event"]?.ToString();
                    if (string.Equals(eventType, "dashboard_ui_event", StringComparison.OrdinalIgnoreCase) && _onStructuredLog != null)
                    {
                        var message = jo["message"]?.ToString() ?? "Dashboard UI event";
                        var fields = new Dictionary<string, object> { ["client_ip"] = clientIp };
                        if (!string.IsNullOrEmpty(jo["element_id"]?.ToString())) fields["element_id"] = jo["element_id"].ToString();
                        if (!string.IsNullOrEmpty(jo["event_type"]?.ToString())) fields["event_type"] = jo["event_type"].ToString();
                        if (jo["value"] != null) fields["value"] = jo["value"].ToString();
                        _onStructuredLog("dashboard_ui_event", message, fields);
                    }
                    else
                    {
                        var level = jo["level"]?.ToString() ?? "info";
                        var message = jo["message"]?.ToString() ?? "";
                        var source = jo["source"]?.ToString() ?? "";
                        _onLog(level, message, source);
                    }
                }
                catch { }
                SendActionResult(socket, action, true, "ok", null);
                return;
            }

            var correlationId = Guid.NewGuid().ToString("N").Substring(0, 8);
            var (success, result, error) = _dispatchAction(action, arg ?? "", correlationId);
            SendActionResult(socket, action, success, result, error);
        }

        private void SendActionResult(IWebSocketConnection socket, string action, bool success, string result, string error)
        {
            var obj = new JObject
            {
                ["type"] = "actionResult",
                ["action"] = action,
                ["success"] = success
            };
            if (!string.IsNullOrEmpty(result)) obj["result"] = result;
            if (!string.IsNullOrEmpty(error)) obj["error"] = error;
            try { socket.Send(obj.ToString()); } catch { }
        }

        private void SendResponse(IWebSocketConnection socket, string type, string error)
        {
            try
            {
                socket.Send(JsonConvert.SerializeObject(new { type, error }));
            }
            catch { }
        }
    }
}
