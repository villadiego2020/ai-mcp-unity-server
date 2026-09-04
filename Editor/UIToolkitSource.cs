using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using UnityEngine;

namespace AIUnityMCPServer
{
    [Serializable]
    internal sealed class UIToolkitAttributeData
    {
        public string name;
        public string value;
    }

    [Serializable]
    internal sealed class UIToolkitNodeData
    {
        public int index;
        public int parentIndex;
        public int depth;
        public int line;
        public string type;
        public string name;
        public string[] classes;
        public string bindingPath;
        public List<UIToolkitAttributeData> attributes = new List<UIToolkitAttributeData>();
    }

    [Serializable]
    internal sealed class UIToolkitDeclarationData
    {
        public string property;
        public string value;
        public int line;
    }

    [Serializable]
    internal sealed class UIToolkitSelectorData
    {
        public string path;
        public string selector;
        public int line;
        public List<UIToolkitDeclarationData> declarations = new List<UIToolkitDeclarationData>();
    }

    [Serializable]
    internal sealed class UIToolkitStyleSheetData
    {
        public string path;
        public string hash;
        public int bytes;
        public int selectors;
        public bool missing;
    }

    [Serializable]
    internal sealed class UIToolkitBindingData
    {
        public string path;
        public int line;
        public string element;
        public string attribute;
        public string bindingPath;
    }

    [Serializable]
    internal sealed class UIToolkitReferenceData
    {
        public string owner;
        public int line;
        public string kind;
        public string value;
        public string resolvedPath;
        public bool exists;
    }

    [Serializable]
    internal sealed class UIToolkitSourceDiagnostic
    {
        public string severity;
        public string code;
        public string path;
        public int line;
        public int column;
        public string message;
    }

    internal sealed class UIToolkitSourceDocument
    {
        public UIToolkitFileSnapshot Source;
        public string Kind;
        public List<UIToolkitNodeData> Nodes = new List<UIToolkitNodeData>();
        public List<UIToolkitStyleSheetData> StyleSheets = new List<UIToolkitStyleSheetData>();
        public List<UIToolkitSelectorData> Selectors = new List<UIToolkitSelectorData>();
        public List<UIToolkitBindingData> Bindings = new List<UIToolkitBindingData>();
        public List<UIToolkitReferenceData> References = new List<UIToolkitReferenceData>();
        public List<UIToolkitSourceDiagnostic> Diagnostics = new List<UIToolkitSourceDiagnostic>();
        public bool NodesTruncated;
        public bool DepthTruncated;
        public bool SelectorsTruncated;
        public bool AttributesTruncated;
    }

    [Serializable]
    internal sealed class UIToolkitInspectSummary
    {
        public int nodes;
        public int styleSheets;
        public int selectors;
        public int bindings;
        public int missingReferences;
    }

    [Serializable]
    internal sealed class UIToolkitInspectTruncation
    {
        public bool any;
        public bool nodes;
        public bool depth;
        public bool selectors;
        public bool attributes;
    }

    [Serializable]
    internal sealed class UIToolkitInspectLimits
    {
        public int maximumSourceBytes;
        public int maximumNodes;
        public int maximumDepth;
        public int maximumSelectors;
        public string analysisMode;
        public string[] unsupported;
    }

    [Serializable]
    internal sealed class UIToolkitInspectResponse
    {
        public bool ok;
        public string status;
        public string kind;
        public string path;
        public string hash;
        public int bytes;
        public UIToolkitInspectSummary summary;
        public List<UIToolkitNodeData> nodes;
        public List<UIToolkitStyleSheetData> styleSheets;
        public List<UIToolkitSelectorData> selectors;
        public List<UIToolkitBindingData> bindings;
        public List<UIToolkitSourceDiagnostic> diagnostics;
        public UIToolkitInspectTruncation truncated;
        public UIToolkitInspectLimits limits;
    }

    internal static class UIToolkitSource
    {
        const int MaximumAttributesPerNode = 64;
        const int MaximumAttributeValueLength = 2048;

