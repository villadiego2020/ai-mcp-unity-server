using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace AIUnityMCPServer
{
    /// <summary>
    /// </summary>
    public static class ClaudeCliClient
    {
        public static async Task<ClaudeResponse> SendAsync(string prompt, List<ClaudeImage> images, CancellationToken token = default, string resumeSessionId = null, int role = 0)
        {
            string projectRoot = Application.dataPath.Replace("/Assets", "");
            string claudeCmd = EditorPrefs.GetString("AIUnityMCPServer_ClaudeCmd", "claude");
            string model = EditorPrefs.GetString("AIUnityMCPServer_CliModel", "claude-sonnet-4-6");
            string effort = EditorPrefs.GetString("AIUnityMCPServer_CliEffort", "medium");   // low|medium|high|max
            bool isWindows = Application.platform == RuntimePlatform.WindowsEditor;

            bool isSkillRun = prompt != null && prompt.TrimStart().StartsWith("/");
            bool bare = EditorPrefs.GetBool("AIUnityMCPServer_CliBare", true) && !isSkillRun;
            int maxTurns = EditorPrefs.GetInt("AIUnityMCPServer_CliMaxTurns", isSkillRun ? 32 : 30);
            bool useEffort = EditorPrefs.GetBool("AIUnityMCPServer_CliUseEffort", false);
            bool useFast   = EditorPrefs.GetBool("AIUnityMCPServer_CliFast", false);
            bool debug     = EditorPrefs.GetBool("AIUnityMCPServer_CliDebug", false);

            var tempFiles = new List<string>();
            string fullPrompt = prompt;
            if (images != null && images.Count > 0)
            {
                var sb = new StringBuilder(prompt);
                sb.Append("\n\nAttached images (read these files):");
                for (int i = 0; i < images.Count; i++)
                {
                    try
                    {
                        string tmp = Path.Combine(Path.GetTempPath(), $"AIUnityMCPServer_{Guid.NewGuid():N}.png");
                        File.WriteAllBytes(tmp, Convert.FromBase64String(images[i].Base64));
                        tempFiles.Add(tmp);
                        sb.Append($"\n- {tmp}");
                    }
                    catch { /* ignore image */ }
                }
                fullPrompt = sb.ToString();
            }

            string skillsBlock = "";
            try
            {
                string idx = SkillIndex.PromptIndex();
                if (!string.IsNullOrEmpty(idx))
                    skillsBlock = "\n=== SKILLS INDEX (local specialist playbooks; read a relevant SKILL.md before analysis: " +
                                  "[project] = <repo>/.claude/skills/<name>/SKILL.md · [user] = ~/.claude/skills/) ===\n" +
                                  idx + "\n";
            }
            catch { }

            string cliRoleHint = "\n[WORKFLOW] Complete the investigation first. Use Read/Grep on the real repository and open relevant SKILL.md files from the index before formatting the final answer.\n" +
                "[FORMAT] Put Header(Dev) and/or Header(Art) on standalone lines, with Dev first when both apply. Do not use the word Header elsewhere. " +
                "Inside each section, summarize findings by risk when there are at least two, then use numbered severity findings and finish with what to fix first. " +
                "Keep numbering continuous across headers and cross-reference one issue viewed from both roles. Send JSON commands alone without any header.\n";
            string promptWithRules = ClaudeAPIClient.BuildSystemPrompt(role, true) + skillsBlock + cliRoleHint + "\n\n=== User request ===\n" + fullPrompt;

            string sendText = string.IsNullOrEmpty(resumeSessionId)
                ? promptWithRules
                : ClaudeAPIClient.BuildBrainSection() + skillsBlock + cliRoleHint + "\n=== User request ===\n" + fullPrompt;

            try
            {
                LastSessionId = null;
                string output = await Task.Run(() => RunProcess(claudeCmd, projectRoot, sendText, model, isWindows, maxTurns, bare, resumeSessionId, effort, useEffort, useFast, debug, token));
                var resp = BuildResponse(output);
                resp.SessionId = LastSessionId;
                return resp;
            }
            catch (OperationCanceledException)
            {
                return new ClaudeResponse { Error = "Cancelled" };
            }
            catch (Exception e)
            {
                return new ClaudeResponse { Error = $"CLI error: {e.Message}\n(Check that Claude Code CLI is installed and signed in by running 'claude' in a terminal.)" };
            }
            finally
            {
                foreach (var f in tempFiles) { try { File.Delete(f); } catch { } }
            }
        }

        static volatile Process _activeProc;

        [UnityEditor.InitializeOnLoadMethod]
        static void HookReload()
        {
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload += KillActive;
        }

        public static void KillActive()
        {
            try { _activeProc?.Kill(); } catch { }
            _activeProc = null;
        }

        public static volatile string LastSessionId;
        public static volatile int LiveToolCalls;

        static string RunProcess(string claudeCmd, string workingDir, string prompt, string model, bool isWindows, int maxTurns, bool bare, string resumeSessionId, string effort, bool useEffort, bool useFast, bool debug, CancellationToken token)
        {
            var psi = new ProcessStartInfo
            {
                FileName = isWindows ? "cmd.exe" : "/bin/bash",
                WorkingDirectory = workingDir,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
            };

            string fastFlag = "";
            if (bare)
            {
                string emptyMcp = Path.Combine(Path.GetTempPath(), "AIUnityMCPServer_empty_mcp.json");
                try
                {
                    if (!File.Exists(emptyMcp))
                    {
                        File.WriteAllText(emptyMcp, "{\"mcpServers\":{}}");
                    }
                }
                catch (Exception exception)
                {
                    UnityEngine.Debug.LogWarning("[AI Unity MCP Server] Create isolated MCP config failed: " + exception.Message);
                }
                fastFlag = $" --strict-mcp-config --mcp-config \"{emptyMcp}\"";
            }
            string resumeFlag = string.IsNullOrEmpty(resumeSessionId) ? "" : $" --resume {resumeSessionId}";
            string extra = "";
            if (useEffort && !string.IsNullOrEmpty(effort)) extra += $" --effort {effort}";
            if (useFast) extra += fastFlag;
            string flags = $"-p --output-format stream-json --verbose --permission-mode bypassPermissions --model {model} --max-turns {maxTurns}{extra}{resumeFlag}";
            if (isWindows)
                psi.Arguments = $"/c {claudeCmd} {flags}";
            else
                psi.Arguments = $"-lc \"{claudeCmd} {flags}\"";

            using var proc = new Process { StartInfo = psi };
            var stderr = new StringBuilder();
            string resultLine = null;
            LiveOutputTokens = 0;
            LiveToolCalls = 0;
            long streamChars = 0;

            proc.OutputDataReceived += (s, e) =>
            {
                if (e.Data == null) return;
                string line = e.Data;

                var m = System.Text.RegularExpressions.Regex.Match(line, "\"output_tokens\"\\s*:\\s*(\\d+)");
                if (m.Success && int.TryParse(m.Groups[1].Value, out int tok) && tok > LiveOutputTokens)
                    LiveOutputTokens = tok;
                else
                {
                    var tm = System.Text.RegularExpressions.Regex.Match(line, "\"text\"\\s*:\\s*\"((?:\\\\.|[^\"\\\\])*)\"");
                    if (tm.Success)
                    {
                        streamChars += tm.Groups[1].Value.Length;
                        int est = (int)(streamChars / 4);
                        if (est > LiveOutputTokens) LiveOutputTokens = est;
                    }
                }
                if (line.IndexOf("tool_use", StringComparison.Ordinal) >= 0) LiveToolCalls++;

                if (debug) UnityEngine.Debug.Log("[AI Unity MCP Server stream] " + line);

                if (line.Contains("\"type\":\"result\"")) resultLine = line;
            };
            proc.ErrorDataReceived += (s, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

            proc.Start();
            _activeProc = proc;
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            using var reg = token.Register(() => { try { proc.Kill(); } catch { } });

            byte[] inBytes = new UTF8Encoding(false).GetBytes(prompt);
            proc.StandardInput.BaseStream.Write(inBytes, 0, inBytes.Length);
            proc.StandardInput.BaseStream.Flush();
            proc.StandardInput.Close();

            if (!proc.WaitForExit(300000))
            {
                try { proc.Kill(); } catch { }
                _activeProc = null;
                throw new Exception("Timeout after 300 seconds. The Unity operation may still have completed; inspect the scene. " +
                                    "For faster responses, select the Haiku model in Settings.");
            }
            _activeProc = null;

            token.ThrowIfCancellationRequested();

            string outText = resultLine != null ? ExtractJsonString(resultLine, "result") : null;
            if (string.IsNullOrEmpty(outText))
            {
                string err = stderr.ToString().Trim();
                throw new Exception(string.IsNullOrEmpty(err) ? "no output" : err);
            }

            if (resultLine != null)
            {
                var fm = System.Text.RegularExpressions.Regex.Match(resultLine, "\"output_tokens\"\\s*:\\s*(\\d+)");
                if (fm.Success && int.TryParse(fm.Groups[1].Value, out int finalTok))
                    LiveOutputTokens = finalTok;
                LastSessionId = ExtractJsonString(resultLine, "session_id");
            }
            return outText;
        }

        public static volatile int LiveOutputTokens;

        static string ExtractJsonString(string json, string key)
        {
            int k = json.IndexOf($"\"{key}\":\"");
            if (k < 0) return null;
            int start = k + key.Length + 4;
            var sb = new StringBuilder();
            for (int i = start; i < json.Length; i++)
            {
                char c = json[i];
                if (c == '\\' && i + 1 < json.Length)
                {
                    char n = json[++i];
                    sb.Append(n switch { 'n' => '\n', 't' => '\t', 'r' => '\0', '"' => '"', '\\' => '\\', _ => n });
                    continue;
                }
                if (c == '"') break;
                sb.Append(c);
            }
            return sb.ToString().Replace("\0", "");
        }

        static ClaudeResponse BuildResponse(string text)
        {
            int cmdStart = ClaudeAPIClient.FindRealCommandStart(text);
            if (cmdStart >= 0)
            {
                int cmdEnd = text.IndexOf('}', cmdStart) + 1;
                if (cmdEnd > cmdStart)
                {
                    string cmdJson = text.Substring(cmdStart, cmdEnd - cmdStart);
                    return new ClaudeResponse { Text = text, CommandJson = cmdJson };
                }
            }
            return new ClaudeResponse { Text = text };
        }
    }
}
