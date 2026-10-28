using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;

namespace AIUnityMCPServer
{
    public static class ManagedPlayerBuildWorker
    {
        // Executed only in an explicitly isolated batch project.
        public static void Run()
        {
            string resultPath = null;
            string revision = null;
            try
            {
                if (!UnityEngine.Application.isBatchMode) throw new InvalidOperationException("Managed builds require an isolated batch worker.");
                string[] arguments = Environment.GetCommandLineArgs();
                int index = Array.IndexOf(arguments, "-aiMcpBuildConfig");
                if (index < 0 || index + 1 >= arguments.Length) throw new ArgumentException("Managed build configuration is missing.");
                string configurationPath = Path.GetFullPath(arguments[index + 1]);
                var configuration = JObject.Parse(File.ReadAllText(configurationPath));
                resultPath = configuration.Value<string>("resultPath");
                revision = configuration.Value<string>("revision");
                string project = Path.GetFullPath(Path.Combine(UnityEngine.Application.dataPath, ".."));
                if (Path.GetDirectoryName(project) != Path.GetDirectoryName(configurationPath) || Path.GetFileName(project) != "project")
                    throw new InvalidOperationException("Build worker project identity does not match its private configuration.");
                if (configuration.Value<string>("unityVersion") != UnityEngine.Application.unityVersion) throw new InvalidOperationException("Build worker Unity version mismatch.");
                string output = configuration.Value<string>("output");
                string[] scenes = EditorBuildSettings.scenes.Where(scene => scene.enabled).Select(scene => scene.path).ToArray();
                if (scenes.Length == 0) throw new InvalidOperationException("No saved enabled build scenes are configured in this private project.");
                Directory.CreateDirectory(Path.GetDirectoryName(output));
                var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions { scenes = scenes, locationPathName = output, target = BuildTarget.StandaloneWindows64, options = BuildOptions.Development });
                bool success = report.summary.result == BuildResult.Succeeded && File.Exists(output);
                ManagedSessionFiles.WriteAtomic(resultPath, new JObject { ["success"] = success, ["revision"] = revision,
                    ["error"] = success ? "" : "BuildPipeline result: " + report.summary.result }.ToString());
                EditorApplication.Exit(success ? 0 : 2);
            }
            catch (Exception exception)
            {
                if (!string.IsNullOrEmpty(resultPath)) ManagedSessionFiles.WriteAtomic(resultPath, new JObject { ["success"] = false, ["revision"] = revision, ["error"] = exception.Message }.ToString());
                UnityEngine.Debug.LogError("[AI Unity MCP Server] Managed build failed: " + exception.Message);
                EditorApplication.Exit(2);
            }
        }
    }
}