        internal static string Inspect(string path, bool includeLinkedStyles, int maximumNodes, int maximumDepth, int maximumSelectors)
        {
            maximumNodes = Clamp(maximumNodes, 1, 2000, 250);
            maximumDepth = Clamp(maximumDepth, 1, 100, 20);
            maximumSelectors = Clamp(maximumSelectors, 1, 2000, 300);

            if (!TryLoad(path, null, includeLinkedStyles, maximumNodes, maximumDepth, maximumSelectors, out UIToolkitSourceDocument document, out string code, out string message))
                return UIToolkitJson.Error(code, message, "Use an existing canonical Assets/... .uxml or .uss path and retry.");

            bool truncated = document.NodesTruncated || document.DepthTruncated || document.SelectorsTruncated || document.AttributesTruncated;
            var response = new UIToolkitInspectResponse
            {
                ok = document.Diagnostics.All(item => item.severity != "error"),
                status = truncated ? "partial" : "complete",
                kind = document.Kind,
                path = document.Source.AssetPath,
                hash = document.Source.Hash,
                bytes = document.Source.Bytes.Length,
                summary = new UIToolkitInspectSummary
                {
                    nodes = document.Nodes.Count,
                    styleSheets = document.StyleSheets.Count,
                    selectors = document.Selectors.Count,
                    bindings = document.Bindings.Count,
                    missingReferences = document.References.Count(item => !item.exists),
                },
                nodes = document.Nodes,
                styleSheets = document.StyleSheets,
                selectors = document.Selectors,
                bindings = document.Bindings,
                diagnostics = document.Diagnostics,
                truncated = new UIToolkitInspectTruncation
                {
                    any = truncated,
                    nodes = document.NodesTruncated,
                    depth = document.DepthTruncated,
                    selectors = document.SelectorsTruncated,
                    attributes = document.AttributesTruncated,
                },
                limits = new UIToolkitInspectLimits
                {
                    maximumSourceBytes = UIToolkitPathGuard.MaximumSourceBytes,
                    maximumNodes = maximumNodes,
                    maximumDepth = maximumDepth,
                    maximumSelectors = maximumSelectors,
                    analysisMode = "source",
                    unsupported = new[]
                    {
                        "Resolved cascade provenance",
                        "Runtime-generated visual elements",
                        "Binding type-graph verification",
                        "UI Builder automation",
                        "Asset-reference checks beyond linked Style and Template elements",
                    },
                },
            };
            return JsonUtility.ToJson(response);
        }

        internal static bool TryLoad(
            string path,
            IReadOnlyDictionary<string, string> virtualContents,
            bool includeLinkedStyles,
            int maximumNodes,
            int maximumDepth,
            int maximumSelectors,
            out UIToolkitSourceDocument document,
            out string code,
            out string message)
        {
            document = null;
            if (!TryReadSource(path, virtualContents, out UIToolkitFileSnapshot source, out code, out message))
                return false;

            document = new UIToolkitSourceDocument
            {
                Source = source,
                Kind = Path.GetExtension(source.AssetPath).Equals(".uxml", StringComparison.OrdinalIgnoreCase) ? "uxml" : "uss",
            };

            if (document.Kind == "uxml")
            {
                ParseUxml(document, maximumNodes, maximumDepth);
                LoadLinkedStyles(document, virtualContents, includeLinkedStyles, maximumSelectors);
            }
            else
            {
                ParseUss(document, source.AssetPath, source.Text, maximumSelectors);
                document.StyleSheets.Add(new UIToolkitStyleSheetData
                {
                    path = source.AssetPath,
                    hash = source.Hash,
                    bytes = source.Bytes.Length,
                    selectors = document.Selectors.Count,
                    missing = false,
                });
            }
            return true;
        }

        internal static bool TryReadSource(
            string path,
            IReadOnlyDictionary<string, string> virtualContents,
            out UIToolkitFileSnapshot source,
            out string code,
            out string message)
        {
            if (!UIToolkitPathGuard.TryResolve(path, virtualContents == null || !virtualContents.ContainsKey(path), out source, out code, out message))
                return false;

            if (virtualContents != null && virtualContents.TryGetValue(source.AssetPath, out string virtualContent))
            {
                byte[] bytes = UIToolkitPathGuard.Encode(virtualContent);
                if (bytes.Length > UIToolkitPathGuard.MaximumSourceBytes)
                {
                    code = "SOURCE_TOO_LARGE";
                    message = $"'{source.AssetPath}' exceeds the {UIToolkitPathGuard.MaximumSourceBytes}-byte source limit.";
                    return false;
                }
                source.Bytes = bytes;
                source.Text = virtualContent;
                source.Hash = UIToolkitPathGuard.ComputeHash(bytes);
                return true;
            }

            return UIToolkitPathGuard.TryRead(source.AssetPath, UIToolkitPathGuard.MaximumSourceBytes, out source, out code, out message);
        }

