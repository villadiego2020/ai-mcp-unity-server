using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Profiling;

namespace AIUnityMCPServer
{
    public static partial class MCPHandlers
    {
        static string ReadConsole(string body)
        {
            var data = string.IsNullOrEmpty(body) ? new ConsoleRequest() : ParseReq<ConsoleRequest>(body);
            int max = data.max > 0 ? data.max : 30;

            return ExecuteOnMainThread(() =>
            {
                try
                {
                    var logEntries = Type.GetType("UnityEditor.LogEntries,UnityEditor");
                    var logEntry = Type.GetType("UnityEditor.LogEntry,UnityEditor");
                    if (logEntries == null || logEntry == null)
                        return "{\"error\":\"LogEntries API not available in this Unity version\"}";

                    int count = (int)logEntries.GetMethod("StartGettingEntries").Invoke(null, null);
                    var getEntry = logEntries.GetMethod("GetEntryInternal");
                    var msgField = logEntry.GetField("message");
                    var modeField = logEntry.GetField("mode");
                    var entryObj = Activator.CreateInstance(logEntry);

                    var sb = new StringBuilder("[");
                    int start = Mathf.Max(0, count - max);
                    int added = 0;
                    for (int i = start; i < count; i++)
                    {
                        getEntry.Invoke(null, new object[] { i, entryObj });
                        string msg = (string)msgField.GetValue(entryObj);
                        int mode = (int)modeField.GetValue(entryObj);
                        string type = (mode & 1) != 0 || (mode & (1 << 1)) != 0 ? "error"
                                    : (mode & (1 << 9)) != 0 ? "warning" : "log";
                        if (added > 0) sb.Append(",");
                        sb.Append($"{{\"type\":\"{type}\",\"message\":\"{EscapeJson(msg)}\"}}");
                        added++;
                    }
                    sb.Append("]");
                    logEntries.GetMethod("EndGettingEntries").Invoke(null, null);
                    return $"{{\"count\":{added},\"entries\":{sb}}}";
                }
                catch (Exception e)
                {
                    return $"{{\"error\":\"{EscapeJson(e.Message)}\"}}";
                }
            });
        }

        static string InspectObject(string body)
        {
            var data = ParseReq<InspectRequest>(body);
            return ExecuteOnMainThread(() =>
            {
                var go = GameObject.Find(data.name);
                if (go == null) return $"{{\"error\":\"Not found: {EscapeJson(data.name)}\"}}";

                var sb = new StringBuilder();
                sb.Append($"{{\"name\":\"{EscapeJson(go.name)}\",\"active\":{go.activeSelf.ToString().ToLower()},");
                sb.Append($"\"tag\":\"{EscapeJson(go.tag)}\",\"layer\":\"{EscapeJson(LayerMask.LayerToName(go.layer))}\",");
                var t = go.transform;
                sb.Append($"\"position\":[{t.localPosition.x},{t.localPosition.y},{t.localPosition.z}],");
                sb.Append($"\"rotation\":[{t.localEulerAngles.x},{t.localEulerAngles.y},{t.localEulerAngles.z}],");
                sb.Append($"\"scale\":[{t.localScale.x},{t.localScale.y},{t.localScale.z}],");
                if (data.deep) sb.Append("\"deep\":true,");
                sb.Append("\"components\":[");

                var comps = go.GetComponents<Component>();
                bool firstC = true;
                foreach (var c in comps)
                {
                    if (c == null) continue;
                    if (!firstC) sb.Append(",");
                    string props = data.deep ? ComponentPropsDeep(c) : ComponentProps(c);
                    sb.Append($"{{\"type\":\"{EscapeJson(c.GetType().Name)}\",\"properties\":{props}}}");
                    firstC = false;
                }
                sb.Append("]}");
                return sb.ToString();
            });
        }

        static string ComponentPropsDeep(Component c)
        {
            var sb = new StringBuilder("{");
            bool first = true;
            int n = 0;
            var type = c.GetType();

            // Fields (public + private instance)
            var flags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;
            foreach (var fi in type.GetFields(flags))
            {
                if (n >= 50) break;
                if (fi.Name.Contains("<")) continue;   // compiler-generated backing fields
                string val;
                try
                {
                    object raw = fi.GetValue(c);
                    val = raw == null ? "null" : raw.ToString();
                    if (val.Length > 200) val = val.Substring(0, 200) + "…";
                }
                catch { continue; }
                if (!first) sb.Append(",");
                sb.Append($"\"{EscapeJson(fi.Name)}\":\"{EscapeJson(val)}\"");
                first = false; n++;
            }

            // Public properties (skip indexers, skip anything that throws)
            foreach (var pi in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (n >= 80) break;
                if (pi.GetIndexParameters().Length > 0) continue;   // indexer
                if (pi.Name.Contains("<")) continue;
                string val;
                try
                {
                    object raw = pi.GetValue(c);
                    val = raw == null ? "null" : raw.ToString();
                    if (val.Length > 200) val = val.Substring(0, 200) + "…";
                }
                catch { continue; }
                if (!first) sb.Append(",");
                sb.Append($"\"{EscapeJson(pi.Name)}\":\"{EscapeJson(val)}\"");
                first = false; n++;
            }

            sb.Append("}");
            return sb.ToString();
        }

