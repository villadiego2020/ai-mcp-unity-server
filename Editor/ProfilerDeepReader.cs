using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor.Profiling;
using UnityEditorInternal;
using UnityEngine;

namespace MCPBridge
{
    /// <summary>
    /// </summary>
    public static class ProfilerDeepReader
    {
        struct Sample
        {
            public string Name;
            public double GcBytes;
            public double SelfMs;
            public double TotalMs;
            public int Calls;
        }

        static readonly string[] IdleMarkers =
        {
            "EditorLoop", "WaitForTargetFPS", "Gfx.WaitForPresent", "Gfx.PresentFrame",
            "Semaphore.WaitForSignal", "WaitForGraphicsThreadStartup", "Gfx.WaitForRenderThread",
            "VSync", "WaitForReadback", "Idle", "Profiler.CollectEditorStats",
            "Application.FlushRenderStats", "Profiler.FlushCounters"
        };

        static bool IsIdle(string name)
        {
            foreach (var m in IdleMarkers)
                if (name.IndexOf(m, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        static string FlagSuspicious(Sample s, int frames)
        {
            string n = s.Name;
            bool Has(string k) => n.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0;
            double gcPerFrame = frames > 0 ? s.GcBytes / frames : s.GcBytes;

            if (Has("FindObjectsOfType") || Has("FindObjectOfType"))
                return $"⚠ {n} — FindObjectOfType is O(n) over all objects. Cache the reference.";
            if (Has("GameObject.Find"))
                return $"⚠ {n} — GameObject.Find scans the whole scene. Cache it in Awake/Start.";
            if (Has("Instantiate") && gcPerFrame > 50_000)
                return $"⚠ {n} — {Bytes((long)gcPerFrame)}/frame from Instantiate. Use object pooling.";
            if (Has("GetComponent") && s.Calls / System.Math.Max(1, frames) > 200)
                return $"⚠ {n} — heavy GetComponent usage. Cache components, don't call in Update.";
            if (Has("Camera.main"))
                return $"⚠ {n} — Camera.main does FindObjectWithTag internally. Cache the camera.";
            if (Has("Linq") || Has("Enumerable"))
                return $"⚠ {n} — LINQ in hot path allocates. Replace with for-loops.";
            if (Has("VolumeManager") && gcPerFrame > 20_000)
                return $"⚠ {n} — {Bytes((long)gcPerFrame)}/frame from URP Volume eval. Reduce active Volumes.";
            if ((Has("Shadows.RenderShadowMap") || Has("Shadows.RenderCollectShadows")) && s.SelfMs / frames > 3.0)
                return $"⚠ {n} — Shadow rendering {s.SelfMs / frames:F1} ms/frame. Reduce Shadow Distance, bake lights as Mixed or Baked, or reduce cascades.";
            if ((Has("Canvas.SendWillRenderCanvases") || Has("Canvas.BuildBatch")) && s.SelfMs / frames > 1.0)
                return $"⚠ {n} — Canvas rebuild {s.SelfMs / frames:F1} ms/frame. Separate dynamic UI from static UI into different Canvases.";
            if (Has("Skinning") && s.SelfMs / frames > 2.0)
                return $"⚠ {n} — CPU Skinning {s.SelfMs / frames:F1} ms/frame. Enable GPU skinning in Project Settings → Graphics, reduce bone counts, or add LODs.";
            if (Has("SendMessage") && s.Calls / System.Math.Max(1, frames) > 50)
                return $"⚠ {n} — SendMessage ~{s.Calls / System.Math.Max(1, frames)}/frame. Prefer direct method calls or C# events/delegates.";
            if (Has("Resources.Load"))
                return $"⚠ {n} — Resources.Load is in a hot path. Preload in Awake/Start or use Addressables.";
            if (Has("OnGUI") && s.SelfMs / frames > 0.5)
                return $"⚠ {n} — OnGUI {s.SelfMs / frames:F1} ms/frame (legacy UI). Prefer uGUI or UI Toolkit.";
            if (Has("PostProcess") && s.SelfMs / frames > 3.0)
                return $"⚠ {n} — Post-processing {s.SelfMs / frames:F1} ms/frame. Disable unnecessary effects or reduce render resolution.";
            if (Has("ParticleSystem") && s.SelfMs / frames > 2.0)
                return $"⚠ {n} — Particle update {s.SelfMs / frames:F1} ms/frame. Reduce maximum particles, disable particle collision, or use GPU particles.";
            if (gcPerFrame > 200_000 && !IsIdle(n) && !IsAggregateMarker(n))
                return $"⚠ {n} — high GC {Bytes((long)gcPerFrame)}/frame. Investigate allocations.";
            return null;
        }

        static readonly string[] AggregateMarkers =
        {
            "Simulation", "FixedUpdate", "BehaviourUpdate", "BeforeSimulation",
            "UpdateManager", "SingleEntrypoint", "InvokeFixedUpdateNetwork",
            "RunClientSideResimulationLoop", "PlayerLoop", "Update.ScriptRunBehaviourUpdate",
        };

        static bool IsAggregateMarker(string name)
        {
            foreach (var m in AggregateMarkers)
                if (name.IndexOf(m, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        public static string DeepAnalysis(int topN = 8)
        {
            var sb = new StringBuilder();
            try
            {
                int first = ProfilerDriver.firstFrameIndex;
                int last = ProfilerDriver.lastFrameIndex;
                if (last < 0)
                {
                    return "\n=== Deep Analysis ===\n(No captured frames. Open the Profiler window and Play so it records CPU data.)";
                }

                int scanStart = Mathf.Max(first, last - 60);
                int framesScanned = last - scanStart + 1;
                var samples = AggregateSamples(scanStart, last);

                if (samples.Count == 0)
                {
                    return "\n=== Deep Analysis ===\n(No CPU hierarchy data — enable the CPU Usage module in the Profiler.)";
                }

                sb.AppendLine($"\n=== Deep Analysis (aggregated over {framesScanned} frames) ===");

                sb.AppendLine("\n-- Top GC Allocators (summed over window — consistent offenders) --");
                foreach (var s in samples.Where(x => x.GcBytes > 0 && !IsIdle(x.Name))
                                         .OrderByDescending(x => x.GcBytes).Take(topN))
                    sb.AppendLine($"  {Bytes(s.GcBytes)} total  |  {Bytes((long)(s.GcBytes / framesScanned))}/frame  ←  {s.Name}");

                if (!samples.Any(x => x.GcBytes > 0))
                    sb.AppendLine("  (no GC allocation — good!)");

                string gcCs = GcCallstackReport();
                if (!string.IsNullOrEmpty(gcCs)) sb.Append(gcCs);

                sb.AppendLine("\n-- Top CPU Self-Time (avg per frame, idle excluded) --");
                foreach (var s in samples.Where(x => !IsIdle(x.Name))
                                         .OrderByDescending(x => x.SelfMs).Take(topN))
                    sb.AppendLine($"  {s.SelfMs / framesScanned:F2} ms/frame  ←  {s.Name}  (total over window: {s.SelfMs:F1} ms)");

                sb.AppendLine("\n-- Suspicious Patterns (top traps) --");
                var flagged = samples
                    .Select(s => new { s, warn = FlagSuspicious(s, framesScanned) })
                    .Where(x => x.warn != null)
                    .OrderByDescending(x => x.s.GcBytes + x.s.SelfMs * 1000)
                    .Take(6)
                    .ToList();
                if (flagged.Count == 0)
                    sb.AppendLine("  (none detected — enable Deep Profile for method-level detail)");
                else
                    foreach (var x in flagged) sb.AppendLine("  " + x.warn);

                // Subsystem CPU breakdown (rendering / physics / UI / animation / audio / network)
                string subsys = SubsystemBreakdown(samples, framesScanned);
                if (!string.IsNullOrEmpty(subsys)) sb.Append(subsys);

                string renderThread = RenderThreadAnalysis(scanStart, last, framesScanned);
                if (!string.IsNullOrEmpty(renderThread)) sb.Append(renderThread);

                var topOffenders = samples.Where(x => !IsIdle(x.Name) && !IsAggregateMarker(x.Name))
                    .OrderByDescending(x => x.GcBytes + x.SelfMs * 1000)
                    .Select(x => x.Name)
                    .Distinct()
                    .Take(6)
                    .ToList();
                var resolved = new StringBuilder();
                var seenFiles = new HashSet<string>();
                foreach (var name in topOffenders)
                {
                    string src = ResolveSource(name, seenFiles);
                    if (src != null) resolved.Append(src);
                }
                if (resolved.Length > 0)
                {
                    sb.AppendLine("\n-- Source of top offenders (project scripts with exact lines) --");
                    sb.Append(resolved);
                }

                // Network stats (Photon Fusion 2)
                string net = ReadNetworkStats();
                if (!string.IsNullOrEmpty(net))
                {
                    sb.AppendLine("\n-- Network (Photon Fusion) --");
                    sb.AppendLine(net);
                }
            }
            catch (Exception e)
            {
                sb.AppendLine($"\n=== Deep Analysis ===\n(error: {e.Message})");
            }
            return sb.ToString();
        }

        public static string GcReport(int topN = 8)
        {
            var sb = new StringBuilder("=== GC Analysis ===");
            try
            {
                int first = ProfilerDriver.firstFrameIndex, last = ProfilerDriver.lastFrameIndex;
                if (last < 0) return sb.Append("\n(No captured frames; remain in Play Mode briefly.)").ToString();
                int scanStart = Mathf.Max(first, last - 60);
                int frames = last - scanStart + 1;
                var samples = AggregateSamples(scanStart, last);

                var gc = samples.Where(x => x.GcBytes > 0 && !IsIdle(x.Name))
                                .OrderByDescending(x => x.GcBytes).Take(topN).ToList();
                if (gc.Count == 0) return sb.Append("\n(No GC allocation captured.)").ToString();

                sb.AppendLine($"\n-- Top GC Allocators ({frames} frames) --");
                foreach (var s in gc)
                    sb.AppendLine($"  {Bytes(s.GcBytes)} total | {Bytes((long)(s.GcBytes / frames))}/frame  ←  {s.Name}");

                var seen = new HashSet<string>();
                var src = new StringBuilder();
                foreach (var s in gc.Take(4))
                {
                    string r = ResolveSource(s.Name, seen);
                    if (r != null) src.Append(r);
                }
                if (src.Length > 0) { sb.AppendLine("\n-- Source of GC contributors --"); sb.Append(src); }

                string cs = GcCallstackReport();
                if (!string.IsNullOrEmpty(cs)) sb.Append(cs);
            }
            catch (Exception e) { sb.Append($"\n(error: {e.Message})"); }
            return sb.ToString();
        }

        // ── GC Allocation Callstacks ────────────────────────────────────────
        public static string GcCallstackReport(int topN = 6, int maxFramesScan = 30, int maxResolve = 500, int maxAttempts = 2000)
        {
            try
            {
                int first = ProfilerDriver.firstFrameIndex, last = ProfilerDriver.lastFrameIndex;
                if (last < 0) return null;
                int scanStart = Mathf.Max(first, last - maxFramesScan + 1);
                int frames = last - scanStart + 1;

                var byLoc = new Dictionary<string, (double bytes, int count, string methodLine)>();
                int resolved = 0;
                int attempts = 0;
                var stack = new Stack<int>();
                var children = new List<int>();

                for (int f = scanStart; f <= last && resolved < maxResolve && attempts < maxAttempts; f++)
                {
                    using var view = ProfilerDriver.GetHierarchyFrameDataView(
                        f, 0, HierarchyFrameDataView.ViewModes.Default,
                        HierarchyFrameDataView.columnGcMemory, false);
                    if (view == null || !view.valid) continue;

                    stack.Clear();
                    stack.Push(view.GetRootItemID());
                    while (stack.Count > 0 && resolved < maxResolve && attempts < maxAttempts)
                    {
                        int id = stack.Pop();
                        children.Clear();
                        view.GetItemChildren(id, children);
                        foreach (int c in children)
                        {
                            string nm = view.GetItemName(c);
                            double gc = view.GetItemColumnDataAsFloat(c, HierarchyFrameDataView.columnGcMemory);
                            if (gc > 0 && attempts < maxAttempts && nm.IndexOf("GC.Alloc", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                string callstack = null;
                                attempts++;
                                try { callstack = view.ResolveItemCallstack(c); } catch { }
                                var (display, methodLine) = ExtractTopProjectFrame(callstack);
                                if (display != null)
                                {
                                    byLoc.TryGetValue(display, out var acc);
                                    byLoc[display] = (acc.bytes + gc, acc.count + 1, methodLine ?? acc.methodLine);
                                    resolved++;
                                }
                            }
                            stack.Push(c);
                        }
                    }
                }

                if (byLoc.Count == 0) return null;

                var sb = new StringBuilder();
                sb.AppendLine($"\n-- GC Allocation Callstacks (top {topN} project allocation sites across {frames} frames) --");
                var top = byLoc.OrderByDescending(x => x.Value.bytes).Take(topN).ToList();
                foreach (var kv in top)
                    sb.AppendLine($"  {Bytes(kv.Value.bytes)} total | {Bytes((long)(kv.Value.bytes / frames))}/frame | {kv.Value.count} allocs  ←  {kv.Key}");

                var seen = new HashSet<string>();
                var code = new StringBuilder();
                foreach (var kv in top.Take(3))
                {
                    if (string.IsNullOrEmpty(kv.Value.methodLine)) continue;
                    string r = ResolveSource(kv.Value.methodLine, seen);
                    if (r != null) code.Append(r);
                }
                if (code.Length > 0) { sb.AppendLine("\n  -- Source of allocation site --"); sb.Append(code); }

                return sb.ToString();
            }
            catch { return null; }
        }

        static (string display, string methodLine) ExtractTopProjectFrame(string callstack)
        {
            if (string.IsNullOrEmpty(callstack)) return (null, null);
            var lines = callstack.Replace("\r\n", "\n").Split('\n');

            foreach (var ln in lines)
            {
                int at = ln.IndexOf("(at ", StringComparison.Ordinal);
                if (at < 0) continue;
                string loc = ln.Substring(at + 4).TrimEnd(')', ' ');
                if (loc.IndexOf("Assets/", StringComparison.OrdinalIgnoreCase) < 0) continue;
                string method = ln.Substring(0, at).Trim();
                int slash = loc.LastIndexOf('/');
                string shortFile = slash >= 0 ? loc.Substring(slash + 1) : loc;   // File.cs:line
                return ($"{ShortMethod(method)} @ {shortFile}", method);
            }

            foreach (var ln in lines)
            {
                string t = ln.Trim();
                if (t.Length == 0) continue;
                if (t.StartsWith("UnityEngine", StringComparison.Ordinal) || t.StartsWith("UnityEditor", StringComparison.Ordinal) ||
                    t.StartsWith("System.", StringComparison.Ordinal) || t.StartsWith("Unity.", StringComparison.Ordinal) ||
                    t.StartsWith("Fusion.", StringComparison.Ordinal)) continue;
                return (ShortMethod(t), t);
            }
            return (null, null);
        }

        // "MyGame.AI.CreepAI.UpdateTarget ()" → "CreepAI.UpdateTarget()"
        static string ShortMethod(string m)
        {
            if (string.IsNullOrEmpty(m)) return m;
            int paren = m.IndexOf('(');
            string head = paren >= 0 ? m.Substring(0, paren).Trim() : m.Trim();
            var parts = head.Split('.');
            string shortHead = parts.Length >= 2 ? parts[parts.Length - 2] + "." + parts[parts.Length - 1] : head;
            return shortHead + "()";
        }

        public static string FrameCulpritWithSource(int frame)
        {
            try
            {
                var samples = CollectSamples(frame)
                    .Where(s => !IsIdle(s.Name) && !IsAggregateMarker(s.Name)).ToList();
                if (samples.Count == 0) return null;
                var sb = new StringBuilder();
                var seen = new HashSet<string>();
                var topGc  = samples.Where(s => s.GcBytes > 0).OrderByDescending(s => s.GcBytes).FirstOrDefault();
                var topCpu = samples.OrderByDescending(s => s.SelfMs).FirstOrDefault();
                if (topGc.GcBytes > 0)
                {
                    sb.AppendLine($"  GC contributor: {topGc.Name}  ({Bytes(topGc.GcBytes)})");
                    var r = ResolveSource(topGc.Name, seen); if (r != null) sb.Append(r);
                }
                if (topCpu.SelfMs > 0 && topCpu.Name != topGc.Name)
                {
                    sb.AppendLine($"  CPU contributor: {topCpu.Name}  ({topCpu.SelfMs:F2} ms)");
                    var r = ResolveSource(topCpu.Name, seen); if (r != null) sb.Append(r);
                }
                return sb.ToString();
            }
            catch { return null; }
        }

        public static string CpuDeepReport(int startFrame, int endFrame, int topN = 10)
        {
            var sb = new StringBuilder("=== CPU Deep (measured method-level processing time) ===");
            try
            {
                if (endFrame < 0 || endFrame <= startFrame)
                    return sb.Append("\n(No deep-capture frames; capture again while in Play Mode.)").ToString();

                int scanStart = Mathf.Max(ProfilerDriver.firstFrameIndex, startFrame + 1);
                scanStart = Mathf.Max(scanStart, endFrame - 150);
                int frames = Mathf.Max(1, endFrame - scanStart + 1);
                var samples = AggregateSamples(scanStart, endFrame);

                var ranked = samples
                    .Where(s => !IsIdle(s.Name) && !IsAggregateMarker(s.Name) && s.SelfMs > 0)
                    .OrderByDescending(s => s.SelfMs)
                    .ToList();

                if (ranked.Count == 0)
                    return sb.Append($"\n({frames} frames captured, but no method is a clear CPU contributor.)").ToString();

                sb.AppendLine($"\n-- Top CPU Self-Time (average per frame across {frames} deep-capture frames) --");
                var seen = new HashSet<string>();
                var code = new StringBuilder();
                int shown = 0;
                foreach (var s in ranked)
                {
                    if (shown >= topN) break;
                    double msF = s.SelfMs / frames;
                    int callsF = (int)(s.Calls / (double)frames);
                    string flag = msF > 3.0 ? " 🔴" : msF > 1.0 ? " 🟡" : " 🟢";
                    sb.AppendLine($"  {msF:F2} ms/frame{flag} | ~{callsF} calls/frame  ←  {s.Name}");
                    if (shown < 4)
                    {
                        string r = ResolveSource(s.Name, seen);
                        if (r != null) code.Append(r);
                    }
                    shown++;
                }
                if (code.Length > 0) { sb.AppendLine("\n-- Source of CPU-intensive methods --"); sb.Append(code); }
                return sb.ToString();
            }
            catch (Exception e) { return sb.Append($"\n(error: {e.Message})").ToString(); }
        }

        public static string NetworkReport()
        {
            string s = ReadNetworkStats();
            return string.IsNullOrEmpty(s)
                ? "=== Network ===\n(No data; enter Play Mode with a Fusion NetworkRunner in the scene.)"
                : "=== Network (Photon Fusion) ===\n" + s;
        }

        static string ResolveSource(string marker, HashSet<string> seenFiles)
        {
            try
            {
                if (string.IsNullOrEmpty(marker)) return null;
                string m = marker;
                int paren = m.IndexOf('(');
                if (paren >= 0) m = m.Substring(0, paren);
                m = m.Replace(":", ".").Trim();
                var parts = m.Split('.');
                if (parts.Length < 2) return null;
                string method = parts[parts.Length - 1];
                string cls = parts[parts.Length - 2];
                if (method.Length == 0 || cls.Length == 0) return null;

                string path = CodebaseIndex.ResolvePath(cls);
                if (string.IsNullOrEmpty(path)) return null;
                if (!seenFiles.Add(path + "::" + method)) return null;

                string content = CodebaseIndex.ReadContent(path, 60000);
                if (content == null) return null;

                var (snippet, startLine, endLine) = ExtractMethod(content, method);
                if (snippet == null) return null;

                var sb = new StringBuilder();
                sb.AppendLine($"\n  ▸ {cls}.{method}()  →  {path}:{startLine}-{endLine}");
                sb.AppendLine("  ```csharp");
                foreach (var ln in snippet) sb.AppendLine("  " + ln);
                sb.AppendLine("  ```");
                return sb.ToString();
            }
            catch { return null; }
        }

        static (List<string>, int, int) ExtractMethod(string content, string method)
        {
            var lines = content.Replace("\r\n", "\n").Split('\n');
            int declLine = -1;
            for (int i = 0; i < lines.Length; i++)
            {
                string t = lines[i];
                int idx = t.IndexOf(method + "(", StringComparison.Ordinal);
                if (idx < 0) continue;
                string before = t.Substring(0, idx);
                bool looksDecl = (before.Contains("void") || before.Contains("public") || before.Contains("private")
                    || before.Contains("protected") || before.Contains("internal") || before.Contains("static")
                    || before.Contains("IEnumerator") || before.Contains("Task") || before.Contains("override")
                    || before.Contains("virtual") || before.Contains("async"))
                    && !t.TrimEnd().EndsWith(";");
                if (looksDecl) { declLine = i; break; }
            }
            if (declLine < 0) return (null, 0, 0);

            int depth = 0; bool started = false; int endLine = declLine;
            for (int i = declLine; i < lines.Length; i++)
            {
                foreach (char c in lines[i])
                {
                    if (c == '{') { depth++; started = true; }
                    else if (c == '}') depth--;
                }
                if (started && depth <= 0) { endLine = i; break; }
                endLine = i;
            }

            var outLines = new List<string>();
            int shown = 0;
            for (int i = declLine; i <= endLine && i < lines.Length; i++)
            {
                if (shown >= 45) { outLines.Add($"… (+{endLine - i + 1} lines)"); break; }
                outLines.Add($"{i + 1,4}| {lines[i]}");
                shown++;
            }
            return (outLines, declLine + 1, endLine + 1);
        }

        static double FrameTotalGc(int frame)
        {
            try
            {
                using var view = ProfilerDriver.GetHierarchyFrameDataView(
                    frame, 0, HierarchyFrameDataView.ViewModes.MergeSamplesWithTheSameName,
                    HierarchyFrameDataView.columnGcMemory, false);
                if (view == null || !view.valid) return 0;
                int root = view.GetRootItemID();
                return view.GetItemColumnDataAsFloat(root, HierarchyFrameDataView.columnGcMemory);
            }
            catch { return 0; }
        }

        static List<Sample> CollectSamples(int frame)
        {
            var list = new List<Sample>();
            try
            {
                using var view = ProfilerDriver.GetHierarchyFrameDataView(
                    frame, 0, HierarchyFrameDataView.ViewModes.MergeSamplesWithTheSameName,
                    HierarchyFrameDataView.columnSelfTime, false);
                if (view == null || !view.valid) return list;

                int root = view.GetRootItemID();
                var stack = new Stack<int>();
                stack.Push(root);
                var children = new List<int>();

                while (stack.Count > 0)
                {
                    int id = stack.Pop();
                    children.Clear();
                    view.GetItemChildren(id, children);
                    foreach (int childId in children)
                    {
                        list.Add(new Sample
                        {
                            Name    = view.GetItemName(childId),
                            GcBytes = view.GetItemColumnDataAsFloat(childId, HierarchyFrameDataView.columnGcMemory),
                            SelfMs  = view.GetItemColumnDataAsFloat(childId, HierarchyFrameDataView.columnSelfTime),
                            TotalMs = view.GetItemColumnDataAsFloat(childId, HierarchyFrameDataView.columnTotalTime),
                            Calls   = (int)view.GetItemColumnDataAsFloat(childId, HierarchyFrameDataView.columnCalls),
                        });
                        stack.Push(childId);
                    }
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning("[AI Unity MCP Server] Profiler sample collection ended early: " + exception.Message);
            }
            return list;
        }

        public static string TopContributor(int frame)
        {
            try
            {
                var samples = CollectSamples(frame).Where(s => !IsIdle(s.Name)).ToList();
                if (samples.Count == 0) return null;

                var topGc = samples.Where(s => s.GcBytes > 0).OrderByDescending(s => s.GcBytes).FirstOrDefault();
                var topCpu = samples.OrderByDescending(s => s.SelfMs).FirstOrDefault();

                var parts = new List<string>();
                if (topGc.GcBytes > 0) parts.Add($"GC: {topGc.Name} ({Bytes(topGc.GcBytes)})");
                if (topCpu.SelfMs > 0) parts.Add($"CPU: {topCpu.Name} ({topCpu.SelfMs:F1}ms)");
                return parts.Count > 0 ? string.Join("  |  ", parts) : null;
            }
            catch { return null; }
        }

        static string _netCache;
        static double _netCacheTime = -10;
        public static string NetworkLine()
        {
            double now = UnityEditor.EditorApplication.timeSinceStartup;
            if (now - _netCacheTime < 1.0) return _netCache;
            _netCacheTime = now;
            _netCache = ReadNetworkStats()?.Replace("\n", "  ")?.Trim();
            return _netCache;
        }

        static List<Sample> AggregateSamples(int startFrame, int endFrame)
        {
            var byName = new Dictionary<string, Sample>();
            for (int f = startFrame; f <= endFrame; f++)
            {
                foreach (var s in CollectSamples(f))
                {
                    if (byName.TryGetValue(s.Name, out var acc))
                    {
                        acc.GcBytes += s.GcBytes;
                        acc.SelfMs  += s.SelfMs;
                        acc.TotalMs += s.TotalMs;
                        acc.Calls   += s.Calls;
                        byName[s.Name] = acc;
                    }
                    else byName[s.Name] = s;
                }
            }
            return byName.Values.ToList();
        }

        static string SubsystemBreakdown(List<Sample> samples, int frames)
        {
            var sb = new StringBuilder("\n-- Subsystem CPU Time (avg per frame) --\n");
            bool any = false;

            bool Row(string label, string tip, params string[] keywords)
            {
                double ms = SumMarkers(samples, keywords) / frames;
                if (ms < 0.1) return false;
                string flag = ms > 5.0 ? " 🔴" : ms > 2.0 ? " 🟡" : " 🟢";
                sb.AppendLine($"  {label,-24} {ms:F2} ms/frame{flag}" + (tip != null ? $"  ← {tip}" : ""));
                return true;
            }

            any |= Row("Camera.Render",     "Reduce draw calls and improve frustum culling", "Camera.Render");
            any |= Row("Shadows",           "Bake lights or reduce Shadow Distance", "Shadows.RenderShadowMap", "Shadows.RenderCollectShadows");
            any |= Row("Post-Processing",   "Disable unused effects or lower resolution", "PostProcess", "PostProcessLayer", "VolumeManager");
            any |= Row("Transparent Pass",  "Transparent content does not batch and causes overdraw", "Transparent", "TransparentGeometry");
            any |= Row("CPU Skinning",      "Enable GPU skinning or reduce bone counts", "Skinning.Update", "SkinnedMeshFinalizer");
            any |= Row("Physics",           "Reduce Fixed Timestep frequency or use primitive colliders", "Physics.Processing", "Physics.Simulate", "Physics2D");
            any |= Row("UI / Canvas",       "Separate dynamic and static Canvases", "Canvas.SendWillRenderCanvases", "Canvas.BuildBatch");
            any |= Row("Animator",          "Set Culling Mode to Cull Completely", "Animator.Update", "Animator.ApplyBuiltinRootMotion");
            any |= Row("Audio",             "Reduce AudioSources or disable distant 3D audio", "Audio.Update", "AudioManager");
            any |= Row("Particles",         "Reduce maximum particles or disable collision", "ParticleSystem.Update", "ParticleSystem.FixedUpdate");
            any |= Row("NavMesh",           "Reduce agent count or use NavMeshQuery", "NavMesh.Update", "NavMeshAgent");
            any |= Row("Network (Fusion)",  "Reduce NetworkObjects or tick rate", "InvokeFixedUpdateNetwork", "RunClientSideResimulationLoop", "Simulation.Update");

            return any ? sb.ToString() : "";
        }

        static double SumMarkers(List<Sample> samples, params string[] keywords)
        {
            double total = 0;
            foreach (var s in samples)
                foreach (var k in keywords)
                    if (s.Name.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0)
                    { total += s.SelfMs; break; }
            return total;
        }

        static string RenderThreadAnalysis(int scanStart, int last, int framesScanned, int topN = 8)
        {
            try
            {
                for (int ti = 1; ti <= 4; ti++)
                {
                    var rs = AggregateThreadSamples(scanStart, last, ti);
                    bool isRenderThread = rs.Any(s =>
                        s.Name.IndexOf("Gfx",      StringComparison.OrdinalIgnoreCase) >= 0 ||
                        s.Name.IndexOf("Render",   StringComparison.OrdinalIgnoreCase) >= 0 ||
                        s.Name.IndexOf("DrawMesh", StringComparison.OrdinalIgnoreCase) >= 0);
                    if (!isRenderThread || rs.Count < 3) continue;

                    var sb = new StringBuilder($"\n-- Render Thread (thread {ti}) — GPU workload markers --\n");
                    foreach (var s in rs.Where(x => !IsIdle(x.Name))
                                        .OrderByDescending(x => x.SelfMs).Take(topN))
                    {
                        double msF = s.SelfMs / framesScanned;
                        string flag = msF > 5.0 ? " 🔴" : msF > 2.0 ? " 🟡" : " 🟢";
                        sb.AppendLine($"  {msF:F2} ms/frame{flag}  ←  {s.Name}");
                    }
                    return sb.ToString();
                }
            }
            catch { }
            return "";
        }

        static List<Sample> AggregateThreadSamples(int startFrame, int endFrame, int threadIndex)
        {
            var byName = new Dictionary<string, Sample>();
            for (int f = startFrame; f <= endFrame; f++)
            {
                foreach (var s in CollectSamplesFromThread(f, threadIndex))
                {
                    if (byName.TryGetValue(s.Name, out var acc))
                    {
                        acc.GcBytes += s.GcBytes; acc.SelfMs  += s.SelfMs;
                        acc.TotalMs += s.TotalMs; acc.Calls   += s.Calls;
                        byName[s.Name] = acc;
                    }
                    else byName[s.Name] = s;
                }
            }
            return byName.Values.ToList();
        }

        static List<Sample> CollectSamplesFromThread(int frame, int threadIndex)
        {
            var list = new List<Sample>();
            try
            {
                using var view = ProfilerDriver.GetHierarchyFrameDataView(
                    frame, threadIndex, HierarchyFrameDataView.ViewModes.MergeSamplesWithTheSameName,
                    HierarchyFrameDataView.columnSelfTime, false);
                if (view == null || !view.valid) return list;

                var stack = new Stack<int>();
                stack.Push(view.GetRootItemID());
                var children = new List<int>();
                while (stack.Count > 0)
                {
                    int id = stack.Pop();
                    children.Clear();
                    view.GetItemChildren(id, children);
                    foreach (int c in children)
                    {
                        list.Add(new Sample
                        {
                            Name    = view.GetItemName(c),
                            GcBytes = view.GetItemColumnDataAsFloat(c, HierarchyFrameDataView.columnGcMemory),
                            SelfMs  = view.GetItemColumnDataAsFloat(c, HierarchyFrameDataView.columnSelfTime),
                            TotalMs = view.GetItemColumnDataAsFloat(c, HierarchyFrameDataView.columnTotalTime),
                            Calls   = (int)view.GetItemColumnDataAsFloat(c, HierarchyFrameDataView.columnCalls),
                        });
                        stack.Push(c);
                    }
                }
            }
            catch { }
            return list;
        }

        static string ReadNetworkStats()
        {
            try
            {
                if (!Application.isPlaying) return null;

                var runnerType = FindType("Fusion.NetworkRunner");
                if (runnerType == null) return null;

                var runner = UnityEngine.Object.FindObjectOfType(runnerType);
                if (runner == null) return null;

                var sb = new StringBuilder();

                // LocalPlayer
                var localPlayerProp = runnerType.GetProperty("LocalPlayer");
                object localPlayer = localPlayerProp?.GetValue(runner);

                var rttMethod = runnerType.GetMethod("GetPlayerRtt");
                if (rttMethod != null && localPlayer != null)
                {
                    object rtt = rttMethod.Invoke(runner, new[] { localPlayer });
                    if (rtt is double d)
                        sb.AppendLine($"  RTT (ping): {d * 1000.0:F0} ms");
                }

                try
                {
                    var activeProp = runnerType.GetProperty("ActivePlayers");
                    if (activeProp?.GetValue(runner) is System.Collections.IEnumerable players && rttMethod != null)
                    {
                        var parts = new List<string>();
                        foreach (var p in players)
                        {
                            object rtt = rttMethod.Invoke(runner, new[] { p });
                            if (rtt is double pd) parts.Add($"P{p}:{pd * 1000.0:F0}ms");
                            if (parts.Count >= 10) break;
                        }
                        if (parts.Count > 1)
                            sb.AppendLine($"  Per-player RTT: {string.Join(", ", parts)}");
                    }
                }
                catch { }

                try
                {
                    var statsMethod = runnerType.GetMethod("GetStats", Type.EmptyTypes);
                    object stats = statsMethod?.Invoke(runner, null);
                    if (stats != null)
                    {
                        var st = stats.GetType();
                        AppendStat(sb, st, stats, "InKBps", "In", "KB/s");
                        AppendStat(sb, st, stats, "OutKBps", "Out", "KB/s");
                        AppendStat(sb, st, stats, "InBandwidth", "In", "");
                        AppendStat(sb, st, stats, "OutBandwidth", "Out", "");
                        AppendStat(sb, st, stats, "PacketLoss", "Loss", "");
                        AppendStat(sb, st, stats, "ResendRate", "Resend", "");
                    }
                }
                catch (Exception exception)
                {
                    Debug.LogWarning("[AI Unity MCP Server] Fusion bandwidth statistics are unavailable: " + exception.Message);
                }

                // IsServer / IsClient
                var isServer = runnerType.GetProperty("IsServer")?.GetValue(runner);
                var isClient = runnerType.GetProperty("IsClient")?.GetValue(runner);
                if (isServer != null || isClient != null)
                    sb.AppendLine($"  Role: {(isServer as bool? == true ? "Server" : "")}{(isClient as bool? == true ? "Client" : "")}");

                return sb.Length > 0 ? sb.ToString().TrimEnd() : null;
            }
            catch { return null; }
        }

        public static double LocalRttMs()
        {
            try
            {
                if (!Application.isPlaying) return -1;
                var rt = FindType("Fusion.NetworkRunner");
                if (rt == null) return -1;
                var runner = UnityEngine.Object.FindObjectOfType(rt);
                if (runner == null) return -1;
                var lp = rt.GetProperty("LocalPlayer")?.GetValue(runner);
                var m = rt.GetMethod("GetPlayerRtt");
                if (lp == null || m == null) return -1;
                object rtt = m.Invoke(runner, new[] { lp });
                return rtt is double d ? d * 1000.0 : -1;
            }
            catch { return -1; }
        }

        static void AppendStat(StringBuilder sb, Type st, object obj, string member, string label, string unit)
        {
            try
            {
                object v = st.GetProperty(member)?.GetValue(obj) ?? st.GetField(member)?.GetValue(obj);
                if (v == null) return;
                double d = Convert.ToDouble(v);
                if (d <= 0) return;
                sb.AppendLine($"  {label}: {d:F1} {unit}".TrimEnd());
            }
            catch { }
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
