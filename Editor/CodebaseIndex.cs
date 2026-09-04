using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace AIUnityMCPServer
{
    /// <summary>
    /// </summary>
    public static class CodebaseIndex
    {
        public struct ScriptEntry
        {
            public string Name;
            public string Path;
        }

        static List<ScriptEntry> _cache;
        static Dictionary<string, string> _byName;
        static Dictionary<string, List<string>> _byType;

        static readonly string[] IncludeRoots = { "Assets" };

        static readonly string[] ExcludeContains =
        {
            "/AIUnityMCPServer/", "/PlayFabSDK/", "/Photon/", "/CBS/", "/GPUInstancer/",
            "/Plugins/", "/MeshBaker/", "/ProBuilder", "/Polybrush", "/TextMesh Pro/",
            "/NuGet/", "/StandaloneFileBrowser/", "/Hexasphere/", "/WorldMapStrategyKit/",
        };

        public static void Refresh()
        {
            _cache = new List<ScriptEntry>();
            _byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _byType = null;
            foreach (var guid in AssetDatabase.FindAssets("t:MonoScript", IncludeRoots))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) continue;
                if (IsExcluded(path)) continue;
                string fileName = System.IO.Path.GetFileName(path);
                _cache.Add(new ScriptEntry { Name = fileName, Path = path });
                if (!_byName.ContainsKey(fileName)) _byName[fileName] = path;
            }
            _cache = _cache.OrderBy(e => e.Name).ToList();
        }

        static bool IsExcluded(string path)
        {
            foreach (var ex in ExcludeContains)
                if (path.IndexOf(ex, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        public static List<ScriptEntry> Search(string query, int max = 8)
        {
            if (_cache == null) Refresh();
            if (string.IsNullOrEmpty(query))
                return _cache.Take(max).ToList();

            string q = query.ToLowerInvariant();
            return _cache
                .Where(e => e.Name.ToLowerInvariant().Contains(q))
                .OrderBy(e => e.Name.ToLowerInvariant().IndexOf(q))
                .ThenBy(e => e.Name.Length)
                .Take(max)
                .ToList();
        }

        public static string ResolvePath(string nameOrFile)
        {
            if (_cache == null) Refresh();
            string fileOnly = System.IO.Path.GetFileName(nameOrFile);
            string n = fileOnly.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ? fileOnly : fileOnly + ".cs";
            return _byName.TryGetValue(n, out string path) ? path : null;
        }

        static readonly Regex _declRe = new Regex(@"\b(?:class|interface|struct|enum)\s+([A-Za-z_]\w*)", RegexOptions.Compiled);

        static void EnsureTypeIndex()
        {
            if (_byType != null) return;
            if (_cache == null) Refresh();
            _byType = new Dictionary<string, List<string>>(StringComparer.Ordinal);   // type C# case-sensitive
            foreach (var e in _cache)
            {
                string content = ReadContent(e.Path, 100000);
                if (content == null) continue;
                string code = StripCommentsAndStrings(content);
                foreach (Match m in _declRe.Matches(code))
                {
                    string t = m.Groups[1].Value;
                    if (!_byType.TryGetValue(t, out var paths)) { paths = new List<string>(); _byType[t] = paths; }
                    if (!paths.Contains(e.Path)) paths.Add(e.Path);
                }
            }
        }

        static string StripCommentsAndStrings(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            s = Regex.Replace(s, @"""[^""\r\n]*""", " ");
            s = Regex.Replace(s, @"/\*[\s\S]*?\*/", " ");   // block comment
            s = Regex.Replace(s, @"//.*", " ");
            return s;
        }

        public static string ResolveTypePath(string typeName)
        {
            var all = ResolveTypePaths(typeName);
            return all.Count > 0 ? all[0] : null;
        }

        public static List<string> ResolveTypePaths(string typeName)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(typeName)) return result;
            EnsureTypeIndex();
            if (_byType.TryGetValue(typeName, out var paths)) result.AddRange(paths);
            else if (_byName.TryGetValue(typeName + ".cs", out string p2)) result.Add(p2);
            return result;
        }

        /// <summary>
        /// </summary>
        public static List<ScriptEntry> ResolveReferencedScripts(string source, string selfPath, int max = 6)
        {
            var result = new List<ScriptEntry>();
            if (_cache == null) Refresh();
            if (string.IsNullOrEmpty(source)) return result;
            EnsureTypeIndex();

            string code = StripCommentsAndStrings(source);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var seenPath = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in Regex.Matches(code, @"\b[A-Z][A-Za-z0-9_]{2,}\b"))
            {
                string name = m.Value;
                if (!seen.Add(name)) continue;
                var paths = ResolveTypePaths(name);
                if (paths.Count == 0) continue;
                foreach (var path in paths)
                {
                    if (string.Equals(path, selfPath, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!seenPath.Add(path)) continue;
                    result.Add(new ScriptEntry { Name = System.IO.Path.GetFileName(path), Path = path });
                    if (result.Count >= max) break;
                }
                if (result.Count >= max) break;
            }
            return result;
        }

        /// <summary>
        /// </summary>
        public static List<string> ResolveBaseTypes(string source)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(source)) return result;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                source, @"(?:class|interface|struct)\s+[A-Za-z_]\w*(?:<[^>]*>)?\s*:\s*([^\{\r\n]+)"))
            {
                string baseList = m.Groups[1].Value;
                int w = baseList.IndexOf(" where ", StringComparison.Ordinal);
                if (w >= 0) baseList = baseList.Substring(0, w);
                foreach (var raw in baseList.Split(','))
                {
                    string name = raw.Trim();
                    int lt = name.IndexOf('<'); if (lt >= 0) name = name.Substring(0, lt);
                    int dot = name.LastIndexOf('.'); if (dot >= 0) name = name.Substring(dot + 1);
                    name = name.Trim();
                    if (name.Length >= 2 && char.IsUpper(name[0]) && seen.Add(name))
                        result.Add(name);
                }
            }
            return result;
        }

        public static string ReadContent(string path, int maxChars = 16000)
        {
            try
            {
                string full = Path.Combine(Application.dataPath.Replace("Assets", ""), path);
                string text = File.ReadAllText(full);
                if (text.Length > maxChars)
                    text = text.Substring(0, maxChars) + "\n... (truncated)";
                return text;
            }
            catch { return null; }
        }
    }
}
