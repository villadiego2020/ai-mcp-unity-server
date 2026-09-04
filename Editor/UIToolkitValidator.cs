using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;

namespace AIUnityMCPServer
{
    [Serializable]
    internal sealed class UIToolkitValidationIssue
    {
        public string severity;
        public string code;
        public string path;
        public int line;
        public int column;
        public string element;
        public string selector;
        public string message;
        public string suggestion;
        public string confidence;
    }

    [Serializable]
    internal sealed class UIToolkitValidationSummary
    {
        public int errors;
        public int warnings;
        public int info;
        public int checkedNodes;
        public int checkedSelectors;
    }

    [Serializable]
    internal sealed class UIToolkitValidationResponse
    {
        public bool ok;
        public string status;
        public string path;
        public string hash;
        public UIToolkitValidationSummary summary;
        public List<UIToolkitValidationIssue> issues;
        public bool truncated;
        public string[] limits;
    }

    internal static class UIToolkitValidator
    {
        const int ValidationNodeLimit = 2000;
        const int ValidationDepthLimit = 100;
        const int ValidationSelectorLimit = 2000;

        internal static string Validate(string path, bool includeLinkedStyles, int maximumIssues)
        {
            if (!TryValidate(path, null, includeLinkedStyles, maximumIssues, out UIToolkitValidationResponse response, out string code, out string message))
                return UIToolkitJson.Error(code, message, "Use an existing canonical Assets/... .uxml or .uss path and retry.");
            return JsonUtility.ToJson(response);
        }

        internal static bool TryValidate(
            string path,
            IReadOnlyDictionary<string, string> virtualContents,
            bool includeLinkedStyles,
            int maximumIssues,
            out UIToolkitValidationResponse response,
            out string code,
            out string message)
        {
            response = null;
            maximumIssues = maximumIssues <= 0 ? 100 : Math.Max(1, Math.Min(500, maximumIssues));
            if (!UIToolkitSource.TryLoad(
                    path,
                    virtualContents,
                    includeLinkedStyles,
                    ValidationNodeLimit,
                    ValidationDepthLimit,
                    ValidationSelectorLimit,
                    out UIToolkitSourceDocument document,
                    out code,
                    out message))
                return false;

            var issues = new List<UIToolkitValidationIssue>();
            AddSourceDiagnostics(document, issues);
            AddMissingReferences(document, issues);
            AddDuplicateNames(document, issues);
            AddBindingIssues(document, issues);
            AddAccessibilityIssues(document, issues);
            AddSelectorIssues(document, issues);
            AddLayoutIssues(document, issues);
            AddPerformanceIssues(document, issues);
            AddCompatibilityIssues(document, issues);

            issues.Sort(CompareIssues);
            int errorCount = issues.Count(issue => issue.severity == "error");
            int warningCount = issues.Count(issue => issue.severity == "warning");
            int informationCount = issues.Count(issue => issue.severity == "info");
            bool truncated = issues.Count > maximumIssues || document.NodesTruncated || document.SelectorsTruncated || document.DepthTruncated;
            if (issues.Count > maximumIssues)
                issues.RemoveRange(maximumIssues, issues.Count - maximumIssues);

            var summary = new UIToolkitValidationSummary
            {
                errors = errorCount,
                warnings = warningCount,
                info = informationCount,
                checkedNodes = document.Nodes.Count,
                checkedSelectors = document.Selectors.Count,
            };
            response = new UIToolkitValidationResponse
            {
                ok = summary.errors == 0,
                status = summary.errors > 0 ? "blocked" : issues.Count == 0 ? "no-findings" : truncated ? "partial" : "complete",
                path = document.Source.AssetPath,
                hash = document.Source.Hash,
                summary = summary,
                issues = issues,
                truncated = truncated,
                limits = new[]
                {
                    "Source validation cannot prove runtime binding types or data availability.",
                    "Selector matching is exact for simple type, #name, and .class selectors; complex cascade behavior is not simulated.",
                    "Accessibility findings are structural heuristics and are not a screen-reader certification.",
                },
            };
            code = null;
            message = null;
            return true;
        }

