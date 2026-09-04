using System;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace AIUnityMCPServer
{
    /// <summary>
    ///
    ///
    /// </summary>
    [InitializeOnLoad]
    public static class CpuDeepCapture
    {
        static CpuDeepCapture()
        {
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeReload;

            try { if (ProfilerDriver.deepProfiling) ProfilerDriver.deepProfiling = false; } catch { }
        }

        static void OnBeforeReload()
        {
            if (!IsCapturing) return;
            EditorApplication.update -= Tick;
            IsCapturing = false;
            Restore();
            try { NetStatsReader.Cancel(); } catch { }
            _onDone = null;
        }

        public static bool  IsCapturing { get; private set; }
        public static float Progress01  { get; private set; }
        public static int   SecondsLeft => Mathf.Max(0, Mathf.CeilToInt(_duration * (1f - Progress01)));

        static double _startTime;
        static float  _duration;
        static int    _startFrame;
        static bool   _prevDeep;
        static bool   _prevAllocCs;
        static Action<string> _onDone;

        public static void Start(float seconds, Action<string> onDone)
        {
            if (IsCapturing) return;
            if (!Application.isPlaying) return;

            try
            {
                _prevDeep    = ProfilerDriver.deepProfiling;
                _prevAllocCs = UnityEngine.Profiling.Profiler.enableAllocationCallstacks;
                ProfilerDriver.enabled = true;
                ProfilerDriver.deepProfiling = true;
                UnityEngine.Profiling.Profiler.enableAllocationCallstacks = true;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[CpuDeepCapture] Could not enable Deep Profile: " + e.Message);
                return;
            }

            try { NetStatsReader.BeginMonitor(); } catch { }

            _onDone     = onDone;
            _duration   = Mathf.Max(1f, seconds);
            _startTime  = EditorApplication.timeSinceStartup;
            _startFrame = ProfilerDriver.lastFrameIndex;
            IsCapturing = true;
            Progress01  = 0f;
            EditorApplication.update += Tick;
        }

        static void Restore()
        {
            try { ProfilerDriver.deepProfiling = _prevDeep; } catch { }
            try { UnityEngine.Profiling.Profiler.enableAllocationCallstacks = _prevAllocCs; } catch { }
        }

        static void Tick()
        {
            double elapsed = EditorApplication.timeSinceStartup - _startTime;
            Progress01 = Mathf.Clamp01((float)(elapsed / _duration));

            try { NetStatsReader.PumpCollect(); } catch { }

            if (elapsed < _duration && Application.isPlaying)
                return;

            EditorApplication.update -= Tick;
            IsCapturing = false;

            int endFrame = ProfilerDriver.lastFrameIndex;
            Restore();

            string report;
            try
            {
                string cpu = ProfilerDeepReader.CpuDeepReport(_startFrame, endFrame);
                string gc  = ProfilerDeepReader.GcCallstackReport();
                string net = null;
                try { net = NetStatsReader.EndMonitorAndReport(); } catch { }
                report = cpu
                       + (string.IsNullOrEmpty(gc)  ? "" : "\n" + gc)
                       + (string.IsNullOrEmpty(net) ? "" : "\n" + net);
            }
            catch (Exception e) { report = "=== Deep Analysis ===\n(Analysis failed: " + e.Message + ")"; }

            var cb = _onDone;
            _onDone = null;
            cb?.Invoke(report);
        }
    }
}
