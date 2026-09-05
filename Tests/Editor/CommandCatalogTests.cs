using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;

namespace AIUnityMCPServer.Tests
{
    public class CommandCatalogTests
    {
        static MCPCommandDefinition Find(string name) => MCPCommandCatalog.Load().Single(item => item.ToolName == name);

        [Test]
        public void WriteGateUsesEditorSessionAndLeavesGlobalPreferenceUnchanged()
        {
            const string key = "AIUnityMCPServer_AllowWrites";
            bool previous = MCPHandlers.AllowWrites;
            bool globalExists = EditorPrefs.HasKey(key);
            bool globalValue = EditorPrefs.GetBool(key, false);
            try
            {
                foreach (bool allowed in new[] { true, false })
                {
                    MCPHandlers.AllowWrites = allowed;
                    Assert.That(SessionState.GetBool(key, !allowed), Is.EqualTo(allowed));
                    Assert.That(MCPHandlers.AllowWrites, Is.EqualTo(allowed));
                    Assert.That(EditorPrefs.HasKey(key), Is.EqualTo(globalExists));
                    Assert.That(EditorPrefs.GetBool(key, false), Is.EqualTo(globalValue));
                }
            }
            finally { MCPHandlers.AllowWrites = previous; }
        }

        [Test]
        public void CatalogPublishesAllCommandsWithRequiredFieldsAndTypedSchemas()
        {
            var catalog = MCPCommandCatalog.Load();
            Assert.That(catalog.Count, Is.EqualTo(73));
            Assert.That(catalog.Select(item => item.ToolName).Distinct().Count(), Is.EqualTo(73));
            foreach (var command in catalog)
            {
                Assert.That(command.ToolName.Length, Is.LessThanOrEqualTo(42));
                Assert.That(MCPHandlers.CommandPaths(), Does.Contain(command.Path));
                Assert.That(command.GetInputSchema().Value<string>("type"), Is.EqualTo("object"));
            }
            var inspect = Find("unity_uitk_inspect").GetInputSchema();
            Assert.That(inspect["required"].Values<string>(), Is.EqualTo(new[] { "path" }));
            Assert.That(inspect["properties"]["maxNodes"]["type"].Value<string>(), Is.EqualTo("integer"));
            Assert.That(inspect["properties"]["maxNodes"]["maximum"].Value<int>(), Is.EqualTo(2000));
            var changes = Find("unity_uitk_apply").GetInputSchema()["properties"]["changes"];
            Assert.That(changes["items"]["type"].Value<string>(), Is.EqualTo("object"));
            Assert.That(changes["minItems"].Value<int>(), Is.EqualTo(1));
            Assert.That(changes["maxItems"].Value<int>(), Is.EqualTo(8));
        }

        [Test]
        public void NormalizationAppliesDefaultsAndDoesNotMutateCallerInput()
        {
            var input = JObject.Parse("{\"path\":\"Assets/Test.uxml\",\"ignored\":\"extra\"}");
            string before = input.ToString();
            Assert.That(Find("unity_uitk_inspect").TryNormalize(input, out var normalized, out var error), Is.True, error);
            Assert.That(normalized.Value<int>("maxNodes"), Is.EqualTo(250));
            Assert.That(normalized.Value<int>("maxDepth"), Is.EqualTo(20));
            Assert.That(normalized["ignored"], Is.Null);
            Assert.That(input.ToString(), Is.EqualTo(before));
        }

        [TestCase("{}")]
        [TestCase("{\"path\":null}")]
        [TestCase("{\"path\":4}")]
        [TestCase("{\"path\":\"Assets/Test.uxml\",\"maxNodes\":1.5}")]
        [TestCase("{\"path\":\"Assets/Test.uxml\",\"maxNodes\":0}")]
        [TestCase("{\"path\":\"Assets/Test.uxml\",\"maxNodes\":2001}")]
        [TestCase("{\"path\":\"Assets/Test.uxml\",\"maxNodes\":\"3\"}")]
        public void InvalidInputsAreRejectedBeforeDispatcherExecution(string json)
        {
            Assert.That(Find("unity_uitk_inspect").TryNormalize(JObject.Parse(json), out _, out var error), Is.False);
            Assert.That(error, Is.Not.Empty);
        }

        [Test]
        public void ObjectArraysPreserveNestedChangeDataAndRejectBoundsOrScalarItems()
        {
            var definition = Find("unity_uitk_apply");
            var change = JObject.Parse("{\"path\":\"Assets/Test.uxml\",\"content\":\"<UXML/>\",\"expectedHash\":\"missing\"}");
            var input = new JObject { ["mode"] = "plan", ["changes"] = new JArray(change) };
            Assert.That(definition.TryNormalize(input, out var normalized, out var error), Is.True, error);
            Assert.That(JToken.DeepEquals(input["changes"], normalized["changes"]), Is.True);
            normalized["changes"][0]["content"] = "changed";
            Assert.That(input["changes"][0].Value<string>("content"), Is.EqualTo("<UXML/>"));
            foreach (var invalid in new[] { new JArray(), new JArray("not-an-object"), new JArray(Enumerable.Range(0, 9).Select(_ => change.DeepClone())) })
            {
                input["changes"] = invalid;
                Assert.That(definition.TryNormalize(input, out _, out _), Is.False);
            }
            input["changes"] = new JArray(change);
            input["mode"] = "PLAN";
            Assert.That(definition.TryNormalize(input, out _, out _), Is.False);
        }
    }
}