        static void ParseUxml(UIToolkitSourceDocument document, int maximumNodes, int maximumDepth)
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreComments = true,
                IgnoreWhitespace = true,
                MaxCharactersInDocument = UIToolkitPathGuard.MaximumSourceBytes,
            };

            try
            {
                using var stringReader = new StringReader(document.Source.Text);
                using XmlReader reader = XmlReader.Create(stringReader, settings);
                var parentByDepth = new Dictionary<int, int>();
                while (reader.Read())
                {
                    if (reader.NodeType != XmlNodeType.Element)
                        continue;

                    int line = (reader as IXmlLineInfo)?.LineNumber ?? 0;
                    int depth = reader.Depth;
                    if (depth > maximumDepth)
                    {
                        document.DepthTruncated = true;
                        continue;
                    }

                    if (document.Nodes.Count >= maximumNodes)
                    {
                        document.NodesTruncated = true;
                        continue;
                    }

                    var node = new UIToolkitNodeData
                    {
                        index = document.Nodes.Count,
                        parentIndex = depth > 0 && parentByDepth.TryGetValue(depth - 1, out int parentIndex) ? parentIndex : -1,
                        depth = depth,
                        line = line,
                        type = reader.LocalName,
                        name = reader.GetAttribute("name") ?? string.Empty,
                        classes = SplitClasses(reader.GetAttribute("class")),
                        bindingPath = reader.GetAttribute("binding-path") ?? string.Empty,
                    };

                    if (reader.HasAttributes)
                    {
                        int attributeCount = 0;
                        while (reader.MoveToNextAttribute())
                        {
                            if (attributeCount++ >= MaximumAttributesPerNode)
                            {
                                document.AttributesTruncated = true;
                                continue;
                            }

                            string value = reader.Value ?? string.Empty;
                            if (value.Length > MaximumAttributeValueLength)
                            {
                                value = value.Substring(0, MaximumAttributeValueLength);
                                document.AttributesTruncated = true;
                            }
                            node.attributes.Add(new UIToolkitAttributeData { name = reader.Name, value = value });

                            if (reader.LocalName.IndexOf("binding-path", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                document.Bindings.Add(new UIToolkitBindingData
                                {
                                    path = document.Source.AssetPath,
                                    line = line,
                                    element = node.name,
                                    attribute = reader.Name,
                                    bindingPath = reader.Value ?? string.Empty,
                                });
                            }
                        }
                        reader.MoveToElement();
                    }

                    document.Nodes.Add(node);
                    parentByDepth[depth] = node.index;
                    RemoveDeeperParents(parentByDepth, depth);
                    CaptureReference(document, node, line);
                }
            }
            catch (XmlException exception)
            {
                string diagnosticCode = exception.Message.IndexOf("DTD", StringComparison.OrdinalIgnoreCase) >= 0
                    ? "DTD_PROHIBITED"
                    : "UXML_PARSE_ERROR";
                document.Diagnostics.Add(new UIToolkitSourceDiagnostic
                {
                    severity = "error",
                    code = diagnosticCode,
                    path = document.Source.AssetPath,
                    line = exception.LineNumber,
                    column = exception.LinePosition,
                    message = exception.Message,
                });
            }
        }

        static void CaptureReference(UIToolkitSourceDocument document, UIToolkitNodeData node, int line)
        {
            bool isStyle = node.type.Equals("Style", StringComparison.OrdinalIgnoreCase);
            bool isTemplate = node.type.Equals("Template", StringComparison.OrdinalIgnoreCase);
            if (!isStyle && !isTemplate)
                return;

            UIToolkitAttributeData sourceAttribute = node.attributes.FirstOrDefault(item => item.name == "src");
            if (sourceAttribute == null)
                return;

            string kind = isStyle ? "style" : "template";
            bool resolved = UIToolkitPathGuard.TryResolveReference(document.Source.AssetPath, sourceAttribute.value, out string resolvedPath);
            document.References.Add(new UIToolkitReferenceData
            {
                owner = document.Source.AssetPath,
                line = line,
                kind = kind,
                value = sourceAttribute.value,
                resolvedPath = resolved ? resolvedPath : string.Empty,
                exists = resolved && File.Exists(Path.Combine(UIToolkitPathGuard.ProjectRoot(), resolvedPath.Replace('/', Path.DirectorySeparatorChar))),
            });
        }

