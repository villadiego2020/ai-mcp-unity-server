using System;
using System.Reflection;
using UnityEngine;

namespace MCPBridge
{
    /// <summary>
    /// </summary>
    public static class HotReloadControl
    {
        public static bool IsRunning()
        {
            try
            {
                var t = FindType("SingularityGroup.HotReload.Editor.ServerHealthCheck");
                if (t == null) return false;
                object inst = t.GetProperty("I", BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
                           ?? t.GetProperty("instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                if (inst == null) return false;
                object healthy = inst.GetType().GetProperty("IsServerHealthy")?.GetValue(inst);
                return healthy is bool b && b;
            }
            catch { return false; }
        }

        public static bool Start(out string msg)
        {
            try
            {
                if (IsRunning()) { msg = "Hot Reload is already running"; return true; }
                var t = FindType("SingularityGroup.HotReload.Editor.Cli.HotReloadCli");
                if (t == null) { msg = "Hot Reload package not found (com.singularitygroup.hotreload)"; return false; }
                var m = t.GetMethod("StartAsync", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                if (m == null) { msg = "HotReloadCli.StartAsync() not found"; return false; }
                m.Invoke(null, null);
                msg = "Hot Reload start requested; allow approximately 2–5 seconds for the server to start";
                return true;
            }
            catch (Exception e) { msg = "Could not start Hot Reload: " + e.Message; return false; }
        }

        static Type FindType(string fullName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { var t = asm.GetType(fullName); if (t != null) return t; } catch { }
            }
            return null;
        }
    }
}
