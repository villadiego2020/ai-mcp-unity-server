using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace AIUnityMCPServer
{
    /// <summary>
    ///
    ///   • runner.TryGetFusionStatistics(out mgr)              → global snapshot (In/OutBandwidth, packets, RTT)
    ///   • mgr.ObjectStatisticsManager                          → per-object manager
    ///   • objMgr.GetNetworkObjectStatistics(NetworkId, out snap)→ NetworkObjectStatisticsSnapshot
    ///
    /// </summary>
    public static class NetStatsReader
    {
        struct Monitored { public object Id; public string Name; }
        static readonly List<Monitored> _monitored = new List<Monitored>();
        static object _objMgr;        // Fusion.Statistics.NetworkObjectStatisticsManager
        static object _runner;        // Fusion.NetworkRunner
        static System.Reflection.MethodInfo _collectM;
        static bool   _active;
        static string _diag;

        const int MAX_MONITOR = 500;

        public static bool BeginMonitor()
        {
            Reset();
            try
            {
                if (!Application.isPlaying) { _diag = "not in Play Mode"; return false; }

                var runnerType = FindType("Fusion.NetworkRunner");
                if (runnerType == null) { _diag = "Fusion.NetworkRunner type not found"; return false; }
                _runner = UnityEngine.Object.FindObjectOfType(runnerType);
                if (_runner == null) { _diag = "NetworkRunner instance not found in the scene"; return false; }

                // TryGetFusionStatistics(out FusionStatisticsManager)
                var tryGet = runnerType.GetMethod("TryGetFusionStatistics");
                if (tryGet == null) { _diag = "TryGetFusionStatistics method not found; the Fusion version may differ"; return false; }
                var a = new object[] { null };
                bool ok = false;
                try { ok = (bool)tryGet.Invoke(_runner, a); }
                catch (Exception e) { _diag = "TryGetFusionStatistics throw: " + e.Message; return false; }
                object mgr = a[0];
                if (!ok || mgr == null) { _diag = $"TryGetFusionStatistics returned {ok} / mgr={(mgr==null?"null":"ok")}; Fusion statistics are not active on the runner"; return false; }

                _objMgr = mgr.GetType().GetProperty("ObjectStatisticsManager")?.GetValue(mgr);
                if (_objMgr == null) { _diag = "ObjectStatisticsManager is null"; return false; }
                _collectM = _objMgr.GetType().GetMethod("CollectStatistics", Type.EmptyTypes);

                var noType = FindType("Fusion.NetworkObject");
                if (noType == null) { _diag = "Fusion.NetworkObject type not found"; return false; }
                var idProp = noType.GetProperty("Id");
                var monitorM = _objMgr.GetType().GetMethod("MonitorNetworkObjectStatistics");
                if (idProp == null || monitorM == null) { _diag = $"idProp={(idProp==null?"null":"ok")} monitorM={(monitorM==null?"null":"ok")}"; return false; }

                int totalFound = 0;
                foreach (var no in UnityEngine.Object.FindObjectsOfType(noType))
                {
                    totalFound++;
                    if (_monitored.Count >= MAX_MONITOR) break;
                    try
                    {
                        var go = (no as Component)?.gameObject;
                        if (go == null || !go.activeInHierarchy) continue;
                        object id = idProp.GetValue(no);
                        monitorM.Invoke(_objMgr, new object[] { id, true });
                        _monitored.Add(new Monitored { Id = id, Name = go.name });
                    }
                    catch (Exception e) { _diag = "monitor obj throw: " + e.Message; }
                }

                _active = _monitored.Count > 0;
                _diag = $"Found {totalFound} NetworkObjects; monitoring {_monitored.Count}";
                if (!_active) _diag += " (no active objects available)";
                return _active;
            }
            catch (Exception e) { _diag = "BeginMonitor throw: " + e.Message; Reset(); return false; }
        }

        public static string LastDiag => _diag;

        public static void PumpCollect()
        {
            if (!_active || _collectM == null) return;
            try { _collectM.Invoke(_objMgr, null); } catch { }
        }

        public static string EndMonitorAndReport()
        {
            if (!_active)
                return "\n-- Network: data unavailable (" + (_diag ?? "monitor not started") + ") --";

            var sb = new StringBuilder();
            int read = 0, withData = 0, snapNull = 0, getThrow = 0; string firstSnapType = null;
            try
            {
                var objMgrType = _objMgr.GetType();
                try { _collectM?.Invoke(_objMgr, null); } catch { }

                var getM = objMgrType.GetMethod("GetNetworkObjectStatistics");
                if (getM == null) return "\n-- Network pinpoint (debug) --\n  GetNetworkObjectStatistics method not found";

                var byPrefab = new Dictionary<string, (double inBw, double outBw, int count)>();
                foreach (var m in _monitored)
                {
                    try
                    {
                        var a = new object[] { m.Id, null };
                        getM.Invoke(_objMgr, a);
                        read++;
                        object snap = a[1];
                        if (snap == null) { snapNull++; continue; }
                        var st = snap.GetType();
                        if (firstSnapType == null) firstSnapType = st.Name;
                        double inBw  = ToD(st.GetProperty("InBandwidth")?.GetValue(snap));
                        double outBw = ToD(st.GetProperty("OutBandwidth")?.GetValue(snap));
                        if (inBw > 0 || outBw > 0) withData++;
                        string key = NormalizeName(m.Name);
                        byPrefab.TryGetValue(key, out var acc);
                        byPrefab[key] = (acc.inBw + inBw, acc.outBw + outBw, acc.count + 1);
                    }
                    catch { getThrow++; }
                }

                if (byPrefab.Count > 0 && withData > 0)
                {
                    sb.AppendLine("\n-- Network bandwidth per prefab (actual Fusion bytes, sorted by outbound traffic) --");
                    sb.AppendLine("  prefab | count | In | Out");
                    foreach (var kv in byPrefab.OrderByDescending(x => x.Value.outBw).Take(12))
                        sb.AppendLine($"  {kv.Key} | x{kv.Value.count} | in {Bytes(kv.Value.inBw)} | out {Bytes(kv.Value.outBw)}");
                    sb.AppendLine("  (High outbound traffic indicates expensive synchronization; consider a lower sync rate, fewer [Networked] values, culling or interpolation.)");
                }
                else if (snapNull > 0 || getThrow > 0)
                {
                    sb.AppendLine($"\n-- Network: per-object data unavailable (snapNull={snapNull} err={getThrow}/{_monitored.Count}); report this to a developer --");
                }
                else
                {
                    sb.AppendLine($"\n-- Network: object synchronization bandwidth is very low ({_monitored.Count} objects, ~0 B); capture again during heavier activity --");
                }

                string global = GlobalLine();
                if (!string.IsNullOrEmpty(global)) { sb.AppendLine("\n-- Network global --"); sb.Append(global); }
            }
            catch (Exception e) { sb.AppendLine("\n(net stats error: " + e.Message + ")"); }
            finally { StopMonitor(); }

            return sb.Length > 0 ? sb.ToString() : null;
        }

        static string GlobalLine()
        {
            try
            {
                var runnerType = _runner?.GetType();
                if (runnerType == null) return null;
                var a = new object[] { null };
                if (!(bool)runnerType.GetMethod("TryGetFusionStatistics").Invoke(_runner, a)) return null;
                object mgr = a[0];
                object snap = mgr?.GetType().GetProperty("CompleteSnapshot")?.GetValue(mgr);
                if (snap == null) return null;
                var st = snap.GetType();
                double inBw  = ToD(st.GetProperty("InBandwidth")?.GetValue(snap));
                double outBw = ToD(st.GetProperty("OutBandwidth")?.GetValue(snap));
                double inUpd = ToD(st.GetProperty("InObjectUpdates")?.GetValue(snap));
                double outUpd= ToD(st.GetProperty("OutObjectUpdates")?.GetValue(snap));
                double resim = ToD(st.GetProperty("Resimulations")?.GetValue(snap));
                double rtt   = ToD(st.GetProperty("RoundTripTime")?.GetValue(snap));
                var sb = new StringBuilder();
                sb.AppendLine($"  In {Bytes(inBw)} | Out {Bytes(outBw)} | objUpdates in/out {inUpd:F0}/{outUpd:F0} | resim {resim:F0} | RTT {rtt * 1000:F0}ms");
                return sb.ToString();
            }
            catch { return null; }
        }

        static void StopMonitor()
        {
            try
            {
                if (_objMgr != null)
                {
                    var t = _objMgr.GetType();
                    var clear = t.GetMethod("ClearMonitoredNetworkObjects");
                    if (clear != null) clear.Invoke(_objMgr, null);
                    else
                    {
                        var monitorM = t.GetMethod("MonitorNetworkObjectStatistics");
                        foreach (var m in _monitored)
                            try { monitorM?.Invoke(_objMgr, new object[] { m.Id, false }); } catch { }
                    }
                }
            }
            catch { }
            Reset();
        }

        public static void Cancel() { if (_active) StopMonitor(); }

        static void Reset() { _monitored.Clear(); _objMgr = null; _runner = null; _collectM = null; _active = false; }

        // ── helpers ──
        static double ToD(object o)
        {
            try { return o == null ? 0 : Convert.ToDouble(o); } catch { return 0; }
        }

        static string NormalizeName(string n)
        {
            if (string.IsNullOrEmpty(n)) return "?";
            n = n.Replace("(Clone)", "").Trim();
            int i = n.Length;
            while (i > 0 && (char.IsDigit(n[i - 1]) || n[i - 1] == ' ' || n[i - 1] == '_' || n[i - 1] == '-')) i--;
            return i > 0 ? n.Substring(0, i) : n;
        }

        static Type FindType(string fullName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = asm.GetType(fullName);
                if (t != null) return t;
            }
            return null;
        }

        static string Bytes(double b)
        {
            if (b <= 0) return "0 B";
            if (b > 1 << 20) return $"{b / (1 << 20):F2} MB";
            if (b > 1 << 10) return $"{b / (1 << 10):F1} KB";
            return $"{b:F0} B";
        }
    }
}