        static string ComponentProps(Component c)
        {
            try
            {
                var so = new SerializedObject(c);
                var prop = so.GetIterator();
                var sb = new StringBuilder("{");
                bool first = true;
                int n = 0;
                prop.Next(true);
                while (prop.NextVisible(false) && n < 25)
                {
                    if (prop.name == "m_Script") continue;
                    string val = PropValue(prop);
                    if (val == null) continue;
                    if (!first) sb.Append(",");
                    sb.Append($"\"{EscapeJson(prop.displayName)}\":{val}");
                    first = false; n++;
                }
                sb.Append("}");
                return sb.ToString();
            }
            catch { return "{}"; }
        }

        static string PropValue(SerializedProperty p)
        {
            switch (p.propertyType)
            {
                case SerializedPropertyType.Integer:   return p.intValue.ToString();
                case SerializedPropertyType.Boolean:   return p.boolValue ? "true" : "false";
                case SerializedPropertyType.Float:     return p.floatValue.ToString("0.###");
                case SerializedPropertyType.String:    return $"\"{EscapeJson(p.stringValue)}\"";
                case SerializedPropertyType.Enum:      return $"\"{EscapeJson(p.enumDisplayNames[Mathf.Clamp(p.enumValueIndex,0,p.enumDisplayNames.Length-1)])}\"";
                case SerializedPropertyType.Vector3:   return $"[{p.vector3Value.x},{p.vector3Value.y},{p.vector3Value.z}]";
                case SerializedPropertyType.Vector2:   return $"[{p.vector2Value.x},{p.vector2Value.y}]";
                case SerializedPropertyType.Color:     return $"\"{ColorUtility.ToHtmlStringRGBA(p.colorValue)}\"";
                case SerializedPropertyType.ObjectReference:
                    return $"\"{(p.objectReferenceValue ? EscapeJson(p.objectReferenceValue.name) : "null")}\"";
                default: return null;
            }
        }

        // ── Add component ────────────────────────────────────────────────────
        static string AddComponent(string body)
        {
            var data = ParseReq<ComponentRequest>(body);
            return ExecuteOnMainThread(() =>
            {
                var go = GameObject.Find(data.name);
                if (go == null) return $"{{\"error\":\"Not found: {EscapeJson(data.name)}\"}}";
                var type = FindComponentTypeAny(data.component);
                if (type == null) return $"{{\"error\":\"Component type not found: {EscapeJson(data.component)}\"}}";

                Undo.AddComponent(go, type);
                return $"{{\"added\":\"{EscapeJson(type.Name)}\",\"to\":\"{EscapeJson(go.name)}\"}}";
            });
        }

        static string SetProperty(string body)
        {
            var data = ParseReq<SetPropertyRequest>(body);
            return ExecuteOnMainThread(() =>
            {
                var go = GameObject.Find(data.name);
                if (go == null) return $"{{\"error\":\"Not found: {EscapeJson(data.name)}\"}}";

                Component comp = string.IsNullOrEmpty(data.component)
                    ? go.transform
                    : go.GetComponents<Component>().FirstOrDefault(c => c != null && c.GetType().Name.Equals(data.component, StringComparison.OrdinalIgnoreCase));
                if (comp == null) return $"{{\"error\":\"Component not found: {EscapeJson(data.component)}\"}}";

                var so = new SerializedObject(comp);
                var prop = so.FindProperty(data.property)
                        ?? FindByDisplayName(so, data.property);
                if (prop == null) return $"{{\"error\":\"Property not found: {EscapeJson(data.property)}\"}}";

                Undo.RecordObject(comp, "AI Unity MCP Server Set Property");
                if (!ApplyValue(prop, data.value)) return $"{{\"error\":\"unsupported property type for '{EscapeJson(data.property)}'\"}}";
                so.ApplyModifiedProperties();
                EditorUtility.SetDirty(comp);
                return $"{{\"set\":\"{EscapeJson(data.property)}\",\"value\":\"{EscapeJson(data.value)}\"}}";
            });
        }

