using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace MCPBridge
{
    /// <summary>
    /// </summary>
    [InitializeOnLoad]
    public static class RuntimeWatch
    {
        class WatchEntry
        {
            public string objectName;
            public string componentType;
            public string fieldName;
            public string resolvedComponent;
            public readonly List<string> history = new List<string>();   // last 10 values
            public readonly List<string> timestamps = new List<string>();
            public bool everFound;
            public int missStreak;

            public string alertOp;        // lt|lte|gt|gte|eq|ne|changed
            public double alertThreshold;
            public int alertCount;
            public bool alertWasTrue;

            public string Key => $"{objectName}.{componentType}.{fieldName}";
            public string ShowComponent => string.IsNullOrEmpty(componentType)
                ? (string.IsNullOrEmpty(resolvedComponent) ? "auto" : resolvedComponent) : componentType;
        }

        public struct WatchView
        {
            public string key, objectName, component, field, value, trend, status;
            public string[] history;
            public string alert;
            public int alertCount;
        }

        static readonly List<WatchEntry> _watches = new List<WatchEntry>();
        static readonly object _lock = new object();
        static double _lastSample;
        static int _sampleCount;
        const int MAX_HISTORY = 10;
        const double SAMPLE_INTERVAL = 0.5;
        const string NOT_FOUND = "(not found)";
        const int DESPAWN_MISS = 10;

        static RuntimeWatch()
        {
            EditorApplication.playModeStateChanged += OnPlayMode;
        }

        static void OnPlayMode(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredPlayMode)
            {
                _lastSample = 0;
                _sampleCount = 0;
                EditorApplication.update += Sample;
            }
            else if (state == PlayModeStateChange.ExitingPlayMode)
            {
                EditorApplication.update -= Sample;
            }
        }

        static void Sample()
        {
            if (!Application.isPlaying) return;
            double now = EditorApplication.timeSinceStartup;
            if (now - _lastSample < SAMPLE_INTERVAL) return;
            _lastSample = now;
            _sampleCount++;

            string ts = DateTime.Now.ToString("HH:mm:ss");
            lock (_lock)
            {
                List<WatchEntry> despawned = null;
                foreach (var w in _watches)
                {
                    string val = SampleValue(w);

                    if (val == NOT_FOUND)
                    {
                        w.missStreak++;
                        if (w.everFound && w.missStreak >= DESPAWN_MISS)
                            (despawned ??= new List<WatchEntry>()).Add(w);
                    }
                    else
                    {
                        w.everFound = true;
                        w.missStreak = 0;
                    }

                    w.history.Add(val);
                    w.timestamps.Add(ts);
                    if (w.history.Count > MAX_HISTORY) { w.history.RemoveAt(0); w.timestamps.RemoveAt(0); }

                    if (!string.IsNullOrEmpty(w.alertOp))
                    {
                        bool hit = EvalAlert(w, val);
                        if (hit && !w.alertWasTrue)
                        {
                            w.alertCount++;
                            Debug.LogWarning($"[Watch alert] {w.Key} {AlertText(w)} → {val} (occurrence {w.alertCount})");
                        }
                        w.alertWasTrue = hit;
                    }
                }
                if (despawned != null) foreach (var w in despawned) _watches.Remove(w);
            }
        }

        static string SampleValue(WatchEntry w)
        {
            try
            {
                var go = ResolveObject(w.objectName);
                if (go == null) return NOT_FOUND;

                if (!string.IsNullOrEmpty(w.componentType))
                {
                    Component comp = null;
                    foreach (var c in go.GetComponents<Component>())
                    {
                        if (c == null) continue;
                        if (c.GetType().Name == w.componentType || c.GetType().FullName == w.componentType)
                        { comp = c; break; }
                    }
                    if (comp == null) return "(component not found)";
                    if (w.fieldName.StartsWith("@"))
                        return SampleAnimator(comp as Animator, w.fieldName);
                    return TryResolvePath(comp, w.fieldName, out object v) ? FormatValue(v) : $"(field not found: {w.fieldName})";
                }

                var comps = go.GetComponents<Component>();
                for (int pass = 0; pass < 2; pass++)
                    foreach (var c in comps)
                    {
                        if (c == null) continue;
                        bool isUnity = (c.GetType().Namespace ?? "").StartsWith("UnityEngine");
                        if (pass == 0 ? isUnity : !isUnity) continue;   // pass0 = game scripts, pass1 = unity
                        if (TryResolvePath(c, w.fieldName, out object v))
                        {
                            w.resolvedComponent = c.GetType().Name;
                            return FormatValue(v);
                        }
                    }
                return $"(field not found: {w.fieldName})";
            }
            catch (Exception e) { return $"(error: {e.Message})"; }
        }

        static string SampleAnimator(Animator anim, string field)
        {
            if (anim == null) return "(not an Animator)";
            if (anim.runtimeAnimatorController == null) return "(no controller)";

            if (field == "@state")
            {
                var st = anim.GetCurrentAnimatorStateInfo(0);
                var ci = anim.GetCurrentAnimatorClipInfo(0);
                string clip = ci.Length > 0 && ci[0].clip != null ? ci[0].clip.name : "state#" + st.shortNameHash;
                string suffix = anim.IsInTransition(0) ? " →(transition)" : "";
                return $"{clip} t={st.normalizedTime % 1f:0.00}{suffix}";
            }

            if (field.StartsWith("@param:"))
            {
                string pname = field.Substring("@param:".Length);
                foreach (var p in anim.parameters)
                {
                    if (p.name != pname) continue;
                    switch (p.type)
                    {
                        case AnimatorControllerParameterType.Float:   return anim.GetFloat(pname).ToString("0.###");
                        case AnimatorControllerParameterType.Int:     return anim.GetInteger(pname).ToString();
                        case AnimatorControllerParameterType.Bool:    return anim.GetBool(pname).ToString().ToLower();
                        case AnimatorControllerParameterType.Trigger: return anim.GetBool(pname) ? "set" : "-";
                    }
                }
                return $"(no param: {pname})";
            }
            return "(use @state or @param:Name)";
        }

        static bool TryResolvePath(object root, string path, out object val)
        {
            object cur = root;
            foreach (var seg in path.Split('.'))
            {
                if (cur == null) { val = null; return true; }
                if (!TryGetMember(cur, seg, out cur)) { val = null; return false; }
            }
            val = cur;
            return true;
        }

        static string FormatValue(object v)
        {
            if (v == null) return "null";
            if (!(v is string) && v is System.Collections.ICollection col)
            {
                var items = new List<string>();
                int i = 0;
                foreach (var it in col)
                {
                    if (i++ >= 8) { items.Add("…"); break; }
                    items.Add(ItemLabel(it));
                }
                return $"count={col.Count} [{string.Join(", ", items)}]";
            }
            return v.ToString();
        }

        static string ItemLabel(object it)
        {
            try
            {
                if (it == null) return "null";
                var t = it.GetType();
                if (t.IsPrimitive || it is string) return it.ToString();

                string id = GetMember(it, "ID")?.ToString() ?? GetMember(it, "Name")?.ToString()
                          ?? GetMember(it, "Key")?.ToString() ?? t.Name;

                object hasDur = GetMember(it, "HasDuration");
                if (hasDur != null)
                {
                    string hs = hasDur.ToString().ToLowerInvariant();
                    if (hs == "false" || hs == "0") return id + "(passive)";
                    object tl = GetMember(it, "TimeLeft");
                    if (tl != null) return $"{id}({tl}s)";
                }
                return id;
            }
            catch { return it?.ToString() ?? "null"; }
        }

        static object GetMember(object obj, string name)
            => TryGetMember(obj, name, out var v) ? v : null;

        static GameObject ResolveObject(string name)
        {
            var sel = Selection.activeGameObject;
            if (string.IsNullOrEmpty(name)) return sel;

            if (sel != null && sel.name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0) return sel;

            var exact = GameObject.Find(name);
            if (exact != null) return exact;

            GameObject contains = null;
            foreach (var t in UnityEngine.Object.FindObjectsByType<Transform>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                string n = t.gameObject.name;
                if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) return t.gameObject;
                if (contains == null && n.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0) contains = t.gameObject;
            }
            if (contains != null) return contains;

            return null;
        }

        static bool TryGetMember(object obj, string name, out object val)
        {
            val = null;
            if (obj == null || string.IsNullOrEmpty(name)) return false;
            var t = obj.GetType();
            var fi = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (fi != null) { val = fi.GetValue(obj); return true; }
            var pi = t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (pi != null && pi.CanRead && pi.GetIndexParameters().Length == 0)
            {
                var getter = pi.GetGetMethod(true);
                if (getter == null) return false;
                val = getter.Invoke(obj, null);
                return true;
            }
            return false;
        }

        // ── Trend detection ───────────────────────────────────────────────────
        static string Trend(List<string> history)
        {
            if (history.Count < 2) return "=";
            string a = history[history.Count - 2];
            string b = history[history.Count - 1];
            if (a == b) return "=";
            if (double.TryParse(a, out double da) && double.TryParse(b, out double db))
                return db > da ? "↑" : "↓";
            return "changed";
        }

        // ── Alert eval ────────────────────────────────────────────────────────
        static bool EvalAlert(WatchEntry w, string val)
        {
            if (w.alertOp == "changed")
                return w.history.Count >= 2 && w.history[w.history.Count - 1] != w.history[w.history.Count - 2];
            if (!double.TryParse(val, out double d)) return false;
            double t = w.alertThreshold;
            switch (w.alertOp)
            {
                case "lt":  return d <  t;
                case "lte": return d <= t;
                case "gt":  return d >  t;
                case "gte": return d >= t;
                case "eq":  return d == t;
                case "ne":  return d != t;
                default:    return false;
            }
        }

        static string AlertText(WatchEntry w)
        {
            if (w.alertOp == "changed") return "changed";
            string sym = w.alertOp switch
            { "lt" => "<", "lte" => "<=", "gt" => ">", "gte" => ">=", "eq" => "==", "ne" => "!=", _ => w.alertOp };
            return $"{sym} {w.alertThreshold}";
        }

        static string NormalizeOp(string op)
        {
            switch ((op ?? "").Trim().ToLowerInvariant())
            {
                case "<": case "lt": return "lt";
                case "<=": case "lte": return "lte";
                case ">": case "gt": return "gt";
                case ">=": case "gte": return "gte";
                case "==": case "=": case "eq": return "eq";
                case "!=": case "ne": case "<>": return "ne";
                case "changed": case "change": case "diff": return "changed";
                default: return null;
            }
        }

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// </summary>
        public static string AddWatch(string objectName, string componentType, string fieldName)
            => AddWatchCore(objectName, componentType, fieldName, null, 0);

        /// <summary>
        /// </summary>
        public static string AddAlert(string objectName, string componentType, string fieldName, string op, double threshold)
        {
            string nop = NormalizeOp(op);
            if (nop == null) return "op must be lt|lte|gt|gte|eq|ne|changed (or < <= > >= == !=)";
            return AddWatchCore(objectName, componentType, fieldName, nop, threshold);
        }

        static string AddWatchCore(string objectName, string componentType, string fieldName, string alertOp, double alertThreshold)
        {
            if (string.IsNullOrEmpty(fieldName)) return "field is required (for example currentHp or Damageable.Hp.Value)";

            if (string.IsNullOrEmpty(objectName))
            {
                var sel = Selection.activeGameObject;
                if (sel == null) return "No object was specified and the Hierarchy selection is empty. Select a GameObject or provide objectName.";
                objectName = sel.name;
            }
            componentType = componentType?.Trim() ?? "";

            lock (_lock)
            {
                string key = $"{objectName}.{componentType}.{fieldName}";
                foreach (var w in _watches)
                    if (w.Key == key)
                    {
                        if (alertOp != null) { w.alertOp = alertOp; w.alertThreshold = alertThreshold; w.alertWasTrue = false; return null; }
                        return $"Watch already exists: {key}";
                    }

                _watches.Add(new WatchEntry
                {
                    objectName = objectName,
                    componentType = componentType,
                    fieldName = fieldName,
                    alertOp = alertOp,
                    alertThreshold = alertThreshold,
                });
            }
            return null;
        }

        public static bool RemoveWatch(string key)
        {
            lock (_lock)
            {
                int idx = _watches.FindIndex(w => w.Key == key);
                if (idx < 0) return false;
                _watches.RemoveAt(idx);
                return true;
            }
        }

        public static List<WatchView> Snapshot()
        {
            var list = new List<WatchView>();
            lock (_lock)
            {
                foreach (var w in _watches)
                {
                    string cur = w.history.Count > 0 ? w.history[w.history.Count - 1] : "n/a";
                    list.Add(new WatchView
                    {
                        key = w.Key, objectName = w.objectName, component = w.ShowComponent,
                        field = w.fieldName, value = cur, trend = Trend(w.history),
                        status = cur.StartsWith("(") || cur == NOT_FOUND ? "error" : "ok",
                        history = w.history.ToArray(),
                        alert = string.IsNullOrEmpty(w.alertOp) ? "" : AlertText(w),
                        alertCount = w.alertCount,
                    });
                }
            }
            return list;
        }

        public static int Count { get { lock (_lock) return _watches.Count; } }

        public static string GetReport()
        {
            var sb = new StringBuilder();
            sb.Append($"{{\"isPlaying\":{Application.isPlaying.ToString().ToLower()},");
            sb.Append($"\"sampleCount\":{_sampleCount},\"watches\":[");

            lock (_lock)
            {
                for (int i = 0; i < _watches.Count; i++)
                {
                    if (i > 0) sb.Append(",");
                    var w = _watches[i];
                    string key = w.Key;
                    string cur = w.history.Count > 0 ? w.history[w.history.Count - 1] : "n/a";
                    string prev = w.history.Count > 1 ? w.history[w.history.Count - 2] : cur;
                    string trend = Trend(w.history);

                    // history array
                    var histSb = new StringBuilder("[");
                    for (int h = 0; h < w.history.Count; h++)
                    {
                        if (h > 0) histSb.Append(",");
                        histSb.Append($"\"{MCPHandlers.EscapeJsonPublic(w.history[h])}\"");
                    }
                    histSb.Append("]");

                    string status = cur.StartsWith("(") ? "error" : "ok";
                    string alertJson = string.IsNullOrEmpty(w.alertOp) ? ""
                        : $"\"alert\":\"{MCPHandlers.EscapeJsonPublic(AlertText(w))}\",\"alertCount\":{w.alertCount},\"alerting\":{w.alertWasTrue.ToString().ToLower()},";
                    sb.Append($"{{{alertJson}\"key\":\"{MCPHandlers.EscapeJsonPublic(key)}\",");
                    sb.Append($"\"object\":\"{MCPHandlers.EscapeJsonPublic(w.objectName)}\",");
                    sb.Append($"\"component\":\"{MCPHandlers.EscapeJsonPublic(w.ShowComponent)}\",");
                    sb.Append($"\"field\":\"{MCPHandlers.EscapeJsonPublic(w.fieldName)}\",");
                    sb.Append($"\"value\":\"{MCPHandlers.EscapeJsonPublic(cur)}\",");
                    sb.Append($"\"prev\":\"{MCPHandlers.EscapeJsonPublic(prev)}\",");
                    sb.Append($"\"trend\":\"{MCPHandlers.EscapeJsonPublic(trend)}\",");
                    sb.Append($"\"history\":{histSb},");
                    sb.Append($"\"status\":\"{status}\"}}");
                }
            }

            sb.Append("]}");
            return sb.ToString();
        }

        public static void ClearAll()
        {
            lock (_lock) { _watches.Clear(); }
            _sampleCount = 0;
        }
    }
}