        static void LoadLinkedStyles(
            UIToolkitSourceDocument document,
            IReadOnlyDictionary<string, string> virtualContents,
            bool includeLinkedStyles,
            int maximumSelectors)
        {
            foreach (UIToolkitReferenceData reference in document.References.Where(item => item.kind == "style"))
            {
                if (string.IsNullOrEmpty(reference.resolvedPath))
                {
                    document.StyleSheets.Add(new UIToolkitStyleSheetData { path = reference.value, missing = true });
                    continue;
                }

                bool existsVirtually = virtualContents != null && virtualContents.ContainsKey(reference.resolvedPath);
                reference.exists = reference.exists || existsVirtually;
                if (!reference.exists)
                {
                    document.StyleSheets.Add(new UIToolkitStyleSheetData { path = reference.resolvedPath, missing = true });
                    continue;
                }

                if (!includeLinkedStyles)
                {
                    document.StyleSheets.Add(new UIToolkitStyleSheetData { path = reference.resolvedPath, missing = false });
                    continue;
                }

                if (!TryReadSource(reference.resolvedPath, virtualContents, out UIToolkitFileSnapshot styleSource, out string code, out string message))
                {
                    document.Diagnostics.Add(new UIToolkitSourceDiagnostic
                    {
                        severity = "error",
                        code = code,
                        path = reference.resolvedPath,
                        message = message,
                    });
                    document.StyleSheets.Add(new UIToolkitStyleSheetData { path = reference.resolvedPath, missing = true });
                    continue;
                }

                int selectorCountBefore = document.Selectors.Count;
                ParseUss(document, reference.resolvedPath, styleSource.Text, maximumSelectors);
                document.StyleSheets.Add(new UIToolkitStyleSheetData
                {
                    path = reference.resolvedPath,
                    hash = styleSource.Hash,
                    bytes = styleSource.Bytes.Length,
                    selectors = document.Selectors.Count - selectorCountBefore,
                    missing = false,
                });
            }
        }

        static void ParseUss(UIToolkitSourceDocument document, string path, string source, int maximumSelectors)
        {
            string text = RemoveCommentsPreservingLines(source, document.Diagnostics, path);
            ParseRuleRange(document, path, text, 0, text.Length, maximumSelectors);
        }

        static void ParseRuleRange(UIToolkitSourceDocument document, string path, string text, int start, int end, int maximumSelectors)
        {
            int cursor = start;
            while (cursor < end)
            {
                SkipWhitespaceAndSemicolons(text, ref cursor, end);
                if (cursor >= end)
                    return;

                if (text.IndexOf("@import", cursor, StringComparison.OrdinalIgnoreCase) == cursor)
                {
                    int importEnd = FindOutsideString(text, ';', cursor, end);
                    if (importEnd < 0)
                    {
                        AddUssDiagnostic(document, path, text, cursor, "USS_INVALID_IMPORT", "USS @import is missing a terminating semicolon.");
                        return;
                    }
                    cursor = importEnd + 1;
                    continue;
                }

                int openBrace = FindOutsideString(text, '{', cursor, end);
                if (openBrace < 0)
                {
                    string trailing = text.Substring(cursor, end - cursor).Trim();
                    if (trailing.Length > 0 && !trailing.StartsWith("@import", StringComparison.OrdinalIgnoreCase))
                        AddUssDiagnostic(document, path, text, cursor, "USS_EXPECTED_BLOCK", "USS content is missing an opening brace.");
                    return;
                }

                string header = text.Substring(cursor, openBrace - cursor).Trim();
                int closeBrace = FindMatchingBrace(text, openBrace, end);
                if (closeBrace < 0)
                {
                    AddUssDiagnostic(document, path, text, openBrace, "USS_UNCLOSED_BLOCK", "USS block is missing a closing brace.");
                    return;
                }

                if (header.StartsWith("@", StringComparison.Ordinal))
                {
                    ParseRuleRange(document, path, text, openBrace + 1, closeBrace, maximumSelectors);
                }
                else
                {
                    AddSelectorBlock(document, path, text, header, cursor, openBrace + 1, closeBrace, maximumSelectors);
                }
                cursor = closeBrace + 1;
            }
        }

