using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

namespace AIUnityMCPServer.Runtime
{
    [Serializable] internal sealed class ManagedRunConfiguration
    {
        public int schemaVersion, frameRate;
        public string runId, owner, mailbox, capability, saveDirectory;
    }
    [Serializable] internal sealed class ManagedPlayerCommand
    {
        public int schemaVersion;
        public string id, action, capability, name, value, reason;
        public long deadline;
    }
    [Serializable] internal sealed class ManagedPlayerResult
    {
        public string state = "done", commandId, runId, value, error, screenshot;
        public int frame;
    }
    [Serializable] internal sealed class ManagedPlayerCapabilities
    {
        public bool capture = true, quit = true;
        public string input = "unsupported", storage = "adapter-opt-in; PlayerPrefs and external services are not redirected";
        public string[] supportedInputs = new string[0];
    }
    [Serializable] internal sealed class ManagedReady
    {
        public int schemaVersion = 1;
        public string runId;
        public ManagedPlayerCapabilities capabilities;
    }

    internal sealed class ManagedPlayerCompanion : MonoBehaviour
    {
        ManagedRunConfiguration configuration;
        float nextPoll;
        bool capturing, quitting;
        ManagedSupervisorLease supervisorLease;
        float nextHeartbeatWarning;
        static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            if (Application.isEditor) return;
            string[] arguments = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(arguments, "-aiMcpRunConfig");
            if (index < 0 || index + 1 >= arguments.Length) return;
            try
            {
                string path = Path.GetFullPath(arguments[index + 1]);
                if (!File.Exists(path) || new FileInfo(path).Length > 8192) throw new InvalidDataException("Invalid managed run configuration.");
                var config = JsonUtility.FromJson<ManagedRunConfiguration>(File.ReadAllText(path));
                if (config == null || config.schemaVersion != 1 || string.IsNullOrEmpty(config.capability) || config.capability.Length != 64 ||
                    string.IsNullOrEmpty(config.runId) || Path.GetFullPath(config.mailbox) != Path.GetDirectoryName(path)) throw new InvalidDataException("Invalid managed run identity.");
                ManagedPlayerApi.SaveDirectory = Path.GetFullPath(config.saveDirectory);
                Directory.CreateDirectory(ManagedPlayerApi.SaveDirectory);
                var host = new GameObject("AI Unity MCP Server Managed Player");
                DontDestroyOnLoad(host);
                host.AddComponent<ManagedPlayerCompanion>().Initialize(config);
            }
            catch (Exception exception)
            {
                Debug.LogError("[AI Unity MCP Server] Managed Player bootstrap failed: " + exception.Message);
                Application.Quit(2);
            }
        }

        void Initialize(ManagedRunConfiguration config)
        {
            configuration = config;
            supervisorLease = new ManagedSupervisorLease(Now);
            Application.runInBackground = true;
            Application.targetFrameRate = Math.Max(10, Math.Min(60, config.frameRate));
            var adapter = ManagedPlayerApi.InputAdapter;
            Write(Path.Combine(config.mailbox, "ready.json"), JsonUtility.ToJson(new ManagedReady
            {
                runId = config.runId, capabilities = new ManagedPlayerCapabilities
                { input = adapter == null ? "unsupported" : "semantic-adapter", supportedInputs = adapter?.SupportedInputs ?? new string[0] }
            }));
        }

        void Update()
        {
            if (configuration == null || quitting || Time.realtimeSinceStartup < nextPoll) return;
            nextPoll = Time.realtimeSinceStartup + 0.25f;
            try
            {
                bool valid = ReadSupervisorHeartbeat(out long supervisor, out string failure);
                supervisorLease.Observe(valid ? (long?)supervisor : null);
                if (supervisorLease.IsExpired(Now)) { Quit("supervisor_lost"); return; }
                if (!valid && Time.realtimeSinceStartup >= nextHeartbeatWarning)
                {
                    nextHeartbeatWarning = Time.realtimeSinceStartup + 30;
                    Debug.LogWarning("[AI Unity MCP Server] Supervisor heartbeat temporarily unavailable; retaining the last valid lease: " + failure);
                }
                Write(Path.Combine(configuration.mailbox, "player-heartbeat"), Now.ToString());
                string close = Path.Combine(configuration.mailbox, "close.request.json");
                if (File.Exists(close))
                {
                    var request = Read(close);
                    if (Matches(request.capability)) { File.Delete(close); Quit(request.reason ?? "owner_closed"); return; }
                }
                if (capturing) return;
                foreach (string path in Directory.EnumerateFiles(configuration.mailbox, "command-*.request.json").OrderBy(item => item, StringComparer.Ordinal).Take(4))
                {
                    ManagedPlayerCommand command;
                    try { command = Read(path); }
                    catch (Exception exception)
                    {
                        File.Delete(path);
                        Finish(new ManagedPlayerCommand { id = Path.GetFileName(path).Replace(".request.json", "") }, error: "INVALID_COMMAND: " + exception.Message);
                        continue;
                    }
                    File.Delete(path);
                    if (!Matches(command.capability) || command.schemaVersion != 1 || string.IsNullOrEmpty(command.id) || command.id != Path.GetFileName(path).Replace(".request.json", "")) continue;
                    if (command.deadline < Now) { Finish(command, error: "COMMAND_TIMEOUT: request expired without execution."); continue; }
                    if (command.action == "capture") { capturing = true; StartCoroutine(Capture(command)); break; }
                    if (command.action != "input") { Finish(command, error: "UNSUPPORTED_ACTION"); continue; }
                    var adapter = ManagedPlayerApi.InputAdapter;
                    if (adapter == null || !(adapter.SupportedInputs ?? new string[0]).Contains(command.name))
                    { Finish(command, error: "INPUT_UNSUPPORTED: the game has not declared this semantic input."); continue; }
                    try { Finish(command, value: adapter.Execute(command.name, command.value)); }
                    catch (Exception exception) { Finish(command, error: exception.Message); }
                }
            }
            catch (Exception exception) { Debug.LogError("[AI Unity MCP Server] Managed Player command failed: " + exception.Message); }
        }

