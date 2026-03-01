using System;
using System.Collections.Generic;
using Fleck;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SimSteward.Plugin
{
    /// <summary>
    /// Fleck WebSocket server for dashboard communication. Thread-safe: Fleck callbacks
    /// run on thread-pool threads; BroadcastState is called from DataUpdate (SimHub thread).
    /// </summary>
    public class DashboardBridge
    {
        private WebSocketServer _server;
        private readonly List<IWebSocketConnection> _clients = new List<IWebSocketConnection>();
        private readonly object _clientLock = new object();
        private readonly Func<string> _getStateForNewClient;
        private readonly Func<string> _getLogTailForNewClient;
        private readonly Func<string, string, (bool success, string result, string error)> _dispatchAction;
        private readonly Action<string, string, string> _onLog;
        private readonly PluginLogger _logger;
        private string _authToken;

        /// <param name="getStateForNewClient">Called when a client connects to get initial state JSON.</param>
        /// <param name="getLogTailForNewClient">Called when a client connects to get recent log entries JSON (may be null).</param>
        /// <param name="dispatchAction">(action, arg) => (success, result, error).</param>
        /// <param name="onLog">(level, message, source) for log action.</param>
        /// <param name="logger">Optional; for bridge lifecycle messages.</param>
        public DashboardBridge(
            Func<string> getStateForNewClient,
            Func<string> getLogTailForNewClient,
            Func<string, string, (bool success, string result, string error)> dispatchAction,
            Action<string, string, string> onLog,
            PluginLogger logger = null)
        {
            _getStateForNewClient = getStateForNewClient ?? (() => "{}");
            _getLogTailForNewClient = getLogTailForNewClient;
            _dispatchAction = dispatchAction ?? ((_, __) => (false, null, "missing_dispatch"));
            _onLog = onLog ?? ((_, __, ___) => { });
            _logger = logger;
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
                        lock (_clientLock)
                        {
                            _clients.Add(socket);
                        }
                        _logger?.Info($"DashboardBridge: client connected ({ClientCount} total)");
                        try
                        {
                            var stateJson = _getStateForNewClient();
                            if (!string.IsNullOrEmpty(stateJson))
                                socket.Send(stateJson);
                        }
                        catch (Exception ex)
                        {
                            _logger?.Warn($"DashboardBridge: getStateForNewClient failed: {ex.Message}");
                        }
                        // Send recent log tail so late-joining clients see context immediately
                        try
                        {
                            var tailJson = _getLogTailForNewClient?.Invoke();
                            if (!string.IsNullOrEmpty(tailJson))
                                socket.Send(tailJson);
                        }
                        catch (Exception ex)
                        {
                            _logger?.Warn($"DashboardBridge: getLogTailForNewClient failed: {ex.Message}");
                        }
                    };

                    socket.OnClose = () =>
                    {
                        lock (_clientLock)
                        {
                            _clients.Remove(socket);
                        }
                        _logger?.Info($"DashboardBridge: client disconnected ({ClientCount} total)");
                    };

                    socket.OnMessage = msg => HandleMessage(socket, msg);
                });
                var suffix = _authToken == null ? " (token not required)" : " (token required)";
                _logger?.Info($"DashboardBridge: WebSocket listening on {address}:{port}{suffix}");
            }
            catch (Exception ex)
            {
                _logger?.Error($"DashboardBridge: failed to start: {ex.Message}", ex);
                throw;
            }
        }

        public void Stop()
        {
            if (_server == null) return;
            try
            {
                _server.Dispose();
            }
            catch (Exception ex)
            {
                _logger?.Warn($"DashboardBridge: dispose error: {ex.Message}");
            }
            _server = null;
            lock (_clientLock)
            {
                _clients.Clear();
            }
            _logger?.Info("DashboardBridge: WebSocket server stopped");
        }

        public int ClientCount
        {
            get { lock (_clientLock) { return _clients.Count; } }
        }

        /// <summary>
        /// Send an arbitrary JSON message to all connected clients. Swallows per-client send exceptions.
        /// Use for push events (logEvents, incidentEvents) that are not the throttled state.
        /// </summary>
        public void Broadcast(string json)
        {
            if (string.IsNullOrEmpty(json)) return;
            List<IWebSocketConnection> snapshot;
            lock (_clientLock)
            {
                if (_clients.Count == 0) return;
                snapshot = new List<IWebSocketConnection>(_clients);
            }
            foreach (var client in snapshot)
            {
                try { client.Send(json); }
                catch { }
            }
        }

        /// <summary>
        /// Send state JSON to all connected clients. Swallows per-client send exceptions.
        /// </summary>
        public void BroadcastState(string json)
        {
            if (string.IsNullOrEmpty(json)) return;
            List<IWebSocketConnection> snapshot;
            lock (_clientLock)
            {
                if (_clients.Count == 0) return;
                snapshot = new List<IWebSocketConnection>(_clients);
            }
            foreach (var client in snapshot)
            {
                try
                {
                    client.Send(json);
                }
                catch
                {
                    // Client likely disconnected; OnClose will remove
                }
            }
        }

        private bool Authenticate(IWebSocketConnection socket)
        {
            if (string.IsNullOrEmpty(_authToken))
                return true;

            var path = socket.ConnectionInfo?.Path ?? string.Empty;
            var token = ExtractQueryValue(path, "token");
            if (string.Equals(token, _authToken, StringComparison.Ordinal))
                return true;

            _logger?.Warn($"DashboardBridge: rejecting connection from {socket.ConnectionInfo?.ClientIpAddress} (missing/invalid token)");
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
                    var level = jo["level"]?.ToString() ?? "info";
                    var message = jo["message"]?.ToString() ?? "";
                    var source = jo["source"]?.ToString() ?? "";
                    _onLog(level, message, source);
                }
                catch { }
                SendActionResult(socket, action, true, "ok", null);
                return;
            }

            var (success, result, error) = _dispatchAction(action, arg ?? "");
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
            try
            {
                socket.Send(obj.ToString());
            }
            catch { }
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
