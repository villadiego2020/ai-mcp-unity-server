using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace AIUnityMCPServer
{
    internal static class MCPSetup
    {
        const string ServerName = "AIUnityMCPServer";
        const int SetupTimeoutMilliseconds = 120000;

        [Serializable]
        sealed class CodexRegistration
        {
            public string name = "";
            public CodexTransport transport = new CodexTransport();
        }

        [Serializable]
        sealed class CodexTransport
        {
            public string type = "";
            public string command = "";
            public string[] args = Array.Empty<string>();
        }

        sealed class CommandResult
        {
            public int ExitCode;
            public string Output = "";
            public string Error = "";
            public bool TimedOut;

            public bool Succeeded => !TimedOut && ExitCode == 0;
        }

        enum RegistrationState
        {
            Missing,
            Current,
            ManagedPrevious,
            Different,
            Unavailable,
        }

        [MenuItem("AI Unity MCP Server/Setup/Configure Codex")]
        static void ConfigureCodex()
        {
            if (!TryPrepareSetup(out MCPRuntimeCache.Plan runtimePlan))
            {
                return;
            }

            RegistrationState state = ReadRegistration(runtimePlan.ServerEntryPath, out string detail);
            if (state == RegistrationState.Current)
            {
                if (!EnsureRuntimeAndDependencies(runtimePlan))
                {
                    return;
                }

                MCPServer.EnsureMcpJsonForServerEntry(runtimePlan.ServerEntryPath);
                Debug.Log("[AI Unity MCP Server Setup] Runtime cache and Node dependencies are ready. Codex registration is unchanged.");
                return;
            }

            if (state == RegistrationState.Different)
            {
                Debug.LogWarning("[AI Unity MCP Server Setup] Codex already has a different 'AIUnityMCPServer' registration. No files changed. "
                               + "Use AI Unity MCP Server → Setup → Repair Codex Registration only if you want to replace it. "
                               + detail);
                return;
            }

            if (state == RegistrationState.Unavailable)
            {
                Debug.LogError("[AI Unity MCP Server Setup] Could not inspect the Codex MCP configuration. No files changed. " + detail);
                return;
            }

            if (!EnsureRuntimeAndDependencies(runtimePlan))
            {
                return;
            }

            if (state == RegistrationState.ManagedPrevious && !TryRemoveRegistration())
            {
                return;
            }

            CommandResult addResult = RunCodex("mcp", "add", ServerName, "--", "node", runtimePlan.ServerEntryPath);
            if (!addResult.Succeeded)
            {
                Debug.LogError("[AI Unity MCP Server Setup] Could not register with Codex: " + DescribeFailure(addResult));
                return;
            }

            MCPServer.EnsureMcpJsonForServerEntry(runtimePlan.ServerEntryPath);
            Debug.Log("[AI Unity MCP Server Setup] Codex configured globally. New Codex sessions can use unity_connection_status and unity_connect.");
        }

        [MenuItem("AI Unity MCP Server/Setup/Repair Codex Registration")]
        static void RepairCodexRegistration()
        {
            if (!TryPrepareSetup(out MCPRuntimeCache.Plan runtimePlan))
            {
                return;
            }

            RegistrationState state = ReadRegistration(runtimePlan.ServerEntryPath, out string detail);
            if (state == RegistrationState.Current)
            {
                if (!EnsureRuntimeAndDependencies(runtimePlan))
                {
                    return;
                }

                MCPServer.EnsureMcpJsonForServerEntry(runtimePlan.ServerEntryPath);
                Debug.Log("[AI Unity MCP Server Setup] Runtime cache and Node dependencies are ready. Codex registration is unchanged.");
                return;
            }

            if (state == RegistrationState.Unavailable)
            {
                Debug.LogError("[AI Unity MCP Server Setup] Repair stopped before changing config because Codex could not be inspected. " + detail);
                return;
            }

            if (state == RegistrationState.Different)
            {
                bool shouldReplace = EditorUtility.DisplayDialog(
                    "Repair AI Unity MCP Server Registration",
                    "Codex already has a different MCP server named 'AIUnityMCPServer'. Repair will replace that registration with this AI Unity MCP Server.\n\n" + detail,
                    "Replace Registration",
                    "Cancel");
                if (!shouldReplace)
                {
                    Debug.Log("[AI Unity MCP Server Setup] Repair cancelled. No config changed.");
                    return;
                }
            }

            if (!EnsureRuntimeAndDependencies(runtimePlan))
            {
                return;
            }

            if ((state == RegistrationState.Different || state == RegistrationState.ManagedPrevious)
                && !TryRemoveRegistration())
            {
                return;
            }

            CommandResult addResult = RunCodex("mcp", "add", ServerName, "--", "node", runtimePlan.ServerEntryPath);
            if (!addResult.Succeeded)
            {
                Debug.LogError("[AI Unity MCP Server Setup] Repair removed the previous registration but could not add the new one: "
                             + DescribeFailure(addResult)
                             + $". Run manually: codex mcp add {ServerName} -- node \"{runtimePlan.ServerEntryPath}\"");
                return;
            }

            MCPServer.EnsureMcpJsonForServerEntry(runtimePlan.ServerEntryPath);
            Debug.Log("[AI Unity MCP Server Setup] Codex registration repaired. Restart the Codex session to refresh its tool list.");
        }

        [MenuItem("AI Unity MCP Server/Setup/Doctor")]
        static void RunDoctor()
        {
            CommandResult node = RunCommand("node", new[] { "--version" }, 10000);
            CommandResult codex = RunCommand("codex", new[] { "--version" }, 10000);
            bool hasRuntimePlan = MCPRuntimeCache.TryCreatePlan(out MCPRuntimeCache.Plan runtimePlan, out string planFailure);
            string registrationDetail = "Codex CLI is unavailable";
            RegistrationState registration = codex.Succeeded && hasRuntimePlan
                ? ReadRegistration(runtimePlan.ServerEntryPath, out registrationDetail)
                : RegistrationState.Unavailable;

            var report = new StringBuilder();
            report.AppendLine("[AI Unity MCP Server Doctor]");
            AppendCheck(report, hasRuntimePlan
                ? "PASS Package bridge source is complete"
                : "FAIL Package bridge source: " + planFailure);
            AppendCheck(report, MCPPackagePaths.IsImmutableInstall()
                ? "PASS Git/registry package uses the per-user runtime cache"
                : "PASS Editable package uses the per-user runtime cache");

            if (hasRuntimePlan)
            {
                MCPRuntimeCache.CacheState cacheState = MCPRuntimeCache.Inspect(runtimePlan, out string cacheDetail);
                AppendCheck(report, cacheState == MCPRuntimeCache.CacheState.Ready
                    ? "PASS Runtime bootstrap ready: " + runtimePlan.RuntimeDirectory
                    : "FAIL Runtime bootstrap " + cacheState.ToString().ToLowerInvariant() + ": " + cacheDetail
                      + " Run AI Unity MCP Server → Setup → Configure Codex.");
                AppendCheck(report, cacheState == MCPRuntimeCache.CacheState.Ready && MCPRuntimeCache.DependenciesReady(runtimePlan)
                    ? "PASS Runtime Node dependencies ready"
                    : cacheState == MCPRuntimeCache.CacheState.Ready
                        ? "FAIL Runtime dependencies missing. Configure Codex installs them with npm."
                        : "BLOCKED Runtime dependencies cannot be checked until bootstrap succeeds.");
            }
            else
            {
                AppendCheck(report, "BLOCKED Runtime bootstrap plan is unavailable.");
                AppendCheck(report, "BLOCKED Runtime dependencies cannot be checked.");
            }

            AppendCheck(report, node.Succeeded ? "PASS Node available: " + FirstLine(node.Output) : "FAIL " + DescribeFailure(node));
            AppendCheck(report, codex.Succeeded ? "PASS Codex available: " + FirstLine(codex.Output) : "FAIL " + DescribeFailure(codex));

            string registrationMessage = registration switch
            {
                RegistrationState.Current => "Codex registration points to this bridge",
                RegistrationState.Missing => "Codex registration is missing; run AI Unity MCP Server → Setup → Configure Codex",
                RegistrationState.ManagedPrevious => "Codex registration points to an older managed cache; run Configure Codex to update it",
                RegistrationState.Different => "Codex registration uses another path; run Repair only if replacement is intended",
                _ => "Codex registration could not be inspected",
            };
            AppendCheck(report, (registration == RegistrationState.Current ? "PASS " : "FAIL ") + registrationMessage);
            AppendCheck(report, MCPServer.IsRunning
                ? $"PASS AI Unity MCP Server online at port {MCPServer.Port}"
                : "FAIL Use unity_connect or AI Unity MCP Server → Server → Start");
            report.AppendLine($"  INFO instanceId={MCPServer.InstanceId} label={MCPServer.Label} autoStartReadOnly={MCPServer.IsAutoStartEnabled} writeGate={(MCPHandlers.AllowWrites ? "ON" : "OFF")}");

            if (codex.Succeeded && registration != RegistrationState.Current && !string.IsNullOrEmpty(registrationDetail))
            {
                report.AppendLine("  INFO " + registrationDetail);
            }

            Debug.Log(report.ToString().TrimEnd());
        }

        static bool TryPrepareSetup(out MCPRuntimeCache.Plan runtimePlan)
        {
            if (!MCPRuntimeCache.TryCreatePlan(out runtimePlan, out string planFailure))
            {
                Debug.LogError("[AI Unity MCP Server Setup] Runtime bootstrap cannot start. No config was changed. " + planFailure);
                return false;
            }

            CommandResult node = RunCommand("node", new[] { "--version" }, 10000);
            if (!node.Succeeded)
            {
                Debug.LogError("[AI Unity MCP Server Setup] Node.js 18+ is required. No config was changed. " + DescribeFailure(node));
                return false;
            }

            CommandResult codex = RunCommand("codex", new[] { "--version" }, 10000);
            if (!codex.Succeeded)
            {
                Debug.LogError("[AI Unity MCP Server Setup] Codex CLI is required. No config was changed. " + DescribeFailure(codex));
                return false;
            }

            return true;
        }

        static bool EnsureRuntimeAndDependencies(MCPRuntimeCache.Plan runtimePlan)
        {
            if (!MCPRuntimeCache.TryEnsure(runtimePlan, out bool cacheChanged, out string bootstrapFailure))
            {
                Debug.LogError("[AI Unity MCP Server Setup] Runtime bootstrap failed. No config was changed. " + bootstrapFailure);
                return false;
            }

            if (cacheChanged)
            {
                Debug.Log("[AI Unity MCP Server Setup] Bootstrapped versioned runtime cache at " + runtimePlan.RuntimeDirectory);
            }

            return EnsureNodeDependencies(runtimePlan);
        }

        static bool EnsureNodeDependencies(MCPRuntimeCache.Plan runtimePlan)
        {
            if (MCPRuntimeCache.DependenciesReady(runtimePlan))
            {
                return true;
            }

            string lockPath = Path.Combine(runtimePlan.RuntimeDirectory, ".dependency-install.lock");
            try
            {
                using (new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                {
                    return MCPRuntimeCache.DependenciesReady(runtimePlan) || InstallNodeDependencies(runtimePlan);
                }
            }
            catch (IOException exception)
            {
                Debug.LogError("[AI Unity MCP Server Setup] Runtime dependencies are being installed by another Unity project, or the cache is locked. Retry Configure Codex. " + exception.Message);
                return false;
            }
            catch (UnauthorizedAccessException exception)
            {
                Debug.LogError("[AI Unity MCP Server Setup] Runtime dependency cache is not writable. No config was changed. " + exception.Message);
                return false;
            }
        }

        static bool InstallNodeDependencies(MCPRuntimeCache.Plan runtimePlan)
        {
            Debug.Log("[AI Unity MCP Server Setup] Installing Node bridge dependencies in the per-user runtime cache...");
            CommandResult install = RunCommand(
                "npm",
                new[]
                {
                    "ci",
                    "--omit=dev",
                    "--ignore-scripts",
                    "--no-audit",
                    "--no-fund",
                    "--prefix",
                    runtimePlan.RuntimeDirectory,
                },
                SetupTimeoutMilliseconds);
            if (install.Succeeded && MCPRuntimeCache.DependenciesReady(runtimePlan))
            {
                return true;
            }

            Debug.LogError("[AI Unity MCP Server Setup] Runtime dependency installation failed. No config was changed. "
                         + DescribeFailure(install)
                         + $" Retry while online or run npm ci --omit=dev --ignore-scripts --prefix \"{runtimePlan.RuntimeDirectory}\".");
            return false;
        }

        static RegistrationState ReadRegistration(string expectedServerEntry, out string detail)
        {
            CommandResult result = RunCodex("mcp", "get", ServerName, "--json");
            if (!result.Succeeded)
            {
                detail = DescribeFailure(result);
                return result.Error.IndexOf("No MCP server named", StringComparison.OrdinalIgnoreCase) >= 0
                    ? RegistrationState.Missing
                    : RegistrationState.Unavailable;
            }

            try
            {
                CodexRegistration registration = JsonUtility.FromJson<CodexRegistration>(result.Output);
                if (registration == null || registration.transport == null)
                {
                    detail = "Codex returned an unreadable registration.";
                    return RegistrationState.Unavailable;
                }

                bool usesNode = string.Equals(Path.GetFileNameWithoutExtension(registration.transport.command), "node", StringComparison.OrdinalIgnoreCase);
                bool hasOneArgument = registration.transport.args != null && registration.transport.args.Length == 1;
                bool samePath = hasOneArgument && PathsEqual(registration.transport.args[0], expectedServerEntry);
                detail = $"configured command={registration.transport.command} args={string.Join(", ", registration.transport.args ?? Array.Empty<string>())}";
                if (usesNode && samePath)
                {
                    return RegistrationState.Current;
                }

                return usesNode && hasOneArgument && MCPRuntimeCache.IsManagedServerEntry(registration.transport.args[0])
                    ? RegistrationState.ManagedPrevious
                    : RegistrationState.Different;
            }
            catch (Exception exception)
            {
                detail = "Codex registration JSON could not be parsed: " + exception.Message;
                return RegistrationState.Unavailable;
            }
        }

        static bool PathsEqual(string left, string right)
        {
            if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right))
            {
                return false;
            }

            string normalizedLeft = Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string normalizedRight = Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            StringComparison comparison = Application.platform == RuntimePlatform.WindowsEditor
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            return string.Equals(normalizedLeft, normalizedRight, comparison);
        }

        static bool TryRemoveRegistration()
        {
            CommandResult removeResult = RunCodex("mcp", "remove", ServerName);
            if (removeResult.Succeeded)
            {
                return true;
            }

            Debug.LogError("[AI Unity MCP Server Setup] Could not remove the previous registration: " + DescribeFailure(removeResult));
            return false;
        }

        static void AppendCheck(StringBuilder report, string result)
        {
            report.AppendLine("  " + result);
        }

        static string FirstLine(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "unknown";
            }

            int newline = value.IndexOfAny(new[] { '\r', '\n' });
            return (newline < 0 ? value : value.Substring(0, newline)).Trim();
        }

        static string DescribeFailure(CommandResult result)
        {
            if (result.TimedOut)
            {
                return "command timed out";
            }

            string message = string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error;
            return $"exit {result.ExitCode}: {FirstLine(message)}";
        }

        static CommandResult RunCodex(params string[] arguments) =>
            RunCommand("codex", arguments, SetupTimeoutMilliseconds);

        static CommandResult RunCommand(string executable, IReadOnlyList<string> arguments, int timeoutMilliseconds)
        {
            var result = new CommandResult { ExitCode = -1 };
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = ExecutableName(executable),
                    Arguments = BuildArguments(arguments),
                    WorkingDirectory = MCPPackagePaths.ProjectRoot(),
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };

                using (var process = Process.Start(startInfo))
                {
                    if (process == null)
                    {
                        result.Error = $"could not start {executable}";
                        return result;
                    }

                    var outputTask = process.StandardOutput.ReadToEndAsync();
                    var errorTask = process.StandardError.ReadToEndAsync();
                    if (!process.WaitForExit(timeoutMilliseconds))
                    {
                        result.TimedOut = true;
                        TryStopTimedOutProcess(process, executable);
                    }
                    else
                    {
                        result.ExitCode = process.ExitCode;
                    }

                    result.Output = outputTask.GetAwaiter().GetResult().Trim();
                    result.Error = errorTask.GetAwaiter().GetResult().Trim();
                }
            }
            catch (Exception exception)
            {
                result.Error = exception.Message;
            }

            return result;
        }

        static string ExecutableName(string executable)
        {
            if (Application.platform == RuntimePlatform.WindowsEditor
                && string.Equals(executable, "npm", StringComparison.OrdinalIgnoreCase))
            {
                return "npm.cmd";
            }

            return executable;
        }

        static void TryStopTimedOutProcess(Process process, string executable)
        {
            try
            {
                process.Kill();
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"[AI Unity MCP Server Setup] Could not stop {executable} after timeout: {exception.Message}");
            }
        }

        static string BuildArguments(IReadOnlyList<string> arguments)
        {
            var commandLine = new StringBuilder();
            for (int index = 0; index < arguments.Count; index++)
            {
                if (index > 0)
                {
                    commandLine.Append(' ');
                }

                commandLine.Append(QuoteArgument(arguments[index]));
            }

            return commandLine.ToString();
        }

        static string QuoteArgument(string argument)
        {
            if (string.IsNullOrEmpty(argument))
            {
                return "\"\"";
            }

            if (argument.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '\"' }) < 0)
            {
                return argument;
            }

            var quoted = new StringBuilder("\"");
            int backslashes = 0;
            foreach (char character in argument)
            {
                if (character == '\\')
                {
                    backslashes++;
                    continue;
                }

                if (character == '\"')
                {
                    quoted.Append('\\', backslashes * 2 + 1);
                    quoted.Append('\"');
                    backslashes = 0;
                    continue;
                }

                quoted.Append('\\', backslashes);
                backslashes = 0;
                quoted.Append(character);
            }

            quoted.Append('\\', backslashes * 2);
            quoted.Append('\"');
            return quoted.ToString();
        }
    }
}
