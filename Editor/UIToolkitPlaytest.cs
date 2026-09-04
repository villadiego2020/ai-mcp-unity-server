using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace AIUnityMCPServer
{
    [Serializable]
    internal sealed class UIToolkitPlaytestRequest
    {
        public string mode;
        public string document;
        public string runId;
        public string action;
        public string selector;
        public string value;
        public int waitFrames;
        public bool screenshot = true;
        public int maxNodes;
    }

    [Serializable]
    internal sealed class UIToolkitElementState
    {
        public int index;
        public int parentIndex;
        public int depth;
        public string type;
        public string name;
        public string[] classes;
        public string text;
        public string value;
        public bool enabled;
        public string display;
        public bool focused;
        public int childCount;
        public float x;
        public float y;
        public float width;
        public float height;
    }

    [Serializable]
    internal sealed class UIToolkitPlaytestLog
    {
        public string type;
        public string message;
        public string stackTrace;
    }

    [Serializable]
    internal sealed class UIToolkitPlaytestEvidence
    {
        public bool stateChanged;
        public List<UIToolkitPlaytestLog> consoleDelta;
        public string exceptions;
        public string screenshot;
        public bool truncated;
    }

    [Serializable]
    internal sealed class UIToolkitPlaytestError
    {
        public string code;
        public string message;
        public string recovery;
    }

    [Serializable]
    internal sealed class UIToolkitPlaytestResponse
    {
        public bool ok;
        public string status;
        public string runId;
        public string document;
        public string action;
        public string target;
        public List<UIToolkitElementState> before;
        public List<UIToolkitElementState> after;
        public UIToolkitPlaytestEvidence evidence;
        public List<string> warnings;
        public UIToolkitPlaytestError error;
    }

    internal sealed class UIToolkitPlaytestRun
    {
        public string RunId;
        public DateTime CreatedUtc;
        public DateTime ExpiresUtc;
        public int DocumentInstanceId;
        public string DocumentPath;
        public string Action;
        public string Selector;
        public string Value;
        public int RemainingFrames;
        public int MaximumNodes;
        public bool CaptureScreenshot;
        public long StartingLogSequence;
        public string StartingExceptions;
        public List<UIToolkitElementState> Before;
        public UIToolkitPlaytestResponse Response;
    }

    [InitializeOnLoad]
    internal static class UIToolkitPlaytest
    {
        const int MaximumRuns = 20;
        const int MaximumLogs = 500;
        static readonly TimeSpan RunLifetime = TimeSpan.FromMinutes(10);
        static readonly Dictionary<string, UIToolkitPlaytestRun> Runs = new Dictionary<string, UIToolkitPlaytestRun>(StringComparer.Ordinal);
        static readonly List<(long sequence, UIToolkitPlaytestLog entry)> Logs = new List<(long sequence, UIToolkitPlaytestLog entry)>();
        static long _logSequence;

        static UIToolkitPlaytest()
        {
            Application.logMessageReceived -= CaptureLog;
            Application.logMessageReceived += CaptureLog;
            EditorApplication.update -= UpdateRuns;
            EditorApplication.update += UpdateRuns;
        }

        internal static string Execute(string body)
        {
            if (!TryParseRequest(body, out UIToolkitPlaytestRequest request, out string error))
                return UIToolkitJson.Error("INVALID_REQUEST", error, "Send a valid playtest request with an explicit mode.");

            string mode = request.mode?.ToLowerInvariant();
            if (mode == "status")
                return GetStatus(request.runId);
            if (mode != "start")
                return UIToolkitJson.Error("INVALID_REQUEST", "Playtest mode must be 'start' or 'status'.", "Use mode 'start' with a document, or mode 'status' with a runId.");

            return Start(request);
        }

        static string Start(UIToolkitPlaytestRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.document))
                return UIToolkitJson.Error("INVALID_REQUEST", "Start mode requires a document name, hierarchy path, or instance ID.", "Pass the exact UIDocument identifier and retry.");

            string action = string.IsNullOrEmpty(request.action) ? "snapshot" : request.action.ToLowerInvariant();
            if (!IsSupportedAction(action))
                return UIToolkitJson.Error("INVALID_REQUEST", $"Unsupported playtest action '{request.action}'.", "Use snapshot, click, set-text, set-toggle, or focus.");
            if (action != "snapshot" && string.IsNullOrWhiteSpace(request.selector))
                return UIToolkitJson.Error("INVALID_REQUEST", $"Action '{action}' requires an exact #name, .class, or type selector.", "Add a selector that resolves to exactly one live element.");
            if (action != "snapshot" && !Application.isPlaying)
                return UIToolkitJson.Error("PLAY_MODE_REQUIRED", $"Action '{action}' requires Play Mode.", "Enter Play Mode manually, wait for the UIDocument to initialize, and retry.");
            if (action != "snapshot" && !MCPHandlers.AllowWrites)
                return UIToolkitJson.Error("READ_ONLY", $"Action '{action}' is blocked while Allow Write Commands is OFF.", "Enable AI Unity MCP Server/Allow Write Commands and retry.");

            if (!TryResolveDocument(request.document, out UIDocument document, out string documentError, out string details))
                return UIToolkitJson.Error(documentError, $"Could not resolve UIDocument '{request.document}'.", "Use an exact hierarchy path, GameObject name, or instance ID from the candidate list.", details);
            if (document.rootVisualElement == null)
                return UIToolkitJson.Error("DOCUMENT_NOT_READY", $"UIDocument '{request.document}' has no live rootVisualElement.", "Wait for the document to initialize and retry without leaving Play Mode.");

            if (!TryResolveElement(document.rootVisualElement, request.selector, action == "snapshot", out VisualElement target, out string selectorCode, out string selectorDetails))
                return UIToolkitJson.Error(selectorCode, $"Selector '{request.selector}' did not resolve to exactly one live element.", "Use an exact #name, .class, or type selector and retry.", selectorDetails);

            PurgeExpiredRuns();
            while (Runs.Count >= MaximumRuns)
            {
                UIToolkitPlaytestRun oldest = Runs.Values.OrderBy(run => run.CreatedUtc).First();
                string oldestRun = oldest.RunId;
                Runs.Remove(oldestRun);
                if (!string.IsNullOrEmpty(oldest.Response?.evidence?.screenshot))
                    TryDeleteScreenshot(oldest.Response.evidence.screenshot);
            }

            int maximumNodes = request.maxNodes <= 0 ? 250 : Math.Max(1, Math.Min(1000, request.maxNodes));
            int waitFrames = request.waitFrames <= 0 ? 2 : Math.Max(1, Math.Min(10, request.waitFrames));
            string runId = Guid.NewGuid().ToString("N");
            string documentPath = HierarchyPath(document.transform);
            List<UIToolkitElementState> before = CaptureState(target, maximumNodes, out _);
            var run = new UIToolkitPlaytestRun
            {
                RunId = runId,
                CreatedUtc = DateTime.UtcNow,
                ExpiresUtc = DateTime.UtcNow + RunLifetime,
                DocumentInstanceId = document.GetInstanceID(),
                DocumentPath = documentPath,
                Action = action,
                Selector = request.selector ?? string.Empty,
                Value = request.value,
                RemainingFrames = waitFrames,
                MaximumNodes = maximumNodes,
                CaptureScreenshot = request.screenshot,
                StartingLogSequence = _logSequence,
                StartingExceptions = ExceptionTracker.GetReport(),
                Before = before,
                Response = new UIToolkitPlaytestResponse
                {
                    ok = true,
                    status = "running",
                    runId = runId,
                    document = documentPath,
                    action = action,
                    target = DescribeTarget(target),
                    before = new List<UIToolkitElementState>(),
                    after = new List<UIToolkitElementState>(),
                    evidence = new UIToolkitPlaytestEvidence
                    {
                        consoleDelta = new List<UIToolkitPlaytestLog>(),
                        exceptions = string.Empty,
                        screenshot = string.Empty,
                    },
                    warnings = BaseWarnings(action),
                },
            };
            Runs.Add(runId, run);
            return JsonUtility.ToJson(run.Response);
        }

        static string GetStatus(string runId)
        {
            PurgeExpiredRuns();
            if (string.IsNullOrWhiteSpace(runId))
                return UIToolkitJson.Error("INVALID_REQUEST", "Status mode requires runId.", "Pass the runId returned by start mode.");
            if (!Runs.TryGetValue(runId, out UIToolkitPlaytestRun run))
                return UIToolkitJson.Error("NOT_FOUND", $"Playtest run '{runId}' was not found or has expired.", "Start a new bounded playtest run and poll its runId within ten minutes.");
            return JsonUtility.ToJson(run.Response);
        }

        static void UpdateRuns()
        {
            PurgeExpiredRuns();
            foreach (UIToolkitPlaytestRun run in Runs.Values.Where(item => item.Response.status == "running").ToArray())
            {
                run.RemainingFrames--;
                if (run.RemainingFrames > 0)
                    continue;
                CompleteRun(run);
            }
        }

        static void CompleteRun(UIToolkitPlaytestRun run)
        {
            UIDocument document = FindDocumentByInstanceId(run.DocumentInstanceId);
            if (document == null || document.rootVisualElement == null)
            {
                FailRun(run, "DOCUMENT_NOT_READY", "The UIDocument was destroyed or lost its live root before the action ran.", "Restore the document, then start a new playtest run.");
                return;
            }
            if (!TryResolveElement(document.rootVisualElement, run.Selector, run.Action == "snapshot", out VisualElement target, out string selectorCode, out string selectorDetails))
            {
                FailRun(run, selectorCode, "The target changed before the action ran.", "Inspect the live document and start a new run with an exact selector.", selectorDetails);
                return;
            }

            if (run.Action != "snapshot")
            {
                if (!Application.isPlaying)
                {
                    FailRun(run, "PLAY_MODE_REQUIRED", "Play Mode ended before the interaction ran.", "Enter Play Mode and start a new playtest run.");
                    return;
                }
                if (!MCPHandlers.AllowWrites)
                {
                    FailRun(run, "READ_ONLY", "Allow Write Commands was disabled immediately before the interaction.", "Enable Allow Write Commands and start a new playtest run.");
                    return;
                }
            }

            try
            {
                ApplyAction(target, run.Action, run.Value);
                run.Response.before = run.Before;
                run.Response.after = CaptureState(target, run.MaximumNodes, out bool truncated);
                run.Response.evidence.stateChanged = JsonUtility.ToJson(new ElementStateList { items = run.Before }) !=
                                                     JsonUtility.ToJson(new ElementStateList { items = run.Response.after });
                run.Response.evidence.consoleDelta = Logs
                    .Where(item => item.sequence > run.StartingLogSequence)
                    .Take(50)
                    .Select(item => item.entry)
                    .ToList();
                run.Response.evidence.truncated = truncated || Logs.Count(item => item.sequence > run.StartingLogSequence) > 50;
                string currentExceptions = ExceptionTracker.GetReport();
                run.Response.evidence.exceptions = currentExceptions == run.StartingExceptions ? string.Empty : currentExceptions;
                if (run.CaptureScreenshot)
                    run.Response.evidence.screenshot = CaptureScreenshot(run.RunId, run.Response.warnings);
                run.Response.ok = true;
                run.Response.status = "done";
            }
            catch (Exception exception)
            {
                FailRun(run, "INTERACTION_FAILED", exception.Message, "Inspect the target type and value, then start a new playtest run with a compatible semantic action.");
            }
        }

        static void ApplyAction(VisualElement target, string action, string value)
        {
            switch (action)
            {
                case "snapshot":
                    return;
                case "click":
                    if (!(target is Button))
                        throw new InvalidOperationException($"Semantic click requires a Button, but the target is {target.GetType().Name}.");
                    using (NavigationSubmitEvent submitEvent = NavigationSubmitEvent.GetPooled())
                    {
                        submitEvent.target = target;
                        target.SendEvent(submitEvent);
                    }
                    return;
                case "set-text":
                    if (!(target is TextField textField))
                        throw new InvalidOperationException($"set-text requires a TextField, but the target is {target.GetType().Name}.");
                    if (value == null)
                        throw new InvalidOperationException("set-text requires value; use an empty string to clear the field.");
                    textField.value = value;
                    return;
                case "set-toggle":
                    if (!(target is Toggle toggle))
                        throw new InvalidOperationException($"set-toggle requires a Toggle, but the target is {target.GetType().Name}.");
                    if (!bool.TryParse(value, out bool toggleValue))
                        throw new InvalidOperationException("set-toggle value must be 'true' or 'false'.");
                    toggle.value = toggleValue;
                    return;
                case "focus":
                    target.Focus();
                    return;
                default:
                    throw new InvalidOperationException($"Unsupported action '{action}'.");
            }
        }

        static string CaptureScreenshot(string runId, List<string> warnings)
        {
            if (!Application.isPlaying)
            {
                warnings.Add("Screenshot evidence is available only in Play Mode; the source snapshot still completed.");
                return string.Empty;
            }
            Texture2D texture = ScreenCapture.CaptureScreenshotAsTexture();
            if (texture == null)
            {
                warnings.Add("Unity returned no playtest screenshot. Keep a Game view available and retry.");
                return string.Empty;
            }
            try
            {
                byte[] png = texture.EncodeToPNG();
                string directory = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Library", "AIUnityMCPServer", "screenshots", "uitk"));
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, $"uitk_{runId}.png");
                File.WriteAllBytes(path, png);
                return path;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        static List<UIToolkitElementState> CaptureState(VisualElement root, int maximumNodes, out bool truncated)
        {
            var states = new List<UIToolkitElementState>();
            var stack = new Stack<(VisualElement element, int parentIndex, int depth)>();
            stack.Push((root, -1, 0));
            truncated = false;
            while (stack.Count > 0)
            {
                (VisualElement element, int parentIndex, int depth) = stack.Pop();
                if (states.Count >= maximumNodes)
                {
                    truncated = true;
                    break;
                }
                int index = states.Count;
                Rect layout = element.layout;
                states.Add(new UIToolkitElementState
                {
                    index = index,
                    parentIndex = parentIndex,
                    depth = depth,
                    type = element.GetType().Name,
                    name = element.name ?? string.Empty,
                    classes = element.GetClasses().ToArray(),
                    text = element is TextElement textElement ? textElement.text ?? string.Empty : string.Empty,
                    value = ReadValue(element),
                    enabled = element.enabledInHierarchy,
                    display = element.resolvedStyle.display.ToString(),
                    focused = element.panel?.focusController?.focusedElement == element,
                    childCount = element.childCount,
                    x = Finite(layout.x),
                    y = Finite(layout.y),
                    width = Finite(layout.width),
                    height = Finite(layout.height),
                });
                for (int childIndex = element.childCount - 1; childIndex >= 0; childIndex--)
                    stack.Push((element[childIndex], index, depth + 1));
            }
            return states;
        }

        static string ReadValue(VisualElement element)
        {
            if (element is TextField textField) return textField.value ?? string.Empty;
            if (element is Toggle toggle) return toggle.value ? "true" : "false";
            if (element is Slider slider) return slider.value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (element is IntegerField integerField) return integerField.value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (element is FloatField floatField) return floatField.value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return string.Empty;
        }

        static bool TryResolveDocument(string identifier, out UIDocument document, out string code, out string details)
        {
            document = null;
            code = null;
            details = null;
            UIDocument[] documents = Resources.FindObjectsOfTypeAll<UIDocument>()
                .Where(item => item != null && !EditorUtility.IsPersistent(item))
                .OrderBy(item => HierarchyPath(item.transform), StringComparer.Ordinal)
                .ThenBy(item => item.GetInstanceID())
                .ToArray();
            var matches = new List<UIDocument>();
            foreach (UIDocument candidate in documents)
            {
                bool idMatch = int.TryParse(identifier, out int instanceId) && candidate.GetInstanceID() == instanceId;
                bool nameMatch = candidate.gameObject.name.Equals(identifier, StringComparison.Ordinal);
                bool pathMatch = HierarchyPath(candidate.transform).Equals(identifier, StringComparison.Ordinal);
                if (idMatch || nameMatch || pathMatch)
                    matches.Add(candidate);
            }
            if (matches.Count == 1)
            {
                document = matches[0];
                return true;
            }
            code = matches.Count == 0 ? "NOT_FOUND" : "AMBIGUOUS_DOCUMENT";
            IEnumerable<UIDocument> candidates;
            if (matches.Count == 0)
                candidates = documents;
            else
                candidates = matches;
            details = string.Join(" | ", candidates.Take(20).Select(item => $"{HierarchyPath(item.transform)} (instanceId {item.GetInstanceID()})"));
            return false;
        }

        static UIDocument FindDocumentByInstanceId(int instanceId)
        {
            return Resources.FindObjectsOfTypeAll<UIDocument>()
                .FirstOrDefault(item => item != null && !EditorUtility.IsPersistent(item) && item.GetInstanceID() == instanceId);
        }

        static bool TryResolveElement(
            VisualElement root,
            string selector,
            bool defaultToRoot,
            out VisualElement element,
            out string code,
            out string details)
        {
            element = null;
            code = null;
            details = null;
            if (string.IsNullOrWhiteSpace(selector))
            {
                if (defaultToRoot)
                {
                    element = root;
                    return true;
                }
                code = "INVALID_REQUEST";
                return false;
            }

            string trimmed = selector.Trim();
            Func<VisualElement, bool> match;
            if (trimmed.StartsWith("#", StringComparison.Ordinal) && trimmed.Length > 1)
                match = item => item.name == trimmed.Substring(1);
            else if (trimmed.StartsWith(".", StringComparison.Ordinal) && trimmed.Length > 1)
                match = item => item.ClassListContains(trimmed.Substring(1));
            else if (trimmed.IndexOfAny(new[] { ' ', '>', ':', '[', ']', ',' }) < 0)
                match = item => item.GetType().Name.Equals(trimmed, StringComparison.Ordinal);
            else
            {
                code = "INVALID_SELECTOR";
                details = "Only exact #name, .class, and type selectors are supported.";
                return false;
            }

            List<VisualElement> matches = Traverse(root, 1000).Where(match).ToList();
            if (matches.Count == 1)
            {
                element = matches[0];
                return true;
            }
            code = matches.Count == 0 ? "NOT_FOUND" : "AMBIGUOUS_SELECTOR";
            details = matches.Count == 0
                ? "No live element matched. Inspect the document snapshot to choose a stable selector."
                : string.Join(" | ", matches.Take(20).Select(DescribeTarget));
            return false;
        }

        static IEnumerable<VisualElement> Traverse(VisualElement root, int maximum)
        {
            var stack = new Stack<VisualElement>();
            stack.Push(root);
            int count = 0;
            while (stack.Count > 0 && count++ < maximum)
            {
                VisualElement element = stack.Pop();
                yield return element;
                for (int childIndex = element.childCount - 1; childIndex >= 0; childIndex--)
                    stack.Push(element[childIndex]);
            }
        }

        static void CaptureLog(string condition, string stackTrace, LogType type)
        {
            Logs.Add((++_logSequence, new UIToolkitPlaytestLog
            {
                type = type.ToString(),
                message = Truncate(condition, 1000),
                stackTrace = Truncate(stackTrace, 2000),
            }));
            if (Logs.Count > MaximumLogs)
                Logs.RemoveRange(0, Logs.Count - MaximumLogs);
        }

        static void PurgeExpiredRuns()
        {
            DateTime now = DateTime.UtcNow;
            foreach (string runId in Runs.Values.Where(run => run.ExpiresUtc <= now).Select(run => run.RunId).ToArray())
            {
                string screenshot = Runs[runId].Response?.evidence?.screenshot;
                Runs.Remove(runId);
                if (!string.IsNullOrEmpty(screenshot))
                    TryDeleteScreenshot(screenshot);
            }
        }

        static void TryDeleteScreenshot(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"[AI Unity MCP Server] Could not expire UI Toolkit playtest screenshot '{path}': {exception.Message}");
            }
        }

        static void FailRun(UIToolkitPlaytestRun run, string code, string message, string recovery, string details = "")
        {
            run.Response.ok = false;
            run.Response.status = "failed";
            run.Response.error = new UIToolkitPlaytestError
            {
                code = code,
                message = string.IsNullOrEmpty(details) ? message : message + " " + details,
                recovery = recovery,
            };
        }

        static bool TryParseRequest(string body, out UIToolkitPlaytestRequest request, out string error)
        {
            request = null;
            error = null;
            if (string.IsNullOrWhiteSpace(body))
            {
                error = "The request body is empty.";
                return false;
            }
            try
            {
                request = JsonUtility.FromJson<UIToolkitPlaytestRequest>(body);
            }
            catch (Exception exception)
            {
                error = $"The request JSON is malformed: {exception.Message}";
                return false;
            }
            if (request == null)
            {
                error = "The request JSON could not be parsed.";
                return false;
            }
            if (body.IndexOf("\"screenshot\"", StringComparison.Ordinal) < 0)
                request.screenshot = true;
            return true;
        }

        static bool IsSupportedAction(string action)
        {
            return action == "snapshot" || action == "click" || action == "set-text" || action == "set-toggle" || action == "focus";
        }

        static string HierarchyPath(Transform transform)
        {
            var segments = new Stack<string>();
            for (Transform current = transform; current != null; current = current.parent)
                segments.Push(current.name);
            return string.Join("/", segments);
        }

        static string DescribeTarget(VisualElement element)
        {
            string name = string.IsNullOrEmpty(element.name) ? "" : "#" + element.name;
            string classes = string.Join("", element.GetClasses().Select(item => "." + item));
            return element.GetType().Name + name + classes;
        }

        static List<string> BaseWarnings(string action)
        {
            var warnings = new List<string>
            {
                "Playtest interactions are semantic, programmatic UI Toolkit events; they are not real operating-system pointer or keyboard input.",
                "Real hover, pressed-state timing, controller navigation, and screen-reader behavior are not simulated.",
            };
            if (action == "snapshot")
                warnings.Add("A snapshot observes the current live visual tree and does not mutate UI state.");
            return warnings;
        }

        static float Finite(float value)
        {
            return float.IsNaN(value) || float.IsInfinity(value) ? 0f : value;
        }

        static string Truncate(string value, int maximum)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return value.Length <= maximum ? value : value.Substring(0, maximum);
        }

        [Serializable]
        sealed class ElementStateList
        {
            public List<UIToolkitElementState> items;
        }
    }
}
