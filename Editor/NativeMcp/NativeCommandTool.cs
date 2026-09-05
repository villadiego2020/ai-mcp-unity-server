#if AI_UNITY_MCP_NATIVE_ASSISTANT
using System;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Unity.AI.MCP.Editor.ToolRegistry;

namespace AIUnityMCPServer.NativeMcp
{
    public sealed class NativeCommandTool : IUnityMcpTool
    {
        readonly MCPCommandDefinition definition;
        readonly JObject inputSchema;

        public NativeCommandTool(MCPCommandDefinition definition)
        {
            this.definition = definition;
            inputSchema = definition.GetInputSchema();
        }

        public object GetInputSchema() => inputSchema;
        public object GetOutputSchema() => new JObject { ["type"] = "object", ["additionalProperties"] = true };

        public Task<object> ExecuteAsync(object parameters)
        {
            if (parameters != null && !(parameters is JObject))
                throw new ArgumentException("Tool parameters must be a JSON object.");
            if (!definition.TryNormalize(parameters as JObject ?? new JObject(), out var body, out string error))
                throw new ArgumentException(error);

            string json = MCPHandlers.DispatchFrom("Native", definition.Path, body.ToString(Formatting.None));
            var result = JToken.Parse(json);
            if (result is JObject response && (response["error"] != null || response.Value<bool?>("ok") == false))
                throw new InvalidOperationException(response["error"]?.ToString(Formatting.None) ?? "The dispatched command failed.");

            // Unity's registry projects success/message/data into structuredContent.
            // Keep the entire dispatcher result in data so no fields are lost.
            AddScreenshotDelivery(result);
            var envelope = new JObject { ["success"] = true, ["message"] = definition.ToolName + " completed.", ["data"] = result };
            return Task.FromResult<object>(envelope);
        }

        void AddScreenshotDelivery(JToken result)
        {
            if (!(result is JObject response)) return;
            string screenshot = definition.Command == "capture_screenshot" ? result.Value<string>("screenshot") :
                definition.Command == "uitk_playtest" && result.Value<string>("status") == "done" ?
                    result["evidence"]?.Value<string>("screenshot") : "";
            if (string.IsNullOrEmpty(screenshot)) return;
            var delivery = new JObject { ["mode"] = "local-file", ["path"] = screenshot, ["mimeType"] = "image/png", ["verified"] = false };
            response["imageDelivery"] = delivery;
            try
            {
                using (var stream = File.OpenRead(screenshot))
                {
                    byte[] signature = { 137, 80, 78, 71, 13, 10, 26, 10 };
                    foreach (byte expected in signature)
                        if (stream.ReadByte() != expected) throw new InvalidDataException("Screenshot file is not a PNG.");
                    delivery["bytes"] = stream.Length;
                }
                delivery["verified"] = true;
                // The supported Unity relay drops top-level image content blocks. Its data
                // contract preserves this local-file instruction without base64 IPC overhead.
                delivery["instruction"] = "Open this PNG with the client's local image viewer. For inline MCP image content, use the Node/TCP connection. The installed Native relay does not forward image blocks.";
            }
            catch (IOException exception) { delivery["error"] = exception.Message; }
            catch (UnauthorizedAccessException exception) { delivery["error"] = exception.Message; }
        }
    }
}
#endif
