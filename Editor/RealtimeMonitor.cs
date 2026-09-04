using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using Debug = UnityEngine.Debug;

namespace AIUnityMCPServer
{
    /// <summary>
    /// </summary>
    [InitializeOnLoad]
    public static class RealtimeMonitor
    {
        const string PREF_ENABLED = "AIUnityMCPServer_MonitorEnabled";
        const string PREF_MEM_MB   = "AIUnityMCPServer_MonitorMemMB";
        const int    SPIKE_MB      = 800;
        const int    STALL_MS      = 5000;
        const int    POLL_MS       = 1000;

        static Thread _thread;
        static volatile bool _running;
        static string _logPath;

        static readonly Stopwatch _sw = Stopwatch.StartNew();
        static long _lastBeatMs;
        static volatile bool _isPlaying;
        static volatile string _scene = "?";
        static volatile int _memThresholdMB = 6000;

        public static bool IsOn => _running;
        public static int MemThresholdMB
        {
            get => EditorPrefs.GetInt(PREF_MEM_MB, 6000);
            set => EditorPrefs.SetInt(PREF_MEM_MB, Mathf.Max(512, value));
        }

        static RealtimeMonitor()
        {
            if (EditorPrefs.GetBool(PREF_ENABLED, false))
                Start();
        }

        public static void Toggle()
        {
            if (_running) Stop(); else Start(clearLog: true);
            EditorPrefs.SetBool(PREF_ENABLED, _running);
        }

        public static void Start(bool clearLog = false)
        {
            if (_running) return;
            _running = true;

            EnsureLogPath();
            if (clearLog) ClearLog();

            EditorApplication.update += Heartbeat;
            EditorApplication.playModeStateChanged += OnPlayMode;
            EditorApplication.quitting += OnQuit;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeReload;

            _memThresholdMB = MemThresholdMB;
            Interlocked.Exchange(ref _lastBeatMs, _sw.ElapsedMilliseconds);
            _thread = new Thread(Loop) { IsBackground = true, Name = "AIUnityMCPServer-Monitor" };
            _thread.Start();
            LogLine("MONITOR", "Monitoring started (threshold " + _memThresholdMB + "MB)");
            Debug.Log($"[AI Unity MCP Server] Realtime monitor ON\nlog: {_logPath}");
        }

        public static void Stop()
        {
            if (!_running) return;
            _running = false;
            EditorApplication.update -= Heartbeat;
            EditorApplication.playModeStateChanged -= OnPlayMode;
            EditorApplication.quitting -= OnQuit;
            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeReload;
            try { _thread?.Join(300); } catch { }
            _thread = null;
            LogLine("MONITOR", "Monitoring stopped");
            Debug.Log($"[AI Unity MCP Server] Realtime monitor OFF\nlog: {_logPath}");
        }

        static int _beatFrame;
        static void Heartbeat()
        {
            Interlocked.Exchange(ref _lastBeatMs, _sw.ElapsedMilliseconds);
            _isPlaying = EditorApplication.isPlaying;
            if ((_beatFrame++ & 63) == 0)
            {
                try { _scene = SceneManager.GetActiveScene().name; } catch { }
                _memThresholdMB = MemThresholdMB;
            }
        }

        static void OnPlayMode(PlayModeStateChange c)
        {
            string extra = "";
            if (c == PlayModeStateChange.ExitingPlayMode)
            {
                try
                {
                    var trAll = Resources.FindObjectsOfTypeAll<Transform>();
                    var mbs = Resources.FindObjectsOfTypeAll<MonoBehaviour>();
                    int ps = UnityEngine.Object.FindObjectsOfType<ParticleSystem>().Length;
                    int rb = UnityEngine.Object.FindObjectsOfType<Rigidbody>().Length;

                    var counts = new System.Collections.Generic.Dictionary<string, int>();
                    foreach (var m in mbs)
                    {
                        if (m == null) continue;
                        string n = m.GetType().Name;
                        counts.TryGetValue(n, out int v);
                        counts[n] = v + 1;
                    }
                    var top = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, int>>(counts);
                    top.Sort((a, b) => b.Value.CompareTo(a.Value));
                    var sb = new System.Text.StringBuilder();
                    for (int i = 0; i < top.Count && i < 10; i++) sb.Append($"{top[i].Key}×{top[i].Value}, ");

                    extra = $" · transforms={trAll.Length} mono={mbs.Length} (particle={ps}, rb={rb})\n         top mono: {sb}";
                }
                catch { }
            }
            LogLine("PLAY", c + " · " + MemLine() + extra);
        }
        static void OnQuit() => LogLine("QUIT", "Unity closing · " + MemLine());
        static void OnBeforeReload() => LogLine("RELOAD", "Before recompile · " + MemLine());

