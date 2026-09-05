using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace AIUnityMCPServer
{
    /// <summary>Shared manifest contract for transports that execute inside the Editor.</summary>
    public static class MCPCommandCatalog
    {
        public static IReadOnlyList<MCPCommandDefinition> Load()
        {
            string json = File.ReadAllText(MCPPackagePaths.CommandManifestPath());
            var commands = JObject.Parse(json)["commands"] as JArray;
            if (commands == null) throw new InvalidDataException("commands.json must contain a commands array.");
            var definitions = commands.Cast<JObject>().Select(entry => new MCPCommandDefinition(entry)).ToArray();
            if (definitions.Select(entry => entry.ToolName).Distinct(StringComparer.Ordinal).Count() != definitions.Length)
                throw new InvalidDataException("commands.json contains duplicate tool names.");
            return definitions;
        }
    }

    public sealed class MCPCommandDefinition
    {
        readonly JObject parameters;
        public string ToolName { get; }
        public string Command { get; }
        public string Path { get; }
        public string Description { get; }

        public MCPCommandDefinition(JObject entry)
        {
            ToolName = RequiredText(entry, "tool");
            Command = RequiredText(entry, "command");
            Path = RequiredText(entry, "path");
            Description = RequiredText(entry, "description");
            parameters = (JObject)(entry["params"] ?? new JObject()).DeepClone();
        }

        public JObject GetInputSchema()
        {
            var properties = new JObject();
            var required = new JArray();
            foreach (var parameter in parameters.Properties())
            {
                var definition = (JObject)parameter.Value;
                properties[parameter.Name] = ParameterSchema(definition);
                if (definition["default"] == null && definition.Value<bool?>("opt") != true)
                    required.Add(parameter.Name);
            }
            // Node's Zod object strips unknown input keys. The native normalizer does the same.
            return new JObject { ["type"] = "object", ["properties"] = properties, ["required"] = required };
        }

        public bool TryNormalize(JObject input, out JObject normalized, out string error)
        {
            normalized = new JObject();
            error = "";
            foreach (var parameter in parameters.Properties())
            {
                var definition = (JObject)parameter.Value;
                if (!input.TryGetValue(parameter.Name, out var value))
                {
                    if (definition["default"] != null) value = definition["default"];
                    else if (definition.Value<bool?>("opt") == true) continue;
                    else { error = $"Missing required parameter '{parameter.Name}'."; return false; }
                }
                if (!ValidValue(value, definition))
                {
                    error = $"Invalid parameter '{parameter.Name}'; expected {definition.Value<string>("type")} within its declared bounds.";
                    return false;
                }
                normalized[parameter.Name] = value.DeepClone();
            }
            return true;
        }

        static string RequiredText(JObject entry, string name)
        {
            string value = entry.Value<string>(name);
            if (string.IsNullOrWhiteSpace(value)) throw new InvalidDataException($"A command is missing '{name}'.");
            return value;
        }

        static JObject ParameterSchema(JObject definition)
        {
            string type = definition.Value<string>("type");
            var schema = new JObject { ["type"] = type == "enum" ? "string" : type.EndsWith("[]", StringComparison.Ordinal) ? "array" : type };
            if (type == "enum") schema["enum"] = definition["values"].DeepClone();
            if (type.EndsWith("[]", StringComparison.Ordinal))
                schema["items"] = new JObject { ["type"] = type.Substring(0, type.Length - 2) };
            if (definition["desc"] != null) schema["description"] = definition["desc"].DeepClone();
            if (definition["default"] != null) schema["default"] = definition["default"].DeepClone();
            CopyBound(definition, schema, "min", type == "string" ? "minLength" : "minimum");
            CopyBound(definition, schema, "max", type == "string" ? "maxLength" : "maximum");
            CopyBound(definition, schema, "minItems", "minItems");
            CopyBound(definition, schema, "maxItems", "maxItems");
            return schema;
        }

        static void CopyBound(JObject definition, JObject schema, string source, string destination)
        {
            if (definition[source] != null) schema[destination] = definition[source].DeepClone();
        }

        static bool ValidValue(JToken value, JObject definition)
        {
            string type = definition.Value<string>("type");
            switch (type)
            {
                case "number":
                case "integer":
                    if (value.Type != JTokenType.Integer && value.Type != JTokenType.Float) return false;
                    double number = value.Value<double>();
                    return !double.IsNaN(number) && !double.IsInfinity(number) &&
                           (type != "integer" || Math.Truncate(number) == number) && InBounds(number, definition, "min", "max");
                case "boolean": return value.Type == JTokenType.Boolean;
                case "enum": return value.Type == JTokenType.String && definition["values"].Any(item => JToken.DeepEquals(item, value));
                case "number[]":
                case "object[]":
                    if (!(value is JArray array) || !InBounds(array.Count, definition, "minItems", "maxItems")) return false;
                    return array.All(item => type == "object[]" ? item.Type == JTokenType.Object :
                        (item.Type == JTokenType.Integer || item.Type == JTokenType.Float) &&
                        !double.IsNaN(item.Value<double>()) && !double.IsInfinity(item.Value<double>()));
                default: return value.Type == JTokenType.String && InBounds(value.Value<string>().Length, definition, "min", "max");
            }
        }

        static bool InBounds(double value, JObject definition, string minimum, string maximum) =>
            (definition[minimum] == null || value >= definition.Value<double>(minimum)) &&
            (definition[maximum] == null || value <= definition.Value<double>(maximum));
    }
}
