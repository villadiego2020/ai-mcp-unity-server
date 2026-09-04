using System.Collections.Generic;
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
    public static class SpikeMonitor
    {
        struct Spike
        {
            public double Ms;
            public long Gc;
            public int FrameIndex;
            public string Cause;
        }

        static ProfilerRecorder _mainThread, _gcAlloc;
        static readonly List<Spike> _spikes = new List<Spike>();
        static bool _active;
        static int _lastFrame = -1;
        const int MAX_SPIKES = 40;

        static SpikeMonitor()
        {
            EditorApplication.playModeStateChanged += OnPlayMode;
        }

        static float ThresholdMs => EditorPrefs.GetFloat("AIUnityMCPServer_SpikeMs", 33.3f); // < 30fps = spike

        static void OnPlayMode(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredPlayMode)
            {
                _spikes.Clear();
                _lastFrame = -1;
                _mainThread = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "Main Thread");
                _gcAlloc    = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Allocated In Frame");
                _active = true;
                EditorApplication.update += Sample;
            }
            else if (state == PlayModeStateChange.ExitingPlayMode && _active)
            {
                EditorApplication.update -= Sample;
                _mainThread.Dispose();
                _gcAlloc.Dispose();
                _active = false;
            }
        }

        static void Sample()
        {
            if (!_active || !Application.isPlaying) return;

            double ms = _mainThread.LastValue * 1e-6; // ns → ms
            if (ms < ThresholdMs) return;

            int frame = ProfilerDriver.lastFrameIndex;
            if (frame == _lastFrame) return;
            _lastFrame = frame;

            _spikes.Add(new Spike { Ms = ms, Gc = _gcAlloc.LastValue, FrameIndex = frame, Cause = null });
            if (_spikes.Count > MAX_SPIKES) _spikes.RemoveAt(0);
        }

        public static string WorstReport()
        {
            if (_spikes.Count == 0)
                return "=== Worst Spike ===\n(No FPS drop has been captured in this Play Mode session. Threshold > " +
                       ThresholdMs.ToString("F0") + " ms)";
            int worstIdx = 0;
            for (int i = 1; i < _spikes.Count; i++)
                if (_spikes[i].Ms > _spikes[worstIdx].Ms) worstIdx = i;
            var w = _spikes[worstIdx];

            var sb = new StringBuilder("=== Worst Spike (largest in this Play Mode session) ===\n");
            sb.AppendLine($"{w.Ms:F1} ms (~{1000.0 / w.Ms:F0} FPS), GC {Bytes(w.Gc)} @frame #{w.FrameIndex}");
            sb.AppendLine($"({_spikes.Count} spikes captured; this is the largest)");
            string src = ProfilerDeepReader.FrameCulpritWithSource(w.FrameIndex);
            if (!string.IsNullOrEmpty(src)) { sb.AppendLine("\n-- Contributor and source --"); sb.Append(src); }
            else sb.AppendLine("(The call tree for this frame is no longer available. Request the worst spike again immediately after it occurs.)");
            return sb.ToString();
        }

        public static string GetReport()
        {
            if (_spikes.Count == 0)
                return "\n=== Auto Spike Monitor ===\n(No FPS drop captured. Current threshold > " + ThresholdMs.ToString("F0") +
                       " ms. Enter Play Mode and reproduce the stutter.)";

            var sb = new StringBuilder();
            sb.AppendLine($"\n=== Auto Spike Monitor — {_spikes.Count} spikes captured ===");
            sb.AppendLine($"(Threshold: frame > {ThresholdMs:F0} ms = below {1000f / ThresholdMs:F0} FPS)");

            int worstIdx = 0;
            for (int i = 1; i < _spikes.Count; i++)
                if (_spikes[i].Ms > _spikes[worstIdx].Ms) worstIdx = i;
            var worst = _spikes[worstIdx];

            string worstCause = ProfilerDeepReader.TopContributor(worst.FrameIndex);
            sb.AppendLine($"\nWorst: {worst.Ms:F1} ms (~{1000.0 / worst.Ms:F0} FPS), GC {Bytes(worst.Gc)} @frame #{worst.FrameIndex}");
            if (!string.IsNullOrEmpty(worstCause))
                sb.AppendLine($"  Contributor → {worstCause}");

            sb.AppendLine("\nCaptured spikes:");
            var sorted = new List<Spike>(_spikes);
            sorted.Sort((a, b) => b.Ms.CompareTo(a.Ms));
            for (int i = 0; i < Mathf.Min(sorted.Count, 8); i++)
            {
                var sp = sorted[i];
                string cause = i < 3 ? ProfilerDeepReader.TopContributor(sp.FrameIndex) : null;
                string tail = string.IsNullOrEmpty(cause) ? $"GC {Bytes(sp.Gc)}" : cause;
                sb.AppendLine($"  {sp.Ms:F0}ms (~{1000.0 / sp.Ms:F0}fps)  ←  {tail}");
            }

            string net = ProfilerDeepReader.NetworkLine();
            if (!string.IsNullOrEmpty(net))
                sb.AppendLine($"\nNetwork: {net}");

            return sb.ToString();
        }

        static string Bytes(long b)
        {
            if (b <= 0) return "0 B";
            if (b > 1 << 20) return $"{b / (double)(1 << 20):F2} MB";
            if (b > 1 << 10) return $"{b / (double)(1 << 10):F1} KB";
            return $"{b} B";
        }
    }
}
