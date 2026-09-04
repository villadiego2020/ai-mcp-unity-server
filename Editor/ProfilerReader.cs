using System.Text;
using Unity.Profiling;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace MCPBridge
{
    /// <summary>
    /// </summary>
    [InitializeOnLoad]
    public static class ProfilerReader
    {
        static ProfilerRecorder _mainThread, _renderThread;
        static ProfilerRecorder _drawCalls, _setPassCalls, _batches, _triangles, _vertices;
        static ProfilerRecorder _gcAlloc, _gcReserved, _totalReserved, _totalUsed;
        static ProfilerRecorder _texMem, _meshMem;
        static bool _active;

        static ProfilerReader()
        {
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeReload;
        }

        static void OnBeforeReload()
        {
            if (_active) CacheLastValues();
            try { ProfilerDriver.enabled = false; } catch { }
        }

        static bool _hasCaptured;

        static void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredPlayMode) StartRecorders();
            else if (state == PlayModeStateChange.EnteredEditMode && _active) CacheLastValues();
        }

        const int HISTORY = 300;

        const bool ENABLED = false;

        static void StartRecorders()
        {
            if (!ENABLED) return;
            try
            {
                UnityEditorInternal.ProfilerDriver.enabled = true;
                UnityEditorInternal.ProfilerDriver.deepProfiling = false;
                UnityEngine.Profiling.Profiler.enableAllocationCallstacks = AllocCallstacks;
            }
            catch { }

            _mainThread    = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "Main Thread", HISTORY);
            _renderThread  = ProfilerRecorder.StartNew(ProfilerCategory.Render,   "Render Thread", HISTORY);
            _drawCalls     = ProfilerRecorder.StartNew(ProfilerCategory.Render,   "Draw Calls Count");
            _setPassCalls  = ProfilerRecorder.StartNew(ProfilerCategory.Render,   "SetPass Calls Count");
            _batches       = ProfilerRecorder.StartNew(ProfilerCategory.Render,   "Batches Count");
            _triangles     = ProfilerRecorder.StartNew(ProfilerCategory.Render,   "Triangles Count");
            _vertices      = ProfilerRecorder.StartNew(ProfilerCategory.Render,   "Vertices Count");
            _gcAlloc       = ProfilerRecorder.StartNew(ProfilerCategory.Memory,   "GC Allocated In Frame", HISTORY);
            _gcReserved    = ProfilerRecorder.StartNew(ProfilerCategory.Memory,   "GC Reserved Memory");
            _totalReserved = ProfilerRecorder.StartNew(ProfilerCategory.Memory,   "Total Reserved Memory");
            _totalUsed     = ProfilerRecorder.StartNew(ProfilerCategory.Memory,   "Total Used Memory");
            _texMem        = ProfilerRecorder.StartNew(ProfilerCategory.Memory,   "Texture Memory");
            _meshMem       = ProfilerRecorder.StartNew(ProfilerCategory.Memory,   "Mesh Memory");
            _active = true;
        }

        static string _cachedSnapshot;

        static void CacheLastValues()
        {
            _cachedSnapshot = BuildLiveReport("Last frame before stop");
            _hasCaptured = true;

            _mainThread.Dispose(); _renderThread.Dispose();
            _drawCalls.Dispose(); _setPassCalls.Dispose(); _batches.Dispose();
            _triangles.Dispose(); _vertices.Dispose();
            _gcAlloc.Dispose(); _gcReserved.Dispose();
            _totalReserved.Dispose(); _totalUsed.Dispose();
            _texMem.Dispose(); _meshMem.Dispose();
            _active = false;
        }

        /// <summary>
        /// </summary>
        public static string Snapshot()
        {
            string summary;
            if (Application.isPlaying && _active)
                summary = BuildLiveReport("LIVE — Play Mode");
            else if (_hasCaptured && !string.IsNullOrEmpty(_cachedSnapshot))
                summary = _cachedSnapshot;
            else
                return "=== Unity Profiler ===\n(No data yet — press Play so the recorders can capture Profiler values, " +
                       "then click this button during play or after stopping.)";

            return summary + SpikeMonitor.GetReport() + (NetMonitor.GetReport() ?? "") + ProfilerDeepReader.DeepAnalysis();
        }

        public static string GcReport()
        {
            var sb = new StringBuilder("=== GC (memory allocation only) ===\n");
            if (IsLive)
                sb.AppendLine($"GC Allocated / frame: {Bytes(_gcAlloc.LastValue)}  (target: ~0; per-frame allocation causes stutter)");
            if (!AllocCallstacks)
                sb.AppendLine("(Enable the GC toggle in chat and reproduce the allocation to identify the allocating method and line.)");
            sb.Append(ProfilerDeepReader.GcReport());
            return sb.ToString();
        }

        public static string NetworkReport()
        {
            var sb = new StringBuilder();
            string mon = NetMonitor.GetReport();
            if (!string.IsNullOrEmpty(mon)) sb.Append(mon).Append('\n');
            sb.Append(ProfilerDeepReader.NetworkReport());
            return sb.ToString();
        }

        const string ALLOC_CS_PREF = "AIUnityMCPServer_AllocCallstacks";
        public static bool AllocCallstacks
        {
            get => EditorPrefs.GetBool(ALLOC_CS_PREF, false);
            set
            {
                EditorPrefs.SetBool(ALLOC_CS_PREF, value);
                try { UnityEngine.Profiling.Profiler.enableAllocationCallstacks = value; } catch { }
            }
        }

        [InitializeOnLoadMethod]
        static void ForceAllocCallstacksOffOnReload()
        {
            EditorPrefs.SetBool(ALLOC_CS_PREF, false);
            try { UnityEngine.Profiling.Profiler.enableAllocationCallstacks = false; } catch { }
        }

        public static bool IsLive => Application.isPlaying && _active;

        public static string LiveStats()
        {
            if (!IsLive) return null;
            double ms = _mainThread.LastValue * 1e-6;
            double fps = ms > 0 ? 1000.0 / ms : 0;
            return
                $"FPS {fps:F0} | {ms:F1}ms | DC {_drawCalls.LastValue} | SetPass {_setPassCalls.LastValue}\n" +
                $"GC {Bytes(_gcAlloc.LastValue)} | Tris {(_triangles.LastValue / 1000):F1}K | Mem {Bytes(_totalUsed.LastValue)}";
        }

        public static float CurrentFps()
        {
            if (!IsLive) return 0;
            double ms = _mainThread.LastValue * 1e-6;
            return ms > 0 ? (float)(1000.0 / ms) : 0;
        }

        static string BuildLiveReport(string tag)
        {
            var sb = new StringBuilder();

            var frameMs = SamplesMs(_mainThread);
            var renderMs = SamplesMs(_renderThread);
            var gcBytes = SamplesRaw(_gcAlloc);

            int n = frameMs.Length;
            sb.AppendLine($"=== Unity Profiler ({tag}) ===");
            sb.AppendLine($"Sampled {n} frames (~{n / 60.0:F1}s window)");

            if (n > 0)
            {
                System.Array.Sort(frameMs);
                double avg = Mean(frameMs);
                double median = Percentile(frameMs, 50);
                double p95 = Percentile(frameMs, 95);
                double p99 = Percentile(frameMs, 99);
                double worst = frameMs[n - 1];
                double onePctLow = OnePercentLowFps(frameMs);
                double tenthPctLow = LowFps(frameMs, 0.001);

                int stutters = 0;
                foreach (var f in frameMs) if (f > median * 1.5) stutters++;

                double mainAvg = avg, renderAvg = Mean(renderMs);
                string bound = renderAvg > mainAvg * 1.15
                    ? $"GPU/Render-bound (render {renderAvg:F1}ms > main {mainAvg:F1}ms) → optimize draw calls, overdraw, shaders and triangles"
                    : $"CPU-bound (main {mainAvg:F1}ms ≥ render {renderAvg:F1}ms) → optimize scripts, physics and GC";

                sb.AppendLine("\n-- Frame Time (Main Thread) --");
                sb.AppendLine($"Avg: {avg:F2} ms (~{1000.0 / avg:F0} FPS)  |  Median: {median:F2} ms");
                sb.AppendLine($"p95: {p95:F2} ms  |  p99: {p99:F2} ms  |  Worst: {worst:F2} ms");
                sb.AppendLine($"1% Low: {onePctLow:F0} FPS  |  0.1% Low: {tenthPctLow:F0} FPS  ← frame-pacing indicators");
                sb.AppendLine($"Stutter frames (>1.5x median): {stutters}/{n}  ({100.0 * stutters / n:F1}%)");
                sb.AppendLine($"Render Thread avg: {renderAvg:F2} ms");
                sb.AppendLine($"\n** Bound: {bound} **");
            }

            if (gcBytes.Length > 0)
            {
                double totalGc = 0; int gcFrames = 0; double maxGc = 0;
                foreach (var g in gcBytes) { totalGc += g; if (g > 0) gcFrames++; if (g > maxGc) maxGc = g; }
                sb.AppendLine("\n-- GC Allocation --");
                sb.AppendLine($"Frames with GC alloc: {gcFrames}/{gcBytes.Length}  ({100.0 * gcFrames / gcBytes.Length:F0}%)");
                sb.AppendLine($"Avg/frame: {Bytes((long)(totalGc / gcBytes.Length))}  |  Worst frame: {Bytes((long)maxGc)}");
                sb.AppendLine($"Total over window: {Bytes((long)totalGc)}  ← larger totals mean more frequent GC and more stutter risk");
            }

            sb.AppendLine("\n-- Rendering (current) --");
            sb.AppendLine($"Draw Calls: {_drawCalls.LastValue}  |  SetPass: {_setPassCalls.LastValue}  |  Batches: {_batches.LastValue}");
            sb.AppendLine($"Triangles: {_triangles.LastValue:N0}  |  Vertices: {_vertices.LastValue:N0}");

            sb.AppendLine("\n-- Memory --");
            sb.AppendLine($"Total Used: {Bytes(_totalUsed.LastValue)}  |  Reserved: {Bytes(_totalReserved.LastValue)}");
            sb.AppendLine($"GC Reserved: {Bytes(_gcReserved.LastValue)}  |  Texture: {Bytes(_texMem.LastValue)}  |  Mesh: {Bytes(_meshMem.LastValue)}");
            return sb.ToString();
        }

        // ── Helpers ──────────────────────────────────────────────────────
        static double[] SamplesMs(ProfilerRecorder rec)
        {
            int c = rec.Count;
            var arr = new double[c];
            for (int i = 0; i < c; i++) arr[i] = rec.GetSample(i).Value * 1e-6; // ns → ms
            return arr;
        }

        static double[] SamplesRaw(ProfilerRecorder rec)
        {
            int c = rec.Count;
            var arr = new double[c];
            for (int i = 0; i < c; i++) arr[i] = rec.GetSample(i).Value;
            return arr;
        }

        static double Mean(double[] a)
        {
            if (a.Length == 0) return 0;
            double s = 0; foreach (var v in a) s += v; return s / a.Length;
        }

        static double Percentile(double[] sorted, double p)
        {
            if (sorted.Length == 0) return 0;
            int idx = Mathf.Clamp((int)(p / 100.0 * sorted.Length), 0, sorted.Length - 1);
            return sorted[idx];
        }

        static double OnePercentLowFps(double[] sorted) => LowFps(sorted, 0.01);

        static double LowFps(double[] sorted, double fraction)
        {
            if (sorted.Length == 0) return 0;
            int count = Mathf.Max(1, (int)(sorted.Length * fraction));
            double sum = 0;
            for (int i = sorted.Length - count; i < sorted.Length; i++) sum += sorted[i];
            double avgMs = sum / count;
            return avgMs > 0 ? 1000.0 / avgMs : 0;
        }

        public static string BoundStatus()
        {
            if (!IsLive) return "";
            double mainMs = _mainThread.LastValue * 1e-6;
            double renderMs = _renderThread.LastValue * 1e-6;
            return renderMs > mainMs * 1.15 ? "GPU-bound" : "CPU-bound";
        }

        static string Bytes(long b)
        {
            if (b <= 0) return "0 B";
            if (b > 1 << 30) return $"{b / (double)(1 << 30):F2} GB";
            if (b > 1 << 20) return $"{b / (double)(1 << 20):F2} MB";
            if (b > 1 << 10) return $"{b / (double)(1 << 10):F2} KB";
            return $"{b} B";
        }
    }
}