        internal static UIToolkitValidationResponse ValidateChanges(IReadOnlyDictionary<string, string> virtualContents, int maximumIssues)
        {
            maximumIssues = maximumIssues <= 0 ? 100 : Math.Max(1, Math.Min(500, maximumIssues));
            var combined = new UIToolkitValidationResponse
            {
                ok = true,
                status = "no-findings",
                path = "batch",
                hash = string.Empty,
                summary = new UIToolkitValidationSummary(),
                issues = new List<UIToolkitValidationIssue>(),
                limits = new[]
                {
                    "Validation covers the proposed virtual .uxml/.uss contents before any file is written.",
                    "Runtime bindings, resolved cascade provenance, and UI Builder behavior remain outside source validation.",
                },
            };

            foreach (string path in virtualContents.Keys.OrderBy(item => item, StringComparer.Ordinal))
            {
                if (!TryValidate(path, virtualContents, true, maximumIssues, out UIToolkitValidationResponse item, out string code, out string message))
                {
                    combined.issues.Add(NewIssue("error", code, path, 0, message, "Correct the path or source and generate a new plan.", "confirmed"));
                    continue;
                }
                combined.summary.checkedNodes += item.summary.checkedNodes;
                combined.summary.checkedSelectors += item.summary.checkedSelectors;
                combined.issues.AddRange(item.issues);
                combined.truncated |= item.truncated;
            }

            combined.issues.Sort(CompareIssues);
            if (combined.issues.Count > maximumIssues)
            {
                combined.issues.RemoveRange(maximumIssues, combined.issues.Count - maximumIssues);
                combined.truncated = true;
            }
            combined.summary.errors = combined.issues.Count(issue => issue.severity == "error");
            combined.summary.warnings = combined.issues.Count(issue => issue.severity == "warning");
            combined.summary.info = combined.issues.Count(issue => issue.severity == "info");
            combined.ok = combined.summary.errors == 0;
            combined.status = combined.summary.errors > 0
                ? "blocked"
                : combined.issues.Count == 0 ? "no-findings" : combined.truncated ? "partial" : "complete";
            return combined;
        }

        static void AddSourceDiagnostics(UIToolkitSourceDocument document, List<UIToolkitValidationIssue> issues)
        {
            foreach (UIToolkitSourceDiagnostic diagnostic in document.Diagnostics)
            {
                issues.Add(NewIssue(
                    diagnostic.severity,
                    diagnostic.code,
                    diagnostic.path,
                    diagnostic.line,
                    diagnostic.message,
                    "Correct the source syntax at the reported location.",
                    "confirmed",
                    diagnostic.column));
            }
        }

        static void AddMissingReferences(UIToolkitSourceDocument document, List<UIToolkitValidationIssue> issues)
        {
            foreach (UIToolkitReferenceData reference in document.References.Where(item => !item.exists))
            {
                issues.Add(NewIssue(
                    "error",
                    "MISSING_REFERENCE",
                    reference.owner,
                    reference.line,
                    $"The {reference.kind} reference '{reference.value}' does not resolve to an asset beneath Assets/.",
                    "Correct the reference or add the missing asset, then validate again.",
                    "confirmed"));
            }
        }

        static void AddDuplicateNames(UIToolkitSourceDocument document, List<UIToolkitValidationIssue> issues)
        {
            foreach (IGrouping<string, UIToolkitNodeData> duplicate in document.Nodes
                         .Where(node => !string.IsNullOrEmpty(node.name))
                         .GroupBy(node => node.name, StringComparer.Ordinal)
                         .Where(group => group.Count() > 1))
            {
                UIToolkitNodeData first = duplicate.First();
                issues.Add(NewIssue(
                    "warning",
                    "DUPLICATE_NAME",
                    document.Source.AssetPath,
                    first.line,
                    $"The name '{duplicate.Key}' appears on {duplicate.Count()} elements and makes #name queries ambiguous.",
                    "Assign unique names to elements that must be queried or automated.",
                    "confirmed",
                    element: duplicate.Key));
            }
        }