        static void AddSelectorBlock(
            UIToolkitSourceDocument document,
            string path,
            string text,
            string header,
            int headerIndex,
            int bodyStart,
            int bodyEnd,
            int maximumSelectors)
        {
            if (string.IsNullOrWhiteSpace(header))
            {
                AddUssDiagnostic(document, path, text, headerIndex, "USS_EMPTY_SELECTOR", "USS rule has an empty selector.");
                return;
            }

            List<UIToolkitDeclarationData> declarations = ParseDeclarations(document, path, text, bodyStart, bodyEnd);
            foreach (string selectorText in SplitOutside(text: header, separator: ','))
            {
                string selector = selectorText.Trim();
                if (selector.Length == 0)
                    continue;
                if (document.Selectors.Count >= maximumSelectors)
                {
                    document.SelectorsTruncated = true;
                    return;
                }
                document.Selectors.Add(new UIToolkitSelectorData
                {
                    path = path,
                    selector = selector,
                    line = LineAt(text, headerIndex),
                    declarations = declarations.Select(item => new UIToolkitDeclarationData
                    {
                        property = item.property,
                        value = item.value,
                        line = item.line,
                    }).ToList(),
                });
            }
        }

        static List<UIToolkitDeclarationData> ParseDeclarations(
            UIToolkitSourceDocument document,
            string path,
            string text,
            int start,
            int end)
        {
            var declarations = new List<UIToolkitDeclarationData>();
            foreach ((string value, int offset) segment in SplitOutsideWithOffsets(text, start, end, ';'))
            {
                string declaration = segment.value.Trim();
                if (declaration.Length == 0)
                    continue;
                int colon = FindOutsideString(declaration, ':', 0, declaration.Length);
                if (colon <= 0)
                {
                    AddUssDiagnostic(document, path, text, segment.offset, "USS_INVALID_DECLARATION", "USS declaration must contain a property and value separated by a colon.");
                    continue;
                }
                string property = declaration.Substring(0, colon).Trim();
                string value = declaration.Substring(colon + 1).Trim();
                if (value.Length == 0)
                {
                    AddUssDiagnostic(document, path, text, segment.offset, "USS_EMPTY_VALUE", $"USS property '{property}' has no value.");
                    continue;
                }
                declarations.Add(new UIToolkitDeclarationData
                {
                    property = property,
                    value = value,
                    line = LineAt(text, segment.offset),
                });
            }
            return declarations;
        }

        static string RemoveCommentsPreservingLines(string source, List<UIToolkitSourceDiagnostic> diagnostics, string path)
        {
            var builder = new StringBuilder(source);
            int cursor = 0;
            while (cursor < builder.Length - 1)
            {
                if (builder[cursor] != '/' || builder[cursor + 1] != '*')
                {
                    cursor++;
                    continue;
                }
                int close = source.IndexOf("*/", cursor + 2, StringComparison.Ordinal);
                if (close < 0)
                {
                    diagnostics.Add(new UIToolkitSourceDiagnostic
                    {
                        severity = "error",
                        code = "USS_UNCLOSED_COMMENT",
                        path = path,
                        line = LineAt(source, cursor),
                        message = "USS comment is missing its closing delimiter.",
                    });
                    close = builder.Length - 2;
                }
                for (int index = cursor; index < Math.Min(close + 2, builder.Length); index++)
                    if (builder[index] != '\n' && builder[index] != '\r') builder[index] = ' ';
                cursor = close + 2;
            }
            return builder.ToString();
        }

        static void AddUssDiagnostic(UIToolkitSourceDocument document, string path, string text, int index, string code, string message)
        {
            document.Diagnostics.Add(new UIToolkitSourceDiagnostic
            {
                severity = "error",
                code = code,
                path = path,
                line = LineAt(text, index),
                column = ColumnAt(text, index),
                message = message,
            });
        }