        static long _lastWorkingMB = -1;
        static bool _wasHigh;
        static bool _wasStalled;
        static long _lastHighLogMs;

        static void Loop()
        {
            var proc = Process.GetCurrentProcess();
            while (_running)
            {
                try
                {
                    Thread.Sleep(POLL_MS);
                    if (!_running) break;

                    proc.Refresh();
                    long workingMB = proc.WorkingSet64 / (1024 * 1024);
                    long now = _sw.ElapsedMilliseconds;

                    long beat = Interlocked.Read(ref _lastBeatMs);
                    long sinceBeat = now - beat;
                    if (sinceBeat > STALL_MS)
                    {
                        if (!_wasStalled)
                        {
                            _wasStalled = true;
                            LogLine("STALL", $"Main thread unresponsive for {sinceBeat / 1000}s (compile/import/freeze?) · ws={workingMB}MB · scene={_scene} · playing={_isPlaying} (inspect the preceding [MARK])");
                        }
                    }
                    else if (_wasStalled)
                    {
                        _wasStalled = false;
                        LogLine("STALL", $"Main thread recovered after ~{STALL_MS / 1000}s+");
                    }

                    if (_lastWorkingMB >= 0 && workingMB - _lastWorkingMB >= SPIKE_MB)
                        LogLine("MEM-SPIKE", $"+{workingMB - _lastWorkingMB}MB in {POLL_MS / 1000.0:0.#}s → ws={workingMB}MB · scene={_scene} · playing={_isPlaying}");

                    bool high = workingMB >= _memThresholdMB;
                    if (high && (!_wasHigh || now - _lastHighLogMs > 30000))
                    {
                        LogLine("MEM-HIGH", $"ws={workingMB}MB (above {_memThresholdMB}MB) · {MemLineBg()} · scene={_scene} · playing={_isPlaying}");
                        _lastHighLogMs = now;
                    }
                    _wasHigh = high;
                    _lastWorkingMB = workingMB;
                }
                catch (Exception e)
                {
                    try { LogLine("ERROR", "loop: " + e.Message); } catch { }
                }
            }
        }

        static string MemLine()
        {
            long mono = UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong() / (1024 * 1024);
            long total = UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong() / (1024 * 1024);
            return $"mono={mono}MB · unityTotal={total}MB";
        }

        static string MemLineBg()
        {
            long gc = GC.GetTotalMemory(false) / (1024 * 1024);
            return $"gcHeap={gc}MB";
        }

        static readonly object _logLock = new object();

        static void EnsureLogPath()
        {
            if (_logPath != null) return;
            try
            {
                string dir = Path.Combine(Application.dataPath, "..", "Library", "AIUnityMCPServer");
                Directory.CreateDirectory(dir);
                _logPath = Path.GetFullPath(Path.Combine(dir, "monitor.log"));
            }
            catch { }
        }

        static void ClearLog()
        {
            lock (_logLock) { try { if (_logPath != null) File.Delete(_logPath); } catch { } }
        }

        static void LogLine(string tag, string msg)
        {
            if (_logPath == null) return;
            lock (_logLock)
            {
                try
                {
                    using (var fs = new FileStream(_logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                    using (var w = new StreamWriter(fs))
                        w.WriteLine($"{DateTime.Now:HH:mm:ss.fff} [{tag}] {msg}");
                }
                catch { }
            }
        }
    }
}