        static SerializedProperty FindByDisplayName(SerializedObject so, string name)
        {
            var it = so.GetIterator();
            it.Next(true);
            while (it.NextVisible(false))
                if (it.displayName.Equals(name, StringComparison.OrdinalIgnoreCase) || it.name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return it.Copy();
            return null;
        }

        static bool ApplyValue(SerializedProperty p, string v)
        {
            try
            {
                switch (p.propertyType)
                {
                    case SerializedPropertyType.Integer: p.intValue = int.Parse(v); return true;
                    case SerializedPropertyType.Boolean: p.boolValue = v == "1" || v.ToLower() == "true"; return true;
                    case SerializedPropertyType.Float:   p.floatValue = float.Parse(v); return true;
                    case SerializedPropertyType.String:  p.stringValue = v; return true;
                    case SerializedPropertyType.Enum:
                        int idx = Array.FindIndex(p.enumDisplayNames, n => n.Equals(v, StringComparison.OrdinalIgnoreCase));
                        if (idx < 0 && int.TryParse(v, out int ei)) idx = ei;
                        if (idx < 0) return false;
                        p.enumValueIndex = idx; return true;
                    case SerializedPropertyType.Vector3:
                        var a = v.Split(','); p.vector3Value = new Vector3(float.Parse(a[0]), float.Parse(a[1]), float.Parse(a[2])); return true;
                    case SerializedPropertyType.Color:
                        if (ColorUtility.TryParseHtmlString(v, out var col)) { p.colorValue = col; return true; } return false;
                    default: return false;
                }
            }
            catch { return false; }
        }

        static string SetTransform(string body)
        {
            var data = ParseReq<SetTransformRequest>(body);
            return ExecuteOnMainThread(() =>
            {
                var go = GameObject.Find(data.name);
                if (go == null) return $"{{\"error\":\"Not found: {EscapeJson(data.name)}\"}}";

                Undo.RecordObject(go.transform, "AI Unity MCP Server Set Transform");
                string set = (data.set ?? "").ToLower();
                if (set.Contains("pos"))   go.transform.localPosition    = new Vector3(data.px, data.py, data.pz);
                if (set.Contains("rot"))   go.transform.localEulerAngles = new Vector3(data.rx, data.ry, data.rz);
                if (set.Contains("scale")) go.transform.localScale        = new Vector3(data.sx, data.sy, data.sz);
                return $"{{\"transformed\":\"{EscapeJson(go.name)}\",\"set\":\"{EscapeJson(set)}\"}}";
            });
        }

        // ── Selection ────────────────────────────────────────────────────────
        static string GetSelection() => ExecuteOnMainThread(() =>
        {
            var sb = new StringBuilder("[");
            for (int i = 0; i < Selection.gameObjects.Length; i++)
            {
                if (i > 0) sb.Append(",");
                sb.Append($"\"{EscapeJson(Selection.gameObjects[i].name)}\"");
            }
            sb.Append("]");
            return $"{{\"count\":{Selection.gameObjects.Length},\"selected\":{sb}}}";
        });

        static string SetSelection(string body)
        {
            var data = ParseReq<NameRequest>(body);
            return ExecuteOnMainThread(() =>
            {
                var go = GameObject.Find(data.name);
                if (go == null) return $"{{\"error\":\"Not found: {EscapeJson(data.name)}\"}}";
                Selection.activeGameObject = go;
                EditorGUIUtility.PingObject(go);
                return $"{{\"selected\":\"{EscapeJson(go.name)}\"}}";
            });
        }

        // ── Scene open / save ────────────────────────────────────────────────
        static string OpenScene(string body)
        {
            var data = ParseReq<PathRequest>(body);
            return ExecuteOnMainThread(() =>
            {
                if (string.IsNullOrEmpty(data.path)) return "{\"error\":\"path required\"}";
                if (EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                {
                    var sc = EditorSceneManager.OpenScene(data.path);
                    return $"{{\"opened\":\"{EscapeJson(sc.path)}\"}}";
                }
                return "{\"error\":\"cancelled\"}";
            });
        }

        static string SaveScene() => ExecuteOnMainThread(() =>
        {
            bool ok = EditorSceneManager.SaveOpenScenes();
            return $"{{\"saved\":{ok.ToString().ToLower()}}}";
        });

        static Type FindComponentTypeAny(string name)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = asm.GetType(name);
                if (t != null && typeof(Component).IsAssignableFrom(t)) return t;
            }
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types; try { types = asm.GetTypes(); } catch { continue; }
                foreach (var t in types)
                    if (t.Name == name && typeof(Component).IsAssignableFrom(t)) return t;
            }
            return null;
        }