        static int FindMatchingBrace(string text, int openBrace, int end)
        {
            int depth = 0;
            char quote = '\0';
            bool escaped = false;
            for (int index = openBrace; index < end; index++)
            {
                char value = text[index];
                if (quote != '\0')
                {
                    if (escaped) escaped = false;
                    else if (value == '\\') escaped = true;
                    else if (value == quote) quote = '\0';
                    continue;
                }
                if (value == '\'' || value == '"') quote = value;
                else if (value == '{') depth++;
                else if (value == '}' && --depth == 0) return index;
            }
            return -1;
        }

        static int FindOutsideString(string text, char target, int start, int end)
        {
            char quote = '\0';
            bool escaped = false;
            int parentheses = 0;
            for (int index = start; index < end; index++)
            {
                char value = text[index];
                if (quote != '\0')
                {
                    if (escaped) escaped = false;
                    else if (value == '\\') escaped = true;
                    else if (value == quote) quote = '\0';
                    continue;
                }
                if (value == '\'' || value == '"') quote = value;
                else if (value == '(') parentheses++;
                else if (value == ')' && parentheses > 0) parentheses--;
                else if (value == target && parentheses == 0) return index;
            }
            return -1;
        }

        static IEnumerable<string> SplitOutside(string text, char separator)
        {
            foreach ((string value, int _) in SplitOutsideWithOffsets(text, 0, text.Length, separator))
                yield return value;
        }

        static IEnumerable<(string value, int offset)> SplitOutsideWithOffsets(string text, int start, int end, char separator)
        {
            int segmentStart = start;
            char quote = '\0';
            bool escaped = false;
            int parentheses = 0;
            for (int index = start; index < end; index++)
            {
                char value = text[index];
                if (quote != '\0')
                {
                    if (escaped) escaped = false;
                    else if (value == '\\') escaped = true;
                    else if (value == quote) quote = '\0';
                    continue;
                }
                if (value == '\'' || value == '"') quote = value;
                else if (value == '(') parentheses++;
                else if (value == ')' && parentheses > 0) parentheses--;
                else if (value == separator && parentheses == 0)
                {
                    yield return (text.Substring(segmentStart, index - segmentStart), segmentStart);
                    segmentStart = index + 1;
                }
            }
            yield return (text.Substring(segmentStart, end - segmentStart), segmentStart);
        }

        static void SkipWhitespaceAndSemicolons(string text, ref int cursor, int end)
        {
            while (cursor < end && (char.IsWhiteSpace(text[cursor]) || text[cursor] == ';')) cursor++;
        }

        static int LineAt(string text, int index)
        {
            int line = 1;
            for (int cursor = 0; cursor < Math.Min(index, text.Length); cursor++)
                if (text[cursor] == '\n') line++;
            return line;
        }

        static int ColumnAt(string text, int index)
        {
            int lastLine = text.LastIndexOf('\n', Math.Max(0, Math.Min(index, text.Length - 1)));
            return index - lastLine;
        }

        static string[] SplitClasses(string value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? Array.Empty<string>()
                : value.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        }

        static void RemoveDeeperParents(Dictionary<int, int> parentByDepth, int currentDepth)
        {
            foreach (int depth in parentByDepth.Keys.Where(depth => depth > currentDepth).ToArray())
                parentByDepth.Remove(depth);
        }

        static int Clamp(int value, int minimum, int maximum, int defaultValue)
        {
            if (value == 0) return defaultValue;
            return Math.Max(minimum, Math.Min(maximum, value));
        }
    }

    internal static class UIToolkitJson
    {
        [Serializable]
        sealed class ErrorEnvelope
        {
            public bool ok;
            public string status;
            public ErrorBody error;
        }

        [Serializable]
        sealed class ErrorBody
        {
            public string code;
            public string message;
            public string recovery;
            public string details;
        }

        internal static string Error(string code, string message, string recovery, string details = "")
        {
            return JsonUtility.ToJson(new ErrorEnvelope
            {
                ok = false,
                status = "blocked",
                error = new ErrorBody
                {
                    code = string.IsNullOrEmpty(code) ? "INVALID_REQUEST" : code,
                    message = message ?? "The request could not be completed.",
                    recovery = recovery ?? "Correct the request and retry.",
                    details = details ?? string.Empty,
                },
            });
        }
    }
}