        ManagedPlayerCommand Read(string path)
        {
            if (new FileInfo(path).Length > 65536) throw new InvalidDataException("Managed command exceeds 64 KiB.");
            return JsonUtility.FromJson<ManagedPlayerCommand>(File.ReadAllText(path)) ?? throw new InvalidDataException("Managed command is missing.");
        }

        bool ReadSupervisorHeartbeat(out long timestamp, out string failure)
        {
            timestamp = 0;
            failure = "invalid timestamp";
            try
            {
                string path = Path.Combine(configuration.mailbox, "supervisor-heartbeat");
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (stream.Length > 64) { failure = "invalid timestamp size"; return false; }
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                        return long.TryParse(reader.ReadToEnd(), out timestamp) && timestamp > 0;
                }
            }
            catch (IOException exception) { failure = exception.Message; return false; }
            catch (UnauthorizedAccessException exception) { failure = exception.Message; return false; }
        }
        bool Matches(string value)
        {
            if (value == null || value.Length != configuration.capability.Length) return false;
            int difference = 0;
            for (int index = 0; index < value.Length; index++) difference |= value[index] ^ configuration.capability[index];
            return difference == 0;
        }

        IEnumerator Capture(ManagedPlayerCommand command)
        {
            yield return new WaitForEndOfFrame();
            Texture2D texture = null;
            try
            {
                texture = ScreenCapture.CaptureScreenshotAsTexture();
                if (texture == null) throw new InvalidOperationException("Player frame capture returned no texture.");
                byte[] bytes = texture.EncodeToPNG();
                if (bytes.Length > 16 * 1024 * 1024) throw new InvalidDataException("Captured PNG exceeds 16 MiB.");
                string path = Path.Combine(configuration.mailbox, command.id + ".png");
                File.WriteAllBytes(path, bytes);
                Finish(command, screenshot: path);
            }
            catch (Exception exception) { Finish(command, error: exception.Message); }
            finally { if (texture != null) Destroy(texture); capturing = false; }
        }

        void Finish(ManagedPlayerCommand command, string value = null, string error = null, string screenshot = null)
        {
            if (value != null && value.Length > 32768) { value = null; error = "Adapter result exceeds 32 KiB."; }
            Write(Path.Combine(configuration.mailbox, command.id + ".result.json"), JsonUtility.ToJson(new ManagedPlayerResult
            { commandId = command.id, runId = configuration.runId, value = value, error = error, screenshot = screenshot, frame = Time.frameCount }));
        }
        void Quit(string reason)
        {
            quitting = true;
            Write(Path.Combine(configuration.mailbox, "exit.json"), "{\"reason\":\"" + reason.Replace("\"", "") + "\"}");
            Application.Quit();
        }
        void OnApplicationQuit()
        {
            if (configuration != null && !quitting) Write(Path.Combine(configuration.mailbox, "exit.json"), "{\"reason\":\"window_closed\"}");
        }
        void OnGUI()
        {
            if (configuration == null) return;
            // Depth belongs to this GUI behaviour; restoring it removes its foreground priority.
            GUI.depth = -10000;
            var badge = new Rect(8, 8, Math.Min(Screen.width - 16, 620), 44);
            Color previousColor = GUI.color;
            try
            {
                GUI.color = Color.white;
                // The built-in black texture has zero alpha; disable blending for an opaque badge.
                GUI.DrawTexture(badge, Texture2D.blackTexture, ScaleMode.StretchToFill, false);
                GUI.Box(badge, configuration.owner + "\n" + configuration.runId);
            }
            finally { GUI.color = previousColor; }
        }
        static void Write(string path, string value)
        {
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, value, new UTF8Encoding(false));
            try
            {
                for (int attempt = 1; ; attempt++)
                {
                    try
                    {
                        if (File.Exists(path)) File.Replace(temporary, path, null);
                        else File.Move(temporary, path);
                        break;
                    }
                    catch (Exception exception) when ((exception is IOException || exception is UnauthorizedAccessException) && attempt < 3)
                    { System.Threading.Thread.Sleep(5 * attempt); }
                }
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
