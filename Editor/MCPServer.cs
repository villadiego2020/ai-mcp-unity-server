using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace AIUnityMCPServer
{
    [InitializeOnLoad]
    public static class MCPServer
    {
        const int BASE_PORT = 23457;
        const int PORT_RANGE = 10;
        const int PRESENCE_SCHEMA_VERSION = 2;
        const double HEARTBEAT_INTERVAL = 2.0;
        const double REGISTRY_SWEEP_INTERVAL = 30.0;
        enum PresenceServerState { Offline, Online }
        static int _port;

        static TcpListener _listener;
        static Thread _thread;
        static volatile bool _running;

        static string _label;        // "Main" / "Clone 0" / "Clone 1"
        static string _instanceId;
        static long _startedAtUnixMs;
        static double _lastHeartbeat;
        static double _lastRegistrySweep;
        public static string Label => _label ?? (_label = DetectLabel());
        public static string InstanceId => _instanceId ?? (_instanceId = CreateStableInstanceId());
        public static int Port => _port;

        static string DetectLabel()
        {
            try
            {
                // Application.dataPath = ".../<ProjectName>[_clone_N]/Assets"
                string proj = Directory.GetParent(Application.dataPath)?.Name ?? "";
                var m = System.Text.RegularExpressions.Regex.Match(proj, @"_clone_(\d+)$");
                if (m.Success) return $"Clone {m.Groups[1].Value}";
                var t = FindType("ParrelSync.ClonesManager");
                var mi = t?.GetMethod("IsClone", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (mi != null && mi.Invoke(null, null) is bool b && b) return "Clone";
                return "Main";
            }
            catch { return "Main"; }
        }

        static int CloneIndex()
        {
            var m = System.Text.RegularExpressions.Regex.Match(Label, @"(\d+)");
            return m.Success ? int.Parse(m.Value) + 1 : 0;   // Main=0 → 23457, Clone 0=1 → 23458
        }

        static string CreateStableInstanceId()
        {
            string projectPath = ToForwardSlashes(ProjectRoot()).TrimEnd('/');
            if (Application.platform == RuntimePlatform.WindowsEditor)
            {
                projectPath = projectPath.ToLowerInvariant();
            }

            return "unity-" + Hash128.Compute(projectPath).ToString();
        }

        static long UnixMillisecondsNow() =>
            (DateTime.UtcNow.Ticks - 621355968000000000L) / TimeSpan.TicksPerMillisecond;

        static Type FindType(string fullName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var type = asm.GetType(fullName);
                    if (type != null)
                    {
                        return type;
                    }
                }
                catch (Exception exception)
                {
                    Debug.LogWarning($"[AI Unity MCP Server] Inspect assembly '{asm.FullName}' failed: {exception.Message}");
                }
            }
            return null;
        }

        static bool WasRunning
        {
            get => SessionState.GetBool("AIUnityMCPServer_WasRunning", false);
            set => SessionState.SetBool("AIUnityMCPServer_WasRunning", value);
        }

        static bool AutoStartEnabled
        {
            get => EditorPrefs.GetBool("AIUnityMCPServer_AutoStart", false);
            set => EditorPrefs.SetBool("AIUnityMCPServer_AutoStart", value);
        }

        static MCPServer()
        {
            if (ShouldSkipCurrentProcess())
            {
                return;
            }

            _startedAtUnixMs = UnixMillisecondsNow();
            MCPHandlers.LoadLog();

            EditorApplication.delayCall += EnsureMcpJson;

            StartHeartbeat();

            if (WasRunning)
                Start();
            else if (AutoStartEnabled)
                StartReadOnly();
            else
            {
                WriteOfflinePresence();
                StartWatching();
            }

            AssemblyReloadEvents.beforeAssemblyReload += () =>
            {
                MCPHandlers.SaveLog();
                StopHeartbeat();
                StopInternal();
            };
            EditorApplication.quitting += () =>
            {
                MCPHandlers.SaveLog();
                StopHeartbeat();
                StopInternal();
                RemovePresence();
            };
        }

        static bool ShouldSkipCurrentProcess() =>
            Application.isBatchMode || AssetDatabase.IsAssetImportWorkerProcess();

        public static bool IsRunning => _running;
        public static bool IsAutoStartEnabled => AutoStartEnabled;

        const string MENU_START = "AI Unity MCP Server/Server/Start";
        const string MENU_STOP  = "AI Unity MCP Server/Server/Stop";
        const string MENU_AUTO_START = "AI Unity MCP Server/Server/Auto Start Read-Only";

        [MenuItem(MENU_START, true)]
        static bool ValidateStart()
        {
            Menu.SetChecked(MENU_START, _running);
            return !_running;
        }

        [MenuItem(MENU_STOP, true)]
        static bool ValidateStop()
        {
            Menu.SetChecked(MENU_STOP, !_running);
            return _running;
        }

        [MenuItem(MENU_AUTO_START, true)]
        static bool ValidateAutoStart()
        {
            Menu.SetChecked(MENU_AUTO_START, AutoStartEnabled);
            return true;
        }

        [MenuItem(MENU_AUTO_START)]
        static void ToggleAutoStart()
        {
            AutoStartEnabled = !AutoStartEnabled;
            Debug.Log($"[AI Unity MCP Server] Auto Start Read-Only {(AutoStartEnabled ? "enabled" : "disabled")}");
            if (AutoStartEnabled && !_running)
            {
                StartReadOnly();
            }
        }

        const string MENU_WRITE = "AI Unity MCP Server/Allow Write Commands";

        [MenuItem(MENU_WRITE, true)]
        static bool ValidateWrite()
        {
            Menu.SetChecked(MENU_WRITE, MCPHandlers.AllowWrites);
            return true;
        }

        [MenuItem(MENU_WRITE)]
        static void ToggleWrite()
        {
            MCPHandlers.AllowWrites = !MCPHandlers.AllowWrites;
            Debug.Log($"[AI Unity MCP Server] Write commands {(MCPHandlers.AllowWrites ? "ENABLED" : "disabled (read-only)")}");
        }

        [MenuItem(MENU_START)]
        public static void Start()
        {
            if (_running) return;

            CleanTrigger();
            StopInternal();

            if (!TryBind())
            {
                Debug.LogWarning($"[AI Unity MCP Server] Could not start: no free port in {BASE_PORT}..{BASE_PORT + PORT_RANGE - 1}. Another instance or stale process may own them.");
                StartWatching();
                return;
            }

            _running = true;
            WasRunning = true;
            StopWatching();
            WriteOnlinePresence(); // presence → serverOn:true + actual port
            _thread = new Thread(Listen) { IsBackground = true, Name = "AIUnityMCPServer-Server" };
            _thread.Start();
            Debug.Log($"[AI Unity MCP Server] Server started — {Label} @ port {_port}");
        }

        static void StartReadOnly()
        {
            MCPHandlers.AllowWrites = false;
            Start();
        }

        static bool TryBind()
        {
            int prefer = BASE_PORT + CloneIndex();
            for (int i = 0; i < PORT_RANGE; i++)
            {
                int p = BASE_PORT + (((prefer - BASE_PORT) + i) % PORT_RANGE);
                TcpListener candidate = null;
                try
                {
                    candidate = new TcpListener(IPAddress.Loopback, p);
                    candidate.Start();
                    _listener = candidate;
                    _port = p;
                    return true;
                }
                catch (SocketException)
                {
                    candidate?.Stop();
                    _listener = null;
                }
                catch (Exception exception)
                {
                    candidate?.Stop();
                    _listener = null;
                    Debug.LogWarning($"[AI Unity MCP Server] Bind port {p} failed: {exception.Message}");
                }
            }
            return false;
        }

        [MenuItem(MENU_STOP)]
        public static void Stop()
        {
            WasRunning = false;
            StopInternal();
        }

        static void StopInternal()
        {
            if (!_running && _listener == null) return;
            _running = false;
            WriteOfflinePresence();
            try { _listener?.Stop(); }
            catch (Exception exception) { Debug.LogWarning($"[AI Unity MCP Server] Stop listener failed: {exception.Message}"); }
            try { _listener?.Server?.Dispose(); }
            catch (Exception exception) { Debug.LogWarning($"[AI Unity MCP Server] Dispose listener failed: {exception.Message}"); }
            _listener = null;
            try { _thread?.Join(300); }
            catch (Exception exception) { Debug.LogWarning($"[AI Unity MCP Server] Join listener thread failed: {exception.Message}"); }
            _thread = null;
            _lastWatchCheck = 0;
            StartWatching();
            Debug.Log("[AI Unity MCP Server] Server stopped");
        }

        static void StartHeartbeat()
        {
            EditorApplication.update -= HeartbeatUpdate;
            EditorApplication.update += HeartbeatUpdate;
            _lastHeartbeat = 0;
            _lastRegistrySweep = 0;
        }

        static void StopHeartbeat()
        {
            EditorApplication.update -= HeartbeatUpdate;
        }

        static void HeartbeatUpdate()
        {
            double now = EditorApplication.timeSinceStartup;
            if (now - _lastHeartbeat < HEARTBEAT_INTERVAL)
            {
                return;
            }

            _lastHeartbeat = now;
            bool shouldSweep = now - _lastRegistrySweep >= REGISTRY_SWEEP_INTERVAL;
            if (shouldSweep)
            {
                _lastRegistrySweep = now;
            }

            if (shouldSweep)
            {
                SweepRegistry();
            }

            WriteCurrentPresence();
        }

        // key = PID → <dir>/<pid>.json = {pid, label, project, projectPath, port, serverOn}
        //   1) per-project <originalRoot>/Library/AIUnityMCPServer/instances (gitignored)
        static string ProjectInstancesDir()
        {
            string projRoot = Directory.GetParent(Application.dataPath).FullName;   // .../<project>[_clone_N]
            string parent   = Directory.GetParent(projRoot).FullName;
            string name     = Path.GetFileName(projRoot);
            var m = System.Text.RegularExpressions.Regex.Match(name, @"^(.*)_clone_\d+$");
            string origRoot = m.Success ? Path.Combine(parent, m.Groups[1].Value) : projRoot;
            return CreateDirOrEmpty(Path.Combine(origRoot, "Library", "AIUnityMCPServer", "instances"));
        }

        static string SharedInstancesDir()
        {
            string home = HomeDir();
            return string.IsNullOrEmpty(home) ? "" : CreateDirOrEmpty(Path.Combine(home, ".AIUnityMCPServer", "instances"));
        }

        static string HomeDir()
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(home)) home = Environment.GetEnvironmentVariable("USERPROFILE");
            if (string.IsNullOrEmpty(home)) home = Environment.GetEnvironmentVariable("HOME");
            return home ?? "";
        }

        static string CreateDirOrEmpty(string dir)
        {
            try { Directory.CreateDirectory(dir); return dir; }
            catch (Exception e) { Debug.LogWarning($"[AI Unity MCP Server] Could not create registry directory: {dir} ({e.Message})"); return ""; }
        }

        static List<string> InstancesDirs()
        {
            var dirs = new List<string>(2);
            string project = ProjectInstancesDir();
            if (!string.IsNullOrEmpty(project)) dirs.Add(project);
            string shared = SharedInstancesDir();
            if (!string.IsNullOrEmpty(shared)) dirs.Add(shared);
            return dirs;
        }

        static int Pid => System.Diagnostics.Process.GetCurrentProcess().Id;
        static string PresenceFileName => $"{Pid}.json";

        static void WriteOnlinePresence()
        {
            SweepRegistry();
            WritePresence(PresenceServerState.Online);
        }

        static void WriteOfflinePresence()
        {
            SweepRegistry();
            WritePresence(PresenceServerState.Offline);
        }

        static void WriteCurrentPresence()
        {
            WritePresence(_running ? PresenceServerState.Online : PresenceServerState.Offline);
        }

        static void WritePresence(PresenceServerState serverState)
        {
            try
            {
                bool serverOn = serverState == PresenceServerState.Online;
                string projPath = Directory.GetParent(Application.dataPath)?.FullName ?? "";
                string proj = Path.GetFileName(projPath);
                int port = serverOn ? _port : (BASE_PORT + CloneIndex());   // off → preferred
                string json = $"{{\"schemaVersion\":{PRESENCE_SCHEMA_VERSION},\"instanceId\":\"{Escape(InstanceId)}\","
                            + $"\"pid\":{Pid},\"processKind\":\"editor\",\"label\":\"{Escape(Label)}\","
                            + $"\"project\":\"{Escape(proj)}\",\"projectPath\":\"{Escape(ToForwardSlashes(projPath))}\","
                            + $"\"port\":{port},\"serverOn\":{(serverOn ? "true" : "false")},"
                            + $"\"heartbeatUnixMs\":{UnixMillisecondsNow()},\"startedAtUnixMs\":{_startedAtUnixMs},"
                            + $"\"packageVersion\":\"{Escape(MCPPackagePaths.PackageVersion())}\"}}";
                foreach (var dir in InstancesDirs())
                {
                    try { WritePresenceAtomically(Path.Combine(dir, PresenceFileName), json); }
                    catch (Exception e) { Debug.LogWarning($"[AI Unity MCP Server] Could not write presence to {dir}: {e.Message}"); }
                }
            }
            catch (Exception e) { Debug.LogWarning($"[AI Unity MCP Server] WritePresence failed: {e.Message}"); }
        }

        static void WritePresenceAtomically(string destinationPath, string json)
        {
            string temporaryPath = destinationPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporaryPath, json, new UTF8Encoding(false));

            try
            {
                if (File.Exists(destinationPath))
                {
                    File.Replace(temporaryPath, destinationPath, null);
                }
                else
                {
                    File.Move(temporaryPath, destinationPath);
                }
            }
            catch
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }

                throw;
            }
        }

        static void RemovePresence()
        {
            foreach (var dir in InstancesDirs())
            {
                try { File.Delete(Path.Combine(dir, PresenceFileName)); }
                catch (Exception e) { Debug.LogWarning($"[AI Unity MCP Server] Could not remove presence from {dir}: {e.Message}"); }
            }
        }

        static void SweepRegistry()
        {
            foreach (var dir in InstancesDirs())
            {
                try
                {
                    foreach (var f in Directory.GetFiles(dir, "*.json"))
                        DeleteIfProcessDead(f);
                }
                catch (Exception e) { Debug.LogWarning($"[AI Unity MCP Server] Registry sweep failed at {dir}: {e.Message}"); }
            }
        }

        static void DeleteIfProcessDead(string presenceFile)
        {
            try
            {
                var m = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(presenceFile), "\"pid\"\\s*:\\s*(\\d+)");
                if (!m.Success) return;
                try { System.Diagnostics.Process.GetProcessById(int.Parse(m.Groups[1].Value)); }
                catch { File.Delete(presenceFile); }
            }
            catch (Exception e) { Debug.LogWarning($"[AI Unity MCP Server] Could not read or remove presence file {presenceFile}: {e.Message}"); }
        }

        static bool   _watching;
        static double _lastWatchCheck;
        const  double WATCH_INTERVAL = 1.5;

        static string RequestStartPath()
        {
            string dir = Path.Combine(Application.dataPath, "..", "Library", "AIUnityMCPServer");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "request-start");
        }

        static void CleanTrigger()
        {
            try { var p = RequestStartPath(); if (File.Exists(p)) File.Delete(p); }
            catch (Exception exception) { Debug.LogWarning($"[AI Unity MCP Server] Clean start request failed: {exception.Message}"); }
        }

        static void StartWatching()
        {
            if (_watching) return;
            EditorApplication.update += WatchUpdate;
            _watching = true;
        }

        static void StopWatching()
        {
            if (!_watching) return;
            EditorApplication.update -= WatchUpdate;
            _watching = false;
        }

        static void WatchUpdate()
        {
            if (_running) { StopWatching(); return; }   // safety
            double now = EditorApplication.timeSinceStartup;
            if (now - _lastWatchCheck < WATCH_INTERVAL) return;
            _lastWatchCheck = now;
            try
            {
                string p = RequestStartPath();
                if (File.Exists(p))
                {
                    File.Delete(p);
                    Debug.Log("[AI Unity MCP Server] request-start detected; starting the server read-only");
                    StartReadOnly();
                }
            }
            catch (Exception e) { Debug.LogWarning($"[AI Unity MCP Server] watch error: {e.Message}"); }
        }

        static void EnsureMcpJson()
        {
            try
            {
                string serverEntry = ResolveLegacyClientServerEntry();
                if (string.IsNullOrEmpty(serverEntry) || !File.Exists(serverEntry))
                {
                    return;
                }

                EnsureMcpJsonForServerEntry(serverEntry);
            }
            catch (Exception e) { Debug.LogWarning($"[AI Unity MCP Server] EnsureMcpJson failed: {e.Message}"); }
        }

        internal static void EnsureMcpJsonForServerEntry(string serverEntry)
        {
            try
            {
                string projectRoot = ProjectRoot();
                string mcpPath = Path.Combine(projectRoot, ".mcp.json");
                string argsPath = McpArgsPath(serverEntry, projectRoot);
                string generated = GeneratedMcpJson(argsPath);

                if (!File.Exists(mcpPath)) { WriteTextNoBom(mcpPath, generated); return; }

                string existing = File.ReadAllText(mcpPath);
                if (IsGeneratedByThisPackage(existing))
                {
                    if (existing.Trim() != generated.Trim()) WriteTextNoBom(mcpPath, generated);
                    return;
                }

                WarnIfUnityEntryPathIsBroken(existing, projectRoot, argsPath, mcpPath);
            }
            catch (Exception exception)
            {
                Debug.LogWarning("[AI Unity MCP Server] EnsureMcpJsonForServerEntry failed: " + exception.Message);
            }
        }

        static string ResolveLegacyClientServerEntry()
        {
            if (!IsImmutablePackageInstall())
            {
                return ServerEntryPath();
            }

            if (!MCPRuntimeCache.TryCreatePlan(out MCPRuntimeCache.Plan runtimePlan, out _))
            {
                return "";
            }

            return MCPRuntimeCache.Inspect(runtimePlan, out _) == MCPRuntimeCache.CacheState.Ready
                ? runtimePlan.ServerEntryPath
                : "";
        }

        static string ProjectRoot() => MCPPackagePaths.ProjectRoot();

        static bool IsImmutablePackageInstall()
        {
            return MCPPackagePaths.IsImmutableInstall();
        }

        static string ServerEntryPath()
        {
            return MCPPackagePaths.ServerEntryPath();
        }

        internal static string McpArgsPath(string serverEntry, string projectRoot)
        {
            string entry = ToForwardSlashes(Path.GetFullPath(serverEntry));
            string root  = ToForwardSlashes(Path.GetFullPath(projectRoot)).TrimEnd('/');
            return entry.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase)
                ? "./" + entry.Substring(root.Length + 1)
                : entry;
        }

        static string ToForwardSlashes(string path) => path.Replace("\\", "/");

        internal static string GeneratedMcpJson(string argsPath) =>
            "{\n  \"mcpServers\": {\n    \"AIUnityMCPServer\": {\n      \"command\": \"node\",\n      \"args\": [\""
            + argsPath.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"]\n    }\n  }\n}\n";

        internal static bool IsGeneratedByThisPackage(string json) =>
            System.Text.RegularExpressions.Regex.IsMatch(json,
                "^\\s*\\{\\s*\"mcpServers\"\\s*:\\s*\\{\\s*\"AIUnityMCPServer\"\\s*:\\s*\\{\\s*\"command\"\\s*:\\s*\"node\"\\s*,"
                + "\\s*\"args\"\\s*:\\s*\\[\\s*\"[^\"]*\"\\s*\\]\\s*\\}\\s*\\}\\s*\\}\\s*$");

        const string WARNED_MCP_JSON_KEY = "AIUnityMCPServer_WarnedMcpJson";

        static void WarnIfUnityEntryPathIsBroken(string json, string projectRoot, string correctArgs, string mcpPath)
        {
            var m = System.Text.RegularExpressions.Regex.Match(json, "\"AIUnityMCPServer\"\\s*:\\s*\\{[\\s\\S]*?\"args\"\\s*:\\s*\\[\\s*\"([^\"]+)\"");
            if (!m.Success) return;

            string configured = m.Groups[1].Value;
            string full = Path.IsPathRooted(configured) ? configured : Path.Combine(projectRoot, configured);
            if (File.Exists(full)) return;
            if (SessionState.GetBool(WARNED_MCP_JSON_KEY, false)) return;

            SessionState.SetBool(WARNED_MCP_JSON_KEY, true);
            Debug.LogWarning($"[AI Unity MCP Server] {mcpPath} points to missing entry '{configured}'. Set args to \"{correctArgs}\". "
                           + "The file was customized, so it was not overwritten automatically.");
        }

        static void WriteTextNoBom(string path, string text) =>
            File.WriteAllText(path, text, new System.Text.UTF8Encoding(false));

        static void Listen()
        {
            while (_running)
            {
                try
                {
                    var client = _listener.AcceptTcpClient();
                    ThreadPool.QueueUserWorkItem(_ => HandleClient(client));
                }
                catch (SocketException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (Exception e) { Debug.LogError($"[AI Unity MCP Server] Listen error: {e.Message}"); }
            }
        }

        static void HandleClient(TcpClient client)
        {
            try
            {
                using (client)
                using (var stream = client.GetStream())
                {
                    client.ReceiveTimeout = 5000;

                    var ms = new MemoryStream();
                    var buf = new byte[8192];
                    int headerEnd = -1, contentLength = 0;

                    while (true)
                    {
                        int n = stream.Read(buf, 0, buf.Length);
                        if (n <= 0) break;
                        ms.Write(buf, 0, n);
                        var data = ms.GetBuffer();
                        int len = (int)ms.Length;

                        if (headerEnd < 0)
                        {
                            headerEnd = IndexOfCrlfCrlf(data, len);
                            if (headerEnd >= 0)
                            {
                                string header = Encoding.ASCII.GetString(data, 0, headerEnd);
                                contentLength = ParseContentLength(header);
                            }
                        }
                        if (headerEnd >= 0 && len - (headerEnd + 4) >= contentLength) break;
                    }

                    var all = ms.ToArray();
                    string headerText = headerEnd >= 0 ? Encoding.ASCII.GetString(all, 0, headerEnd) : "";
                    string path = ParsePath(headerText);
                    string body = "";
                    if (headerEnd >= 0 && contentLength > 0 && headerEnd + 4 + contentLength <= all.Length)
                        body = Encoding.UTF8.GetString(all, headerEnd + 4, contentLength);

                    int qIdx = path.IndexOf('?');
                    string query = qIdx >= 0 ? path.Substring(qIdx + 1) : "";
                    if (qIdx >= 0) path = path.Substring(0, qIdx);
                    if (!string.IsNullOrEmpty(query) && (string.IsNullOrEmpty(body) || body == "{}"))
                        body = QueryToJson(query);

                    string result;
                    int status = 200;
                    try { result = MCPHandlers.Dispatch(path, body); }
                    catch (Exception e) { result = $"{{\"error\":\"{Escape(e.Message)}\"}}"; status = 500; }

                    WriteResponse(stream, status, result);
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[AI Unity MCP Server] Request error: {e.Message}");
            }
        }

        static void WriteResponse(NetworkStream stream, int status, string json)
        {
            byte[] payload = Encoding.UTF8.GetBytes(json ?? "");
            var sb = new StringBuilder();
            sb.Append($"HTTP/1.1 {status} {(status == 200 ? "OK" : "Internal Server Error")}\r\n");
            sb.Append("Content-Type: application/json; charset=utf-8\r\n");
            sb.Append($"Content-Length: {payload.Length}\r\n");
            sb.Append("Connection: close\r\n\r\n");
            byte[] head = Encoding.ASCII.GetBytes(sb.ToString());
            stream.Write(head, 0, head.Length);
            stream.Write(payload, 0, payload.Length);
            stream.Flush();
        }

        static int IndexOfCrlfCrlf(byte[] data, int len)
        {
            for (int i = 0; i + 3 < len; i++)
                if (data[i] == 13 && data[i + 1] == 10 && data[i + 2] == 13 && data[i + 3] == 10)
                    return i;
            return -1;
        }

        static int ParseContentLength(string header)
        {
            foreach (var line in header.Split('\n'))
            {
                var t = line.Trim();
                if (t.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                {
                    var v = t.Substring("Content-Length:".Length).Trim();
                    if (int.TryParse(v, out int len)) return len;
                }
            }
            return 0;
        }

        // request line: "POST /path HTTP/1.1" → "/path"
        static string ParsePath(string header)
        {
            int nl = header.IndexOf('\n');
            string first = nl >= 0 ? header.Substring(0, nl) : header;
            var parts = first.Trim().Split(' ');
            return parts.Length >= 2 ? parts[1] : "/";
        }

        static string QueryToJson(string query)
        {
            if (string.IsNullOrEmpty(query)) return "{}";
            var sb = new StringBuilder("{");
            foreach (var pair in query.Split('&'))
            {
                int eq = pair.IndexOf('=');
                if (eq < 0) continue;
                string k = Uri.UnescapeDataString(pair.Substring(0, eq));
                string v = Uri.UnescapeDataString(pair.Substring(eq + 1));
                bool isBool = v == "true" || v == "false";
                bool isNum  = double.TryParse(v, System.Globalization.NumberStyles.Any,
                                System.Globalization.CultureInfo.InvariantCulture, out _);
                string jsonVal = (isBool || isNum) ? v : $"\"{v.Replace("\"", "\\\"")}\"";
                if (sb.Length > 1) sb.Append(',');
                sb.Append($"\"{k}\":{jsonVal}");
            }
            sb.Append('}');
            return sb.ToString();
        }

        static string Escape(string s) => (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ");
    }
}
