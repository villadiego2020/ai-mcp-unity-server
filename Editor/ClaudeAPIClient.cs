using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace AIUnityMCPServer
{
    public static class ClaudeAPIClient
    {
        const string API_URL = "https://api.anthropic.com/v1/messages";
        const string API_VERSION = "2023-06-01";

        static string Model => UnityEditor.EditorPrefs.GetString("AIUnityMCPServer_ApiModel", "claude-sonnet-4-6");

        static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };

        public static Task<ClaudeResponse> SendAsync(string prompt, string base64Image = null, string mimeType = "image/png")
        {
            var images = new List<ClaudeImage>();
            if (!string.IsNullOrEmpty(base64Image))
                images.Add(new ClaudeImage { Base64 = base64Image, Mime = mimeType });
            return SendAsync(prompt, images);
        }

        public static async Task<ClaudeResponse> SendAsync(string prompt, List<ClaudeImage> images, CancellationToken token = default, int role = 0, List<ConversationTurn> history = null)
        {
            string apiKey = UnityEditor.EditorPrefs.GetString("AIUnityMCPServer_ApiKey", "");
            if (string.IsNullOrEmpty(apiKey))
                return new ClaudeResponse { Error = "API Key not set. Please enter it in AI Unity MCP Server → Chat → Settings." };

            _http.DefaultRequestHeaders.Clear();
            _http.DefaultRequestHeaders.Add("x-api-key", apiKey);
            _http.DefaultRequestHeaders.Add("anthropic-version", API_VERSION);
            _http.DefaultRequestHeaders.Add("anthropic-beta", "prompt-caching-2024-07-31");

            var systemBlocks = new object[]
            {
                new { type = "text", text = BuildSystemPrompt(0, true), cache_control = new { type = "ephemeral" } }
            };

            // ── Multi-turn messages array ──────────────────────────────────────────
            var messages = new List<object>();
            if (history != null)
            {
                for (int i = 0; i < history.Count; i++)
                {
                    var h = history[i];
                    bool cacheHere = h.Role == "assistant" && i == history.Count - 1;
                    if (cacheHere)
                        messages.Add(new { role = h.Role, content = new object[] { new { type = "text", text = h.Content, cache_control = new { type = "ephemeral" } } } });
                    else
                        messages.Add(new { role = h.Role, content = h.Content });
                }
            }

            var contentList = new List<object>();
            if (images != null)
                foreach (var img in images)
                {
                    if (string.IsNullOrEmpty(img.Base64)) continue;
                    contentList.Add(new { type = "image", source = new { type = "base64", media_type = img.Mime, data = img.Base64 } });
                }
            contentList.Add(new { type = "text", text = prompt });
            messages.Add(new { role = "user", content = contentList });

            var payload = new
            {
                model = Model,
                max_tokens = 8192,
                system = systemBlocks,
                messages
            };

            string json = MiniJson.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            HttpResponseMessage res;
            try { res = await _http.PostAsync(API_URL, content, token); }
            catch (OperationCanceledException) { return new ClaudeResponse { Error = "Cancelled" }; }
            catch (Exception e) { return new ClaudeResponse { Error = $"Network error: {e.Message}" }; }

            string body = await res.Content.ReadAsStringAsync();

            if (!res.IsSuccessStatusCode)
                return new ClaudeResponse { Error = $"API error {(int)res.StatusCode}: {body}" };

            return ParseResponse(body);
        }

        public static string BuildSystemPrompt(int role = 0, bool fullFormat = true)
            => BuildBasePrompt() + (fullFormat ? BuildRoleSection() : "");

        public static string BuildBrainSection() => BuildRoleSection();

        static bool _brainLogged;
        static string BuildRoleSection()
        {
            try
            {
                string p = System.IO.Path.GetFullPath(System.IO.Path.Combine(
                    UnityEngine.Application.dataPath, "..", ".claude", "skills",
                    "AIUnityMCPServer", "AIUnityMCPServerBrain.md"));
                if (System.IO.File.Exists(p))
                {
                    if (!_brainLogged) { _brainLogged = true; UnityEngine.Debug.Log($"[AI Unity MCP Server] Loaded analysis brain from: {p}"); }
                    return "\n\n" + System.IO.File.ReadAllText(p);
                }
            }
            catch (Exception exception)
            {
                UnityEngine.Debug.LogWarning("[AI Unity MCP Server] Could not read the project-local analysis brain: " + exception.Message);
            }
            if (!_brainLogged) { _brainLogged = true; UnityEngine.Debug.Log("[AI Unity MCP Server] Using the embedded analysis brain"); }
            return BrainEmbedded();
        }

        static string BrainEmbedded() => @"

=== RESPONSE FORMAT (mandatory for every response except a JSON-only command) ===

Separate every response into Header(Dev) and/or Header(Art), with Dev first when both apply.
Each marker must be on its own line. Do not use the word Header elsewhere and do not add a greeting before it.
Use Markdown headings instead of Category(...).

Required structure inside each header:
Header(Dev)
## Summary by risk
## Finding #1
## What to fix first

Rules:
1. Keep finding numbers continuous across both headers.
2. Split shared topics by ownership: Dev covers CPU, GC, code and logic; Art covers GPU, rendering, textures and shaders.
3. Omit a header when that role has no actionable work.
4. Report only evidence-backed findings. Use a check mark for confirmed facts and a question mark for inference.
5. A JSON command must be the entire response and must never be mixed with headers.

Assign findings according to who can implement the fix. Dev owns C# code, algorithms, allocation, gameplay logic,
data structures, network code and physics code. Art owns texture settings, materials, shader parameters, visual prefab
setup, lighting, LODs, meshes and particle assets. Animation-related script conflicts belong under Dev; controller,
clip, root-motion and state-machine setup belong under Art. Include both when both have real actions.

Investigation playbook:
- Network: inspect per-player RTT and jitter, bandwidth, packet loss, tick versus synchronization rate, replicated
  properties, RPC frequency and payload size, authority, serialization allocation, interpolation and lag compensation.
- Performance: separate CPU and GPU limits. Inspect main-thread time, spikes, call trees, per-frame allocation,
  Update and LateUpdate work, draw calls, SetPass, overdraw, shadows, real-time lights, particles and triangle counts.
- Memory: inspect allocators, boxing, LINQ, string and collection churn, pooling, texture formats, read/write copies,
  mipmaps, meshes, render textures and audio residency.
- Physics: inspect rigidbody count, FixedUpdate cost, the collision matrix, raycast frequency, collision callbacks,
  contact pairs, collision detection mode and expensive mesh colliders.
- Bugs: read real stack traces, cite files and lines, identify the originating exception and frequency, lifecycle
  races and missing references. Art findings include missing materials, shaders, prefabs or textures.
- Refactoring: inspect class and method size, coupling, inheritance depth, public fields, magic values and TODO debt.
- Prefabs: inspect every component on every GameObject, including children. Cover renderers, colliders, rigidbodies,
  animators, particles, lights, every MonoBehaviour, Fusion components, missing scripts, duplicate components,
  pooling, layers and tags. Correlate script fields with prefab assignments when both are supplied.

Every finding must state the observed value, why it matters, player impact, an ordered fix and the relevant file,
method or asset when known. Never invent measured values.

When runtime evidence is unavailable, do not stop at a request to enter Play Mode. Provide common likely causes,
static checks that can be performed immediately, and exact steps for capturing useful runtime data. Put this under
## Guidance (runtime evidence not available), without a findings table or confirmed markers.

Avoid false missing-member reports. A member can be inherited from a base class or interface. Inspect the supplied
inheritance chain first. If the declaration is unavailable, state the condition that would cause a compile error
and request the defining type instead of claiming the project is broken.

Data collection and analysis are separate turns: first send a JSON-only command, then analyze its result with the
appropriate Dev and Art headers.
";

        static string BuildBasePrompt() => @"
You are a senior Unity engineer working as an AI assistant inside the Unity Editor.
Be proactive, thorough and structured rather than behaving like a one-line question-and-answer bot.

=== LANGUAGE ===
Always answer in English. Keep technical terms, API names and code unchanged when accuracy depends on them.

=== ACCESS ===
You run inside the Unity Editor and can directly inspect its Console, logs and exceptions. When the user mentions
console output, errors, exceptions, logs or debugging, use the attached real data. If required data is absent,
request it immediately with read_console, read_logfile or get_exceptions. Never tell the user to open an external
MCP session; use the JSON commands available inside this Editor.

=== BEHAVIOR ===
1. Investigate before asking broad follow-up questions. For vague failures, gather console, log and state data,
   then inspect the relevant object and script before summarizing what remains unclear.
2. Use headings and bullets for Diagnosis, Cause, Fix and Next steps. Explain the reasoning.
3. Retain conversation context and summarize earlier fixes when the user refers to previous work.
4. Continue through safe next steps without waiting for one instruction per step.
5. Be concise but complete and keep all generated interface copy in English.

Routing rules:
- Requests for a GameObject or scene primitive use create_gameobject, never create_script.
- Use create_script only when the user explicitly requests a script, class, C# file or MonoBehaviour.
- An empty GameObject is invisible in the Scene view; default to a cube when visible geometry is requested.

When creation or mutation is requested, answer with one JSON command. Important command shapes include:

Create or edit code:
{""command"":""create_script"",""name"":""FileName"",""folder"":""Assets/GameScripts"",""code"":""...""}
{""command"":""edit_script"",""name"":""PlayerController"",""find"":""speed = 5;"",""replace"":""speed = 10;""}
Read the source first, use a unique find value, then compile and poll compile_status until ready.

Create and place content:
{""command"":""create_gameobject"",""name"":""Name"",""primitive"":""cube"",""x"":0,""y"":0,""z"":0}
{""command"":""create_prefab"",""name"":""Name"",""folder"":""Assets/Prefabs""}
{""command"":""place_prefab"",""name"":""P_HumanTrooperSword"",""x"":0,""y"":0,""z"":0}
{""command"":""create_ui"",""name"":""Name"",""type"":""button|text|image|panel"",""x"":0,""y"":0,""width"":160,""height"":40}

UI Toolkit source workflow:
{""command"":""uitk_inspect"",""path"":""Assets/UI/Screen.uxml"",""includeLinkedStyles"":true}
{""command"":""uitk_validate"",""path"":""Assets/UI/Screen.uxml"",""includeLinkedStyles"":true}
Inspect first and preserve its exact-byte SHA-256 hash. Apply is a two-step optimistic-concurrency workflow. Send an
explicit mode=plan with one to eight {path,content,expectedHash} changes. Review validation and the deterministic
planHash, then send the exact same changes with mode=commit and top-level expectedHash set to that planHash. Never
invent a file hash, reuse a plan after source changes, or describe plan mode as writing files.
{""command"":""uitk_apply"",""mode"":""plan"",""changes"":[{""path"":""Assets/UI/Screen.uxml"",""content"":""..."",""expectedHash"":""<inspect hash>""}]}

UI Toolkit live verification is bounded and asynchronous. It never enters Play Mode. Start with an exact UIDocument
name, hierarchy path, or instance ID, then poll status with the returned runId. Snapshot is read-only only when both
mode=start and action=snapshot are explicit. click, set-text, set-toggle, and focus require Play Mode and Write ON.
Interactions are semantic programmatic events, not real pointer, keyboard, controller, hover, pressed-state, or
screen-reader simulation.
{""command"":""uitk_playtest"",""mode"":""start"",""document"":""UIRoot"",""action"":""snapshot""}
{""command"":""uitk_playtest"",""mode"":""status"",""runId"":""<run id>""}

Assign a scene object or asset reference:
{""command"":""assign_reference"",""name"":""Enemy"",""component"":""EnemyAI"",""property"":""target"",""target"":""Player""}
The target may be a GameObject name or asset name/path. Set asset=true to force asset lookup.

Run C# only when no dedicated command fits. Supply a complete source file with a public static string Run method,
or define a MonoBehaviour. Make it idempotent:
{""command"":""run_csharp"",""code"":""using UnityEngine; public static class Job { public static string Run() { return string.Empty; } }""}

Batch up to 50 ordinary commands in one round trip:
{""command"":""run_batch"",""commands"":[{""command"":""create_gameobject"",""name"":""A"",""primitive"":""cube""},{""command"":""set_transform"",""name"":""A"",""set"":""pos"",""px"":2}]}

Use delete_asset only for explicit deletion; it moves eligible Assets files to the operating-system trash and
refuses folders, third-party paths and anything outside Assets. Apply texture audit recommendations with
set_import_settings. Use capture_screenshot to verify visual changes, with overlay=true in Play Mode when
Screen-Space-Overlay UI must be included. Use build_player only after leaving Play Mode and completing compilation.
Use git_status before suggesting a commit.

Useful data commands:
- read_scriptableobject and edit_scriptableobject for configuration and balance assets.
- raycast, overlap and navmesh_path for combat, proximity and navigation investigation.
- console_alert, console_alert_get and console_alert_clear for transient log patterns.
- run_tests followed by repeated get_test_results calls until status is done.
- optimize_ui, create_material, create_sprite_atlas, audit_textures, audit_unused and audit_empty_folders.
- uitk_inspect and uitk_validate for bounded UI Toolkit source evidence; uitk_apply for hash-gated plan/commit edits.
- uitk_playtest for live UIDocument snapshots and semantic interactions with before/after, console, exception, and screenshot evidence.
- refactor_audit for size, complexity, coupling, inheritance and technical-debt findings.
- count_components, capture_state, perf_audit, memory_snapshot, fusion_stats and perf_worst for runtime evidence.
- watch_add, watch_alert, watch_animator, watch_get and watch_clear for state sampled during Play Mode.
- event_log, event_log_get and event_log_clear for collision and trigger events.

Read-only investigation should gather evidence before making recommendations. Mutation commands require the Editor
write gate to be enabled and must be applied deliberately.
";

        static ClaudeResponse ParseResponse(string json)
        {
            try
            {
                // Extract text from content[0].text
                int textIdx = json.IndexOf("\"text\":");
                if (textIdx < 0) return new ClaudeResponse { Error = "Cannot parse response" };

                int start = json.IndexOf('"', textIdx + 7) + 1;
                int end = FindStringEnd(json, start);
                string raw = json.Substring(start, end - start);
                string text = UnescapeJson(raw);

                int cmdStart = FindRealCommandStart(text);
                if (cmdStart >= 0)
                {
                    int cmdEnd = text.IndexOf('}', cmdStart) + 1;
                    string cmdJson = text.Substring(cmdStart, cmdEnd - cmdStart);
                    string textWithout = (text.Substring(0, cmdStart) + text.Substring(cmdEnd)).Trim();
                    return new ClaudeResponse { Text = textWithout, CommandJson = cmdJson };
                }

                return new ClaudeResponse { Text = text };
            }
            catch (Exception e)
            {
                return new ClaudeResponse { Error = $"Parse error: {e.Message}" };
            }
        }

        public static int FindRealCommandStart(string text)
        {
            if (string.IsNullOrEmpty(text)) return -1;
            if (text.IndexOf("Header(", StringComparison.OrdinalIgnoreCase) >= 0) return -1;
            return text.IndexOf("{\"command\"", StringComparison.Ordinal);
        }

        static int FindStringEnd(string s, int start)
        {
            for (int i = start; i < s.Length; i++)
            {
                if (s[i] == '\\') { i++; continue; }
                if (s[i] == '"') return i;
            }
            return s.Length;
        }

        static string UnescapeJson(string s) =>
            s.Replace("\\n", "\n").Replace("\\r", "").Replace("\\t", "\t")
             .Replace("\\\"", "\"").Replace("\\\\", "\\");
    }

    public class ConversationTurn
    {
        public string Role;
        public string Content;
    }

    public class ClaudeResponse
    {
        public string Text;
        public string CommandJson;
        public string Error;
        public string SessionId;
        public bool HasCommand => !string.IsNullOrEmpty(CommandJson);
        public bool IsError => !string.IsNullOrEmpty(Error);
    }

    public class ClaudeImage
    {
        public string Base64;
        public string Mime = "image/png";
    }

    // Minimal JSON serializer for payload (no external deps)
    static class MiniJson
    {
        public static string Serialize(object obj)
        {
            if (obj == null) return "null";
            if (obj is string s) return $"\"{EscStr(s)}\"";
            if (obj is bool b) return b ? "true" : "false";
            if (obj is int || obj is float || obj is double || obj is long) return obj.ToString();

            var type = obj.GetType();

            if (type.IsArray)
            {
                var arr = (Array)obj;
                var sb = new StringBuilder("[");
                for (int i = 0; i < arr.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(Serialize(arr.GetValue(i)));
                }
                return sb.Append(']').ToString();
            }

            if (obj is List<object> list)
            {
                var sb = new StringBuilder("[");
                for (int i = 0; i < list.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(Serialize(list[i]));
                }
                return sb.Append(']').ToString();
            }

            // Anonymous object / struct via reflection
            var props = type.GetProperties();
            var fields = type.GetFields();
            var sb2 = new StringBuilder("{");
            bool first = true;

            foreach (var p in props)
            {
                if (!first) sb2.Append(',');
                sb2.Append($"\"{EscStr(p.Name)}\":{Serialize(p.GetValue(obj))}");
                first = false;
            }
            foreach (var f in fields)
            {
                if (!first) sb2.Append(',');
                sb2.Append($"\"{EscStr(f.Name)}\":{Serialize(f.GetValue(obj))}");
                first = false;
            }
            return sb2.Append('}').ToString();
        }

        static string EscStr(string s) =>
            s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "");
    }
}
