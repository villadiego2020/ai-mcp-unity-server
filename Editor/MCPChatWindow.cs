// MCPChatWindow — AI Unity MCP Server chat UI
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace AIUnityMCPServer
{
    public class MCPChatWindow : EditorWindow
    {
        [SerializeField] ChatSession _api = new ChatSession();
        [SerializeField] ChatSession _cli = new ChatSession();

        ChatSession S => CurrentBackend() == 0 ? _api : _cli;

        string _apiKey = "";
        bool _showSettings;
        int _lastRole = -1;
        Vector2 _inputScroll;
        bool _autoScroll = true;

        bool _showScriptList;
        string _scriptQuery = "";
        Vector2 _scriptScroll;

        bool _showPrefabList;
        string _prefabQuery = "";
        Vector2 _prefabScroll;

        bool _showSkillList;
        string _skillQuery = "";
        Vector2 _skillScroll;

        int _caretToEndFrames;
        bool _showLive;
        double _lastLiveRepaint;
        double _lastThinkRepaint;
        bool _showKeywords;
        bool _showWatch;
        string _watchField = "";
        double _lastWatchRepaint;

        [SerializeField] int _activeTab;
        Vector2 _logScroll;

        GUIStyle _msgTextStyle, _roleUser, _roleClaude;
        Font _logFont;
        Font LogFont => _logFont != null ? _logFont
            : (_logFont = Font.CreateDynamicFontFromOSFont(
                new[] { "Consolas", "Menlo", "Courier New", "monospace" }, FONT_SIZE - 1));
        const string UI_FONT_NAME = "IBMPlexSansThaiLooped";
        Font _uiFont;
        Font UiFont
        {
            get
            {
                if (_uiFont != null) return _uiFont;
                foreach (var guid in AssetDatabase.FindAssets($"{UI_FONT_NAME} t:Font"))
                {
                    _uiFont = AssetDatabase.LoadAssetAtPath<Font>(AssetDatabase.GUIDToAssetPath(guid));
                    if (_uiFont != null) break;
                }
                if (_uiFont == null)
                    _uiFont = Font.CreateDynamicFontFromOSFont(
                        new[] { "Inter", "Segoe UI Variable", "Leelawadee UI", "Thonburi", "Tahoma", "Segoe UI", "Arial" }, MSG_FONT);
                return _uiFont;
            }
        }

        static void DrawSparkline(Rect r, string[] hist, Color col)
        {
            if (Event.current.type != EventType.Repaint || hist == null || hist.Length < 2) return;
            var nums = new float[hist.Length];
            float mn = float.MaxValue, mx = float.MinValue; int finite = 0;
            for (int i = 0; i < hist.Length; i++)
            {
                if (float.TryParse(hist[i], out float f)) { nums[i] = f; mn = Mathf.Min(mn, f); mx = Mathf.Max(mx, f); finite++; }
                else nums[i] = float.NaN;
            }
            if (finite < 2) return;
            float range = Mathf.Approximately(mx, mn) ? 1f : (mx - mn);
            float bw = r.width / hist.Length;
            var c = new Color(col.r, col.g, col.b, 0.55f);
            for (int i = 0; i < hist.Length; i++)
            {
                if (float.IsNaN(nums[i])) continue;
                float t = (nums[i] - mn) / range;            // 0..1
                float h = Mathf.Max(2f, t * (r.height - 2f) + 1f);
                EditorGUI.DrawRect(new Rect(r.x + i * bw, r.y + r.height - h, Mathf.Max(1.5f, bw - 1f), h), c);
            }
        }

        static string Trunc(string s, int max) =>
            string.IsNullOrEmpty(s) ? "" : (s.Length > max ? s.Substring(0, max) + "…" : s);

        static string FmtTime(double sec)
        {
            int s = (int)sec;
            return s < 60 ? $"{s}s" : $"{s / 60}m {s % 60:00}s";
        }


        // smooth scroll
        float _scrollTarget = -1f;
        bool _scrollAnim;
        bool _stickBottom = true;
        const string THINKING = "\x02THINKING";
        const string QUEUED   = "\x03QUEUED";

        const int MAX_IMAGES = 8;
        const int FONT_SIZE = 12;
        const int MSG_FONT  = 14;
        const int SCRIPT_LIST_HEIGHT = 162;
        const float INPUT_MIN = 40f;
        const float INPUT_MAX = 160f;
        float _inputHeight = INPUT_MIN;

        // ── Theme (Midnight Indigo — cool charcoal + indigo accent) ──────────
        static readonly Color BG_DARK     = new Color(0.059f, 0.067f, 0.090f); // #0F1117 base
        static readonly Color BG_SURFACE  = new Color(0.094f, 0.106f, 0.133f); // #181B22 bubble/input
        static readonly Color BG_RAISED   = new Color(0.125f, 0.141f, 0.180f); // #20242E header / chips
        static readonly Color BORDER      = new Color(0.176f, 0.200f, 0.251f); // #2D3340
        static readonly Color BORDER_SOFT = new Color(0.137f, 0.157f, 0.204f); // #232834
        static readonly Color ACCENT      = new Color(0.486f, 0.424f, 1.000f); // #7C6CFF indigo-violet
        static readonly Color ACCENT_2    = new Color(0.847f, 0.475f, 0.961f); // #D879F5 violet-pink (Art role)
        static readonly Color TEXT_WHITE  = new Color(0.933f, 0.941f, 0.957f); // #EEF0F4 near-white
        static readonly Color TEXT_MUTE   = new Color(0.604f, 0.627f, 0.678f); // #9AA0AD secondary
        static readonly Color TEXT_HINT   = new Color(0.361f, 0.388f, 0.439f); // #5C6370 hint
        static readonly Color ONLINE      = new Color(0.239f, 0.863f, 0.592f); // #3DDC97 green dot
        static readonly Color DANGER      = new Color(1.000f, 0.420f, 0.420f); // #FF6B6B error/red
        static readonly Color WARN        = new Color(1.000f, 0.722f, 0.282f); // #FFB848 warning

        // ── rounded-rect helpers (Unity 2022.3 GUI.DrawTexture borderRadius) ──
        static void RRect(Rect r, Color c, float radius)
        {
            if (Event.current.type != EventType.Repaint) return;
            GUI.DrawTexture(r, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0f, c, 0f, radius);
        }
        static void RRect4(Rect r, Color c, float tl, float tr, float br, float bl)
        {
            if (Event.current.type != EventType.Repaint) return;
            GUI.DrawTexture(r, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0f, c,
                Vector4.zero, new Vector4(tl, tr, br, bl));
        }
        static void RBox(Rect r, Color fill, Color border, float radius)
        {
            RRect(r, border, radius);
            RRect(new Rect(r.x + 1f, r.y + 1f, r.width - 2f, r.height - 2f), fill, Mathf.Max(0f, radius - 1f));
        }
        static void CenterLabel(Rect r, string text, Color c, int fontSize)
        {
            var st = new GUIStyle(EditorStyles.label) { alignment = TextAnchor.MiddleCenter, fontSize = fontSize, richText = true };
            st.normal.textColor = c;
            GUI.Label(r, text, st);
        }

        static Texture2D _tabIcon;
        static Texture2D TabIcon()
        {
            if (_tabIcon != null) return _tabIcon;
            const int S = 32;
            float c = (S - 1) / 2f, half = S * 0.46f, corner = S * 0.26f, star = S * 0.30f;
            var t = new Texture2D(S, S, TextureFormat.RGBA32, false)
                { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Bilinear };
            for (int y = 0; y < S; y++)
            for (int x = 0; x < S; x++)
            {
                float dx = x - c, dy = y - c;
                float qx = Mathf.Max(Mathf.Abs(dx) - (half - corner), 0f);
                float qy = Mathf.Max(Mathf.Abs(dy) - (half - corner), 0f);
                bool inBg = Mathf.Sqrt(qx * qx + qy * qy) <= corner;
                float a = Mathf.Pow(Mathf.Abs(dx) / star, 2f / 3f) + Mathf.Pow(Mathf.Abs(dy) / star, 2f / 3f);
                t.SetPixel(x, S - 1 - y, !inBg ? Color.clear : (a <= 1f ? Color.white : ACCENT));
            }
            t.Apply();
            return t;
        }

        // ── Open ──────────────────────────────────────────────────────────
        [MenuItem("AI Unity MCP Server/Chat _F12")]
        public static void Open() => GetWindow<MCPChatWindow>("AI Unity MCP Server").minSize = new Vector2(440, 600);

        void OnEnable()
        {
            titleContent = new GUIContent("AI Unity MCP Server", TabIcon());
            wantsMouseMove = true;
            _apiKey = EditorPrefs.GetString("AIUnityMCPServer_ApiKey", "");
            _api.backend = 0;
            _cli.backend = 1;
            _api.Reinit(); _cli.Reinit();
            if (_api.messages.Count == 0) LoadHistory(_api);
            if (_cli.messages.Count == 0) LoadHistory(_cli);
            CleanupStalePlaceholders(_api);
            CleanupStalePlaceholders(_cli);
            CodebaseIndex.Refresh();
            SkillIndex.Refresh();
            PrefabIndex.RefreshAsync();
        }

        void OnDisable() { SaveHistory(_api); SaveHistory(_cli); }

        void OnInspectorUpdate() { if (CpuDeepCapture.IsCapturing) Repaint(); }

        static int CurrentBackend() => EditorPrefs.GetInt("AIUnityMCPServer_Backend", 0);

        static string HistoryPath(int backend)
        {
            string dir = System.IO.Path.Combine(Application.dataPath, "..", "Library", "AIUnityMCPServer");
            System.IO.Directory.CreateDirectory(dir);
            return System.IO.Path.Combine(dir, $"chat_{backend}.json");
        }

        [Serializable] class HistoryWrap { public List<ChatMessage> items; }

        void SaveHistory(ChatSession s)
        {
            try
            {
                var keep = s.messages.FindAll(m => !(m.Role == "assistant" && (m.Content == THINKING || m.Content == QUEUED)));
                System.IO.File.WriteAllText(HistoryPath(s.backend), JsonUtility.ToJson(new HistoryWrap { items = keep }));
            }
            catch { }
        }

        static void CleanupStalePlaceholders(ChatSession s)
        {
            int removed = s.messages.RemoveAll(m => m.Role == "assistant" && (m.Content == THINKING || m.Content == QUEUED));
            if (removed > 0)
                Debug.Log($"[AI Unity MCP Server] Removed {removed} stale thinking bubbles interrupted by compilation or reload. Submit the request again.");
        }

        void LoadHistory(ChatSession s)
        {
            try
            {
                string path = HistoryPath(s.backend);
                string json = System.IO.File.Exists(path) ? System.IO.File.ReadAllText(path) : "";

                if (string.IsNullOrEmpty(json))
                    json = EditorPrefs.GetString($"AIUnityMCPServer_ChatHistory_{s.backend}", "");

                var wrap = string.IsNullOrEmpty(json) ? null : JsonUtility.FromJson<HistoryWrap>(json);
                s.messages = wrap?.items ?? new List<ChatMessage>();
                s.messages.RemoveAll(m => string.IsNullOrEmpty(m.Role) || m.Content == null);
                foreach (var m in s.messages)
                {
                    if (m.Role == "user" || !string.IsNullOrEmpty(m.Stat)) continue;
                    var mt = System.Text.RegularExpressions.Regex.Match(m.Content, @"\n*⏱[^\n]*$");
                    if (mt.Success) { m.Stat = mt.Value.Trim(); m.Content = m.Content.Substring(0, mt.Index).TrimEnd(); }
                }
            }
            catch { s.messages = new List<ChatMessage>(); }
        }

        void SwitchBackend(int target)
        {
            if (target == CurrentBackend()) return;
            EditorPrefs.SetInt("AIUnityMCPServer_Backend", target);
            _showScriptList = false;
            GUI.FocusControl(null);
        }

        readonly Queue<System.Action> _pending = new Queue<System.Action>();
        bool _refocusInput;

        // ── GUI ───────────────────────────────────────────────────────────
        void OnGUI()
        {
            int curRoleNow = CurrentRole();
            if (curRoleNow != _lastRole)
            {
                _lastRole = curRoleNow;
                foreach (var m in _api.messages) m.InvalidateCaches();
                foreach (var m in _cli.messages) m.InvalidateCaches();
            }

            if (Event.current.type == EventType.Layout && _pending.Count > 0)
            {
                bool wasTyping = GUI.GetNameOfFocusedControl() == "PromptField";
                while (_pending.Count > 0) _pending.Dequeue()?.Invoke();
                if (wasTyping) _refocusInput = true;
            }

            EditorGUI.DrawRect(new Rect(0, 0, position.width, position.height), BG_DARK);

            if ((_showSettings || _showScriptList || _showPrefabList || _showSkillList) &&
                Event.current.type == EventType.MouseMove)
                Repaint();

            DrawTabs();
            var tabSep = GUILayoutUtility.GetRect(0, 1, GUILayout.ExpandWidth(true));
            if (Event.current.type == EventType.Repaint) EditorGUI.DrawRect(tabSep, BORDER_SOFT);
            EditorGUILayout.Space(6);

            if (EditorApplication.isCompiling)
            {
                DrawCompilingOverlay();
                Repaint();
                return;
            }

            if (_showSettings) { DrawSettings(); return; }

            if (_activeTab == 2) { DrawMcpLog(); return; }
            DrawChatHistory();
            DrawInputArea();

            if (_refocusInput && Event.current.type == EventType.Repaint)
            {
                EditorGUI.FocusTextInControl("PromptField");
                _refocusInput = false;
                _caretToEndFrames = 2;
                Repaint();
            }
            if (_caretToEndFrames > 0 && Event.current.type == EventType.Repaint)
            {
                if (GUI.GetNameOfFocusedControl() == "PromptField")
                {
                    var te = GUIUtility.QueryStateObject(typeof(TextEditor), GUIUtility.keyboardControl) as TextEditor;
                    if (te != null) { te.cursorIndex = te.text.Length; te.selectIndex = te.cursorIndex; }
                }
                _caretToEndFrames--;
                Repaint();
            }
        }

        void DrawTabs()
        {
            int backend = CurrentBackend();

            int logCount = MCPHandlers.Log.Count;
            bool srvOn = MCPServer.IsRunning;
            string srvDot = srvOn ? "● " : "○ ";
            var labels = new[] {
                "API Chat"     + Badge(_api),
                "Subscription" + Badge(_cli),
                srvDot + "Claude In" + (logCount > 0 ? $" ({logCount})" : ""),
            };

            if (_activeTab < 2 && _activeTab != backend) _activeTab = backend;

            var barR = EditorGUILayout.GetControlRect(false, 38, GUILayout.ExpandWidth(true));

            var lblStyle = new GUIStyle(EditorStyles.label)
            {
                fontSize  = FONT_SIZE,
                alignment = TextAnchor.MiddleCenter,
            };

            const float SEG_PAD = 18f;
            const float TRK_PAD = 4f;
            var segW = new float[labels.Length];
            float totalW = 0f;
            for (int i = 0; i < labels.Length; i++)
            {
                lblStyle.fontStyle = FontStyle.Bold;
                segW[i] = Mathf.Ceil(lblStyle.CalcSize(new GUIContent(labels[i])).x) + SEG_PAD * 2;
                totalW += segW[i];
            }

            var track = new Rect(barR.x + 12, barR.y + 4, totalW + TRK_PAD * 2, barR.height - 8);
            RBox(track, BG_SURFACE, BORDER_SOFT, 9f);

            int picked = _activeTab;
            float sx = track.x + TRK_PAD;
            for (int ti = 0; ti < labels.Length; ti++)
            {
                bool isActive  = _activeTab == ti;
                var  segR      = new Rect(sx, track.y + 3, segW[ti], track.height - 6);
                var  tabR      = new Rect(sx, barR.y, segW[ti], barR.height);

                if (Event.current.type == EventType.Repaint)
                {
                    if (isActive) RRect(segR, ACCENT, 7f);

                    lblStyle.fontStyle = isActive ? FontStyle.Bold : FontStyle.Normal;
                    lblStyle.normal.textColor = isActive ? Color.white : TEXT_MUTE;
                    GUI.Label(segR, labels[ti], lblStyle);
                }

                if (GUI.Button(tabR, GUIContent.none, GUIStyle.none))
                    picked = ti;

                sx += segW[ti];
            }

            bool srvOn2     = MCPServer.IsRunning;
            bool onClaudeIn = _activeTab == 2;
            float rightX = barR.xMax - 12;

            var gear = new GUIStyle(EditorStyles.label) { alignment = TextAnchor.MiddleCenter, fontSize = FONT_SIZE + 3 };
            gear.normal.textColor = _showSettings ? ACCENT : TEXT_MUTE;
            var gearR = new Rect(rightX - 22, barR.y + 7, 22, 24);
            GUI.Label(gearR, "⚙", gear);
            if (GUI.Button(gearR, GUIContent.none, GUIStyle.none)) { _showSettings = !_showSettings; Repaint(); }
            rightX -= 32;

            {
                Color liveC = srvOn2 ? ONLINE : DANGER;
                const float pw = 86f;
                var pillR = new Rect(rightX - pw, barR.y + 8, pw, 22);
                RRect(pillR, new Color(liveC.r, liveC.g, liveC.b, 0.13f), 11f);
                RRect(new Rect(pillR.x + 11, pillR.y + 7, 8, 8), liveC, 4f);
                var pillStyle = new GUIStyle(EditorStyles.label) { alignment = TextAnchor.MiddleLeft, fontSize = FONT_SIZE - 1 };
                pillStyle.normal.textColor = liveC;
                GUI.Label(new Rect(pillR.x + 26, pillR.y, pw - 26, pillR.height), srvOn2 ? "online" : "offline", pillStyle);
                rightX -= pw + 10;
            }

            if (!onClaudeIn)
            {
                int curRole = CurrentRole();
                var roleR = new Rect(rightX - 66, barR.y + 8, 66, 22);
                RBox(roleR, BG_RAISED, BORDER, 8f);
                Color sq = curRole == 0 ? ACCENT : ACCENT_2;
                RRect(new Rect(roleR.x + 10, roleR.y + 8, 7, 7), sq, 2f);
                var roleTxt = new GUIStyle(EditorStyles.label) { alignment = TextAnchor.MiddleLeft, fontSize = FONT_SIZE - 2 };
                roleTxt.normal.textColor = TEXT_WHITE;
                GUI.Label(new Rect(roleR.x + 22, roleR.y, 30, roleR.height), curRole == 0 ? "Dev" : "Art", roleTxt);
                var swap = new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleCenter, fontSize = FONT_SIZE - 3 };
                swap.normal.textColor = TEXT_HINT;
                GUI.Label(new Rect(roleR.xMax - 18, roleR.y, 14, roleR.height), "⇄", swap);
                if (GUI.Button(roleR, GUIContent.none, GUIStyle.none))
                {
                    int newRole = curRole == 0 ? 1 : 0;
                    EditorPrefs.SetInt("AIUnityMCPServer_Role", newRole);
                    foreach (var m in _api.messages) m.InvalidateCaches();
                    foreach (var m in _cli.messages) m.InvalidateCaches();
                    _lastRole = newRole;
                    Repaint();
                    GUIUtility.ExitGUI();
                }
                rightX -= 76;

                var testR = new Rect(rightX - 78, barR.y + 8, 78, 22);
                RBox(testR, BG_RAISED, BORDER, 8f);
                var testTxt = new GUIStyle(EditorStyles.label) { alignment = TextAnchor.MiddleCenter, fontSize = FONT_SIZE - 2 };
                testTxt.normal.textColor = ONLINE;
                GUI.Label(testR, "🧪 Test", testTxt);
                if (GUI.Button(testR, GUIContent.none, GUIStyle.none))
                {
                    S.draft = "test";
                    Enqueue();
                    GUIUtility.ExitGUI();
                }
            }

            if (picked != _activeTab)
            {
                _activeTab = picked;
                _showSettings = false;
                if (picked < 2) SwitchBackend(picked);
                GUIUtility.ExitGUI();
            }
        }

        public static int CurrentRole() => EditorPrefs.GetInt("AIUnityMCPServer_Role", 0);

        // ── Tab 3 (index 2): MCP Log + Server controls ────────────────────────
        void DrawMcpLog()
        {
            var log = MCPHandlers.Log;
            bool srvOn = MCPServer.IsRunning;

            // ── Server control bar ──
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            // status dot + label
            var dotStyle = new GUIStyle(EditorStyles.toolbarButton) { fontSize = FONT_SIZE - 1 };
            dotStyle.normal.textColor = srvOn ? ONLINE : DANGER;
            GUILayout.Label(srvOn ? $"● {MCPServer.Label}  port {MCPServer.Port}" : $"○ {MCPServer.Label}  stopped", dotStyle, GUILayout.Width(170));

            // Start / Stop
            var btnStyle = new GUIStyle(EditorStyles.toolbarButton) { fontSize = FONT_SIZE - 1 };
            btnStyle.normal.textColor = srvOn ? DANGER : ONLINE;
            if (GUILayout.Button(srvOn ? "⏹ Stop" : "▶ Start", btnStyle, GUILayout.Width(62)))
            {
                if (srvOn) MCPServer.Stop(); else MCPServer.Start();
            }

            GUILayout.Space(8);

            // Allow Writes toggle
            bool curAllow = MCPHandlers.AllowWrites;
            var allowStyle = new GUIStyle(EditorStyles.toolbarButton) { fontSize = FONT_SIZE - 1 };
            allowStyle.normal.textColor = curAllow ? ACCENT : TEXT_MUTE;
            bool newAllow = GUILayout.Toggle(curAllow, curAllow ? "✏ Write ON" : "✏ Write OFF", allowStyle, GUILayout.Width(88));
            if (newAllow != curAllow) MCPHandlers.AllowWrites = newAllow;

            GUILayout.FlexibleSpace();

            // quick stats
            int errCount = 0;
            lock (log) foreach (var e in log) if (e.IsError) errCount++;
            var statStyle = new GUIStyle(EditorStyles.miniLabel) { fontSize = FONT_SIZE - 2 };
            statStyle.normal.textColor = TEXT_HINT;
            GUILayout.Label($"{log.Count} cmds  {errCount} err", statStyle);

            if (GUILayout.Button("Clear", EditorStyles.toolbarButton, GUILayout.Width(44)))
                MCPHandlers.ClearLog();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(4);

            if (log.Count == 0)
            {
                EditorGUILayout.Space(20);
                var empty = new GUIStyle(EditorStyles.centeredGreyMiniLabel) { fontSize = FONT_SIZE };
                GUILayout.Label("No commands yet. Start a chat and ask Claude to work with Unity.", empty);
                return;
            }

            var timeStyle = new GUIStyle(EditorStyles.miniLabel)
                { font = LogFont, fontSize = FONT_SIZE - 2 };
            timeStyle.normal.textColor = TEXT_MUTE;

            var pathStyleOk = new GUIStyle(EditorStyles.label)
                { font = UiFont, fontSize = FONT_SIZE, richText = false };
            pathStyleOk.normal.textColor = TEXT_WHITE;
            var pathStyleErr = new GUIStyle(pathStyleOk);
            pathStyleErr.normal.textColor = DANGER;

            var arrowStyle = new GUIStyle(EditorStyles.miniLabel)
                { font = LogFont, fontSize = FONT_SIZE - 1 };

            var monoStyle = new GUIStyle(EditorStyles.label)
            {
                font = LogFont, fontSize = FONT_SIZE - 2,
                wordWrap = true, richText = false,
                padding = new RectOffset(0, 0, 2, 2),
            };

            var msStyle = new GUIStyle(EditorStyles.miniLabel)
                { font = LogFont, fontSize = FONT_SIZE - 2 };

            _logScroll = EditorGUILayout.BeginScrollView(_logScroll, false, false, GUIStyle.none, GUIStyle.none, GUIStyle.none);

            List<MCPHandlers.MCPLogEntry> snapshot;
            lock (log) { snapshot = new List<MCPHandlers.MCPLogEntry>(log); }

            for (int i = snapshot.Count - 1; i >= 0; i--)
            {
                var e = snapshot[i];
                Color bgRow   = e.IsError ? new Color(0.18f, 0.09f, 0.11f) : BG_SURFACE;
                Color accent  = e.IsError ? DANGER : ACCENT;
                Color respCol = e.IsError ? DANGER    : ONLINE;

                var hdrFull = GUILayoutUtility.GetRect(0, 27, GUILayout.ExpandWidth(true));
                var hdr = new Rect(hdrFull.x + 6, hdrFull.y, hdrFull.width - 12, 25);
                if (Event.current.type == EventType.Repaint)
                {
                    RRect(hdr, bgRow, 7f);
                    RRect4(new Rect(hdr.x, hdr.y, 3, hdr.height), accent, 7f, 0f, 0f, 7f);
                }

                arrowStyle.normal.textColor = TEXT_HINT;
                GUI.Label(new Rect(hdr.x + 9,  hdr.y + 4, 14, 16), e.Expanded ? "▼" : "▶", arrowStyle);

                GUI.Label(new Rect(hdr.x + 25, hdr.y + 4, 58, 16), e.Time, timeStyle);

                string friendlyLabel = FriendlyPath(e.Path);
                GUI.Label(new Rect(hdr.x + 88, hdr.y + 3, hdr.width - 150, 19),
                    friendlyLabel, e.IsError ? pathStyleErr : pathStyleOk);

                msStyle.normal.textColor = e.Ms > 200 ? WARN
                                         : e.Ms > 50  ? new Color(0.85f, 0.85f, 0.45f)
                                         : ONLINE;
                GUI.Label(new Rect(hdr.xMax - 56, hdr.y + 4, 48, 16), $"{e.Ms}ms", msStyle);

                if (GUI.Button(hdrFull, GUIContent.none, GUIStyle.none)) { e.Expanded = !e.Expanded; Repaint(); }

                // ── expanded: request body + response (pretty-printed) ──
                if (e.Expanded)
                {
                    EditorGUILayout.BeginVertical();
                    GUILayout.Space(2);

                    var rawStyle = new GUIStyle(EditorStyles.miniLabel) { fontSize = FONT_SIZE - 3 };
                    rawStyle.normal.textColor = TEXT_HINT;
                    EditorGUILayout.BeginHorizontal();
                    GUILayout.Space(31);
                    GUILayout.Label(e.Path, rawStyle);
                    EditorGUILayout.EndHorizontal();
                    GUILayout.Space(2);

                    if (!string.IsNullOrEmpty(e.Body) && e.Body != "{}")
                    {
                        EditorGUILayout.BeginHorizontal();
                        GUILayout.Space(31);
                        arrowStyle.normal.textColor = TEXT_MUTE;
                        GUILayout.Label("→", arrowStyle, GUILayout.Width(14));
                        monoStyle.normal.textColor  = TEXT_MUTE;
                        GUILayout.Label(JsonToReadable(e.Body), monoStyle);
                        EditorGUILayout.EndHorizontal();
                        GUILayout.Space(2);
                    }

                    EditorGUILayout.BeginHorizontal();
                    GUILayout.Space(31);
                    arrowStyle.normal.textColor = respCol;
                    GUILayout.Label("←", arrowStyle, GUILayout.Width(14));
                    monoStyle.normal.textColor  = respCol;
                    GUILayout.Label(JsonToReadable(e.Response), monoStyle);
                    EditorGUILayout.EndHorizontal();
                    GUILayout.Space(4);

                    EditorGUILayout.EndVertical();
                }

                EditorGUILayout.Space(3);
            }

            EditorGUILayout.EndScrollView();
        }

        static string FriendlyPath(string path) => path switch
        {
            "/ping"                    => "🔌  Ping Unity",
            "/compile"                 => "⚙️  Compile Scripts",
            "/compile-status"          => "⚙️  Check Compile Status",
            "/server/stop"             => "⏹  Stop AI Unity MCP Server",
            "/atlas/create"            => "🖼  Create Sprite Atlas",
            "/diagnose/exceptions-clear" => "🚨  Clear Exceptions",
            "/gameobject/create"       => "➕  Create GameObject",
            "/gameobject/delete"       => "🗑  Delete GameObject",
            "/object/add-component"    => "🧩  Add Component",
            "/object/set-property"     => "✏️  Set Property",
            "/object/set-transform"    => "📐  Set Transform",
            "/object/inspect"          => "🔍  Inspect Object",
            "/scene/hierarchy"         => "🌳  Read Scene Hierarchy",
            "/scene/list"              => "📋  List Scenes",
            "/scene/open"              => "📂  Open Scene",
            "/scene/save"              => "💾  Save Scene",
            "/scene/count"             => "🔢  Count Components",
            "/prefab/create"           => "📦  Create Prefab",
            "/prefab/place"            => "📌  Place Prefab",
            "/script/create"           => "📝  Create Script",
            "/script/read"             => "📖  Read Script",
            "/code/run"                => "⚡  Run C# (live)",
            "/ui/create"               => "🖼  Create UI",
            "/ui/optimize"             => "⚡  Optimize UI",
            "/material/create"         => "🎨  Create Material",
            "/terrain/create"          => "🏔  Create Terrain",
            "/terrain/set-heights"     => "🏔  Set Terrain Heights",
            "/asset/find"              => "🔎  Find Asset",
            "/console/read"            => "📟  Read Console",
            "/console/logfile"         => "📄  Read Log File",
            "/console/clear"           => "🧹  Clear Console",
            "/console/logs"            => "📟  Fetch Logs",
            "/perf/audit"              => "📊  Perf Audit",
            "/perf/worst"              => "📊  Worst Frames",
            "/diagnose/state"          => "🩺  Capture State",
            "/diagnose/deep"           => "🔬  Deep Diagnose",
            "/diagnose/memory"         => "💾  Memory Snapshot",
            "/diagnose/fusion"         => "🌐  Fusion Stats",
            "/diagnose/exceptions"     => "🚨  Read Exceptions",
            "/hot-reload"              => "🔥  Hot Reload",
            "/play/control"            => "▶️  Play Control",
            "/selection/get"           => "🖱  Get Selection",
            "/selection/set"           => "🖱  Set Selection",
            "/watch/add"               => "👁  Watch Add",
            "/watch/get"               => "👁  Watch Get",
            "/watch/clear"             => "👁  Watch Clear",
            "/audit/textures"          => "🖼  Audit Textures",
            "/audit/unused"            => "🗂  Audit Unused",
            "/audit/empty-folders"     => "📁  Audit Folders",
            "/code/refactor-audit"     => "♻️  Refactor Audit",
            _                          => path   // fallback = raw path
        };

        const int TREE_MAX_DEPTH = 3;
        const int TREE_MAX_ITEMS = 6;
        const int TREE_MAX_VAL   = 500;

        static string JsonToReadable(string json)
        {
            if (string.IsNullOrEmpty(json)) return json;
            string s = json.Trim();
            if (!s.StartsWith("{") && !s.StartsWith("["))
                return s.Length > 300 ? s.Substring(0, 300) + "…" : s;

            var sb = new System.Text.StringBuilder();
            RenderJsonNode(s, sb, "", 0);
            string outp = sb.ToString().TrimEnd('\n');
            if (outp.Length == 0) return PrettyJson(json);
            return outp.Length > 6000 ? outp.Substring(0, 6000) + "\n…(truncated)" : outp;
        }

        static void RenderJsonNode(string raw, System.Text.StringBuilder sb, string prefix, int depth)
        {
            raw = raw.Trim();
            if (raw.StartsWith("{"))
            {
                var pairs = ParseJsonPairs(raw);
                for (int i = 0; i < pairs.Count; i++)
                    RenderJsonPair(pairs[i].Key, pairs[i].Value, sb, prefix, i == pairs.Count - 1, depth);
            }
            else if (raw.StartsWith("["))
            {
                var items = SplitJsonArray(raw);
                int show = Mathf.Min(items.Count, TREE_MAX_ITEMS);
                for (int i = 0; i < show; i++)
                    RenderJsonPair($"[{i}]", items[i], sb, prefix, i == items.Count - 1, depth);
                if (items.Count > TREE_MAX_ITEMS)
                    sb.Append(prefix).Append("└ …").Append(items.Count - TREE_MAX_ITEMS).Append(" more items\n");
            }
        }

        static void RenderJsonPair(string key, string val, System.Text.StringBuilder sb, string prefix, bool last, int depth)
        {
            string branch = last ? "└ " : "├ ";
            string child  = prefix + (last ? "   " : "│  ");
            val = val.Trim();

            bool nested = (val.StartsWith("{") && val.Length > 2) || (val.StartsWith("[") && val.Length > 2);
            if (nested && depth < TREE_MAX_DEPTH)
            {
                string count = val.StartsWith("[") ? $"  ({SplitJsonArray(val).Count})" : "";
                sb.Append(prefix).Append(branch).Append(key).Append(count).Append('\n');
                RenderJsonNode(val, sb, child, depth + 1);
                return;
            }

            string v = val.Replace("\\n", "\n").Replace("\\t", "  ").Replace("\\r", "").Replace("\\\"", "\"");
            if (v.Length > TREE_MAX_VAL) v = v.Substring(0, TREE_MAX_VAL) + "…";
            if (v.Contains('\n'))
            {
                sb.Append(prefix).Append(branch).Append(key).Append(":\n");
                foreach (var line in v.Split('\n'))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    sb.Append(child).Append(line.TrimEnd()).Append('\n');
                }
            }
            else
                sb.Append(prefix).Append(branch).Append(key).Append(": ").Append(v).Append('\n');
        }

        static System.Collections.Generic.List<string> SplitJsonArray(string raw)
        {
            var items = new System.Collections.Generic.List<string>();
            int len = raw.Length, depth = 0, start = 1; bool inStr = false;
            for (int i = 1; i < len - 1; i++)
            {
                char c = raw[i];
                if (inStr) { if (c == '\\') i++; else if (c == '"') inStr = false; continue; }
                if (c == '"') inStr = true;
                else if (c == '{' || c == '[') depth++;
                else if (c == '}' || c == ']') depth--;
                else if (c == ',' && depth == 0)
                {
                    string it = raw.Substring(start, i - start).Trim();
                    if (it.Length > 0) items.Add(it);
                    start = i + 1;
                }
            }
            if (len >= 2)
            {
                string tail = raw.Substring(start, Mathf.Max(0, len - 1 - start)).Trim();
                if (tail.Length > 0) items.Add(tail);
            }
            return items;
        }

        static System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string,string>>
            ParseJsonPairs(string s)
        {
            var pairs = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string,string>>();
            int i = 1, len = s.Length;

            while (i < len - 1)
            {
                while (i < len && s[i] != '"' && s[i] != '}') i++;
                if (i >= len - 1 || s[i] == '}') break;

                string key = ReadJsonString(s, ref i);

                while (i < len && s[i] != ':') i++;
                i++;
                while (i < len && char.IsWhiteSpace(s[i])) i++;

                string val = ReadJsonValue(s, ref i);
                pairs.Add(new System.Collections.Generic.KeyValuePair<string,string>(key, val));

                while (i < len && s[i] != ',' && s[i] != '}') i++;
                if (i < len && s[i] == ',') i++;
            }
            return pairs;
        }

        static string ReadJsonString(string s, ref int i)
        {
            if (i >= s.Length || s[i] != '"') return "";
            i++;
            var sb = new System.Text.StringBuilder();
            while (i < s.Length && s[i] != '"')
            {
                if (s[i] == '\\' && i + 1 < s.Length) { sb.Append(s[i]); sb.Append(s[i+1]); i += 2; }
                else { sb.Append(s[i]); i++; }
            }
            if (i < s.Length) i++;
            return sb.ToString();
        }

        static string ReadJsonValue(string s, ref int i)
        {
            if (i >= s.Length) return "";
            char c = s[i];
            if (c == '"') return ReadJsonString(s, ref i);

            if (c == '[' || c == '{')
            {
                char open = c, close = c == '[' ? ']' : '}';
                int depth = 0, start = i; bool inStr = false;
                while (i < s.Length)
                {
                    char ch = s[i];
                    if (inStr) { if (ch == '\\') i++; else if (ch == '"') inStr = false; }
                    else if (ch == '"') inStr = true;
                    else if (ch == open) depth++;
                    else if (ch == close && --depth == 0) { i++; break; }
                    i++;
                }
                return s.Substring(start, i - start);
            }

            // number / bool / null
            int vs = i;
            while (i < s.Length && s[i] != ',' && s[i] != '}' && s[i] != ']') i++;
            return s.Substring(vs, i - vs).Trim();
        }

        static string PrettyJson(string json)
        {
            if (string.IsNullOrEmpty(json)) return json;
            const int MAX = 4000;
            var sb = new System.Text.StringBuilder();
            int indent = 0; bool inStr = false;
            for (int i = 0; i < Mathf.Min(json.Length, MAX); i++)
            {
                char c = json[i];
                if (c == '"' && (i == 0 || json[i - 1] != '\\')) inStr = !inStr;
                if (inStr) { sb.Append(c); continue; }
                switch (c)
                {
                    case '{': case '[':
                        sb.Append(c); indent++;
                        sb.Append('\n'); sb.Append(' ', indent * 2); break;
                    case '}': case ']':
                        indent = Mathf.Max(0, indent - 1);
                        sb.Append('\n'); sb.Append(' ', indent * 2);
                        sb.Append(c); break;
                    case ',':
                        sb.Append(c);
                        sb.Append('\n'); sb.Append(' ', indent * 2); break;
                    case ':': sb.Append(": "); break;
                    default:  sb.Append(c); break;
                }
            }
            if (json.Length > MAX) sb.Append("\n…(truncated)");
            return sb.ToString();
        }

        static string Badge(ChatSession s)
        {
            int pending = s.queue.Count + (s.isLoading ? 1 : 0);
            if (pending <= 0) return "";
            return s.isLoading ? $" ⏳{pending}" : $" •{pending}";
        }

        void DrawCompilingOverlay()
        {
            var area = GUILayoutUtility.GetRect(10, 10, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
            const float W = 460f, H = 170f;
            var card = new Rect(area.x + (area.width - W) * 0.5f, area.y + (area.height - H) * 0.5f, W, H);
            RBox(card, BG_RAISED, BORDER, 14f);

            string[] frames = { "◐", "◓", "◑", "◒" };
            int fi = (int)(EditorApplication.timeSinceStartup * 8) % frames.Length;
            var sp = new GUIStyle(EditorStyles.label) { alignment = TextAnchor.MiddleCenter, fontSize = FONT_SIZE + 18 };
            sp.normal.textColor = ACCENT;
            GUI.Label(new Rect(card.x, card.y + 18, card.width, 42), frames[fi], sp);

            CenterLabel(new Rect(card.x, card.y + 70, card.width, 30), "<b>Compiling scripts…</b>", TEXT_WHITE, FONT_SIZE + 7);
            CenterLabel(new Rect(card.x, card.y + 108, card.width, 24), "Chat is paused; your draft is preserved.", TEXT_MUTE, FONT_SIZE + 2);
        }

        static readonly string[] CLAUDE_MODEL_IDS =
        {
            "claude-fable-5", "claude-opus-4-8", "claude-opus-4-7",
            "claude-opus-4-6", "claude-sonnet-4-6", "claude-haiku-4-5",
        };
        static readonly string[] CLAUDE_MODEL_LABELS =
        {
            "Fable 5 — highest capability", "Opus 4.8 — latest", "Opus 4.7",
            "Opus 4.6", "Sonnet 4.6 — balanced", "Haiku 4.5 — fastest",
        };
        const int CLAUDE_MODEL_DEFAULT = 4;   // Sonnet 4.6

        // ── Settings UI helpers (warm theme) ─────────────────────────────────
        static void SettingsLabel(string text)
        {
            var st = new GUIStyle(EditorStyles.boldLabel) { fontSize = FONT_SIZE - 1 };
            st.normal.textColor = TEXT_MUTE;
            EditorGUILayout.LabelField(text, st);
        }

        static int SegRow(int cur, string[] labels)
        {
            var r = EditorGUILayout.GetControlRect(false, 30, GUILayout.ExpandWidth(true));
            RBox(r, BG_SURFACE, BORDER_SOFT, 9f);
            float w = (r.width - 8f) / labels.Length;
            int picked = cur;
            var st = new GUIStyle(EditorStyles.label) { fontSize = FONT_SIZE - 1, alignment = TextAnchor.MiddleCenter };
            for (int i = 0; i < labels.Length; i++)
            {
                var seg = new Rect(r.x + 4f + i * w, r.y + 3f, w, r.height - 6f);
                bool active = i == cur;
                bool hover  = seg.Contains(Event.current.mousePosition);
                if (Event.current.type == EventType.Repaint)
                {
                    if (active)     RRect(seg, ACCENT, 7f);
                    else if (hover) RRect(seg, new Color(1f, 1f, 1f, 0.04f), 7f);
                    st.fontStyle = active ? FontStyle.Bold : FontStyle.Normal;
                    st.normal.textColor = active ? Color.white : hover ? TEXT_WHITE : TEXT_MUTE;
                    GUI.Label(seg, labels[i], st);
                }
                if (GUI.Button(seg, GUIContent.none, GUIStyle.none)) picked = i;
            }
            return picked;
        }

        static string ThemedTextField(string value, bool password = false)
        {
            var box = EditorGUILayout.GetControlRect(false, 28);
            RBox(box, BG_SURFACE, BORDER, 8f);
            var st = new GUIStyle(EditorStyles.label) { fontSize = FONT_SIZE, alignment = TextAnchor.MiddleLeft };
            st.normal.textColor = TEXT_WHITE; st.focused.textColor = TEXT_WHITE; st.hover.textColor = TEXT_WHITE;
            var inner = new Rect(box.x + 10, box.y + 2, box.width - 20, box.height - 4);
            return password ? GUI.PasswordField(inner, value ?? "", '•', st)
                            : GUI.TextField(inner, value ?? "", st);
        }

        string _openDrop;
        void ThemedDropdown(string prefKey, string[] ids, string[] labels, int defIdx)
        {
            string cur = EditorPrefs.GetString(prefKey, ids[defIdx]);
            int idx = Array.IndexOf(ids, cur); if (idx < 0) idx = defIdx;
            bool open = _openDrop == prefKey;

            var r = EditorGUILayout.GetControlRect(false, 28);
            RBox(r, BG_SURFACE, open ? ACCENT : BORDER, 8f);
            var st = new GUIStyle(EditorStyles.label) { fontSize = FONT_SIZE, alignment = TextAnchor.MiddleLeft };
            st.normal.textColor = TEXT_WHITE;
            GUI.Label(new Rect(r.x + 10, r.y, r.width - 40, r.height), labels[idx], st);
            var caret = new GUIStyle(EditorStyles.label) { alignment = TextAnchor.MiddleCenter, fontSize = FONT_SIZE - 3 };
            caret.normal.textColor = open ? ACCENT : TEXT_MUTE;
            GUI.Label(new Rect(r.xMax - 24, r.y, 18, r.height), open ? "▴" : "▾", caret);
            if (GUI.Button(r, GUIContent.none, GUIStyle.none)) { _openDrop = open ? null : prefKey; Repaint(); }
            if (!open) return;

            const float rowH = 26f;
            int n = ids.Length;
            EditorGUILayout.Space(2);
            var panel = EditorGUILayout.GetControlRect(false, n * rowH + 8);
            RBox(panel, BG_RAISED, BORDER, 8f);
            var rowSt = new GUIStyle(EditorStyles.label) { fontSize = FONT_SIZE, alignment = TextAnchor.MiddleLeft };
            for (int i = 0; i < n; i++)
            {
                var row = new Rect(panel.x + 4, panel.y + 4 + i * rowH, panel.width - 8, rowH);
                bool sel = i == idx;
                bool hov = row.Contains(Event.current.mousePosition);
                if (Event.current.type == EventType.Repaint)
                {
                    if (sel)      RRect(row, new Color(ACCENT.r, ACCENT.g, ACCENT.b, 0.20f), 6f);
                    else if (hov) RRect(row, new Color(1f, 1f, 1f, 0.045f), 6f);
                    rowSt.normal.textColor = sel ? ACCENT : hov ? TEXT_WHITE : TEXT_MUTE;
                    GUI.Label(new Rect(row.x + 26, row.y, row.width - 30, row.height), labels[i], rowSt);
                    if (sel) CenterLabel(new Rect(row.x + 4, row.y, 20, row.height), "✓", ACCENT, FONT_SIZE - 1);
                }
                if (GUI.Button(row, GUIContent.none, GUIStyle.none))
                {
                    EditorPrefs.SetString(prefKey, ids[i]);
                    _openDrop = null;
                    Repaint();
                }
            }
        }

        void InfoCard(string text)
        {
            var st = new GUIStyle(EditorStyles.label) { fontSize = FONT_SIZE - 1, wordWrap = true, padding = new RectOffset(12, 12, 8, 8) };
            st.normal.textColor = TEXT_MUTE;
            float h = st.CalcHeight(new GUIContent(text), position.width - 32);
            var r = EditorGUILayout.GetControlRect(false, h);
            RBox(r, BG_SURFACE, BORDER_SOFT, 8f);
            GUI.Label(r, text, st);
        }

        void DrawSettings()
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(12);
            EditorGUILayout.BeginVertical();

            EditorGUILayout.Space(10);
            var title = new GUIStyle(EditorStyles.boldLabel) { fontSize = FONT_SIZE + 3 };
            title.normal.textColor = TEXT_WHITE;
            EditorGUILayout.LabelField("Settings", title);
            var sub = new GUIStyle(EditorStyles.label) { fontSize = FONT_SIZE - 1 };
            sub.normal.textColor = TEXT_HINT;
            EditorGUILayout.LabelField("Configure the backend, model and AI Unity MCP Server behavior", sub);
            EditorGUILayout.Space(12);

            SettingsLabel("Backend");
            int backend = CurrentBackend();
            int newBackend = SegRow(backend, new[] { "API Key", "Claude CLI (subscription)" });
            if (newBackend != backend) { SwitchBackend(newBackend); backend = newBackend; }

            EditorGUILayout.Space(10);

            if (backend == 0)
            {
                SettingsLabel("Anthropic API Key");
                string newKey = ThemedTextField(_apiKey, password: true);
                if (newKey != _apiKey) { _apiKey = newKey; EditorPrefs.SetString("AIUnityMCPServer_ApiKey", _apiKey); }

                EditorGUILayout.Space(10);
                SettingsLabel("Model — select a Claude model");
                ThemedDropdown("AIUnityMCPServer_ApiModel", CLAUDE_MODEL_IDS, CLAUDE_MODEL_LABELS, CLAUDE_MODEL_DEFAULT);

                EditorGUILayout.Space(10);
                InfoCard("Sonnet and Haiku are faster and cheaper for interactive work. Opus and Fable offer higher capability at greater cost and latency. The key is stored in EditorPrefs and is not committed to git.");
            }
            else
            {
                SettingsLabel("Claude CLI command");
                string cmd = EditorPrefs.GetString("AIUnityMCPServer_ClaudeCmd", "claude");
                string newCmd = ThemedTextField(cmd);
                if (newCmd != cmd) EditorPrefs.SetString("AIUnityMCPServer_ClaudeCmd", newCmd);

                EditorGUILayout.Space(10);
                SettingsLabel("Model — select a Claude model");
                ThemedDropdown("AIUnityMCPServer_CliModel", CLAUDE_MODEL_IDS, CLAUDE_MODEL_LABELS, CLAUDE_MODEL_DEFAULT);

                EditorGUILayout.Space(10);
                SettingsLabel("Effort (depth versus speed)");
                ThemedDropdown("AIUnityMCPServer_CliEffort",
                    new[] { "low", "medium", "high", "max" },
                    new[] { "Low — fastest", "Medium — balanced (default)", "High — deeper reasoning", "Max — deepest but slowest" }, 1);

                EditorGUILayout.Space(10);
                SettingsLabel("Experimental flags (enable only when stable)");
                var togSt = new GUIStyle(EditorStyles.label) { fontSize = FONT_SIZE - 1 };
                togSt.normal.textColor = TEXT_WHITE;
                bool useEffort = EditorPrefs.GetBool("AIUnityMCPServer_CliUseEffort", false);
                bool newUseEffort = EditorGUILayout.ToggleLeft(new GUIContent(" Send the selected --effort value (unsupported CLI versions may hang)"), useEffort, togSt);
                if (newUseEffort != useEffort) EditorPrefs.SetBool("AIUnityMCPServer_CliUseEffort", newUseEffort);
                bool fast = EditorPrefs.GetBool("AIUnityMCPServer_CliFast", false);
                bool newFast = EditorGUILayout.ToggleLeft(new GUIContent(" Fast mode: skip MCP loading with --strict-mcp-config; faster but may hang on Windows"), fast, togSt);
                if (newFast != fast) EditorPrefs.SetBool("AIUnityMCPServer_CliFast", newFast);

                EditorGUILayout.Space(10);
                InfoCard("Uses the Claude Code CLI subscription without consuming an API key.\nInstall Claude Code and sign in first.\nEach request cold-starts the CLI, so Haiku or Sonnet responds faster.");
            }

            EditorGUILayout.Space(14);
            var backR = EditorGUILayout.GetControlRect(false, 30);
            RBox(backR, BG_RAISED, BORDER, 9f);
            bool backHover = backR.Contains(Event.current.mousePosition);
            CenterLabel(backR, "←  Back", backHover ? TEXT_WHITE : TEXT_MUTE, FONT_SIZE);
            if (GUI.Button(backR, GUIContent.none, GUIStyle.none)) _showSettings = false;

            EditorGUILayout.EndVertical();
            GUILayout.Space(12);
            EditorGUILayout.EndHorizontal();
        }

        void DrawChatHistory()
        {
            var s = S;
            float reserved = 116 + _inputHeight + (s.images.Count > 0 ? 48 : 0);
            if (_showScriptList || _showPrefabList || _showSkillList) reserved += SCRIPT_LIST_HEIGHT;
            if (_showLive) reserved += 44;
            if (_showKeywords) reserved += 92;
            if (_showWatch) reserved += 64 + Mathf.Min(RuntimeWatch.Count, 8) * 19;
            float historyHeight = Mathf.Max(100, position.height - reserved);

            var ev = Event.current;
            const float histTop = 45f;
            bool overHistory = ev.mousePosition.y >= histTop && ev.mousePosition.y < histTop + historyHeight;
            if (ev.type == EventType.ScrollWheel && overHistory)
            {
                if (ev.delta.y < 0) _stickBottom = false;
                if (!_scrollAnim) _scrollTarget = s.chatScroll.y;
                _scrollTarget = Mathf.Max(0, _scrollTarget + ev.delta.y * 30f);
                _scrollAnim = true;
                ev.Use();
            }
            if (_scrollAnim)
            {
                float ny = Mathf.Lerp(s.chatScroll.y, _scrollTarget, 0.35f);
                if (Mathf.Abs(ny - _scrollTarget) < 0.5f) { ny = _scrollTarget; _scrollAnim = false; }
                s.chatScroll.y = Mathf.Max(0, ny);
                Repaint();
            }

            float wantY = s.chatScroll.y;
            s.chatScroll = EditorGUILayout.BeginScrollView(s.chatScroll,
                false, false, GUIStyle.none, GUIStyle.none, GUIStyle.none,
                GUILayout.Height(historyHeight), GUILayout.ExpandWidth(true));
            if (_scrollAnim && Mathf.Abs(s.chatScroll.y - wantY) > 0.5f)
            {
                if (wantY > s.chatScroll.y + 0.5f) _stickBottom = true;
                _scrollTarget = s.chatScroll.y;
                _scrollAnim = false;
            }

            float bubbleWidth = position.width - 36;

            if (_msgTextStyle == null)
                _msgTextStyle = new GUIStyle(EditorStyles.label)
                {
                    wordWrap = true, richText = true, fontSize = MSG_FONT,
                    padding = new RectOffset(12, 12, 9, 9)
                };
            _msgTextStyle.fontSize = MSG_FONT;
            _msgTextStyle.font = UiFont;
            _msgTextStyle.normal.textColor = TEXT_WHITE;

            _roleUser   = new GUIStyle(EditorStyles.miniBoldLabel) { fontSize = FONT_SIZE - 1, richText = true };
            _roleClaude = new GUIStyle(EditorStyles.miniBoldLabel) { fontSize = FONT_SIZE - 1, richText = true };
            _roleUser.normal.textColor   = TEXT_MUTE;
            _roleClaude.normal.textColor = ACCENT;
            var textStyle = _msgTextStyle;

            for (int mi = 0; mi < s.messages.Count; mi++)
            {
                var msg = s.messages[mi];

                if (msg.Role == "assistant" && mi > 0 && s.messages[mi - 1].Role == "user" && s.messages[mi - 1].collapsed)
                    continue;

                if (msg.Content == THINKING || msg.Content == QUEUED)
                {
                    bool thinking = msg.Content == THINKING;
                    var think = new GUIStyle(EditorStyles.label) { fontSize = FONT_SIZE };
                    think.normal.textColor = TEXT_MUTE;
                    string t;
                    if (thinking)
                    {
                        double sec = EditorApplication.timeSinceStartup - s.requestStart;
                        t = $"◌ Thinking...  ({FmtTime(sec)}";
                        if (s.backend == 1 && ClaudeCliClient.LiveOutputTokens > 0) t += $" · {ClaudeCliClient.LiveOutputTokens:N0} tokens";
                        t += ")";
                    }
                    else t = "⏳ Queued...";

                    var thRow = GUILayoutUtility.GetRect(bubbleWidth, 24);
                    var thAv = new Rect(thRow.x + 8, thRow.y + 1, 20, 20);
                    RRect(thAv, ACCENT, 10f);
                    var thAvSt = new GUIStyle(EditorStyles.label) { alignment = TextAnchor.MiddleCenter, fontSize = FONT_SIZE - 1 };
                    thAvSt.normal.textColor = Color.white;
                    GUI.Label(thAv, "✦", thAvSt);
                    var thName = new GUIStyle(_roleClaude) { alignment = TextAnchor.MiddleLeft };
                    GUI.Label(new Rect(thAv.xMax + 9, thRow.y, 200, thRow.height), "AI Unity MCP Server", thName);

                    var rrFull = GUILayoutUtility.GetRect(bubbleWidth, 28);
                    var rr = new Rect(rrFull.x + 8, rrFull.y, rrFull.width - 16, 26);
                    RRect4(rr, BG_SURFACE, 4f, 12f, 12f, 12f);
                    RRect4(new Rect(rr.x, rr.y, 3, rr.height), ACCENT, 3f, 0f, 0f, 3f);
                    GUI.Label(new Rect(rr.x + 12, rr.y, rr.width - 84, rr.height), t, think);
                    var xr = new Rect(rr.xMax - 62, rr.y + 3, 54, rr.height - 6);
                    RRect(xr, new Color(0.32f, 0.16f, 0.18f), 6f);
                    var xStyle = new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleCenter, fontSize = FONT_SIZE - 3 };
                    xStyle.normal.textColor = new Color(1f, 0.86f, 0.82f);
                    GUI.Label(xr, "✕ Cancel", xStyle);
                    if (GUI.Button(xr, GUIContent.none, GUIStyle.none))
                    {
                        if (thinking) { s.cts?.Cancel(); s.cliSessionId = null; s.cliTurnCount = 0; }
                        else CancelQueued(s, mi);
                    }
                    EditorGUILayout.Space(6);
                    if (thinking && Event.current.type == EventType.Repaint)
                    {
                        double now = EditorApplication.timeSinceStartup;
                        double interval = Application.isPlaying ? 0.5 : 0.25;
                        if (now - _lastThinkRepaint > interval) { _lastThinkRepaint = now; Repaint(); }
                    }
                    continue;
                }

                if (msg.Role == "user" && mi > 0)
                {
                    EditorGUILayout.Space(2);
                    var pairDiv = GUILayoutUtility.GetRect(bubbleWidth, 1);
                    EditorGUI.DrawRect(new Rect(pairDiv.x + 8, pairDiv.y, pairDiv.width - 16, 1), BORDER_SOFT);
                    EditorGUILayout.Space(4);
                }

                if (msg.Role == "user" && msg.collapsed)
                {
                    var crFull = GUILayoutUtility.GetRect(bubbleWidth, 34);
                    var cr = new Rect(crFull.x + 8, crFull.y, crFull.width - 16, 31);
                    RRect(cr, BG_RAISED, 8f);
                    RRect4(new Rect(cr.x, cr.y, 2, cr.height), ACCENT, 8f, 0f, 0f, 8f);
                    var toggleR = new Rect(cr.x + 9, cr.y, 16, cr.height);
                    var toggleStyle = new GUIStyle(EditorStyles.label) { fontSize = FONT_SIZE, alignment = TextAnchor.MiddleCenter };
                    toggleStyle.normal.textColor = ACCENT;
                    GUI.Label(toggleR, "▶", toggleStyle);
                    string preview = msg.Content;
                    int _nl = preview.IndexOf('\n');
                    if (_nl >= 0) preview = preview.Substring(0, _nl);
                    if (preview.Length > 80) preview = preview.Substring(0, 77) + "...";
                    var previewStyle = new GUIStyle(EditorStyles.label) { font = UiFont, fontSize = MSG_FONT, alignment = TextAnchor.MiddleLeft };
                    previewStyle.normal.textColor = TEXT_WHITE;
                    GUI.Label(new Rect(cr.x + 30, cr.y, cr.width - 38, cr.height), preview, previewStyle);
                    if (GUI.Button(cr, GUIContent.none, GUIStyle.none)) { msg.collapsed = false; Repaint(); }
                    EditorGUILayout.Space(4);
                    continue;
                }

                float fade = msg.FadeAlpha(EditorApplication.timeSinceStartup);
                var fadeGroup = EditorGUILayout.BeginVertical();

                var displayMsg = msg.RoleView(CurrentRole());
                bool isUser = displayMsg.Role == "user";
                Color accent = isUser ? TEXT_MUTE : ACCENT;
                Color bg     = BG_SURFACE;

                if (isUser)
                {
                    var hrFull = GUILayoutUtility.GetRect(bubbleWidth, 34);
                    var hr = new Rect(hrFull.x + 8, hrFull.y, hrFull.width - 16, 31);
                    RRect(hr, BG_RAISED, 8f);
                    RRect4(new Rect(hr.x, hr.y, 2, hr.height), ACCENT, 8f, 0f, 0f, 8f);
                    var toggleStyle2 = new GUIStyle(EditorStyles.label) { fontSize = FONT_SIZE, alignment = TextAnchor.MiddleCenter };
                    toggleStyle2.normal.textColor = ACCENT;
                    GUI.Label(new Rect(hr.x + 9, hr.y, 16, hr.height), "▼", toggleStyle2);
                    string hdrPreview = msg.Content;
                    int _hdrNl = hdrPreview.IndexOf('\n');
                    if (_hdrNl >= 0) hdrPreview = hdrPreview.Substring(0, _hdrNl);
                    float hdrMaxW = hr.width - 60f;
                    if (hdrPreview.Length > 55) hdrPreview = hdrPreview.Substring(0, 52) + "...";
                    var hdrPreviewStyle = new GUIStyle(EditorStyles.label) { font = UiFont, fontSize = MSG_FONT, alignment = TextAnchor.MiddleLeft };
                    hdrPreviewStyle.normal.textColor = TEXT_WHITE;
                    GUI.Label(new Rect(hr.x + 30, hr.y, hdrMaxW, hr.height), hdrPreview, hdrPreviewStyle);
                    if (GUI.Button(hr, GUIContent.none, GUIStyle.none)) { msg.collapsed = true; Repaint(); }

                    var tagRow = GUILayoutUtility.GetRect(bubbleWidth, 22);
                    var tagSt = new GUIStyle(EditorStyles.label) { alignment = TextAnchor.MiddleRight, font = UiFont, fontSize = MSG_FONT, fontStyle = FontStyle.Bold };
                    tagSt.normal.textColor = ACCENT;
                    GUI.Label(new Rect(tagRow.x, tagRow.y, tagRow.width - 12, 22), "You", tagSt);
                }
                else
                {
                    var hrow = GUILayoutUtility.GetRect(bubbleWidth, 24);
                    var avR = new Rect(hrow.x + 8, hrow.y + 1, 20, 20);
                    RRect(avR, ACCENT, 10f);
                    var avStyle = new GUIStyle(EditorStyles.label) { alignment = TextAnchor.MiddleCenter, fontSize = FONT_SIZE - 1 };
                    avStyle.normal.textColor = Color.white;
                    GUI.Label(avR, "✦", avStyle);
                    string roleTag = CurrentRole() == 1 ? "Art" : "Dev";
                    var nameStyle = new GUIStyle(_roleClaude) { alignment = TextAnchor.MiddleLeft };
                    GUI.Label(new Rect(avR.xMax + 9, hrow.y, bubbleWidth - 200, hrow.height), $"AI Unity MCP Server  ·  {roleTag}", nameStyle);
                    var copyR = new Rect(hrow.xMax - 70, hrow.y + 3, 58, 18);
                    RBox(copyR, BG_RAISED, BORDER, 6f);
                    CenterLabel(copyR, "Copy All", TEXT_MUTE, FONT_SIZE - 3);
                    if (GUI.Button(copyR, GUIContent.none, GUIStyle.none))
                    {
                        EditorGUIUtility.systemCopyBuffer = displayMsg.DisplayContent;
                        Debug.Log("[AI Unity MCP Server] Copied AI response to clipboard.");
                    }
                    if (!string.IsNullOrEmpty(displayMsg.Stat))
                    {
                        var statStyle = new GUIStyle(EditorStyles.miniLabel) { fontSize = FONT_SIZE - 4, alignment = TextAnchor.MiddleRight };
                        statStyle.normal.textColor = new Color(0.42f, 0.44f, 0.50f);
                        GUI.Label(new Rect(hrow.x, hrow.y, copyR.x - hrow.x - 8, hrow.height), displayMsg.Stat, statStyle);
                    }
                }
                EditorGUILayout.Space(3);

                if (displayMsg.HasRich)
                {
                    DrawSegments(displayMsg, accent);
                }
                else
                {
                    string rich = displayMsg.Rich();
                    float availEst = bubbleWidth - 16f;
                    float cw_est = isUser ? availEst * 0.80f : availEst;
                    float h = displayMsg.Height(textStyle, cw_est - 6);
                    Rect row = GUILayoutUtility.GetRect(bubbleWidth, h);
                    float avail = row.width - 16f;
                    float cw = isUser ? avail * 0.80f : avail;
                    float x  = isUser ? row.x + 8f + (avail - cw) : row.x + 8f;
                    Rect box = new Rect(x, row.y, cw, h);

                    if (isUser)
                    {
                        RRect4(box, bg, 12f, 12f, 4f, 12f);
                        RRect4(new Rect(box.xMax - 3, box.y, 3, box.height), accent, 0f, 3f, 3f, 0f);
                    }
                    else
                    {
                        RRect4(box, bg, 4f, 12f, 12f, 12f);
                        RRect4(new Rect(box.x, box.y, 3, box.height), accent, 3f, 0f, 0f, 3f);
                    }

                    EditorGUI.SelectableLabel(new Rect(box.x + 6, box.y, box.width - 10, box.height), rich, textStyle);
                }
                EditorGUILayout.Space(8);

                EditorGUILayout.EndVertical();
                if (fade < 1f)
                {
                    if (Event.current.type == EventType.Repaint)
                        EditorGUI.DrawRect(fadeGroup, new Color(BG_DARK.r, BG_DARK.g, BG_DARK.b, 1f - fade));
                    Repaint();
                }
            }

            var loading = new GUIStyle(EditorStyles.centeredGreyMiniLabel) { fontSize = FONT_SIZE - 2 };
            loading.normal.textColor = new Color(0.5f, 0.5f, 0.5f);
            EditorGUILayout.LabelField(s.queue.Count > 0 ? $"({s.queue.Count} in queue)" : "", loading);

            EditorGUILayout.EndScrollView();

            if (_autoScroll && Event.current.type == EventType.Repaint)
            {
                if (_stickBottom) { _scrollTarget = 100000f; _scrollAnim = true; }
                _autoScroll = false;
            }
        }

        GUIStyle _codeStyle, _codeHeaderStyle, _segTextStyle, _tableCellStyle, _tableHeadStyle;
        Font _monoFont;

        void DrawSegments(ChatMessage msg, Color accent)
        {
            if (_segTextStyle == null)
            {
                _segTextStyle = new GUIStyle(EditorStyles.label) { wordWrap = true, richText = true, fontSize = MSG_FONT, padding = new RectOffset(10, 10, 6, 6) };
                _segTextStyle.normal.textColor = TEXT_WHITE;
                _monoFont = Font.CreateDynamicFontFromOSFont(new[] { "Consolas", "Menlo", "Courier New", "monospace" }, MSG_FONT);
                _codeStyle = new GUIStyle(EditorStyles.label) { wordWrap = false, richText = true, fontSize = MSG_FONT - 1, font = _monoFont, padding = new RectOffset(10, 10, 8, 8) };
                _codeStyle.normal.textColor = new Color(0.82f, 0.84f, 0.88f);
                _codeHeaderStyle = new GUIStyle(EditorStyles.miniLabel) { fontSize = FONT_SIZE - 2, padding = new RectOffset(8, 8, 3, 3), richText = true };
            }
            _segTextStyle.fontSize = MSG_FONT;
            _segTextStyle.font = UiFont;
            _segTextStyle.normal.textColor = TEXT_WHITE;
            _codeStyle.fontSize = MSG_FONT - 1;
            _codeStyle.normal.textColor = new Color(0.82f, 0.84f, 0.88f);

            float w = position.width - 62;
            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(8);
            var barRect = GUILayoutUtility.GetRect(2, 2, GUILayout.Width(2), GUILayout.ExpandHeight(true));
            GUILayout.Space(8);
            EditorGUILayout.BeginVertical();

            foreach (var seg in msg.Segments())
            {
                if (seg.Table)
                {
                    DrawSegHeader(seg, w, "[=]", "table", null);
                    if (!seg.Collapsed) DrawTable(seg, w);
                }
                else if (!seg.Code)
                {
                    float h = _segTextStyle.CalcHeight(new GUIContent(seg.Rendered), w);
                    var r = GUILayoutUtility.GetRect(w, h);
                    RRect(r, BG_SURFACE, 8f);
                    EditorGUI.SelectableLabel(r, seg.Rendered, _segTextStyle);
                }
                else
                {
                    DrawSegHeader(seg, w, "</>", seg.Header, seg.Raw);
                    if (!seg.Collapsed)
                    {
                        float ch = _codeStyle.CalcHeight(new GUIContent(seg.Rendered), w);
                        var cr = GUILayoutUtility.GetRect(w, ch);
                        RRect4(cr, new Color(0.063f, 0.071f, 0.094f), 0f, 0f, 8f, 8f);
                        EditorGUI.SelectableLabel(cr, seg.Rendered, _codeStyle);
                    }
                }
                EditorGUILayout.Space(2);
            }
            EditorGUILayout.Space(6);
            EditorGUILayout.EndVertical();
            EditorGUILayout.EndHorizontal();
            if (Event.current.type == EventType.Repaint)
                EditorGUI.DrawRect(new Rect(barRect.x, barRect.y, 2, GUILayoutUtility.GetLastRect().yMax - barRect.y), accent);
        }

        void DrawSegHeader(Seg seg, float w, string icon, string title, string copyText)
        {
            var hbar = GUILayoutUtility.GetRect(w, 24);
            RRect4(hbar, BG_RAISED, 8f, 8f, 0f, 0f);
            string arrow = seg.Collapsed ? "▶" : "▼";
            GUI.Label(new Rect(hbar.x + 6, hbar.y, hbar.width - 76, hbar.height), $"<color=white>{arrow}  {icon} {title}</color>", _codeHeaderStyle);

            float clickW = hbar.width;
            if (!string.IsNullOrEmpty(copyText))
            {
                clickW = hbar.width - 72;
                if (GUI.Button(new Rect(hbar.xMax - 66, hbar.y + 1, 60, 18), "Copy", EditorStyles.miniButton))
                {
                    EditorGUIUtility.systemCopyBuffer = copyText;
                    Debug.Log($"[AI Unity MCP Server] Copied {title} to clipboard.");
                }
            }
            if (GUI.Button(new Rect(hbar.x, hbar.y, clickW, hbar.height), GUIContent.none, GUIStyle.none))
                seg.Collapsed = !seg.Collapsed;
        }

        void DrawTable(Seg seg, float w)
        {
            var cellStyle = new GUIStyle(EditorStyles.label) { font = UiFont, wordWrap = true, richText = true, fontSize = FONT_SIZE - 1, padding = new RectOffset(8, 8, 6, 6), alignment = TextAnchor.MiddleLeft };
            cellStyle.normal.textColor = Color.white;
            var headStyle = new GUIStyle(cellStyle) { fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            headStyle.normal.textColor = Color.white;
            _tableCellStyle = cellStyle; _tableHeadStyle = headStyle;

            int cols = seg.Cols;
            var rows = seg.Rows;

            const float OUTER = 0f;
            const float PAD   = 10f;
            float boxW = w - OUTER * 2;
            float gridW = boxW - PAD * 2;

            var weight = new float[cols];
            foreach (var row in rows)
                for (int c = 0; c < cols; c++)
                    if (c < row.Length) weight[c] = Mathf.Max(weight[c], Mathf.Max(3, row[c].Length));
            float sumW = 0f;
            for (int c = 0; c < cols; c++) { if (weight[c] < 3) weight[c] = 3; sumW += weight[c]; }
            var cw = new float[cols];
            for (int c = 0; c < cols; c++) cw[c] = gridW * weight[c] / sumW;

            var rowH = new float[rows.Count];
            float totalH = 0f;
            for (int r = 0; r < rows.Count; r++)
            {
                float hMax = r == 0 ? 26f : 24f;
                var st = r == 0 ? _tableHeadStyle : _tableCellStyle;
                for (int c = 0; c < cols; c++)
                {
                    string txt = c < rows[r].Length ? rows[r][c] : "";
                    hMax = Mathf.Max(hMax, st.CalcHeight(new GUIContent(txt), cw[c]));
                }
                rowH[r] = hMax;
                totalH += hMax;
            }

            var line   = new Color(1f, 1f, 1f, 0.08f);
            var cardBg = new Color(0.063f, 0.071f, 0.094f);
            var cellBg = BG_SURFACE;

            float boxH = totalH + PAD * 2;
            var slot = GUILayoutUtility.GetRect(w, boxH + OUTER * 2);
            var box  = new Rect(slot.x + OUTER, slot.y + OUTER, boxW, boxH);
            RRect4(box, cardBg, 0f, 0f, 8f, 8f);

            float gx = box.x + PAD, gy = box.y + PAD;
            var grid = new Rect(gx, gy, gridW, totalH);
            EditorGUI.DrawRect(grid, cellBg);

            float y = gy;
            for (int r = 0; r < rows.Count; r++)
            {
                float x = gx;
                var st = r == 0 ? _tableHeadStyle : _tableCellStyle;
                for (int c = 0; c < cols; c++)
                {
                    string raw = c < rows[r].Length ? rows[r][c] : "";
                    string txt = r == 0 ? $"<b><color=#FFFFFF>{raw}</color></b>" : $"<color=#FFFFFF>{raw}</color>";
                    GUI.Label(new Rect(x + 6, y, cw[c] - 12, rowH[r]), txt, st);
                    x += cw[c];
                }
                y += rowH[r];
            }

            void HLine(float yy) => EditorGUI.DrawRect(new Rect(grid.x, Mathf.Min(yy, grid.yMax - 1), gridW, 1), line);
            void VLine(float xx) => EditorGUI.DrawRect(new Rect(Mathf.Min(xx, grid.xMax - 1), grid.y, 1, totalH), line);
            float yy2 = grid.y; HLine(yy2);
            for (int r = 0; r < rows.Count; r++) { yy2 += rowH[r]; HLine(yy2); }
            float xx2 = grid.x; VLine(xx2);
            for (int c = 0; c < cols; c++) { xx2 += cw[c]; VLine(xx2); }
        }

        void DrawAttachToolbar()
        {
            var s = S;
            EditorGUILayout.BeginHorizontal();

            var small = new GUIStyle(GUI.skin.button) { fontSize = FONT_SIZE - 2 };

            if (GUILayout.Button("+ Image", small, GUILayout.Height(20), GUILayout.Width(72)))
                BrowseImages();

            const bool SHOW_PROFILER_UI = false;
            if (SHOW_PROFILER_UI)
            {
            var gcStyle = new GUIStyle(small);
            if (ProfilerReader.AllocCallstacks) gcStyle.normal.textColor = WARN;
            else if (!Application.isPlaying) gcStyle.normal.textColor = new Color(0.5f, 0.5f, 0.5f);
            string gcLabel = ProfilerReader.AllocCallstacks ? "📍 GC+" : "📍 GC";
            if (GUILayout.Button(new GUIContent(gcLabel, "Toggle GC allocation callstack capture.\nIn Play Mode, enable it and use the gc or perf keyword to see exact allocation lines.\nOnly new allocations are captured. Compilation disables capture automatically."), gcStyle, GUILayout.Height(20), GUILayout.Width(54)))
            {
                if (!Application.isPlaying)
                    ShowNotification(new GUIContent("Enter Play Mode first; GC capture works only while playing."));
                else
                    ProfilerReader.AllocCallstacks = !ProfilerReader.AllocCallstacks;
            }

            var deepStyle = new GUIStyle(small);
            string deepLabel;
            if (CpuDeepCapture.IsCapturing) { deepStyle.normal.textColor = DANGER; deepLabel = $"⏺ {CpuDeepCapture.SecondsLeft}s"; }
            else deepLabel = "🔬 Deep";
            if (GUILayout.Button(new GUIContent(deepLabel, "Capture five seconds of CPU methods and lines, GC allocation sites, and per-object network bandwidth, then send the evidence to the AI.\nReproduce the slowdown during the countdown. A draft question is included. Capture requires Play Mode and stops automatically."), deepStyle, GUILayout.Height(20), GUILayout.Width(64))
                && !CpuDeepCapture.IsCapturing)
            {
                if (!Application.isPlaying)
                    ShowNotification(new GUIContent("Enter Play Mode first; Deep CPU capture works only while playing."));
                else
                    CpuDeepCapture.Start(5f, report =>
                    {
                        S.attached["Deep Analysis"] = report;
                        if (string.IsNullOrEmpty(S.draft.Trim()))
                            S.draft = "Analyze the attached Deep profiler data: CPU methods, GC callstacks, and per-object network bandwidth. Identify expensive methods and lines, allocation sites, and bandwidth-heavy NetworkObjects. Rank risks and recommend fixes.";
                        Enqueue();
                    });
            }

            var liveStyle = new GUIStyle(small);
            if (_showLive) liveStyle.normal.textColor = ONLINE;
            if (GUILayout.Button(_showLive ? "🟢 Live" : "📈 Live", liveStyle, GUILayout.Height(20), GUILayout.Width(60)))
                _showLive = !_showLive;
            } // SHOW_PROFILER_UI

            // toggle keyword panel
            var kwStyle = new GUIStyle(small);
            if (_showKeywords) kwStyle.normal.textColor = ACCENT;
            if (GUILayout.Button("🔑 Keys", kwStyle, GUILayout.Height(20), GUILayout.Width(60)))
                _showKeywords = !_showKeywords;

            var wStyle = new GUIStyle(small);
            int wn = RuntimeWatch.Count;
            if (_showWatch) wStyle.normal.textColor = ACCENT;
            if (GUILayout.Button(new GUIContent(wn > 0 ? $"👁 Watch ({wn})" : "👁 Watch", "Inspect a live field or property in Play Mode. Select an object, enter a field, then press ＋."),
                wStyle, GUILayout.Height(20), GUILayout.Width(wn > 0 ? 86 : 70)))
                _showWatch = !_showWatch;

            var monStyle = new GUIStyle(small);
            if (RealtimeMonitor.IsOn) monStyle.normal.textColor = DANGER;
            string monLabel = RealtimeMonitor.IsOn ? "🔴 Monitor" : "🩺 Monitor";
            if (GUILayout.Button(new GUIContent(monLabel, "Monitor Unity health in real time, including memory and stalls → Library/AIUnityMCPServer/monitor.log"), monStyle, GUILayout.Height(20), GUILayout.Width(78)))
                RealtimeMonitor.Toggle();

            if (s.images.Count > 0)
            {
                var lbl = new GUIStyle(EditorStyles.miniLabel) { fontSize = FONT_SIZE - 2 };
                GUILayout.Label($"{s.images.Count} img", lbl, GUILayout.Width(45));
                if (GUILayout.Button("✕", small, GUILayout.Height(20), GUILayout.Width(24)))
                    s.images.Clear();
            }

            GUILayout.FlexibleSpace();
            var tip = new GUIStyle(EditorStyles.miniLabel) { fontSize = FONT_SIZE - 3, alignment = TextAnchor.MiddleRight };
            GUILayout.Label("@ = script  •  # = prefab  •  / = skill  •  Ctrl+V = paste image", tip);
            EditorGUILayout.EndHorizontal();

            if (s.images.Count > 0)
            {
                EditorGUILayout.BeginHorizontal();
                for (int i = 0; i < s.images.Count; i++)
                {
                    if (GUILayout.Button(s.images[i].Texture, GUILayout.Width(40), GUILayout.Height(40)))
                    {
                        s.images.RemoveAt(i);
                        break;
                    }
                }
                GUILayout.FlexibleSpace();
                EditorGUILayout.EndHorizontal();
            }
        }

        void DrawLivePanel()
        {
            if (!_showLive) return;

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            if (ProfilerReader.IsLive)
            {
                float fps = ProfilerReader.CurrentFps();
                Color c = fps >= 55 ? ONLINE
                        : fps >= 30 ? WARN
                        : DANGER;
                var style = new GUIStyle(EditorStyles.label) { fontSize = FONT_SIZE - 1, richText = true };
                style.normal.textColor = c;
                string bound = ProfilerReader.BoundStatus();
                GUILayout.Label($"● LIVE [{bound}]  " + ProfilerReader.LiveStats(), style);
                double now = EditorApplication.timeSinceStartup;
                double liveInterval = Application.isPlaying ? 0.5 : 0.2;
                if (now - _lastLiveRepaint > liveInterval) { _lastLiveRepaint = now; Repaint(); }
            }
            else
            {
                var style = new GUIStyle(EditorStyles.miniLabel) { fontSize = FONT_SIZE - 2 };
                GUILayout.Label("📈 Live — enter Play Mode for real-time Profiler values", style);
            }
            EditorGUILayout.EndVertical();
        }

        void DrawWatchPanel()
        {
            if (!_showWatch) return;
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            var sel = Selection.activeGameObject;
            EditorGUILayout.BeginHorizontal();
            var selSt = new GUIStyle(EditorStyles.miniLabel) { fontSize = FONT_SIZE - 2, alignment = TextAnchor.MiddleLeft };
            selSt.normal.textColor = sel != null ? TEXT_WHITE : TEXT_HINT;
            GUILayout.Label(new GUIContent(sel != null ? $"👁 {Trunc(sel.name, 16)}" : "👁 Select object",
                sel != null ? sel.name : "Select a GameObject in the Hierarchy first"), selSt, GUILayout.Width(118));

            GUI.SetNextControlName("watchField");
            var fldSt = new GUIStyle(EditorStyles.textField) { fontSize = FONT_SIZE - 1 };
            _watchField = EditorGUILayout.TextField(_watchField, fldSt);
            bool addClick = GUILayout.Button(new GUIContent("＋ Watch", "Watch a field on the selected object; the component is detected automatically."),
                EditorStyles.miniButton, GUILayout.Width(64));
            EditorGUILayout.EndHorizontal();

            if (string.IsNullOrEmpty(_watchField) && Event.current.type == EventType.Repaint)
            {
                var ph = new GUIStyle(EditorStyles.miniLabel) { fontSize = FONT_SIZE - 2 };
                ph.normal.textColor = TEXT_HINT;
                var r = GUILayoutUtility.GetLastRect();
                GUI.Label(new Rect(126, r.y + 1, 160, 16), "field, for example currentHp", ph);
            }

            bool enterAdd = Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Return
                            && GUI.GetNameOfFocusedControl() == "watchField";
            if ((addClick || enterAdd) && !string.IsNullOrWhiteSpace(_watchField))
            {
                string err = RuntimeWatch.AddWatch(sel != null ? sel.name : "", "", _watchField.Trim());
                if (err != null) ShowNotification(new GUIContent(err));
                else { _watchField = ""; GUI.FocusControl(null); }
                if (enterAdd) Event.current.Use();
                Repaint();
            }

            var snap = RuntimeWatch.Snapshot();
            var hintSt = new GUIStyle(EditorStyles.miniLabel) { fontSize = FONT_SIZE - 2, wordWrap = true };
            hintSt.normal.textColor = TEXT_HINT;
            if (snap.Count == 0)
            {
                GUILayout.Label("No watches yet. Enter a field and press ＋, or ask the AI to \"watch currentHp\".", hintSt);
            }
            else
            {
                if (!Application.isPlaying)
                    GUILayout.Label("Enter Play Mode to sample values every 0.5 seconds.", hintSt);

                foreach (var v in snap)
                {
                    EditorGUILayout.BeginHorizontal();
                    var keySt = new GUIStyle(EditorStyles.label) { fontSize = FONT_SIZE - 1 };
                    keySt.normal.textColor = TEXT_MUTE;
                    GUILayout.Label(new GUIContent($"{Trunc(v.objectName, 12)}·{v.field}", $"{v.objectName}.{v.component}.{v.field}"),
                        keySt, GUILayout.Width(140));

                    Color tc = v.status == "error" ? DANGER
                             : v.trend == "↑" ? ONLINE
                             : v.trend == "↓" ? WARN
                             : v.trend == "changed" ? ACCENT : TEXT_WHITE;
                    var valSt = new GUIStyle(EditorStyles.label) { fontSize = FONT_SIZE - 1 };
                    valSt.normal.textColor = tc;
                    string arrow = v.trend == "=" ? "" : v.trend + " ";
                    GUILayout.Label($"{arrow}{Trunc(v.value, 18)}", valSt);

                    GUILayout.FlexibleSpace();

                    if (!string.IsNullOrEmpty(v.alert))
                    {
                        var alSt = new GUIStyle(EditorStyles.miniLabel) { fontSize = FONT_SIZE - 2 };
                        alSt.normal.textColor = v.alertCount > 0 ? DANGER : TEXT_HINT;
                        GUILayout.Label(new GUIContent(v.alertCount > 0 ? $"🔔{v.alertCount}" : "🔔",
                            $"alert {v.alert} — triggered {v.alertCount} times"), alSt, GUILayout.Width(v.alertCount > 0 ? 30 : 18));
                    }

                    var spark = GUILayoutUtility.GetRect(50f, 16f);
                    DrawSparkline(spark, v.history, tc);

                    var xSt = new GUIStyle(EditorStyles.miniButton) { fontSize = FONT_SIZE - 2 };
                    if (GUILayout.Button(new GUIContent("✕", "Remove this watch"), xSt, GUILayout.Width(22), GUILayout.Height(17)))
                    { RuntimeWatch.RemoveWatch(v.key); Repaint(); }
                    EditorGUILayout.EndHorizontal();
                }

                EditorGUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                var clrSt = new GUIStyle(EditorStyles.miniButton) { fontSize = FONT_SIZE - 2 };
                clrSt.normal.textColor = TEXT_MUTE;
                if (GUILayout.Button("Clear all", clrSt, GUILayout.Width(64)))
                { RuntimeWatch.ClearAll(); Repaint(); }
                EditorGUILayout.EndHorizontal();
            }

            if (Application.isPlaying)
            {
                double now = EditorApplication.timeSinceStartup;
                if (now - _lastWatchRepaint > 0.5) { _lastWatchRepaint = now; Repaint(); }
            }
            EditorGUILayout.EndVertical();
        }

        // ══════════════════════════════════════════════════════════════════════
        // ══════════════════════════════════════════════════════════════════════
        enum KwG { Dev, Art, Both }
        sealed class Kw
        {
            public readonly string Word; public readonly KwG Group;
            public readonly string Path; public readonly string Desc; public readonly bool Chip;
            public Kw(string word, KwG group, string path = null, string desc = "", bool chip = true)
            { Word = word; Group = group; Path = path; Desc = desc; Chip = chip; }
        }

        static readonly Kw[] _keywords =
        {
            // 💻 DEV
            new Kw("gc",         KwG.Dev,  "/perf/audit",          "GC alloc/frame + top allocators"),
            new Kw("spike",      KwG.Dev,  "/perf/audit",          "FPS drops and the cause of each spike"),
            new Kw("net",        KwG.Dev,  "/perf/audit",          "network: ping/jitter/bandwidth"),
            new Kw("physics",    KwG.Dev,  "/perf/audit",          "rigidbody + non-convex collider"),
            new Kw("console",    KwG.Dev,  "/console/read",        "latest Console errors and warnings"),
            new Kw("log",        KwG.Dev,  "/console/logfile",     "Editor.log with full stack traces"),
            new Kw("state",      KwG.Dev,  "/diagnose/state",      "runtime snapshot (fps/freeze/network)"),
            new Kw("exceptions", KwG.Dev,  "/diagnose/exceptions", "runtime exceptions + stack trace"),
            new Kw("profiler",   KwG.Dev,  "/perf/audit",          "call tree and contributing methods"),
            new Kw("memory",     KwG.Dev,  "/diagnose/memory",     "memory snapshot (heap/native/GFX/GC gen)"),
            new Kw("fusion",     KwG.Dev,  "/diagnose/fusion",     "Fusion 2 tick, RTT, bandwidth and resimulation data (Play Mode required)"),
            new Kw("refactor",   KwG.Dev,  null,                   "scan scripts for refactoring opportunities (expensive AI-driven scan)"),
            new Kw("code",       KwG.Dev,  null,                   "analyze code, usually with an @script reference"),
            new Kw("script",     KwG.Dev,  null,                   "read source with: script <name>"),
            new Kw("watch",      KwG.Dev,  null,                   "inspect a live field: watch <field>. Select an object first, use the Watch panel to inspect or remove, wv to read, and watchclear to clear."),
            // 🎨 ART
            new Kw("draw",       KwG.Art,  "/perf/audit",          "draw calls + SetPass + batching"),
            new Kw("batches",    KwG.Art,  "/perf/audit",          "batch count"),
            new Kw("setpass",    KwG.Art,  "/perf/audit",          "SetPass calls"),
            new Kw("overdraw",   KwG.Art,  "/perf/audit",          "transparent overdraw"),
            new Kw("shader",     KwG.Art,  "/perf/audit",          "multi-pass / GrabPass shader"),
            new Kw("instancing", KwG.Art,  "/perf/audit",          "GPU instancing status"),
            new Kw("lod",        KwG.Art,  "/perf/audit",          "LOD group coverage"),
            new Kw("particle",   KwG.Art,  "/perf/audit",          "particle system count"),
            new Kw("shadow",     KwG.Art,  "/perf/audit",          "shadow caster count"),
            new Kw("light",      KwG.Art,  "/perf/audit",          "realtime light count"),
            new Kw("tex",        KwG.Art,  null,                   "audit textures (expensive AI-driven scan)"),
            new Kw("unused",     KwG.Art,  null,                   "find potentially unused assets (expensive AI-driven scan)"),
            // ⚡ BOTH
            new Kw("fps",        KwG.Both, "/perf/audit",          "FPS + frame stats + CPU/GPU-bound"),
            new Kw("perf",       KwG.Both, "/perf/audit",          "complete health check"),
            new Kw("audit",      KwG.Both, "/perf/audit",          "complete health check"),
            new Kw("hier",       KwG.Both, "/scene/hierarchy",     "scene tree structure"),
            new Kw("scene",      KwG.Both, null,                   "scene <name> to list or open a scene"),
            new Kw("find",       KwG.Both, null,                   "find an asset: find <name>"),
            new Kw("play",       KwG.Both, null,                   "enter Play Mode"),
            new Kw("stop",       KwG.Both, null,                   "exit Play Mode"),
            new Kw("pause",      KwG.Both, null,                   "pause Play Mode"),
            new Kw("clear",      KwG.Both, null,                   "clear the Console"),

            new Kw("stutter",  KwG.Dev,  "/perf/audit",          "", false),
            new Kw("worst",    KwG.Dev,  "/perf/worst",          "", false),
            new Kw("deep",     KwG.Dev,  null,                   "", false),
            new Kw("network",  KwG.Dev,  "/perf/audit",          "", false),
            new Kw("ping",     KwG.Dev,  "/perf/audit",          "", false),
            new Kw("rtt",      KwG.Dev,  "/perf/audit",          "", false),
            new Kw("bandwidth",KwG.Dev,  "/perf/audit",          "", false),
            new Kw("bw",       KwG.Dev,  "/perf/audit",          "", false),
            new Kw("mem",      KwG.Both, "/diagnose/memory",     "", false),
            new Kw("drawcalls",KwG.Art,  "/perf/audit",          "", false),
            new Kw("tris",     KwG.Art,  "/perf/audit",          "", false),
            new Kw("errors",   KwG.Dev,  "/console/read",        "", false),
            new Kw("debug",    KwG.Dev,  "/console/read",        "", false),
            new Kw("err",      KwG.Dev,  "/console/read",        "", false),
            new Kw("exc",      KwG.Dev,  "/diagnose/exceptions", "", false),
            new Kw("hierarchy",KwG.Both, "/scene/hierarchy",     "", false),
            new Kw("sel",      KwG.Both, "/selection/get",       "", false),
            new Kw("selection",KwG.Both, "/selection/get",       "", false),
            new Kw("watches",  KwG.Dev,  "/watch/get",           "", false),
            new Kw("wv",       KwG.Dev,  "/watch/get",           "", false),
            new Kw("watchget", KwG.Dev,  "/watch/get",           "", false),
            new Kw("watchclear",KwG.Dev, "/watch/clear",         "", false),
            new Kw("unwatch",  KwG.Dev,  "/watch/clear",         "", false),
        };

        static readonly Dictionary<string, string> _kwAutoGather = BuildAutoGatherMap();
        static Dictionary<string, string> BuildAutoGatherMap()
        {
            var d = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);
            foreach (var k in _keywords)
                if (!string.IsNullOrEmpty(k.Path)) d[k.Word] = k.Path;
            return d;
        }
        static readonly Dictionary<string, string> _pathLabel = new Dictionary<string, string>
        {
            {"/perf/audit","perf_audit"}, {"/console/read","console"}, {"/console/logfile","logfile"},
            {"/diagnose/exceptions","exceptions"}, {"/diagnose/state","state"}, {"/audit/textures","textures"},
            {"/audit/unused","unused"}, {"/code/refactor-audit","refactor"}, {"/scene/hierarchy","hierarchy"},
            {"/selection/get","selection"}, {"/watch/get","watches"},
            {"/diagnose/memory","memory_snapshot"}, {"/diagnose/fusion","fusion_stats"},
            {"/diagnose/deep","deep_analysis"}, {"/perf/worst","worst_spike"},
        };

        List<KeyValuePair<string, string>> AutoGather(string prompt)
        {
            var results = new List<KeyValuePair<string, string>>();
            if (string.IsNullOrEmpty(prompt)) return results;
            var paths = new List<string>();
            var tokens = System.Text.RegularExpressions.Regex.Split(prompt.ToLowerInvariant(), @"[^a-z0-9]+");
            foreach (var t in tokens)
                if (!string.IsNullOrEmpty(t) && _kwAutoGather.TryGetValue(t, out string p) && !paths.Contains(p))
                    paths.Add(p);
            foreach (var path in paths)
            {
                try
                {
                    string data = MCPHandlers.Dispatch(path, "{}");
                    string label = _pathLabel.TryGetValue(path, out var l) ? l : path;
                    results.Add(new KeyValuePair<string, string>(label, data));
                }
                catch (System.Exception e) { UnityEngine.Debug.LogWarning($"[AI Unity MCP Server] auto-gather {path}: {e.Message}"); }
            }
            return results;
        }

        void DrawKeywordPanel()
        {
            if (!_showKeywords) return;
            var s = S;
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            var hint = new GUIStyle(EditorStyles.miniLabel) { fontSize = FONT_SIZE - 3 };
            hint.normal.textColor = TEXT_MUTE;
            GUILayout.Label("Click a keyword to insert it into the prompt. Hover for a description.", hint);

            DrawKwRow(s, "Dev",  KwG.Dev,  ACCENT);
            DrawKwRow(s, "Art",  KwG.Art,  ACCENT_2);
            DrawKwRow(s, "⚡ Both", KwG.Both, WARN);

            EditorGUILayout.EndVertical();
        }

        void DrawKwRow(ChatSession s, string label, KwG group, Color col)
        {
            EditorGUILayout.BeginHorizontal();
            var lbl = new GUIStyle(EditorStyles.miniBoldLabel) { fontSize = FONT_SIZE - 2 };
            lbl.normal.textColor = col;
            GUILayout.Label(label, lbl, GUILayout.Width(52));

            var chip = new GUIStyle(EditorStyles.miniButton) { fontSize = FONT_SIZE - 2, padding = new RectOffset(8, 8, 2, 2) };
            chip.normal.textColor = col;
            foreach (var k in _keywords)
            {
                if (k.Group != group || !k.Chip) continue;
                if (GUILayout.Button(new GUIContent(k.Word, k.Desc), chip, GUILayout.Height(19)))
                {
                    s.draft += (s.draft.Length > 0 ? " " : "") + k.Word;
                    GUI.FocusControl("PromptField");
                }
            }
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
        }

        void DrawInputArea()
        {
            var s = S;
            DrawAttachToolbar();
            DrawLivePanel();
            DrawKeywordPanel();
            DrawWatchPanel();

            if (_showScriptList)      DrawScriptList();
            else if (_showPrefabList) DrawPrefabList();
            else if (_showSkillList)  DrawSkillList();

            var inputStyle = new GUIStyle(EditorStyles.textArea)
            {
                fontSize = FONT_SIZE, wordWrap = true,
                padding  = new RectOffset(10, 10, 8, 8),
            };
            inputStyle.normal.textColor   = TEXT_WHITE;
            inputStyle.focused.textColor  = TEXT_WHITE;
            inputStyle.hover.textColor    = TEXT_WHITE;
            inputStyle.normal.background  = null;
            inputStyle.focused.background = null;
            inputStyle.active.background  = null;
            inputStyle.hover.background   = null;

            TryPasteImage();

            float vsbWEst  = GUI.skin.verticalScrollbar.fixedWidth + 2;   // estimate scrollbar width
            float textWEst = (position.width - 36) - vsbWEst;             // approx available text width
            float contentH = inputStyle.CalcHeight(new GUIContent(s.draft + " "), textWEst) + 6;
            _inputHeight = Mathf.Clamp(contentH, INPUT_MIN, INPUT_MAX);

            var boxR  = EditorGUILayout.GetControlRect(false, _inputHeight + 2);
            var innerR = new Rect(boxR.x + 1, boxR.y + 1, boxR.width - 2, boxR.height - 2);
            if (Event.current.type == EventType.Repaint)
            {
                bool inputFocused = GUI.GetNameOfFocusedControl() == "PromptField";
                Color borderCol = inputFocused ? ACCENT : BORDER;
                RRect(boxR, borderCol, 11f);
                RRect(innerR, inputFocused ? BG_RAISED : BG_SURFACE, 10f);
            }

            float vsbW  = GUI.skin.verticalScrollbar.fixedWidth + 2;
            float textW = innerR.width - vsbW;
            float textH = Mathf.Max(innerR.height, contentH);

            _inputScroll = GUI.BeginScrollView(innerR, _inputScroll,
                new Rect(0, 0, textW, textH),
                false, false,
                GUIStyle.none,
                GUI.skin.verticalScrollbar);
            GUI.SetNextControlName("PromptField");
            EditorGUI.BeginChangeCheck();
            s.draft = GUI.TextArea(new Rect(0, 0, textW, textH), s.draft, inputStyle);
            if (EditorGUI.EndChangeCheck())
                UpdateScriptMention();
            GUI.EndScrollView();

            if (string.IsNullOrEmpty(s.draft) && Event.current.type == EventType.Repaint
                && GUI.GetNameOfFocusedControl() != "PromptField")
            {
                var phStyle = new GUIStyle(inputStyle) { padding = new RectOffset(11, 10, 9, 8) };
                phStyle.normal.textColor = TEXT_HINT;
                phStyle.normal.background = null;
                GUI.Label(innerR, "Ask Claude or control Unity…   Enter to send · Shift+Enter for a new line", phStyle);
            }

            HandleDragDrop(GUILayoutUtility.GetLastRect());

            if ((_showScriptList || _showPrefabList || _showSkillList) &&
                Event.current.type == EventType.Repaint &&
                GUI.GetNameOfFocusedControl() != "PromptField")
            {
                _refocusInput = true;
                Repaint();
            }

            var btnRow = GUILayoutUtility.GetRect(0, 30, GUILayout.ExpandWidth(true));
            bool busy = s.Busy;
            bool canSend = !EditorApplication.isCompiling && !string.IsNullOrEmpty(s.draft.Trim());

            float rx = btnRow.xMax;

            var clearR = new Rect(rx - 64, btnRow.y, 64, btnRow.height);
            RBox(clearR, BG_RAISED, BORDER, 8f);
            CenterLabel(clearR, "Clear", TEXT_MUTE, FONT_SIZE - 1);
            if (GUI.Button(clearR, GUIContent.none, GUIStyle.none))
            {
                s.queue.Clear();
                s.cts?.Cancel();
                s.messages.Clear();
                s.images.Clear();
                s.draft = "";
                s.cliSessionId = null;
                s.cliTurnCount = 0;
                try { System.IO.File.Delete(HistoryPath(s.backend)); } catch { }
            }
            rx -= 72;

            if (busy)
            {
                var stopR = new Rect(rx - 76, btnRow.y, 76, btnRow.height);
                RRect(stopR, new Color(0.32f, 0.16f, 0.18f), 8f);
                CenterLabel(stopR, "⛔ Stop", new Color(1f, 0.80f, 0.80f), FONT_SIZE - 1);
                if (GUI.Button(stopR, GUIContent.none, GUIStyle.none))
                    StopSession(s);
                rx -= 84;
            }

            var sendR = new Rect(btnRow.x, btnRow.y, Mathf.Max(80f, rx - btnRow.x), btnRow.height);
            string sendLabel = busy ? $"＋ Queue ({s.queue.Count + (s.isLoading ? 1 : 0)})" : "Send  ↑";
            RRect(sendR, canSend ? ACCENT : new Color(ACCENT.r, ACCENT.g, ACCENT.b, 0.32f), 8f);
            CenterLabel(sendR, sendLabel, canSend ? Color.white : new Color(1f, 1f, 1f, 0.55f), FONT_SIZE - 1);
            if (canSend && GUI.Button(sendR, GUIContent.none, GUIStyle.none))
                Enqueue();

            if (Event.current.type == EventType.KeyDown &&
                Event.current.keyCode == KeyCode.Return &&
                !Event.current.shift &&
                GUI.GetNameOfFocusedControl() == "PromptField")
            {
                Enqueue();
                Event.current.Use();
            }
        }

        void TryPasteImage()
        {
            var e = Event.current;
            bool paste = e.type == EventType.KeyDown && e.keyCode == KeyCode.V && (e.control || e.command);
            if (!paste) return;

            if (!string.IsNullOrEmpty(EditorGUIUtility.systemCopyBuffer)) return;

            string path = ClipboardImage.TryGetImagePath();
            if (!string.IsNullOrEmpty(path))
            {
                AddImage(path);
                e.Use();
                Repaint();
            }
        }

        // ── @mention script picker ─────────────────────────────────────────
        string CurrentMentionQuery()
        {
            string draft = S.draft;
            int at = draft.LastIndexOf('@');
            if (at < 0) return null;
            string tail = draft.Substring(at + 1);
            if (tail.Contains(' ') || tail.Contains('\n')) return null;
            return tail;
        }

        void UpdateScriptMention()
        {
            bool wasOpen = _showScriptList || _showPrefabList || _showSkillList;
            string draft = S.draft;
            string sq = CurrentMentionQuery();        // '@'
            string pq = CurrentTokenQuery('#');       // '#'
            int atIdx = draft.LastIndexOf('@');
            int hashIdx = draft.LastIndexOf('#');
            if (sq != null && (pq == null || atIdx > hashIdx))
            {
                _scriptQuery = sq; _showScriptList = true; _showPrefabList = false;
            }
            else if (pq != null)
            {
                _prefabQuery = pq; _showPrefabList = true; _showScriptList = false;
            }
            else { _showScriptList = false; _showPrefabList = false; }

            if (CurrentBackend() == 1)
            {
                string sk = CurrentTokenQuery('/');
                if (sk != null) { _skillQuery = sk; _showSkillList = true; }
                else _showSkillList = false;
            }
            else _showSkillList = false;

            bool nowOpen = _showScriptList || _showPrefabList || _showSkillList;
            if (wasOpen != nowOpen) { _refocusInput = true; Repaint(); }
        }

        string CurrentTokenQuery(char lead)
        {
            string draft = S.draft;
            int idx = draft.LastIndexOf(lead);
            if (idx < 0) return null;
            if (idx > 0 && draft[idx - 1] != ' ' && draft[idx - 1] != '\n') return null;
            string tail = draft.Substring(idx + 1);
            if (tail.Contains(' ') || tail.Contains('\n')) return null;
            return tail;
        }

        void DrawSkillList()
        {
            var results = SkillIndex.Search(_skillQuery, 12);
            var items = new List<PickerItem>(results.Count);
            foreach (var sk in results)
            {
                var name = sk.Name;
                items.Add(new PickerItem { Name = "/" + sk.Name, Desc = sk.Description, Pick = () => InsertSkillMention(name) });
            }
            DrawPickerPanel("/ skill — run a skill (Subscription mode)", items, ref _skillScroll, "No matching skill found");
        }

        void InsertSkillMention(string skillName)
        {
            var s = S;
            int at = s.draft.LastIndexOf('/');
            if (at < 0) return;
            s.draft = s.draft.Substring(0, at) + "/" + skillName + " ";
            _showSkillList = false;
            _refocusInput = true;
            Repaint();
        }

        struct PickerItem { public string Name, Desc; public Action Pick; }

        void DrawPickerPanel(string title, List<PickerItem> items, ref Vector2 scroll, string emptyText)
        {
            const float rowH = 26f;
            var panelR = EditorGUILayout.GetControlRect(false, SCRIPT_LIST_HEIGHT);
            var panel = new Rect(panelR.x + 4, panelR.y, panelR.width - 8, panelR.height - 4);
            RBox(panel, BG_RAISED, BORDER, 10f);

            var tSt = new GUIStyle(EditorStyles.miniLabel) { fontSize = FONT_SIZE - 2 };
            tSt.normal.textColor = TEXT_HINT;
            GUI.Label(new Rect(panel.x + 12, panel.y + 4, panel.width - 24, 15), title, tSt);

            var inner = new Rect(panel.x + 4, panel.y + 22, panel.width - 8, panel.height - 27);
            if (items == null || items.Count == 0)
            {
                var eSt = new GUIStyle(EditorStyles.label) { alignment = TextAnchor.MiddleCenter, fontSize = FONT_SIZE - 1 };
                eSt.normal.textColor = TEXT_HINT;
                GUI.Label(inner, emptyText, eSt);
                return;
            }

            var rowSt = new GUIStyle(EditorStyles.label) { font = UiFont, fontSize = MSG_FONT - 1, alignment = TextAnchor.MiddleLeft, richText = true };
            scroll = GUI.BeginScrollView(inner, scroll, new Rect(0, 0, inner.width - 4, items.Count * rowH),
                false, false, GUIStyle.none, GUIStyle.none);
            for (int i = 0; i < items.Count; i++)
            {
                var row = new Rect(2, i * rowH, inner.width - 8, rowH);
                bool hov = row.Contains(Event.current.mousePosition);
                if (Event.current.type == EventType.Repaint)
                {
                    if (hov)
                    {
                        RRect(row, new Color(ACCENT.r, ACCENT.g, ACCENT.b, 0.18f), 6f);
                        RRect4(new Rect(row.x, row.y, 2, row.height), ACCENT, 6f, 0f, 0f, 6f);
                    }
                    else if ((i & 1) == 1)
                        RRect(row, new Color(1f, 1f, 1f, 0.03f), 6f);   // zebra
                }
                EditorGUIUtility.AddCursorRect(row, MouseCursor.Link);
                string desc = string.IsNullOrEmpty(items[i].Desc) ? "" :
                    "  <color=#9C948A>" + items[i].Desc.Replace("<", "«").Replace(">", "»") + "</color>";
                GUI.Label(new Rect(row.x + 10, row.y, row.width - 16, rowH),
                    $"<color={(hov ? "#FFFFFF" : "#E8A87F")}>{items[i].Name}</color>{desc}", rowSt);
                if (Event.current.type == EventType.MouseDown && row.Contains(Event.current.mousePosition))
                {
                    Event.current.Use();
                    items[i].Pick();
                    GUIUtility.ExitGUI();
                }
            }
            GUI.EndScrollView();
        }

        void DrawScriptList()
        {
            var results = CodebaseIndex.Search(_scriptQuery, 12);
            var items = new List<PickerItem>(results.Count);
            foreach (var sc in results)
            {
                var name = sc.Name;
                items.Add(new PickerItem { Name = "@" + sc.Name, Desc = sc.Path, Pick = () => InsertScriptMention(name) });
            }
            DrawPickerPanel("@ script — attach a file for the AI to read", items, ref _scriptScroll, "No matching script found");
        }

        void InsertScriptMention(string scriptName)
        {
            var s = S;
            int at = s.draft.LastIndexOf('@');
            if (at < 0) return;
            s.draft = s.draft.Substring(0, at) + "@" + scriptName + " ";
            _showScriptList = false;
            _refocusInput = true;
            Repaint();
        }

        void DrawPrefabList()
        {
            if (PrefabIndex.Building && !PrefabIndex.Ready)
            {
                DrawPickerPanel("# prefab", null, ref _prefabScroll, "Building the prefab index…");
                return;
            }
            var results = PrefabIndex.Search(_prefabQuery, 12);
            var items = new List<PickerItem>(results.Count);
            foreach (var pf in results)
            {
                var name = pf.Name;
                items.Add(new PickerItem { Name = "# " + pf.Name, Desc = pf.Path, Pick = () => InsertPrefabMention(name) });
            }
            DrawPickerPanel("# prefab — attach prefab contents", items, ref _prefabScroll, "No matching prefab found");
        }

        void InsertPrefabMention(string prefabName)
        {
            var s = S;
            int at = s.draft.LastIndexOf('#');
            if (at < 0) return;
            string token = System.Text.RegularExpressions.Regex.IsMatch(prefabName, @"^[A-Za-z0-9_]+$")
                ? prefabName : $"[{prefabName}]";
            s.draft = s.draft.Substring(0, at) + "#" + token + " ";
            _showPrefabList = false;
            _refocusInput = true;
            Repaint();
        }

        string BuildPromptWithScripts(string prompt, out List<string> primaryNames, out List<string> depNames)
        {
            primaryNames = new List<string>();
            depNames = new List<string>();
            var matches = System.Text.RegularExpressions.Regex.Matches(prompt, @"@([\w/]+\.cs)");
            if (matches.Count == 0) return prompt;

            var seen = new HashSet<string>();
            var attachedPaths = new List<string>();
            var sb = new System.Text.StringBuilder(prompt);
            sb.Append("\n\n--- Referenced scripts (full source) ---\n");

            foreach (System.Text.RegularExpressions.Match m in matches)
            {
                string name = m.Groups[1].Value;
                if (!seen.Add(name)) continue;
                string path = CodebaseIndex.ResolvePath(name);
                if (path == null) continue;
                string content = CodebaseIndex.ReadContent(path);
                if (content == null) continue;
                sb.Append($"\n// FILE: {path}\n```csharp\n{content}\n```\n");
                attachedPaths.Add(path);
                primaryNames.Add(System.IO.Path.GetFileName(path));
            }

            if (primaryNames.Count > 0 && IsAnalysisIntent(prompt))
            {
                const int MAX_DEPS = 12;
                var depSeen = new HashSet<string>(attachedPaths, System.StringComparer.OrdinalIgnoreCase);
                var deps = new List<CodebaseIndex.ScriptEntry>();

                foreach (var p in attachedPaths)
                {
                    string src = CodebaseIndex.ReadContent(p);
                    foreach (var dep in CodebaseIndex.ResolveReferencedScripts(src, p, 12))
                    {
                        if (!depSeen.Add(dep.Path)) continue;
                        deps.Add(dep);
                        if (deps.Count >= MAX_DEPS) break;
                    }
                    if (deps.Count >= MAX_DEPS) break;
                }

                if (deps.Count < MAX_DEPS)
                {
                    var toScan = new Queue<string>();
                    foreach (var p in attachedPaths) toScan.Enqueue(p);
                    foreach (var d in new List<CodebaseIndex.ScriptEntry>(deps)) toScan.Enqueue(d.Path);
                    int guard = 0;
                    while (toScan.Count > 0 && deps.Count < MAX_DEPS && guard++ < 40)
                    {
                        string src = CodebaseIndex.ReadContent(toScan.Dequeue());
                        foreach (var baseName in CodebaseIndex.ResolveBaseTypes(src))
                        {
                            string bp = CodebaseIndex.ResolvePath(baseName);
                            if (bp == null || !depSeen.Add(bp)) continue;
                            deps.Add(new CodebaseIndex.ScriptEntry { Name = baseName + ".cs", Path = bp });
                            toScan.Enqueue(bp);
                            if (deps.Count >= MAX_DEPS) break;
                        }
                    }
                }

                if (deps.Count > 0)
                {
                    sb.Append("\n--- Referenced dependencies and inheritance chain, including base classes or interfaces that may define members ---\n");
                    foreach (var dep in deps)
                    {
                        string content = CodebaseIndex.ReadContent(dep.Path, 14000);
                        if (content == null) continue;
                        sb.Append($"\n// DEP: {dep.Path}\n```csharp\n{content}\n```\n");
                        depNames.Add(System.IO.Path.GetFileName(dep.Path));
                    }
                }
            }

            return (primaryNames.Count + depNames.Count) > 0 ? sb.ToString() : prompt;
        }

        static bool IsAnalysisIntent(string prompt)
        {
            if (string.IsNullOrEmpty(prompt)) return false;
            string p = prompt.ToLowerInvariant();
            string[] kw = { "refactor", "optimize", "optimise", "review", "improve", "analyze", "analyse",
                            "fix", "inspect", "problem", "bug", "why", "missing", "stuck", "incorrect", "unexpected",
                            "crash", "error", "exception", "broken", "not working", "not found", "trace" };
            foreach (var k in kw) if (p.Contains(k)) return true;
            return false;
        }

        static bool IsRuntimeWatchIntent(string prompt)
        {
            if (string.IsNullOrEmpty(prompt)) return false;
            string p = prompt.ToLowerInvariant();
            string[] kw = { "watch", "runtime", "value", "state", "track", "debug", "why", "not decreasing",
                            "not increasing", "not changing", "stuck", "running", "play mode", "live", "bug",
                            "hp", "mp", "mana", "health", "inspect value" };
            foreach (var k in kw) if (p.Contains(k)) return true;
            return false;
        }

        void AttachPart(string label, string data)
        {
            var s = S;
            if (s.attached.ContainsKey(label)) { s.attached.Remove(label); Repaint(); return; }
            s.attached[label] = data;
            if (string.IsNullOrEmpty(s.draft.Trim()))
                s.draft = "Analyze the attached Profiler data. Identify issues, rank by risk, and suggest fixes.";
            Repaint();
        }

        void BrowseImages()
        {
            string path = EditorUtility.OpenFilePanel("Select image (add more)", "", "png,jpg,jpeg,webp");
            if (!string.IsNullOrEmpty(path)) AddImage(path);
        }

        void HandleDragDrop(Rect dropArea)
        {
            var e = Event.current;
            if (!dropArea.Contains(e.mousePosition)) return;

            if (e.type == EventType.DragUpdated || e.type == EventType.DragPerform)
            {
                DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
                if (e.type == EventType.DragPerform)
                {
                    DragAndDrop.AcceptDrag();
                    foreach (var path in DragAndDrop.paths)
                    {
                        string ext = Path.GetExtension(path).ToLower();
                        if (ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".webp")
                            AddImage(path);
                    }
                }
                e.Use();
            }
        }

        void AddImage(string path)
        {
            var s = S;
            if (s.images.Count >= MAX_IMAGES)
            {
                EditorUtility.DisplayDialog("Images", $"Maximum {MAX_IMAGES} images allowed.", "OK");
                return;
            }
            if (s.images.Exists(im => im.Path == path)) return;

            byte[] data = File.ReadAllBytes(path);
            var tex = new Texture2D(2, 2);
            if (tex.LoadImage(data))
            {
                string ext = Path.GetExtension(path).ToLower();
                string mime = ext == ".jpg" || ext == ".jpeg" ? "image/jpeg"
                            : ext == ".webp" ? "image/webp" : "image/png";
                s.images.Add(new AttachedImage { Path = path, Texture = tex, Mime = mime });
                Repaint();
            }
        }

        static string BuildHealthCheckReply()
        {
            var sb = new System.Text.StringBuilder();
            if (MCPServer.IsRunning)
            {
                sb.AppendLine($"🟢 **AI Unity MCP Server is running** — {MCPServer.Label} · port {MCPServer.Port} · Write {(MCPHandlers.AllowWrites ? "ON ✏" : "OFF (read-only)")}");
                sb.AppendLine();
                var paths = MCPHandlers.CommandPaths();
                sb.AppendLine($"## 📋 Available commands ({paths.Count})");
                sb.AppendLine("| # | Command | Path |");
                sb.AppendLine("|---|--------|------|");
                int i = 1;
                foreach (var p in paths)
                    sb.AppendLine($"| {i++} | {FriendlyPath(p)} | {p} |");
            }
            else
            {
                sb.AppendLine("🔴 **AI Unity MCP Server is not running**");
                sb.AppendLine("Open **Claude In**, press **▶ Start**, then enter \"test\" again to verify the connection.");
            }
            return sb.ToString().TrimEnd();
        }

        void Enqueue()
        {
            var s = S;
            string prompt = s.draft.Trim();
            if (string.IsNullOrEmpty(prompt)) return;

            _showScriptList = false;
            _showPrefabList = false;

            if (prompt.Equals("test", StringComparison.OrdinalIgnoreCase))
            {
                s.messages.Add(new ChatMessage("user", prompt));
                s.messages.Add(new ChatMessage("assistant", BuildHealthCheckReply()));
                s.draft = "";
                _stickBottom = true;
                _autoScroll = true;
                SaveHistory(s);
                Repaint();
                return;
            }

            if (!MCPServer.IsRunning)
            {
                s.messages.Add(new ChatMessage("user", prompt));
                s.messages.Add(new ChatMessage("assistant",
                    "🔴 **AI Unity MCP Server is not connected**\n\n" +
                    "Open **AI Unity MCP Server → Claude In → ▶ Start**.\n" +
                    "When the header indicator turns green and reads **online**, submit the request again."));
                s.draft = "";
                s.images.Clear();
                s.attached.Clear();
                _stickBottom = true;
                _autoScroll = true;
                SaveHistory(s);
                Repaint();
                return;
            }

            var imagesSnap = new List<AttachedImage>(s.images);
            var attachedSnap = new Dictionary<string, string>(s.attached);
            var historyTurns = BuildHistoryTurns(s);

            s.messages.Add(new ChatMessage("user", prompt));
            s.messages.Add(new ChatMessage("assistant", QUEUED));
            int userIndex = s.messages.Count - 2;
            int phIndex = s.messages.Count - 1;

            s.draft = "";
            s.images.Clear();
            s.attached.Clear();
            _stickBottom = true;
            _autoScroll = true;
            Repaint();

            var sc = s;
            EditorApplication.delayCall += () => EnqueueHeavy(sc, prompt, imagesSnap, attachedSnap, historyTurns, userIndex, phIndex);
        }

        void EnqueueHeavy(ChatSession s, string prompt, List<AttachedImage> images,
                          Dictionary<string, string> attached, List<ConversationTurn> historyTurns,
                          int userIndex, int phIndex)
        {
            string fullPrompt = BuildPromptWithScripts(prompt, out var primaryScripts, out var depScripts);
            bool hasProfiler = attached.Count > 0;
            if (hasProfiler)
                foreach (var kv in attached)
                    fullPrompt += $"\n\n--- Unity Profiler data: {kv.Key} ---\n```\n" + kv.Value + "\n```";

            var gathered = hasProfiler ? new List<KeyValuePair<string, string>>() : AutoGather(prompt);
            foreach (var g in gathered)
                fullPrompt += $"\n\n--- Unity {g.Key} (auto-gathered) ---\n```json\n{g.Value}\n```";

            var prefabNames = new List<string>();
            var inspectedPrefabs = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            if (primaryScripts.Count > 0 && IsAnalysisIntent(prompt))
            {
                var pfPaths = new List<string>();
                foreach (var sn in primaryScripts)
                {
                    string sp = CodebaseIndex.ResolvePath(sn);
                    if (sp == null) continue;
                    string guid = UnityEditor.AssetDatabase.AssetPathToGUID(sp);
                    foreach (var pf in PrefabIndex.PrefabsUsing(guid))
                        if (!pfPaths.Contains(pf)) pfPaths.Add(pf);
                }
                if (pfPaths.Count > 0)
                {
                    const int INSPECT_CAP = 4;
                    pfPaths.Sort((a, b) =>
                    {
                        int ra = PrefabRelevance(System.IO.Path.GetFileNameWithoutExtension(a), primaryScripts);
                        int rb = PrefabRelevance(System.IO.Path.GetFileNameWithoutExtension(b), primaryScripts);
                        if (ra != rb) return rb - ra;
                        return System.IO.Path.GetFileNameWithoutExtension(a).Length
                             - System.IO.Path.GetFileNameWithoutExtension(b).Length;
                    });
                    int inspected = 0;
                    foreach (var pf in pfPaths)
                    {
                        string pname = System.IO.Path.GetFileNameWithoutExtension(pf);
                        prefabNames.Add(pname);
                        if (inspected < INSPECT_CAP)
                        {
                            string report = PrefabInspector.Inspect(pf);
                            if (!string.IsNullOrEmpty(report))
                            {
                                fullPrompt += $"\n\n--- Prefab contents: {pname} (scripts {string.Join("/", primaryScripts)} are attached to this prefab) ---\n```\n{report}\n```";
                                inspectedPrefabs.Add(pf);
                                inspected++;
                            }
                        }
                    }
                    if (pfPaths.Count > INSPECT_CAP)
                        fullPrompt += $"\n\n(+ {pfPaths.Count - INSPECT_CAP} more prefabs use this script; inspected the {INSPECT_CAP} most relevant to limit context size.)";
                }
                else if (PrefabIndex.Building)
                {
                    fullPrompt += "\n\n(The prefab index is still building. Prefab details are unavailable for this request; retry shortly.)";
                }
            }

            // if (primaryScripts.Count > 0 && Application.isPlaying && IsRuntimeWatchIntent(prompt)) { ... WatchAuto.AutoWatch ... }

            var prefabMentions = new List<string>();
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(prompt, @"#\[([^\]]+)\]|#([A-Za-z0-9_]+)"))
            {
                string name = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                if (prefabMentions.Contains(name)) continue;
                string path = PrefabIndex.ResolvePath(name);
                if (path == null) continue;
                prefabMentions.Add(name);
                if (inspectedPrefabs.Contains(path)) continue;
                string report = PrefabInspector.Inspect(path);
                if (string.IsNullOrEmpty(report)) continue;
                fullPrompt += $"\n\n--- Prefab contents: {name} (#mention) ---\n```\n{report}\n```";
                inspectedPrefabs.Add(path);
            }

            var payloadImages = new List<ClaudeImage>();
            foreach (var img in images)
            {
                try
                {
                    byte[] bytes = ImageOptimizer.ResizeForApi(img.Path, 1568, out string mime);
                    payloadImages.Add(new ClaudeImage { Base64 = Convert.ToBase64String(bytes), Mime = mime });
                }
                catch
                {
                    payloadImages.Add(new ClaudeImage { Base64 = Convert.ToBase64String(File.ReadAllBytes(img.Path)), Mime = img.Mime });
                }
            }

            string note = "";
            if (images.Count > 0) note += $"\n<i>[{images.Count} image(s) attached]</i>";
            if (primaryScripts.Count > 0)
            {
                string slabel = $"{string.Join(", ", primaryScripts)}";
                if (depScripts.Count > 0) slabel += $"  +  auto: {string.Join(", ", depScripts)}";
                note += $"\n<i>[{slabel}]</i>";
            }
            if (hasProfiler) note += $"\n<i>[profiler: {string.Join(", ", attached.Keys)} attached]</i>";
            if (gathered.Count > 0)
            {
                var names = new List<string>();
                foreach (var g in gathered) names.Add(g.Key);
                note += $"\n<i>[🔍 auto: {string.Join(", ", names)}]</i>";
            }
            if (prefabNames.Count > 0) note += $"\n<i>[🧩 uses prefab: {string.Join(", ", prefabNames)}]</i>";
            if (prefabMentions.Count > 0) note += $"\n<i>[🧩 #prefab: {string.Join(", ", prefabMentions)}]</i>";

            if (!string.IsNullOrEmpty(note) && userIndex >= 0 && userIndex < s.messages.Count && s.messages[userIndex].Role == "user")
                s.messages[userIndex] = new ChatMessage("user", prompt + note);

            s.queue.Enqueue(new QueuedItem { FullPrompt = fullPrompt, RawPrompt = prompt, Images = payloadImages, PlaceholderIndex = phIndex, History = historyTurns });
            _autoScroll = true;
            Repaint();

            if (!s.pumping) PumpQueue(s);
        }

        static List<ConversationTurn> BuildHistoryTurns(ChatSession s, int maxTurns = 6)
        {
            var result = new List<ConversationTurn>();
            if (s.messages.Count == 0) return result;

            int start = Mathf.Max(0, s.messages.Count - maxTurns);
            for (int i = start; i < s.messages.Count; i++)
            {
                var m = s.messages[i];
                if (m.Content == THINKING || m.Content == QUEUED || m.Content.StartsWith("⏳")) continue;
                if (m.Role != "user" && (m.Content.StartsWith("✅") || m.Content.StartsWith("⚠️") || m.Content.StartsWith("❌")))
                    continue;

                if (result.Count > 0 && result[result.Count - 1].Role == m.Role)
                    result.RemoveAt(result.Count - 1);

                result.Add(new ConversationTurn { Role = m.Role, Content = m.Content });
            }

            while (result.Count > 0 && result[result.Count - 1].Role == "user")
                result.RemoveAt(result.Count - 1);

            return result;
        }


        async void PumpQueue(ChatSession s)
        {
            s.pumping = true;
            while (s.queue.Count > 0)
            {
                var item = s.queue.Dequeue();
                s.isLoading = true;
                s.requestStart = EditorApplication.timeSinceStartup;
                s.cts = new System.Threading.CancellationTokenSource();
                var token = s.cts.Token;
                _pending.Enqueue(() => SetMessage(s, item.PlaceholderIndex, "assistant", THINKING));
                Repaint();

                int curRole = CurrentRole();
                ClaudeResponse response;
                try
                {
                    const int MAX_RESUME_TURNS = 5;
                    string resumeId = s.cliSessionId;
                    if (s.backend == 1 && s.cliTurnCount >= MAX_RESUME_TURNS)
                    {
                        resumeId = null;
                        s.cliTurnCount = 0;
                        UnityEngine.Debug.Log($"[AI Unity MCP Server] Started a new CLI session after {MAX_RESUME_TURNS} turns; context reset.");
                    }

                    response = s.backend == 1
                        ? await ClaudeCliClient.SendAsync(item.FullPrompt, item.Images, token, resumeId, curRole)
                        : await ClaudeAPIClient.SendAsync(item.FullPrompt, item.Images, token, curRole, item.History);
                    if (s.backend == 1 && !string.IsNullOrEmpty(response?.SessionId))
                    {
                        s.cliSessionId = response.SessionId;
                        s.cliTurnCount++;
                    }
                }
                catch (OperationCanceledException)
                {
                    response = new ClaudeResponse { Error = "Cancelled" };
                }

                s.isLoading = false;
                s.cts?.Dispose();
                s.cts = null;

                string content, stat = null;
                if (response.IsError)
                    content = $"❌ {response.Error}";
                else
                {
                    content = response.Text;
                    if (response.HasCommand)
                    {
                        string execResult = await System.Threading.Tasks.Task.Run(() => ExecuteCommand(s, response.CommandJson));
                        string cmdName = ExtractCommandName(response.CommandJson);
                        if (cmdName == "uitk_playtest")
                            execResult = await PollUIToolkitPlaytest(execResult, token);
                        bool execErr = execResult.StartsWith("⚠️") || execResult.Contains("\"error\"");

                        if (!execErr && _dataCommands.Contains(cmdName))
                        {
                            _pending.Enqueue(() => SetMessage(s, item.PlaceholderIndex, "assistant", THINKING));
                            Repaint();

                            List<ClaudeImage> followImages = null;
                            string fp;
                            if (cmdName == "capture_screenshot" || cmdName == "uitk_playtest")
                            {
                                string shotPath = ExtractScreenshotPath(execResult);
                                followImages = BuildScreenshotImages(shotPath);
                                fp = cmdName == "capture_screenshot"
                                    ? $"Original user request: {item.RawPrompt}\n\n" +
                                      (followImages != null
                                          ? "This is a Unity screenshot. Analyze what is visible and answer the original request using Header(Dev)/Header(Art)."
                                          : $"The screenshot was captured but could not be loaded ({EscapeForPrompt(execResult)}). Briefly inform the user.")
                                    : $"Original user request: {item.RawPrompt}\n\n" +
                                      $"This is the completed UI Toolkit playtest result:\n{execResult}\n\n" +
                                      (followImages != null ? "Use the attached screenshot as additional visual evidence. " : "") +
                                      "Summarize the before/after state, findings, console or exception evidence, limitations, and next actions. Do not display raw JSON.";
                            }
                            else
                            {
                                fp = $"Original user request: {item.RawPrompt}\n\n" +
                                     $"This is the JSON result of Unity command {cmdName}:\n{execResult}\n\n" +
                                     "Analyze the result while answering the original request using Header(Dev)/Header(Art). " +
                                     "Do not display raw JSON; group and count the data and highlight meaningful findings.";
                            }
                            ClaudeResponse follow;
                            try
                            {
                                follow = s.backend == 1
                                    ? await ClaudeCliClient.SendAsync(fp, followImages, token, s.cliSessionId, curRole)
                                    : await ClaudeAPIClient.SendAsync(fp, followImages, token, curRole, item.History);
                                if (s.backend == 1 && !string.IsNullOrEmpty(follow?.SessionId))
                                    s.cliSessionId = follow.SessionId;
                            }
                            catch (OperationCanceledException) { follow = new ClaudeResponse { Error = "Cancelled" }; }

                            if (follow != null && !follow.IsError && !string.IsNullOrEmpty(follow.Text))
                                content = string.IsNullOrEmpty(response.Text) ? follow.Text : response.Text + "\n\n" + follow.Text;
                            else
                                content += "\n\n" + execResult;
                        }
                        else
                        {
                            content += "\n\n" + execResult;
                        }
                    }

                    double sec = EditorApplication.timeSinceStartup - s.requestStart;
                    stat = $"⏱ {FmtTime(sec)}";
                    if (s.backend == 1 && ClaudeCliClient.LiveOutputTokens > 0)
                        stat += $" · {ClaudeCliClient.LiveOutputTokens:N0} tokens";
                }
                int idx = item.PlaceholderIndex; string c = content, st = stat;
                _pending.Enqueue(() => SetMessage(s, idx, "assistant", c, st));

                _autoScroll = true;
                Repaint();
            }
            s.pumping = false;
            SaveHistory(s);
            Repaint();
        }

        void StopSession(ChatSession s)
        {
            foreach (var q in s.queue)
                if (q.PlaceholderIndex >= 0 && q.PlaceholderIndex < s.messages.Count)
                    s.messages[q.PlaceholderIndex] = new ChatMessage("assistant", "❌ Cancelled");
            s.queue.Clear();
            s.cts?.Cancel();
            s.cliSessionId = null;
            s.cliTurnCount = 0;
            Repaint();
        }

        void CancelQueued(ChatSession s, int placeholderIndex)
        {
            var keep = new Queue<QueuedItem>();
            while (s.queue.Count > 0)
            {
                var q = s.queue.Dequeue();
                if (q.PlaceholderIndex == placeholderIndex)
                    s.messages[placeholderIndex] = new ChatMessage("assistant", "❌ Cancelled");
                else keep.Enqueue(q);
            }
            while (keep.Count > 0) s.queue.Enqueue(keep.Dequeue());
            Repaint();
        }

        static readonly HashSet<string> _dataCommands = new HashSet<string>
        {
            "count_components","find_asset","inspect_object","scene_hierarchy","scene_list",
            "read_console","read_logfile","capture_state","perf_audit","perf_worst",
            "refactor_audit","audit_textures","audit_unused","audit_empty_folders",
            "memory_snapshot","fusion_stats","get_exceptions","watch_get","read_script",
            "capture_screenshot","uitk_inspect","uitk_validate","uitk_apply","uitk_playtest",
        };

        static async System.Threading.Tasks.Task<string> PollUIToolkitPlaytest(string initialResult, System.Threading.CancellationToken token)
        {
            if (string.IsNullOrEmpty(initialResult) || !initialResult.Contains("\"status\":\"running\""))
                return initialResult;

            var runMatch = System.Text.RegularExpressions.Regex.Match(initialResult, "\"runId\"\\s*:\\s*\"([^\"]+)\"");
            if (!runMatch.Success)
                return initialResult;

            string runId = runMatch.Groups[1].Value;
            for (int attempt = 0; attempt < 20; attempt++)
            {
                await System.Threading.Tasks.Task.Delay(100, token);
                string body = $"{{\"mode\":\"status\",\"runId\":\"{MCPHandlers.EscapeJsonPublic(runId)}\"}}";
                string result = await System.Threading.Tasks.Task.Run(() => MCPHandlers.Dispatch("/uitk/playtest", body), token);
                if (!result.Contains("\"status\":\"running\""))
                    return "✅ Execute: " + result;
            }
            return initialResult + "\nPlaytest is still running. Poll uitk_playtest with mode=status and the returned runId.";
        }

        static int PrefabRelevance(string prefabName, List<string> scripts)
        {
            if (string.IsNullOrEmpty(prefabName) || scripts == null) return 0;
            string pn = prefabName.ToLowerInvariant();
            int best = 0;
            foreach (var s in scripts)
            {
                if (string.IsNullOrEmpty(s)) continue;
                string sb = s.ToLowerInvariant();
                if (sb.EndsWith(".cs")) sb = sb.Substring(0, sb.Length - 3);
                if (sb.Length < 2) continue;
                if (pn == sb)            best = Math.Max(best, 100);
                else if (pn.Contains(sb)) best = Math.Max(best, 50 + sb.Length);
                else if (sb.Contains(pn)) best = Math.Max(best, 30);
            }
            return best;
        }

        static string ExtractCommandName(string json)
        {
            if (string.IsNullOrEmpty(json)) return "";
            var m = System.Text.RegularExpressions.Regex.Match(json, "\"command\"\\s*:\\s*\"([^\"]+)\"");
            return m.Success ? m.Groups[1].Value : "";
        }

        string ExecuteCommand(ChatSession s, string cmdJson)
        {
            try
            {
                string result = MCPHandlers.Dispatch(CommandJsonToPath(cmdJson), cmdJson);
                return $"✅ Execute: {result}";
            }
            catch (Exception e)
            {
                return $"⚠️ Execute error: {e.Message}";
            }
        }

        static void SetMessage(ChatSession s, int index, string role, string content, string stat = null)
        {
            if (index < 0 || index >= s.messages.Count) return;
            var old = s.messages[index];
            s.messages[index] = new ChatMessage(role, content) { Stat = stat };
        }

        static string CommandJsonToPath(string json)
            => MCPHandlers.ResolvePath(ExtractCommandName(json));

        static string ExtractScreenshotPath(string execResult)
        {
            if (string.IsNullOrEmpty(execResult)) return null;
            var m = System.Text.RegularExpressions.Regex.Match(execResult, "\"screenshot\"\\s*:\\s*\"((?:\\\\.|[^\"\\\\])*)\"");
            if (!m.Success) return null;
            return m.Groups[1].Value.Replace("\\\\", "\\").Replace("\\\"", "\"");
        }

        static List<ClaudeImage> BuildScreenshotImages(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            try
            {
                byte[] bytes = ImageOptimizer.ResizeForApi(path, 1568, out string mime);
                return new List<ClaudeImage> { new ClaudeImage { Base64 = Convert.ToBase64String(bytes), Mime = mime } };
            }
            catch
            {
                try { return new List<ClaudeImage> { new ClaudeImage { Base64 = Convert.ToBase64String(File.ReadAllBytes(path)), Mime = "image/png" } }; }
                catch { return null; }
            }
        }

        static string EscapeForPrompt(string s) =>
            string.IsNullOrEmpty(s) ? "" : (s.Length > 300 ? s.Substring(0, 300) + "…" : s);

        // ── Types ─────────────────────────────────────────────────────────
        [Serializable]
        class ChatSession
        {
            public int backend;
            public List<ChatMessage> messages = new List<ChatMessage>();
            public string draft = "";
            public Vector2 chatScroll;

            [NonSerialized] public bool isLoading;
            [NonSerialized] public bool pumping;
            [NonSerialized] public List<AttachedImage> images = new List<AttachedImage>();
            [NonSerialized] public Dictionary<string, string> attached = new Dictionary<string, string>();
            [NonSerialized] public string cliSessionId;
            [NonSerialized] public int cliTurnCount;
            [NonSerialized] public Queue<QueuedItem> queue = new Queue<QueuedItem>();
            [NonSerialized] public System.Threading.CancellationTokenSource cts;
            [NonSerialized] public double requestStart;

            public bool Busy => isLoading || queue.Count > 0;

            public void Reinit()
            {
                isLoading = false;
                pumping = false;
                if (attached == null) attached = new Dictionary<string, string>(); else attached.Clear();
                cts = null;
                if (images == null) images = new List<AttachedImage>();
                if (queue == null) queue = new Queue<QueuedItem>();
                else queue.Clear();
                if (messages == null) messages = new List<ChatMessage>();
            }
        }

        class QueuedItem
        {
            public string FullPrompt;
            public string RawPrompt;
            public List<ClaudeImage> Images;
            public int PlaceholderIndex;
            public List<ConversationTurn> History;
        }

        // ── Role parser v2 ──────────────────────────────────────────────────
        static readonly System.Text.RegularExpressions.Regex _headerRe =
            new System.Text.RegularExpressions.Regex(
                @"(?m)^[ \t>*#]*Header\s*\(\s*(Dev|Art)\s*\)[ \t*:]*\r?$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        static bool HasHeaderMarkers(string content)
            => !string.IsNullOrEmpty(content) && _headerRe.IsMatch(content);

        static string ExtractHeaderContent(string content, string role)
        {
            if (string.IsNullOrEmpty(content)) return null;
            var ms = _headerRe.Matches(content);
            if (ms.Count == 0) return null;
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < ms.Count; i++)
            {
                if (!string.Equals(ms[i].Groups[1].Value, role, StringComparison.OrdinalIgnoreCase)) continue;
                int start = ms[i].Index + ms[i].Length;
                int end = i + 1 < ms.Count ? ms[i + 1].Index : content.Length;
                string part = content.Substring(start, end - start).Trim();
                if (part.Length > 0) sb.Append(part).Append("\n\n");
            }
            return sb.Length == 0 ? null : sb.ToString().Trim();
        }

        static readonly System.Text.RegularExpressions.Regex _altRoleRe =
            new System.Text.RegularExpressions.Regex(
                @"(?m)^[^\w\u0E00-\u0E7F\r\n]{0,8}(Dev|Art)\b[*_]*\s*(?:[—–:：]|-\s)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        static string ExtractAltRoleSection(string content, string roleName)
        {
            if (string.IsNullOrEmpty(content)) return null;
            var ms = _altRoleRe.Matches(content);
            if (ms.Count == 0) return null;
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < ms.Count; i++)
            {
                if (!string.Equals(ms[i].Groups[1].Value, roleName, StringComparison.OrdinalIgnoreCase)) continue;
                int start = ms[i].Index;
                int end = i + 1 < ms.Count ? ms[i + 1].Index : content.Length;
                string part = content.Substring(start, end - start).Trim();
                if (part.Length > 0) sb.Append(part).Append("\n\n");
            }
            return sb.Length == 0 ? "" : sb.ToString().Trim();
        }

        static bool IsThinContent(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return true;
            if (System.Text.RegularExpressions.Regex.IsMatch(s, @"[0-9]|✓|✔|✅|❌|⚠|🔴|🟡|🟢")) return false;
            string stripped = System.Text.RegularExpressions.Regex.Replace(
                s, @"Category\([^)]*\)|Header\([^)]*\)|[#*`\-:\s]", "");
            return !System.Text.RegularExpressions.Regex.IsMatch(stripped, @"\p{L}");
        }

        [Serializable]
        class ChatMessage
        {
            public string Role;
            public string Content;
            public string Stat;
            public bool   IsDual;
            public string ArtContent;
            [NonSerialized] public bool collapsed;
            [NonSerialized] string _rich;
            [NonSerialized] float _height = -1, _heightWidth = -1;
            [NonSerialized] ChatMessage _devView;   // cached filtered view for Dev
            [NonSerialized] ChatMessage _artView;   // cached filtered view for Art
            public ChatMessage(string role, string content) { Role = role; Content = content; }

            public ChatMessage RoleView(int role)
            {
                if (Role == "user") return this;

                var cached = role == 0 ? _devView : _artView;
                if (cached != null) return cached;

                string roleName = role == 0 ? "Dev" : "Art";
                bool hasAnyHeader = HasHeaderMarkers(Content);

                ChatMessage result;
                string extracted = hasAnyHeader
                    ? ExtractHeaderContent(Content, roleName)
                    : ExtractAltRoleSection(Content, roleName);

                if (extracted == null && !hasAnyHeader)
                {
                    result = this;
                }
                else if (!IsThinContent(extracted))
                {
                    result = new ChatMessage("assistant", InjectSummaryTableIfMissing(extracted)) { Stat = Stat };
                }
                else
                {
                    string label = role == 1 ? "Visual (Art)" : "Technical (Dev)";
                    result = new ChatMessage(role.ToString(), "ℹ️ No " + label + " data is available for this prompt.") { Stat = Stat };
                }

                if (role == 0) _devView = result;
                else _artView = result;
                return result;
            }

            static readonly System.Text.RegularExpressions.Regex _cardRe =
                new System.Text.RegularExpressions.Regex(@"^\s*#{1,4}\s*(🔴|🟡|🟢)\s*#?\d*\s*[—\-–]\s*(.+?)\s*$");

            static string InjectSummaryTableIfMissing(string content)
            {
                if (string.IsNullOrEmpty(content)) return content;
                if (System.Text.RegularExpressions.Regex.IsMatch(content, @"(?m)^\s*\|\s*(#|order)\s*\|", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return content;
                if (content.IndexOf("ordered by risk", System.StringComparison.OrdinalIgnoreCase) >= 0) return content;
                if (content.IndexOf("highest to lowest risk", System.StringComparison.OrdinalIgnoreCase) >= 0) return content;

                var lines = content.Replace("\r\n", "\n").Split('\n');
                var rows = new System.Collections.Generic.List<string[]>();   // {n, emoji, title, conf, val, loc}
                string emoji = null, title = null, conf = null, val = null, loc = null;

                void Flush()
                {
                    if (emoji != null && !string.IsNullOrEmpty(title))
                        rows.Add(new[] { (rows.Count + 1).ToString(), emoji, San(title), conf ?? "?", San(val ?? "—"), San(loc ?? "—") });
                    emoji = title = conf = val = loc = null;
                }

                foreach (var raw in lines)
                {
                    var m = _cardRe.Match(raw);
                    if (m.Success)
                    {
                        Flush();
                        emoji = m.Groups[1].Value;
                        string rest = m.Groups[2].Value;
                        conf = rest.Contains("✓") ? "✓" : rest.Contains("❌") ? "❌" : rest.Contains("⏸") ? "⏸️" : "?";
                        int cut = rest.IndexOfAny(new[] { '✓', '❌', '?', '⏸' });
                        title = (cut >= 0 ? rest.Substring(0, cut) : rest).Trim().TrimEnd('—', '-', '–', ' ');
                        continue;
                    }
                    if (emoji == null) continue;
                    string t = raw.Trim();
                    if (loc == null) loc = Field(t, "Location");
                    if (val == null) val = Field(t, "Measured value");
                }
                Flush();

                if (rows.Count < 2) return content;

                var sb = new System.Text.StringBuilder();
                sb.AppendLine("## 🎯 Summary (ordered by risk)");
                sb.AppendLine("| # | Finding | Status | Measured value / budget | Confidence | Location |");
                sb.AppendLine("|---|-------|------|------------------|-------|-----|");
                foreach (var r in rows)
                    sb.AppendLine($"| {r[0]} | {r[2]} | {r[1]} | {r[4]} | {r[3]} | {r[5]} |");
                sb.AppendLine();
                return sb.ToString() + content;
            }

            static string Field(string line, string field)
            {
                var m = System.Text.RegularExpressions.Regex.Match(line, @"^\**\s*" + field + @"\s*\**\s*[:：]\s*(.+)$");
                return m.Success ? m.Groups[1].Value.Trim() : null;
            }

            static string San(string s)
            {
                if (string.IsNullOrEmpty(s)) return "—";
                s = s.Replace("|", "/").Replace("**", "").Replace("`", "").Trim();
                if (s.Length > 48) s = s.Substring(0, 46) + "…";
                return s.Length == 0 ? "—" : s;
            }

            public void InvalidateCaches() { _devView = null; _artView = null; }

            [NonSerialized] double _shownAt = -1;
            public float FadeAlpha(double now)
            {
                if (_shownAt < 0) _shownAt = now;
                double t = (now - _shownAt) / 0.28;
                return t >= 1.0 ? 1f : (float)t;
            }

            public string DisplayContent
            {
                get
                {
                    if (Role == "user" || string.IsNullOrEmpty(Content)) return Content;
                    var lines = Content.Split('\n');
                    var kept = new System.Collections.Generic.List<string>(lines.Length);
                    foreach (var line in lines)
                    {
                        string t = line.Trim();
                        if (_headerRe.IsMatch(t) ||
                            t.StartsWith("CATEGORIES:", System.StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (t.StartsWith("Header(Dev)", System.StringComparison.OrdinalIgnoreCase) ||
                            t.StartsWith("Header(Art)", System.StringComparison.OrdinalIgnoreCase))
                        {
                            string rest = t.Substring(11).TrimStart(' ', '\t', ':', '*', '-', '—');
                            if (rest.Length > 0) kept.Add(rest);
                            continue;
                        }
                        if (System.Text.RegularExpressions.Regex.IsMatch(t, @"^#{1,6}\s*$")) continue;
                        kept.Add(line);
                    }
                    return string.Join("\n", kept).TrimStart('\r', '\n');
                }
            }

            public string Rich()
            {
                if (_rich == null || (_cachedRichFor != DisplayContent))
                {
                    _cachedRichFor = DisplayContent;
                    _rich = Role == "user" ? Content : MarkdownColor.ToRichText(DisplayContent);
                    _segs = null;
                    _height = -1;
                }
                return _rich;
            }
            [NonSerialized] string _cachedRichFor;

            public float Height(GUIStyle style, float width)
            {
                if (_height < 0 || !Mathf.Approximately(_heightWidth, width))
                {
                    _height = style.CalcHeight(new GUIContent(Rich()), width);
                    _heightWidth = width;
                }
                return _height;
            }

            [NonSerialized] List<Seg> _segs;
            public bool HasRich { get { Parse(); return _hasCode || _hasTable; } }
            [NonSerialized] bool _hasCode, _hasTable;

            public List<Seg> Segments() { Parse(); return _segs; }

            void Parse()
            {
                if (_segs != null) return;
                _segs = new List<Seg>();
                if (Role == "user")
                {
                    _segs.Add(new Seg { Code = false, Rendered = Rich() });
                    return;
                }
                var parts = DisplayContent.Split(new[] { "```" }, System.StringSplitOptions.None);
                for (int i = 0; i < parts.Length; i++)
                {
                    if (i % 2 == 0)
                    {
                        AddTextSegs(parts[i]);
                    }
                    else // code
                    {
                        string body = parts[i];
                        int nl = body.IndexOf('\n');
                        string lang = nl > 0 ? body.Substring(0, nl).Trim() : "";
                        string code = nl > 0 ? body.Substring(nl + 1) : body;
                        code = code.TrimEnd('\n');
                        string header = ExtractHeader(code, lang);
                        _segs.Add(new Seg { Code = true, Raw = code, Rendered = CodeHighlight.Highlight(code), Header = header });
                        _hasCode = true;
                    }
                }
                if (_segs.Count == 0) _segs.Add(new Seg { Code = false, Rendered = Rich() });
            }

            void AddTextSegs(string text)
            {
                if (string.IsNullOrEmpty(text) || text.Trim().Length == 0) return;
                var lines = text.Split('\n');
                var buf = new List<string>();
                int i = 0;
                while (i < lines.Length)
                {
                    bool here = IsTableLine(lines[i]);
                    bool next = i + 1 < lines.Length && IsTableLine(lines[i + 1]);
                    if (here && next)
                    {
                        FlushText(buf);
                        var tbl = new List<string>();
                        while (i < lines.Length && IsTableLine(lines[i])) { tbl.Add(lines[i]); i++; }
                        var seg = BuildTable(tbl);
                        if (seg != null) { _segs.Add(seg); _hasTable = true; }
                    }
                    else { buf.Add(lines[i]); i++; }
                }
                FlushText(buf);
            }

            void FlushText(List<string> buf)
            {
                if (buf.Count == 0) return;
                string t = string.Join("\n", buf).Trim();
                if (t.Length > 0) _segs.Add(new Seg { Code = false, Rendered = MarkdownColor.ToRichText(t) });
                buf.Clear();
            }

            static bool IsTableLine(string line)
            {
                if (line == null) return false;
                string t = line.Trim();
                return t.Length > 0 && t.IndexOf('|') >= 0;
            }

            static List<string> SplitCells(string line)
            {
                var raw = new List<string>(line.Split('|'));
                for (int k = 0; k < raw.Count; k++) raw[k] = raw[k].Trim();
                if (raw.Count > 0 && raw[0].Length == 0) raw.RemoveAt(0);
                if (raw.Count > 0 && raw[raw.Count - 1].Length == 0) raw.RemoveAt(raw.Count - 1);
                return raw;
            }

            static bool IsSeparatorRow(List<string> cells)
            {
                if (cells.Count == 0) return false;
                foreach (var c in cells)
                {
                    string t = c.Replace(":", "").Replace("-", "").Trim();
                    if (t.Length != 0 || c.IndexOf('-') < 0) return false;
                }
                return true;
            }

            static string CleanCell(string s)
            {
                if (string.IsNullOrEmpty(s)) return "";
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\*\*(.+?)\*\*", "<b>$1</b>");
                s = s.Replace("`", "");
                return s;
            }

            static Seg BuildTable(List<string> lines)
            {
                var rows = new List<string[]>();
                int cols = 0;
                foreach (var ln in lines)
                {
                    var cells = SplitCells(ln);
                    if (IsSeparatorRow(cells)) continue;
                    if (cells.Count == 0) continue;
                    var arr = new string[cells.Count];
                    for (int c = 0; c < cells.Count; c++) arr[c] = CleanCell(cells[c]);
                    rows.Add(arr);
                    if (cells.Count > cols) cols = cells.Count;
                }
                if (rows.Count < 1 || cols < 1) return null;
                return new Seg { Table = true, Rows = rows, Cols = cols };
            }

            static string ExtractHeader(string code, string lang)
            {
                var m = System.Text.RegularExpressions.Regex.Match(code, @"//\s*FILE:\s*(\S+)");
                if (m.Success) return m.Groups[1].Value;
                var m2 = System.Text.RegularExpressions.Regex.Match(code, @"(Assets/[\w/]+\.cs)");
                if (m2.Success) return m2.Groups[1].Value;
                return "Code";
            }
        }

        class Seg
        {
            public bool Code;
            public bool Table;
            public bool Collapsed;
            public string Rendered;
            public string Raw;
            public string Header;
            public List<string[]> Rows;
            public int Cols;
            public float Height = -1;
        }

        class AttachedImage
        {
            public string Path;
            public Texture2D Texture;
            public string Mime;
        }
    }
}