        static void AddBindingIssues(UIToolkitSourceDocument document, List<UIToolkitValidationIssue> issues)
        {
            foreach (UIToolkitBindingData binding in document.Bindings)
            {
                if (string.IsNullOrWhiteSpace(binding.bindingPath))
                {
                    issues.Add(NewIssue(
                        "warning",
                        "EMPTY_BINDING_PATH",
                        binding.path,
                        binding.line,
                        $"Element '{binding.element}' declares an empty {binding.attribute}.",
                        "Provide a binding path or remove the unused binding attribute.",
                        "confirmed",
                        element: binding.element));
                    continue;
                }

                if (binding.bindingPath.StartsWith(".", StringComparison.Ordinal) || binding.bindingPath.EndsWith(".", StringComparison.Ordinal))
                {
                    issues.Add(NewIssue(
                        "warning",
                        "SUSPICIOUS_BINDING_PATH",
                        binding.path,
                        binding.line,
                        $"Binding path '{binding.bindingPath}' starts or ends with a separator.",
                        "Verify the property path against the runtime data source.",
                        "inferred",
                        element: binding.element));
                }
            }
        }

        static void AddAccessibilityIssues(UIToolkitSourceDocument document, List<UIToolkitValidationIssue> issues)
        {
            foreach (UIToolkitNodeData node in document.Nodes)
            {
                bool interactive = node.type.Equals("Button", StringComparison.OrdinalIgnoreCase) ||
                                   node.type.Equals("Toggle", StringComparison.OrdinalIgnoreCase) ||
                                   node.type.Equals("TextField", StringComparison.OrdinalIgnoreCase);
                if (!interactive)
                    continue;

                bool hasText = Attribute(node, "text").Length > 0 || Attribute(node, "label").Length > 0;
                bool hasTooltip = Attribute(node, "tooltip").Length > 0;
                if (!hasText && !hasTooltip && string.IsNullOrEmpty(node.name))
                {
                    issues.Add(NewIssue(
                        "warning",
                        "ACCESSIBLE_NAME_MISSING",
                        document.Source.AssetPath,
                        node.line,
                        $"Interactive {node.type} has no name, text, label, or tooltip in source.",
                        "Provide visible text or a meaningful name and verify the runtime accessibility path.",
                        "inferred",
                        element: node.name));
                }
            }
        }

        static void AddSelectorIssues(UIToolkitSourceDocument document, List<UIToolkitValidationIssue> issues)
        {
            if (document.Nodes.Count == 0)
                return;

            foreach (UIToolkitSelectorData selector in document.Selectors)
            {
                if (!TryParseSimpleSelector(selector.selector, out string kind, out string value))
                    continue;

                bool matched = document.Nodes.Any(node => SelectorMatches(node, kind, value));
                if (matched)
                    continue;
                issues.Add(NewIssue(
                    "warning",
                    "UNMATCHED_SIMPLE_SELECTOR",
                    StylePathForSelector(document, selector),
                    selector.line,
                    $"Simple selector '{selector.selector}' matches no element in the inspected UXML source.",
                    "Remove the selector or correct the target. Runtime-generated elements are not visible to this check.",
                    "inferred",
                    selector: selector.selector));
            }
        }

        static void AddLayoutIssues(UIToolkitSourceDocument document, List<UIToolkitValidationIssue> issues)
        {
            foreach (UIToolkitSelectorData selector in document.Selectors)
            {
                bool absolute = selector.declarations.Any(item => item.property == "position" && item.value.Equals("absolute", StringComparison.OrdinalIgnoreCase));
                if (!absolute)
                    continue;
                bool horizontalAnchor = selector.declarations.Any(item => item.property == "left" || item.property == "right");
                bool verticalAnchor = selector.declarations.Any(item => item.property == "top" || item.property == "bottom");
                if (horizontalAnchor && verticalAnchor)
                    continue;
                issues.Add(NewIssue(
                    "warning",
                    "ABSOLUTE_POSITION_UNDERCONSTRAINED",
                    StylePathForSelector(document, selector),
                    selector.line,
                    $"Selector '{selector.selector}' uses absolute positioning without both horizontal and vertical anchors.",
                    "Add left/right and top/bottom constraints, or confirm that runtime code supplies the missing position.",
                    "inferred",
                    selector: selector.selector));
            }
        }

