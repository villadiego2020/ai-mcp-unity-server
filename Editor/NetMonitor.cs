using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace AIUnityMCPServer
{
    /// <summary>
    /// </summary>
    [InitializeOnLoad]
    public static class NetMonitor
    {
        static readonly List<double> _rtt = new List<double>();   // ms, ~last 60 samples
        static int _spikeCount;
        static double _lastSample;
        static bool _active;
        const int MAX = 60;
        const double SAMPLE_INTERVAL = 0.5;
        const double SPIKE_FACTOR = 1.8;

        static NetMonitor()
        {
            EditorApplication.playModeStateChanged += OnPlayMode;
        }

        static void OnPlayMode(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredPlayMode)
            {
                _rtt.Clear(); _spikeCount = 0; _lastSample = 0; _active = true;
                EditorApplication.update += Sample;
            }
            else if (state == PlayModeStateChange.ExitingPlayMode && _active)
            {
                EditorApplication.update -= Sample;
                _active = false;
            }
        }

        static void Sample()
        {
            if (!_active || !Application.isPlaying) return;
            double now = EditorApplication.timeSinceStartup;
            if (now - _lastSample < SAMPLE_INTERVAL) return;
            _lastSample = now;

            double rtt = ProfilerDeepReader.LocalRttMs();
            if (rtt < 0) return;

            if (_rtt.Count >= 5)
            {
                double avg = 0; foreach (var v in _rtt) avg += v; avg /= _rtt.Count;
                if (rtt > avg * SPIKE_FACTOR && rtt > 80) _spikeCount++;
            }

            _rtt.Add(rtt);
            if (_rtt.Count > MAX) _rtt.RemoveAt(0);
        }

        public static string GetReport()
        {
            if (_rtt.Count == 0) return null;

            double min = double.MaxValue, max = 0, sum = 0;
            foreach (var v in _rtt) { if (v < min) min = v; if (v > max) max = v; sum += v; }
            double avg = sum / _rtt.Count;

            double var = 0; foreach (var v in _rtt) var += (v - avg) * (v - avg);
            double jitter = Math.Sqrt(var / _rtt.Count);

            var sb = new StringBuilder();
            sb.AppendLine($"\n=== Network Monitor ({_rtt.Count} samples ~{_rtt.Count * SAMPLE_INTERVAL:F0}s) ===");
            sb.AppendLine($"RTT (ping): avg {avg:F0} ms  |  min {min:F0}  |  max {max:F0} ms");
            sb.AppendLine($"Jitter: {jitter:F0} ms  ← {(jitter > 30 ? "high; variable ping can cause stutter or rubber-banding" : "stable")}");
            sb.AppendLine($"Ping spikes: {_spikeCount}  {(_spikeCount > 3 ? "← unstable network" : "")}");
            return sb.ToString();
        }
    }
}
