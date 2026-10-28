using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AIUnityMCPServer
{
    public static partial class MCPHandlers
    {
        static bool ManagedRequestRequiresWrite(string path, string body)
        {
            if (!path.StartsWith("/managed/", System.StringComparison.Ordinal)) return false;
            try { return ManagedSessionManager.RequiresWrites(path.Substring(9), JObject.Parse(body ?? "{}").Value<string>("action")); }
            catch (JsonException) { return true; }
        }

        static string ManagedRequest(string group, string body)
        {
            string activation = ManagedSessionQualification.ActivationReason;
            if (activation != null) return ManagedSessionManager.Error("MANAGED_QUALIFICATION_REQUIRED", activation).ToString(Formatting.None);
            string exclusion = ManagedSessionQualification.ExclusionReason(UnityEngine.Application.unityVersion);
            if (exclusion != null) return ManagedSessionManager.Error("UNITY_VERSION_NOT_QUALIFIED", exclusion).ToString(Formatting.None);
            ManagedSessionHost.Initialize();
            if (ManagedSessionHost.Manager == null) return ManagedSessionManager.Error("HOST_UNAVAILABLE", "Managed sessions require an interactive Windows Editor host.").ToString(Formatting.None);
            try { return ManagedSessionHost.Manager.Execute(group, JObject.Parse(body ?? "{}")).ToString(Formatting.None); }
            catch (JsonException exception) { return ManagedSessionManager.Error("INVALID_JSON", exception.Message).ToString(Formatting.None); }
        }
    }
}
