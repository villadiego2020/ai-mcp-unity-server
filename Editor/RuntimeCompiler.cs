using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace MCPBridge
{
    /// <summary>
    ///
    ///
    /// </summary>
    public static class RuntimeCompiler
    {
        public static bool CompileAndRun(string code, out string log)
        {
            var sb = new StringBuilder();
            try
            {
                string tmpDir = Path.Combine(Path.GetTempPath(), "AIUnityMCPServer_LiveCode");
                Directory.CreateDirectory(tmpDir);
                string srcFile = Path.Combine(tmpDir, "LiveCode.cs");
                string outDll  = Path.Combine(tmpDir, $"LiveCode_{DateTime.Now.Ticks}.dll");
                File.WriteAllText(srcFile, code, new UTF8Encoding(false));

                var refs = AppDomain.CurrentDomain.GetAssemblies()
                    .Where(a => !a.IsDynamic)
                    .Select(a => { try { return a.Location; } catch { return null; } })
                    .Where(p => !string.IsNullOrEmpty(p) && File.Exists(p))
                    .Distinct()
                    .ToList();

                string rsp = Path.Combine(tmpDir, "args.rsp");
                var args = new StringBuilder();
                args.AppendLine("-target:library");
                args.AppendLine("-langversion:latest");
                args.AppendLine("-nologo");
                args.AppendLine($"-out:\"{outDll}\"");
                foreach (var r in refs) args.AppendLine($"-r:\"{r}\"");
                args.AppendLine($"\"{srcFile}\"");
                File.WriteAllText(rsp, args.ToString());

                if (!FindCompiler(out string exe, out string preArg))
                {
                    log = "Roslyn csc was not found in the Unity installation. Provide the Editor path to improve detection.";
                    return false;
                }

                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = (string.IsNullOrEmpty(preArg) ? "" : $"\"{preArg}\" ") + $"@\"{rsp}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                using var proc = Process.Start(psi);
                string stdout = proc.StandardOutput.ReadToEnd();
                string stderr = proc.StandardError.ReadToEnd();
                proc.WaitForExit(30000);

                if (!File.Exists(outDll))
                {
                    log = "Compilation failed:\n" + stdout + "\n" + stderr;
                    return false;
                }

                byte[] bytes = File.ReadAllBytes(outDll);
                var asm = Assembly.Load(bytes);
                int ran = RunAssembly(asm, sb);

                if (ran == 0) sb.AppendLine("⚠ Assembly loaded, but no entry point ran. Provide a MonoBehaviour or `public static void Run()`.");
                else sb.Insert(0, $"✅ Compiled and loaded successfully; ran {ran} entry points.\n");
                log = sb.ToString();
                return true;
            }
            catch (Exception e)
            {
                log = "error: " + e;
                return false;
            }
        }

        static int RunAssembly(Assembly asm, StringBuilder sb)
        {
            int ran = 0;
            foreach (var type in asm.GetTypes())
            {
                if (typeof(MonoBehaviour).IsAssignableFrom(type))
                {
                    var go = new GameObject("LiveCode_" + type.Name);
                    go.AddComponent(type);
                    sb.AppendLine($"  • attached MonoBehaviour '{type.Name}' to a GameObject in the live scene");
                    ran++;
                }
                else
                {
                    var run = type.GetMethod("Run", BindingFlags.Public | BindingFlags.Static);
                    if (run != null && run.GetParameters().Length == 0)
                    {
                        object result = run.Invoke(null, null);
                        sb.AppendLine($"  • {type.Name}.Run() → {result ?? "void"}");
                        ran++;
                    }
                }
            }
            return ran;
        }

        static bool FindCompiler(out string exe, out string preArg)
        {
            exe = null; preArg = null;
            string root = EditorApplication.applicationContentsPath;   // .../Editor/Data

            string cscExe = Path.Combine(root, "Tools", "Roslyn", "csc.exe");
            if (File.Exists(cscExe)) { exe = cscExe; preArg = null; return true; }

            string cscDll = Path.Combine(root, "DotNetSdkRoslyn", "csc.dll");
            if (File.Exists(cscDll))
            {
                foreach (var dn in new[]
                {
                    Path.Combine(root, "NetCoreRuntime", "dotnet.exe"),
                    Path.Combine(root, "Tools", "netcorerun", "netcorerun.exe"),
                })
                {
                    if (File.Exists(dn)) { exe = dn; preArg = cscDll; return true; }
                }
                exe = "dotnet"; preArg = cscDll; return true;
            }
            return false;
        }
    }

}