        static string ReadLogFile(string body)
        {
            var data = string.IsNullOrEmpty(body) ? new ConsoleRequest() : ParseReq<ConsoleRequest>(body);
            int max = data.max > 0 ? data.max : 120;

            return ExecuteOnMainThread(() =>
            {
                try
                {
                    string path = Application.consoleLogPath;
                    if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
                        return "{\"error\":\"log file not found\"}";

                    string[] lines;
                    using (var fs = new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite))
                    using (var sr = new System.IO.StreamReader(fs))
                        lines = sr.ReadToEnd().Split('\n');

                    int start = Mathf.Max(0, lines.Length - max);
                    var sb = new StringBuilder();
                    for (int i = start; i < lines.Length; i++)
                        sb.Append(lines[i]).Append('\n');

                    return $"{{\"path\":\"{EscapeJson(path)}\",\"lines\":{lines.Length - start},\"tail\":\"{EscapeJson(sb.ToString())}\"}}";
                }
                catch (Exception e) { return $"{{\"error\":\"{EscapeJson(e.Message)}\"}}"; }
            });
        }

        static string CaptureState() => ExecuteOnMainThread(() =>
        {
            var sb = new StringBuilder("{");
            sb.Append($"\"isPlaying\":{Application.isPlaying.ToString().ToLower()},");
            sb.Append($"\"isPaused\":{EditorApplication.isPaused.ToString().ToLower()},");
            sb.Append($"\"timeScale\":{Time.timeScale},");
            sb.Append($"\"frameCount\":{Time.frameCount},");
            sb.Append($"\"realtime\":{Time.realtimeSinceStartup:F1},");
            sb.Append($"\"scene\":\"{EscapeJson(UnityEngine.SceneManagement.SceneManager.GetActiveScene().name)}\",");

            float fps = ProfilerReader.CurrentFps();
            sb.Append($"\"fps\":{fps:F0},");

            string net = ProfilerDeepReader.NetworkLine();
            sb.Append($"\"network\":\"{EscapeJson(net ?? "n/a")}\",");

            int behaviours = UnityEngine.Object.FindObjectsOfType<MonoBehaviour>().Length;
            sb.Append($"\"activeMonoBehaviours\":{behaviours}");
            sb.Append("}");

            string spikes = SpikeMonitor.GetReport();
            return sb.ToString() + (string.IsNullOrEmpty(spikes) ? "" : "\n" + spikes);
        });

        static string PerfWorst() => ExecuteOnMainThread(() =>
            $"{{\"report\":\"{EscapeJson(SpikeMonitor.WorstReport())}\"}}");

        static string HotReload(string body) => ExecuteOnMainThread(() =>
        {
            var data = ParseReq<HotReloadRequest>(body);
            string action = string.IsNullOrEmpty(data?.action) ? "status" : data.action.ToLowerInvariant();
            if (action == "start")
            {
                bool ok = HotReloadControl.Start(out string msg);
                return $"{{\"started\":{(ok ? "true" : "false")},\"running\":{(HotReloadControl.IsRunning() ? "true" : "false")},\"message\":\"{EscapeJson(msg)}\"}}";
            }
            return $"{{\"running\":{(HotReloadControl.IsRunning() ? "true" : "false")}}}";
        });

        static string Compile() => ExecuteOnMainThread(() =>
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                return "{\"status\":\"already_compiling\",\"message\":\"Unity is already compiling. Poll unity_compile_status until isCompiling:false before continuing; do not trigger another compilation.\"}";

            if (EditorApplication.isPlaying || EditorApplication.isPaused)
            {
                EditorApplication.isPlaying = false;
                return "{\"status\":\"exiting_play_mode\",\"message\":\"Exiting Play Mode first. Wait briefly, then call unity_compile again.\"}";
            }

            AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
            CompilationPipeline.RequestScriptCompilation(RequestScriptCompilationOptions.None);
            return "{\"status\":\"compiling\",\"message\":\"Compilation triggered. Poll unity_compile_status until isCompiling:false before continuing.\"}";
        });

        static string CompileStatus() => ExecuteOnMainThread(() =>
        {
            bool compiling = EditorApplication.isCompiling || EditorApplication.isUpdating;
            bool playing   = EditorApplication.isPlaying || EditorApplication.isPaused;
            string status  = playing ? "play_mode" : compiling ? "compiling" : "ready";
            return $"{{\"isCompiling\":{(compiling ? "true" : "false")},\"isPlaying\":{(playing ? "true" : "false")},\"status\":\"{status}\"}}";
        });

        static string ServerStop() => ExecuteOnMainThread(() =>
        {
            MCPServer.Stop();
            return "{\"stopped\":true,\"message\":\"AI Unity MCP Server stop requested. A failed subsequent ping confirms that the server stopped.\"}";
        });

        static string PerfAudit() => ExecuteOnMainThread(() =>
        {
            var sb = new StringBuilder("{");

            if (ProfilerReader.IsLive)
            {
                float fps = ProfilerReader.CurrentFps();
                sb.Append($"\"fps\":{fps:F0},");
            }

            int renderers = 0, skinned = 0, particles = 0, rtLights = 0, animators = 0,
                audio = 0, rigidbodies = 0, meshColliders = 0, canvases = 0, trails = 0;

            int transparentRenderers = 0, shadowCasters = 0;
            foreach (var r in UnityEngine.Object.FindObjectsOfType<Renderer>())
            {
                renderers++;
                if (r is SkinnedMeshRenderer) skinned++;
                if (r.shadowCastingMode != UnityEngine.Rendering.ShadowCastingMode.Off) shadowCasters++;
                foreach (var mat in r.sharedMaterials)
                    if (mat != null && mat.renderQueue >= 2450) { transparentRenderers++; break; }
            }
            particles   = UnityEngine.Object.FindObjectsOfType<ParticleSystem>().Length;
            animators   = UnityEngine.Object.FindObjectsOfType<Animator>().Length;
            audio       = UnityEngine.Object.FindObjectsOfType<AudioSource>().Length;
            rigidbodies = UnityEngine.Object.FindObjectsOfType<Rigidbody>().Length;
            canvases    = UnityEngine.Object.FindObjectsOfType<Canvas>().Length;
            trails      = UnityEngine.Object.FindObjectsOfType<TrailRenderer>().Length;
            int dirLights = 0, pointLights = 0, spotLights = 0;
            foreach (var l in UnityEngine.Object.FindObjectsOfType<Light>())
            {
                if (l.lightmapBakeType == LightmapBakeType.Baked) continue;
                rtLights++;
                switch (l.type) {
                    case LightType.Directional: dirLights++; break;
                    case LightType.Point:  pointLights++; break;
                    case LightType.Spot:   spotLights++; break;
                }
            }
            foreach (var mc in UnityEngine.Object.FindObjectsOfType<MeshCollider>())
                if (!mc.convex) meshColliders++;

            int activeCameras = 0;
            foreach (var c in UnityEngine.Object.FindObjectsOfType<Camera>())
                if (c.enabled && c.gameObject.activeInHierarchy) activeCameras++;

            int rtReflProbes = 0;
            foreach (var rp in UnityEngine.Object.FindObjectsOfType<ReflectionProbe>())
                if (rp.mode == UnityEngine.Rendering.ReflectionProbeMode.Realtime) rtReflProbes++;

            int lodGroups = UnityEngine.Object.FindObjectsOfType<LODGroup>().Length;

            sb.Append($"\"census\":{{\"renderers\":{renderers},\"skinnedMeshes\":{skinned},\"particleSystems\":{particles}," +
                      $"\"realtimeLights\":{rtLights},\"dirLights\":{dirLights},\"pointLights\":{pointLights},\"spotLights\":{spotLights}," +
                      $"\"animators\":{animators},\"audioSources\":{audio}," +
                      $"\"rigidbodies\":{rigidbodies},\"nonConvexMeshColliders\":{meshColliders},\"canvases\":{canvases},\"trailRenderers\":{trails}," +
                      $"\"transparentRenderers\":{transparentRenderers},\"shadowCasters\":{shadowCasters}," +
                      $"\"activeCameras\":{activeCameras},\"rtReflProbes\":{rtReflProbes},\"lodGroups\":{lodGroups}}},");

            var groups = new Dictionary<string, int>();
            foreach (var go in UnityEngine.Object.FindObjectsOfType<GameObject>())
            {
                if (!go.activeInHierarchy) continue;
                string key = NormalizeName(go.name);
                groups[key] = groups.TryGetValue(key, out var n) ? n + 1 : 1;
            }
            var top = groups.Where(g => g.Value >= 10).OrderByDescending(g => g.Value).Take(15).ToList();
            sb.Append("\"heavyGroups\":[");
            for (int i = 0; i < top.Count; i++)
            {
                if (i > 0) sb.Append(",");
                sb.Append($"{{\"name\":\"{EscapeJson(top[i].Key)}\",\"count\":{top[i].Value}}}");
            }
            sb.Append("],");

            var matUsage = new Dictionary<Material, int>();
            foreach (var r in UnityEngine.Object.FindObjectsOfType<Renderer>())
                foreach (var mat in r.sharedMaterials)
                    if (mat != null) matUsage[mat] = (matUsage.TryGetValue(mat, out var mu) ? mu : 0) + 1;

            var instanceCandidates = matUsage
                .Where(p => p.Value >= 20 && !p.Key.enableInstancing && p.Key.renderQueue < 2450)
                .OrderByDescending(p => p.Value).Take(5).ToList();

            sb.Append("\"gpuInstancing\":[");
            for (int i = 0; i < instanceCandidates.Count; i++) {
                if (i > 0) sb.Append(",");
                sb.Append($"{{\"mat\":\"{EscapeJson(instanceCandidates[i].Key.name)}\",\"uses\":{instanceCandidates[i].Value}}}");
            }
            sb.Append("],");

            // 5) shader complexity — multi-pass + GrabPass
            int multiPassMats = 0, grabPassMats = 0;
            var checkedShaders = new HashSet<Shader>();
            foreach (var pair in matUsage)
            {
                if (pair.Key.shader == null || !checkedShaders.Add(pair.Key.shader)) continue;
                if (pair.Key.shader.passCount > 1) multiPassMats++;
                if (pair.Key.shader.name.IndexOf("Grab", StringComparison.OrdinalIgnoreCase) >= 0) grabPassMats++;
            }
            sb.Append($"\"shaderComplexity\":{{\"multiPassShaders\":{multiPassMats},\"grabPassShaders\":{grabPassMats}}},");

            var usedLayers = new List<int>();
            for (int i = 0; i < 32; i++)
                if (!string.IsNullOrEmpty(LayerMask.LayerToName(i))) usedLayers.Add(i);
            int activePairs = 0, maxPairs = usedLayers.Count * (usedLayers.Count + 1) / 2;
            for (int i = 0; i < usedLayers.Count; i++)
                for (int j = i; j < usedLayers.Count; j++)
                    if (!Physics.GetIgnoreLayerCollision(usedLayers[i], usedLayers[j])) activePairs++;
            sb.Append($"\"physicsMatrix\":{{\"usedLayers\":{usedLayers.Count},\"activePairs\":{activePairs},\"maxPairs\":{maxPairs}}},");

            // 7) batching analysis — dynamic eligible + static miss
            int dynamicBatchEligible = 0, staticMiss = 0;
            foreach (var mf in UnityEngine.Object.FindObjectsOfType<MeshFilter>())
                if (mf.sharedMesh != null && mf.gameObject.activeInHierarchy && mf.sharedMesh.vertexCount < 300)
                    dynamicBatchEligible++;
            foreach (var mr in UnityEngine.Object.FindObjectsOfType<MeshRenderer>())
            {
                if (!mr.enabled || !mr.gameObject.activeInHierarchy || mr.gameObject.isStatic) continue;
                if (mr.GetComponentInParent<Rigidbody>() != null || mr.GetComponentInParent<Animator>() != null) continue;
                if (mr.sharedMaterials.Any(m => m != null && m.renderQueue >= 2450)) continue;
                staticMiss++;
            }
            sb.Append($"\"batching\":{{\"dynamicEligible\":{dynamicBatchEligible},\"staticMiss\":{staticMiss}}},");

            // 8) network bandwidth + ping (Fusion)
            string net = ProfilerDeepReader.NetworkLine();
            sb.Append($"\"network\":\"{EscapeJson(net ?? "n/a")}\",");

            var warn = new List<string>();
            if (rtLights > 8)       warn.Add($"{rtLights} realtime lights are expensive. Bake stationary lighting or reduce the count.");
            if (skinned > 60)       warn.Add($"{skinned} SkinnedMesh renderers. Use LOD and culling, reduce bone counts, and consider GPU skinning.");
            if (particles > 40)     warn.Add($"{particles} ParticleSystems. Pool them, reduce maximum particles, and enable culling.");
            if (meshColliders > 15) warn.Add($"{meshColliders} non-convex MeshColliders are expensive during collision. Prefer primitive colliders.");
            if (canvases > 10)      warn.Add($"{canvases} Canvases. Split dynamic and static content to avoid full-screen rebuilds.");
            if (animators > 80)            warn.Add($"{animators} Animators. Cull off-screen animators or use GPU animation.");
            if (transparentRenderers > 50) warn.Add($"{transparentRenderers} transparent renderers cannot batch effectively and cause overdraw. Reduce them or use opaque alpha-cutout materials.");
            if (shadowCasters > 300)       warn.Add($"{shadowCasters} shadow casters require shadow-map draws. Reduce Shadow Distance or disable Cast Shadows on distant objects.");
            if (instanceCandidates.Count > 0)
                warn.Add($"GPU Instancing is disabled on {instanceCandidates.Count} materials used by at least 20 renderers. Enable it in the material inspector to reduce draw calls.");
            if (multiPassMats > 0)
                warn.Add($"{multiPassMats} multi-pass shaders render the scene more than once. Prefer URP single-pass shaders.");
            if (grabPassMats > 0)
                warn.Add($"{grabPassMats} GrabPass shaders copy the framebuffer and are very expensive. Use Camera Opaque Texture in the URP Pipeline Asset.");
            if (activeCameras > 1)
                warn.Add($"{activeCameras} active cameras each add a full render pass. Disable unused cameras or combine them with Camera Stacking.");
            if (rtReflProbes > 0)
                warn.Add($"{rtReflProbes} realtime Reflection Probes are very expensive. Use Baked mode or a Custom Cubemap.");
            if (pointLights > 4)
                warn.Add($"{pointLights} Point Lights. Each shadowed light renders six cubemap faces; bake lighting or use Light Probes.");
            if (spotLights > 6)
                warn.Add($"{spotLights} Spot Lights. Bake them or use cookie textures instead of realtime shadows.");
            if (skinned > 20 && lodGroups < skinned / 3)
                warn.Add($"{skinned} SkinnedMesh renderers but only {lodGroups} LODGroups. Add character LODs to reduce distant bone and polygon cost.");
            if (usedLayers.Count > 4 && activePairs > maxPairs * 0.7f)
                warn.Add($"Physics layer matrix: {activePairs}/{maxPairs} pairs still collide. Disable unnecessary pairs in Project Settings → Physics.");
            if (staticMiss > 50)
                warn.Add($"Approximately {staticMiss} MeshRenderers may be static but are not marked. Mark them Static to enable static batching and occlusion culling.");
            foreach (var g in top)
                if (g.Value >= 200) warn.Add($"'{g.Key}' has {g.Value} instances. Apply pooling, culling, LOD and GPU instancing.");

            sb.Append("\"warnings\":[");
            for (int i = 0; i < warn.Count; i++)
            {
                if (i > 0) sb.Append(",");
                sb.Append($"\"{EscapeJson(warn[i])}\"");
            }
            sb.Append("]}");

            string frame = ProfilerReader.IsLive ? ProfilerReader.Snapshot() : (SpikeMonitor.GetReport() ?? "");
            return sb.ToString() + "\n" + frame;
        });

        static string NormalizeName(string name)
        {
            name = name.Replace("(Clone)", "");
            name = System.Text.RegularExpressions.Regex.Replace(name, @"[\s_]*\(?\d+\)?\s*$", "");
            return name.Trim();
        }

        static string ClearConsole() => ExecuteOnMainThread(() =>
        {
            try
            {
                var logEntries = Type.GetType("UnityEditor.LogEntries,UnityEditor");
                logEntries?.GetMethod("Clear")?.Invoke(null, null);
                return "{\"cleared\":true}";
            }
            catch (Exception e) { return $"{{\"error\":\"{EscapeJson(e.Message)}\"}}"; }
        });

        static string PlayControl(string body)
        {
            var data = ParseReq<PlayRequest>(body);
            return ExecuteOnMainThread(() =>
            {
                switch ((data.action ?? "").ToLower())
                {
                    case "enter": EditorApplication.isPlaying = true;  return "{\"play\":\"entering\"}";
                    case "exit":  EditorApplication.isPlaying = false; Time.timeScale = 1f; return "{\"play\":\"exiting\"}";
                    case "pause": EditorApplication.isPaused = true;   return "{\"play\":\"paused\"}";
                    case "resume":EditorApplication.isPaused = false;  return "{\"play\":\"resumed\"}";
                    case "step":  EditorApplication.Step();            return "{\"play\":\"stepped\"}";
                    case "timescale": case "slowmo":
                        float sc = data.scale > 0 ? Mathf.Clamp(data.scale, 0.01f, 100f) : 0.2f;
                        Time.timeScale = sc;
                        return $"{{\"play\":\"timescale\",\"timeScale\":{sc}}}";
                    default: return "{\"error\":\"action must be enter|exit|pause|resume|step|timescale\"}";
                }
            });
        }

        static string ReadScript(string body)
        {
            var data = ParseReq<ReadScriptRequest>(body);
            return ExecuteOnMainThread(() =>
            {
                string path = CodebaseIndex.ResolvePath(data.name);
                if (path == null) return $"{{\"error\":\"script not found: {EscapeJson(data.name)}\"}}";

                string content = CodebaseIndex.ReadContent(path, 40000);
                if (content == null) return $"{{\"error\":\"cannot read: {EscapeJson(path)}\"}}";

                var lines = content.Split('\n');
                var sb = new StringBuilder();
                bool filter = !string.IsNullOrEmpty(data.method);
                int braceDepth = 0; bool inMethod = false; int emitted = 0;

                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i];
                    if (filter)
                    {
                        if (!inMethod && line.Contains(data.method) && (line.Contains("(")))
                        {
                            inMethod = true; braceDepth = 0;
                        }
                        if (!inMethod) continue;
                        braceDepth += CountChar(line, '{') - CountChar(line, '}');
                        sb.Append($"{i + 1}: {line}\n");
                        emitted++;
                        if (braceDepth <= 0 && line.Contains("}")) break;
                        if (emitted > 200) break;
                    }
                    else
                    {
                        sb.Append($"{i + 1}: {line}\n");
                    }
                }
                if (filter && emitted == 0)
                    return $"{{\"error\":\"method '{EscapeJson(data.method)}' not found in {EscapeJson(path)}\"}}";

                return $"{{\"path\":\"{EscapeJson(path)}\",\"source\":\"{EscapeJson(sb.ToString())}\"}}";
            });
        }

        static int CountChar(string s, char c)
        {
            int n = 0; foreach (var ch in s) if (ch == c) n++; return n;
        }

        // /diagnose/deep — full profiler deep analysis (CPU + GC + suspicious + source code)
        // wraps ProfilerDeepReader.DeepAnalysis() which already exists
        static string DiagnoseDeep(string body)
        {
            var data = ParseReq<TopNRequest>(body);
            int n = data.topN > 0 ? data.topN : 8;
            return ExecuteOnMainThread(() =>
            {
                string report = ProfilerDeepReader.DeepAnalysis(n);
                return $"{{\"report\":\"{EscapeJson(report)}\"}}";
            });
        }

        // /diagnose/memory — managed + native + graphics memory snapshot
        static string MemorySnapshot() => ExecuteOnMainThread(() =>
        {
            var sb = new System.Text.StringBuilder("{");
            sb.Append($"\"monoUsedMB\":{Profiler.GetMonoUsedSizeLong() / 1048576f:F2},");
            sb.Append($"\"monoHeapMB\":{Profiler.GetMonoHeapSizeLong() / 1048576f:F2},");
            sb.Append($"\"unityAllocMB\":{Profiler.GetTotalAllocatedMemoryLong() / 1048576f:F2},");
            sb.Append($"\"unityReservedMB\":{Profiler.GetTotalReservedMemoryLong() / 1048576f:F2},");
            sb.Append($"\"unusedReservedMB\":{Profiler.GetTotalUnusedReservedMemoryLong() / 1048576f:F2},");
            sb.Append($"\"graphicsMB\":{Profiler.GetAllocatedMemoryForGraphicsDriver() / 1048576f:F2},");
            sb.Append($"\"gc0\":{GC.CollectionCount(0)},");
            sb.Append($"\"gc1\":{GC.CollectionCount(1)},");
            sb.Append($"\"gc2\":{GC.CollectionCount(2)},");
            sb.Append($"\"isPlaying\":{Application.isPlaying.ToString().ToLower()}");
            sb.Append("}");
            return sb.ToString();
        });

        // /diagnose/fusion — detailed Photon Fusion 2 stats via reflection
        static string FusionStats() => ExecuteOnMainThread(() =>
        {
            try
            {
                if (!Application.isPlaying)
                    return "{\"error\":\"Enter Play Mode first; the Fusion runner exists only at runtime.\"}";

                Type runnerType = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    runnerType = asm.GetType("Fusion.NetworkRunner");
                    if (runnerType != null) break;
                }
                if (runnerType == null)
                    return "{\"error\":\"Fusion.NetworkRunner type not found. Verify that Photon Fusion 2 is installed.\"}";

                var runner = UnityEngine.Object.FindObjectOfType(runnerType);
                if (runner == null)
                    return "{\"error\":\"No NetworkRunner exists in the scene.\"}";

                object Get(string prop) => runnerType.GetProperty(prop)?.GetValue(runner);

                var sb = new System.Text.StringBuilder("{");

                // Tick / simulation
                var tick     = Get("Tick");
                var tickRate = Get("TickRate");
                var simTime  = Get("SimulationTime");
                var isServer = Get("IsServer");
                var isClient = Get("IsClient");
                var isResim  = Get("IsResimulating");
                var connCount = Get("ActivePlayers");

                if (tick != null)     sb.Append($"\"tick\":{tick},");
                if (tickRate != null) sb.Append($"\"tickRate\":{tickRate},");
                if (simTime != null)  sb.Append($"\"simulationTimeSec\":{Convert.ToDouble(simTime):F3},");
                if (isServer != null) sb.Append($"\"isServer\":{isServer.ToString().ToLower()},");
                if (isClient != null) sb.Append($"\"isClient\":{isClient.ToString().ToLower()},");
                if (isResim != null)  sb.Append($"\"isResimulating\":{isResim.ToString().ToLower()},");

                // count active players
                int playerCount = 0;
                if (connCount is System.Collections.IEnumerable en)
                    foreach (var _ in en) playerCount++;
                sb.Append($"\"connectedPlayers\":{playerCount},");

                // RTT local player
                var localPlayer = Get("LocalPlayer");
                var rttMethod = runnerType.GetMethod("GetPlayerRtt");
                var perRtt = new System.Collections.Generic.List<string>();
                if (rttMethod != null && connCount is System.Collections.IEnumerable players2)
                {
                    foreach (var p in players2)
                    {
                        object rtt = rttMethod.Invoke(runner, new[] { p });
                        if (rtt is double d) perRtt.Add($"\"P{p}\":{d * 1000.0:F0}");
                        if (perRtt.Count >= 10) break;
                    }
                }
                sb.Append($"\"rttMs\":{{{string.Join(",", perRtt)}}},");

                // GetStats() — bandwidth / packet loss / resend
                try
                {
                    var statsMethod = runnerType.GetMethod("GetStats", Type.EmptyTypes);
                    object stats = statsMethod?.Invoke(runner, null);
                    if (stats != null)
                    {
                        var st = stats.GetType();
                        object Val(string m) => st.GetProperty(m)?.GetValue(stats) ?? st.GetField(m)?.GetValue(stats);
                        void AppNum(string key, string member)
                        {
                            var v = Val(member);
                            if (v != null) { double d = Convert.ToDouble(v); if (d > 0) sb.Append($"\"{key}\":{d:F1},"); }
                        }
                        AppNum("inKBps",       "InKBps");
                        AppNum("outKBps",      "OutKBps");
                        AppNum("inBandwidth",  "InBandwidth");
                        AppNum("outBandwidth", "OutBandwidth");
                        AppNum("packetLoss",   "PacketLoss");
                        AppNum("resendRate",   "ResendRate");

                        var resimCount = Val("ResimulationCount") ?? Val("Resimulations");
                        if (resimCount != null) sb.Append($"\"resimCount\":{resimCount},");

                        var snapSize = Val("SnapshotSize") ?? Val("StateDeltaSize");
                        if (snapSize != null) sb.Append($"\"snapshotDeltaBytes\":{snapSize},");
                    }
                }
                catch { }

                // trim trailing comma
                string result = sb.ToString().TrimEnd(',');
                return result + "}";
            }
            catch (Exception e)
            {
                return $"{{\"error\":\"{EscapeJson(e.Message)}\"}}";
            }
        });

        // ── Request models ────────────────────────────────────────────────────
        [Serializable] class ConsoleRequest      { public int max; }
        [Serializable] class PlayRequest         { public string action; public float scale; }
        [Serializable] class ReadScriptRequest   { public string name; public string method; }
        [Serializable] class ComponentRequest    { public string name; public string component; }
        [Serializable] class SetPropertyRequest  { public string name; public string component; public string property; public string value; }
        [Serializable] class SetTransformRequest { public string name; public string set; public float px, py, pz, rx, ry, rz, sx, sy, sz; }
        [Serializable] class PathRequest         { public string path; }
        [Serializable] class TopNRequest         { public int topN; }
        [Serializable] class InspectRequest      { public string name; public bool deep; }
    }
}
