using System;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace AIUnityMCPServer.Tests
{
    public class UIToolkitCapabilityTests
    {
        string _assetDirectory;
        string _absoluteDirectory;
        bool _previousAllowWrites;

        [SetUp]
        public void SetUp()
        {
            _previousAllowWrites = MCPHandlers.AllowWrites;
            MCPHandlers.AllowWrites = false;
            _assetDirectory = "Assets/__AIUnityMCPServerTests_" + Guid.NewGuid().ToString("N");
            _absoluteDirectory = Path.Combine(Application.dataPath, _assetDirectory.Substring("Assets/".Length));
            Directory.CreateDirectory(_absoluteDirectory);
        }

        [TearDown]
        public void TearDown()
        {
            MCPHandlers.AllowWrites = _previousAllowWrites;
            AssetDatabase.DeleteAsset(_assetDirectory);
            if (Directory.Exists(_absoluteDirectory)) Directory.Delete(_absoluteDirectory, true);
            if (File.Exists(_absoluteDirectory + ".meta")) File.Delete(_absoluteDirectory + ".meta");
            AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
        }

        [TestCase("../outside.uxml", "UNSAFE_PATH")]
        [TestCase("Assets/../outside.uxml", "UNSAFE_PATH")]
        [TestCase("Assets//screen.uxml", "UNSAFE_PATH")]
        [TestCase("Assets\\screen.uxml", "UNSAFE_PATH")]
        [TestCase("C:/outside.uxml", "UNSAFE_PATH")]
        [TestCase("Packages/com.example/screen.uxml", "UNSAFE_PATH")]
        [TestCase("Assets/screen.xml", "INVALID_REQUEST")]
        public void PathGuardRejectsEscapesAndUnsupportedExtensions(string path, string expectedCode)
        {
            Assert.That(UIToolkitPathGuard.TryResolve(path, false, out _, out string code, out _), Is.False);
            Assert.That(code, Is.EqualTo(expectedCode));
        }

        [Test]
        public void ExactByteHashIncludesLineEndingsAndBom()
        {
            string path = Write("bytes.uss", new byte[] { 0xEF, 0xBB, 0xBF, (byte)'a', (byte)'\r', (byte)'\n' });
            Assert.That(UIToolkitPathGuard.TryRead(path, 100, out UIToolkitFileSnapshot snapshot, out _, out string message), Is.True, message);
            Assert.That(snapshot.Bytes, Is.EqualTo(new byte[] { 0xEF, 0xBB, 0xBF, (byte)'a', (byte)'\r', (byte)'\n' }));
            Assert.That(snapshot.Hash, Is.EqualTo(UIToolkitPathGuard.ComputeHash(snapshot.Bytes)));
            Assert.That(UIToolkitPathGuard.ComputeHash(new byte[] { (byte)'a', (byte)'b', (byte)'c' }),
                Is.EqualTo("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"));
        }

        [Test]
        public void SourceReadRejectsFilesAboveMaximumSize()
        {
            string path = Write("oversized.uss", new byte[UIToolkitPathGuard.MaximumSourceBytes + 1]);
            Assert.That(UIToolkitPathGuard.TryRead(path, UIToolkitPathGuard.MaximumSourceBytes, out _, out string code, out _), Is.False);
            Assert.That(code, Is.EqualTo("SOURCE_TOO_LARGE"));
        }

        [Test]
        public void InspectBoundsNodesDepthSelectorsAndReportsMalformedSources()
        {
            WriteText("styles.uss", ".one { color: red; } .two { color: blue; } .three { color: white; }");
            string uxml = WriteText("bounded.uxml",
                "<UXML><Style src=\"styles.uss\"/><VisualElement><VisualElement><Button name=\"deep\"/></VisualElement></VisualElement></UXML>");
            UIToolkitInspectResponse response = JsonUtility.FromJson<UIToolkitInspectResponse>(UIToolkitSource.Inspect(uxml, true, 2, 1, 2));
            Assert.That(response.status, Is.EqualTo("partial"));
            Assert.That(response.nodes, Has.Count.EqualTo(2));
            Assert.That(response.selectors, Has.Count.EqualTo(2));
            Assert.That(response.truncated.nodes, Is.True);
            Assert.That(response.truncated.depth, Is.True);
            Assert.That(response.truncated.selectors, Is.True);

            string malformedUxml = WriteText("malformed.uxml", "<!DOCTYPE UXML [<!ENTITY x SYSTEM \"file:///outside\">]><UXML>&x;</UXML>");
            UIToolkitInspectResponse xmlResult = JsonUtility.FromJson<UIToolkitInspectResponse>(UIToolkitSource.Inspect(malformedUxml, false, 20, 10, 20));
            Assert.That(xmlResult.ok, Is.False);
            Assert.That(xmlResult.diagnostics.Any(item => item.code == "DTD_PROHIBITED"), Is.True);

            string malformedUss = WriteText("malformed.uss", ".broken { color: red;");
            UIToolkitInspectResponse ussResult = JsonUtility.FromJson<UIToolkitInspectResponse>(UIToolkitSource.Inspect(malformedUss, false, 20, 10, 20));
            Assert.That(ussResult.ok, Is.False);
            Assert.That(ussResult.diagnostics.Any(item => item.code == "USS_UNCLOSED_BLOCK"), Is.True);
        }

        [Test]
        public void ValidateReportsReferencesNamesSelectorsAccessibilityAndLayoutWithoutClaimingComplexMatching()
        {
            WriteText("validation.uss",
                ".missing { position: absolute; left: 0; }\n" +
                ".same { color: red; }\n" +
                "VisualElement Button { color: blue; }\n" +
                "VisualElement:has(Button) { color: white; }");
            string path = WriteText("validation.uxml",
                "<UXML><Style src=\"validation.uss\"/><Template src=\"missing.uxml\"/>" +
                "<Button name=\"same\"/><Button name=\"same\"/><Button/></UXML>");

            UIToolkitValidationResponse response = JsonUtility.FromJson<UIToolkitValidationResponse>(UIToolkitValidator.Validate(path, true, 100));
            string[] codes = response.issues.Select(item => item.code).ToArray();
            CollectionAssert.Contains(codes, "MISSING_REFERENCE");
            CollectionAssert.Contains(codes, "DUPLICATE_NAME");
            CollectionAssert.Contains(codes, "UNMATCHED_SIMPLE_SELECTOR");
            CollectionAssert.Contains(codes, "ACCESSIBLE_NAME_MISSING");
            CollectionAssert.Contains(codes, "ABSOLUTE_POSITION_UNDERCONSTRAINED");
            CollectionAssert.Contains(codes, "UNSUPPORTED_SELECTOR_HAS");
            Assert.That(response.issues.Any(item => item.code == "UNMATCHED_SIMPLE_SELECTOR" && item.selector == "VisualElement Button"), Is.False);
            Assert.That(response.limits.Any(item => item.Contains("complex cascade behavior is not simulated")), Is.True);
        }

        [Test]
        public void ApplyPlanIsReadOnlyAndDeterministicAcrossChangeOrder()
        {
            string first = WriteText("first.uss", ".first { color: red; }");
            string second = WriteText("second.uss", ".second { color: blue; }");
            string firstHash = ReadHash(first);
            string secondHash = ReadHash(second);
            string firstAbsolute = Absolute(first);
            string secondAbsolute = Absolute(second);
            DateTime firstWrite = File.GetLastWriteTimeUtc(firstAbsolute);
            DateTime secondWrite = File.GetLastWriteTimeUtc(secondAbsolute);

            UIToolkitApplyPlanResponse forward = Plan(Change(first, ".first { color: green; }", firstHash), Change(second, ".second { color: white; }", secondHash));
            UIToolkitApplyPlanResponse reverse = Plan(Change(second, ".second { color: white; }", secondHash), Change(first, ".first { color: green; }", firstHash));

            Assert.That(forward.ok, Is.True);
            Assert.That(forward.planHash, Is.EqualTo(reverse.planHash));
            Assert.That(File.ReadAllText(firstAbsolute), Is.EqualTo(".first { color: red; }"));
            Assert.That(File.ReadAllText(secondAbsolute), Is.EqualTo(".second { color: blue; }"));
            Assert.That(File.GetLastWriteTimeUtc(firstAbsolute), Is.EqualTo(firstWrite));
            Assert.That(File.GetLastWriteTimeUtc(secondAbsolute), Is.EqualTo(secondWrite));
            Assert.That(Directory.GetFiles(_absoluteDirectory, "*.aimcp.*", SearchOption.AllDirectories), Is.Empty);
        }

        [Test]
        public void ApplyRejectsMissingWrongStaleAndMismatchedHashes()
        {
            string path = WriteText("hashes.uss", ".item { color: red; }");
            string hash = ReadHash(path);
            StringAssert.Contains("HASH_REQUIRED", UIToolkitApply.Execute(
                "{\"mode\":\"plan\",\"changes\":[{\"path\":\"" + path + "\",\"content\":\".item { color: blue; }\"}]}"));
            StringAssert.Contains("STALE_SOURCE", UIToolkitApply.Execute(ApplyBody("plan", null, Change(path, ".item { color: blue; }", "wrong"))));

            UIToolkitApplyChangeRequest change = Change(path, ".item { color: blue; }", hash);
            UIToolkitApplyPlanResponse plan = Plan(change);
            StringAssert.Contains("PLAN_HASH_MISMATCH", UIToolkitApply.Execute(ApplyBody("commit", "wrong", change)));
            File.WriteAllText(Absolute(path), ".item { color: white; }");
            StringAssert.Contains("STALE_SOURCE", UIToolkitApply.Execute(ApplyBody("commit", plan.planHash, change)));
        }

        [Test]
        public void ApplyCommitRequiresWriteAndCanOverwriteAndCreate()
        {
            string existing = WriteText("existing.uss", ".item { color: red; }");
            string created = Asset("created.uxml");
            UIToolkitApplyChangeRequest update = Change(existing, ".item { color: blue; }", ReadHash(existing));
            UIToolkitApplyChangeRequest create = Change(created, "<UXML><Label text=\"Created\"/></UXML>", "missing");
            UIToolkitApplyPlanResponse plan = Plan(update, create);

            MCPHandlers.AllowWrites = false;
            StringAssert.Contains("READ_ONLY", MCPHandlers.Dispatch("/uitk/apply", ApplyBody("commit", plan.planHash, update, create), false));
            Assert.That(File.ReadAllText(Absolute(existing)), Is.EqualTo(".item { color: red; }"));
            Assert.That(File.Exists(Absolute(created)), Is.False);

            MCPHandlers.AllowWrites = true;
            string result = MCPHandlers.Dispatch("/uitk/apply", ApplyBody("commit", plan.planHash, update, create), false);
            StringAssert.Contains("\"status\":\"complete\"", result);
            Assert.That(File.ReadAllText(Absolute(existing)), Is.EqualTo(".item { color: blue; }"));
            Assert.That(File.ReadAllText(Absolute(created)), Is.EqualTo("<UXML><Label text=\"Created\"/></UXML>"));
            Assert.That(ReadHash(existing), Is.EqualTo(UIToolkitPathGuard.ComputeHash(UIToolkitPathGuard.Encode(".item { color: blue; }"))));
        }

        [Test]
        public void MultiFileCommitRollsBackWhenSecondReplacementFailsOnWindows()
        {
            if (Application.platform != RuntimePlatform.WindowsEditor) Assert.Ignore("The deterministic sharing-violation seam is Windows-specific.");
            string first = WriteText("rollback-first.uss", ".first { color: red; }");
            string second = WriteText("rollback-second.uss", ".second { color: blue; }");
            UIToolkitApplyChangeRequest firstChange = Change(first, ".first { color: green; }", ReadHash(first));
            UIToolkitApplyChangeRequest secondChange = Change(second, ".second { color: white; }", ReadHash(second));
            UIToolkitApplyPlanResponse plan = Plan(firstChange, secondChange);

            MCPHandlers.AllowWrites = true;
            using (new FileStream(Absolute(second), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                string result = UIToolkitApply.Execute(ApplyBody("commit", plan.planHash, firstChange, secondChange));
                StringAssert.Contains("COMMIT_FAILED", result);
                StringAssert.Contains("Rollback completed successfully", result);
            }

            Assert.That(File.ReadAllText(Absolute(first)), Is.EqualTo(".first { color: red; }"));
            Assert.That(File.ReadAllText(Absolute(second)), Is.EqualTo(".second { color: blue; }"));
            Assert.That(Directory.GetFiles(_absoluteDirectory, "*.aimcp.*", SearchOption.AllDirectories), Is.Empty);
        }

        [Test]
        public void PlaytestReadOnlyAndMutationGatesAreRequestAware()
        {
            MCPHandlers.AllowWrites = false;
            string snapshot = MCPHandlers.Dispatch("/uitk/playtest", "{\"mode\":\"start\",\"document\":\"missing\",\"action\":\"snapshot\"}", false);
            StringAssert.Contains("NOT_FOUND", snapshot);
            StringAssert.DoesNotContain("READ_ONLY", snapshot);
            string status = MCPHandlers.Dispatch("/uitk/playtest", "{\"mode\":\"status\",\"runId\":\"missing\"}", false);
            StringAssert.Contains("NOT_FOUND", status);
            StringAssert.DoesNotContain("READ_ONLY", status);
            string mutation = MCPHandlers.Dispatch("/uitk/playtest", "{\"mode\":\"start\",\"document\":\"missing\",\"action\":\"click\",\"selector\":\"#go\"}", false);
            StringAssert.Contains("READ_ONLY", mutation);

            MCPHandlers.AllowWrites = true;
            string playRequired = MCPHandlers.Dispatch("/uitk/playtest", "{\"mode\":\"start\",\"document\":\"missing\",\"action\":\"click\",\"selector\":\"#go\"}", false);
            StringAssert.Contains("PLAY_MODE_REQUIRED", playRequired);
        }

        [Test]
        public void PlaytestDocumentAndSelectorResolutionAreExactAndBounded()
        {
            var first = new GameObject("DuplicateDocument");
            var second = new GameObject("DuplicateDocument");
            try
            {
                first.AddComponent<UIDocument>();
                second.AddComponent<UIDocument>();
                string ambiguous = MCPHandlers.Dispatch("/uitk/playtest", "{\"mode\":\"start\",\"document\":\"DuplicateDocument\",\"action\":\"snapshot\"}", false);
                StringAssert.Contains("AMBIGUOUS_DOCUMENT", ambiguous);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(first);
                UnityEngine.Object.DestroyImmediate(second);
            }

            var root = new VisualElement();
            root.Add(new Button { name = "duplicate" });
            root.Add(new Button { name = "duplicate" });
            AssertSelector(root, "#duplicate", false, "AMBIGUOUS_SELECTOR");
            AssertSelector(root, "Button:hover", false, "INVALID_SELECTOR");
            for (int index = 0; index < 1001; index++) root.Add(new Label { name = "node-" + index });
            AssertSelector(root, "#node-1000", false, "NOT_FOUND");
            AssertSelector(root, "#node-10", true, null);
        }

        [Test]
        public void PlaytestDocumentIdentityUsesTheSupportedUnityApiAndRoundTripsThroughPublicResolution()
        {
            var gameObject = new GameObject("IdentityDocument");
            try
            {
                UIDocument document = gameObject.AddComponent<UIDocument>();
#if UNITY_6000_5_OR_NEWER
                int identity = unchecked((int)EntityId.ToULong(document.GetEntityId()));
#else
                int identity = document.GetInstanceID();
#endif
                string response = MCPHandlers.Dispatch("/uitk/playtest",
                    "{\"mode\":\"start\",\"document\":\"" + identity + "\",\"action\":\"snapshot\"}", false);
                StringAssert.DoesNotContain("NOT_FOUND", response);
                Assert.That(response.Contains("DOCUMENT_NOT_READY") || response.Contains("\"status\":\"running\""), Is.True, response);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }

        void AssertSelector(VisualElement root, string selector, bool expected, string expectedCode)
        {
            MethodInfo method = typeof(UIToolkitPlaytest).GetMethod("TryResolveElement", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(method, Is.Not.Null);
            object[] arguments = { root, selector, false, null, null, null };
            bool result = (bool)method.Invoke(null, arguments);
            Assert.That(result, Is.EqualTo(expected));
            Assert.That(arguments[4] as string, Is.EqualTo(expectedCode));
        }

        UIToolkitApplyPlanResponse Plan(params UIToolkitApplyChangeRequest[] changes)
        {
            return JsonUtility.FromJson<UIToolkitApplyPlanResponse>(UIToolkitApply.Execute(ApplyBody("plan", null, changes)));
        }

        string ApplyBody(string mode, string planHash, params UIToolkitApplyChangeRequest[] changes)
        {
            var request = new UIToolkitApplyRequest { mode = mode, changes = changes, expectedHash = planHash, maxIssues = 100 };
            return JsonUtility.ToJson(request);
        }

        static UIToolkitApplyChangeRequest Change(string path, string content, string expectedHash)
        {
            return new UIToolkitApplyChangeRequest { path = path, content = content, expectedHash = expectedHash };
        }

        string ReadHash(string path)
        {
            Assert.That(UIToolkitPathGuard.TryRead(path, UIToolkitPathGuard.MaximumSourceBytes, out UIToolkitFileSnapshot snapshot, out _, out string message), Is.True, message);
            return snapshot.Hash;
        }

        string WriteText(string fileName, string content)
        {
            string path = Asset(fileName);
            File.WriteAllText(Absolute(path), content, new System.Text.UTF8Encoding(false));
            return path;
        }

        string Write(string fileName, byte[] bytes)
        {
            string path = Asset(fileName);
            File.WriteAllBytes(Absolute(path), bytes);
            return path;
        }

        string Asset(string fileName) => _assetDirectory + "/" + fileName;

        static string Absolute(string assetPath)
        {
            return Path.Combine(UIToolkitPathGuard.ProjectRoot(), assetPath.Replace('/', Path.DirectorySeparatorChar));
        }
    }
}