        static void AddPerformanceIssues(UIToolkitSourceDocument document, List<UIToolkitValidationIssue> issues)
        {
            foreach (UIToolkitSelectorData selector in document.Selectors)
            {
                int relationshipCount = Regex.Matches(selector.selector, @"\s+|>").Count;
                if (relationshipCount < 5)
                    continue;
                issues.Add(NewIssue(
                    "info",
                    "DEEP_SELECTOR_CHAIN",
                    StylePathForSelector(document, selector),
                    selector.line,
                    $"Selector '{selector.selector}' has a deep relationship chain.",
                    "Prefer a stable class on the target element when this selector is evaluated frequently.",
                    "inferred",
                    selector: selector.selector));
            }
        }

        static void AddCompatibilityIssues(UIToolkitSourceDocument document, List<UIToolkitValidationIssue> issues)
        {
            foreach (UIToolkitSelectorData selector in document.Selectors)
            {
                if (selector.selector.IndexOf(":has(", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                issues.Add(NewIssue(
                    "warning",
                    "UNSUPPORTED_SELECTOR_HAS",
                    StylePathForSelector(document, selector),
                    selector.line,
                    $"Selector '{selector.selector}' uses :has(), which is not supported by Unity USS.",
                    "Replace it with a class assigned to the parent or target element.",
                    "confirmed",
                    selector: selector.selector));
            }
        }

        static string Attribute(UIToolkitNodeData node, string name)
        {
            return node.attributes.FirstOrDefault(attribute => attribute.name.Equals(name, StringComparison.OrdinalIgnoreCase))?.value ?? string.Empty;
        }

        static bool TryParseSimpleSelector(string selector, out string kind, out string value)
        {
            kind = null;
            value = null;
            string trimmed = selector?.Trim() ?? string.Empty;
            if (Regex.IsMatch(trimmed, @"^#[A-Za-z_][A-Za-z0-9_-]*$"))
            {
                kind = "name";
                value = trimmed.Substring(1);
                return true;
            }
            if (Regex.IsMatch(trimmed, @"^\.[A-Za-z_][A-Za-z0-9_-]*$"))
            {
                kind = "class";
                value = trimmed.Substring(1);
                return true;
            }
            if (Regex.IsMatch(trimmed, @"^[A-Za-z_][A-Za-z0-9_-]*$"))
            {
                kind = "type";
                value = trimmed;
                return true;
            }
            return false;
        }

        static bool SelectorMatches(UIToolkitNodeData node, string kind, string value)
        {
            return kind switch
            {
                "name" => node.name == value,
                "class" => node.classes.Contains(value),
                "type" => node.type.Equals(value, StringComparison.OrdinalIgnoreCase),
                _ => false,
            };
        }

        static string StylePathForSelector(UIToolkitSourceDocument document, UIToolkitSelectorData selector)
        {
            return string.IsNullOrEmpty(selector.path) ? document.Source.AssetPath : selector.path;
        }

        static UIToolkitValidationIssue NewIssue(
            string severity,
            string code,
            string path,
            int line,
            string message,
            string suggestion,
            string confidence,
            int column = 0,
            string element = "",
            string selector = "")
        {
            return new UIToolkitValidationIssue
            {
                severity = severity,
                code = code,
                path = path,
                line = line,
                column = column,
                element = element ?? string.Empty,
                selector = selector ?? string.Empty,
                message = message,
                suggestion = suggestion,
                confidence = confidence,
            };
        }

        static int CompareIssues(UIToolkitValidationIssue left, UIToolkitValidationIssue right)
        {
            int severity = SeverityRank(left.severity).CompareTo(SeverityRank(right.severity));
            if (severity != 0) return severity;
            int path = string.Compare(left.path, right.path, StringComparison.Ordinal);
            if (path != 0) return path;
            int line = left.line.CompareTo(right.line);
            if (line != 0) return line;
            return string.Compare(left.code, right.code, StringComparison.Ordinal);
        }

        static int SeverityRank(string severity)
        {
            return severity == "error" ? 0 : severity == "warning" ? 1 : 2;
        }
    }
}
