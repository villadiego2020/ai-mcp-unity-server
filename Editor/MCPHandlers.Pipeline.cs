using System;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Unity.Pipeline.Commands;

namespace AIUnityMCPServer
{
    public static partial class MCPHandlers
    {
        [CliCommand(
            "ai_mcp_list_commands",
            "List the AI Unity MCP Server dispatcher routes available in this Editor")]
        public static JToken ListPipelineCommands()
        {
            return new JObject
            {
                ["commands"] = new JArray(CommandPaths()),
                ["tools"] = new JArray(MCPCommandCatalog.Load().Select(command => new JObject
                {
                    ["name"] = command.ToolName,
                    ["command"] = command.Command,
                    ["path"] = command.Path,
                    ["description"] = command.Description,
                    ["inputSchema"] = command.GetInputSchema()
                })),
                ["writeCommandsAllowed"] = AllowWrites
            };
        }

        [CliCommand(
            "ai_mcp_dispatch",
            "Run one AI Unity MCP Server command through its existing dispatcher and safety gates")]
        public static JToken DispatchPipelineCommand(
            [CliArg("command", "Command name or route, such as ping or /scene/hierarchy")]
            string command,
            [CliArg("body", "JSON object passed to the command; defaults to an empty object")]
            string body = "{}")
        {
            if (string.IsNullOrWhiteSpace(command))
                throw new ArgumentException("command is required.", nameof(command));

            JObject request = ParsePipelineBody(body);
            string response = DispatchFrom("Pipeline", command.Trim(), request.ToString(Formatting.None));
            JToken result = ParsePipelineResponse(response);
            ThrowIfPipelineFailure(result);
            return result;
        }

        static JObject ParsePipelineBody(string body)
        {
            if (string.IsNullOrWhiteSpace(body))
                throw new ArgumentException("body must be a JSON object.", nameof(body));

            try
            {
                JToken parsed = JToken.Parse(body);
                if (parsed.Type != JTokenType.Object)
                    throw new ArgumentException("body must be a JSON object.", nameof(body));
                return (JObject)parsed;
            }
            catch (JsonReaderException)
            {
                throw new ArgumentException("body must be a valid JSON object.", nameof(body));
            }
        }

        static JToken ParsePipelineResponse(string response)
        {
            if (string.IsNullOrWhiteSpace(response))
                return JValue.CreateNull();

            try
            {
                return JToken.Parse(response);
            }
            catch (JsonReaderException)
            {
                throw new InvalidOperationException(
                    "The AI Unity MCP Server dispatcher returned invalid JSON.");
            }
        }

        static void ThrowIfPipelineFailure(JToken result)
        {
            if (!(result is JObject response))
                return;

            JToken error = response["error"];
            bool explicitlyFailed = response["ok"]?.Type == JTokenType.Boolean &&
                                    response.Value<bool>("ok") == false;
            if (error == null && !explicitlyFailed)
                return;

            throw new InvalidOperationException(BuildPipelineErrorMessage(error));
        }

        static string BuildPipelineErrorMessage(JToken error)
        {
            JObject structuredError = error as JObject;
            string code = structuredError?["code"]?.Value<string>();
            string message = error?.Type == JTokenType.String
                ? error.Value<string>()
                : structuredError?["message"]?.Value<string>();

            if (string.IsNullOrWhiteSpace(message))
                message = "The dispatched command failed.";

            string normalized = new string(message.Where(character => !char.IsControl(character)).ToArray());
            if (normalized.Length > 512)
                normalized = normalized.Substring(0, 512);

            return string.IsNullOrWhiteSpace(code)
                ? normalized
                : $"{code}: {normalized}";
        }
    }
}
