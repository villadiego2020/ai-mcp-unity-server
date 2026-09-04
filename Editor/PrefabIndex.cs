using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UnityEngine;

namespace MCPBridge
{
    /// <summary>
    /// </summary>
    public static class PrefabIndex
    {
        public struct PrefabEntry { public string Name; public string Path; }

        static Dictionary<string, List<string>> _map;
        static List<PrefabEntry> _all;
        static volatile bool _building;

        public static bool Ready => _map != null;
        public static bool Building => _building;

        static readonly Regex _scriptRef =
            new Regex(@"m_Script:\s*\{fileID:\s*11500000,\s*guid:\s*([0-9a-f]{32})", RegexOptions.Compiled);

        public static void RefreshAsync()
        {
            if (_building) return;
            _building = true;
            string assetsPath = Application.dataPath;
            Task.Run(() => Build(assetsPath));
        }

        static void Build(string assetsPath)
        {
            try
            {
                var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                var all = new List<PrefabEntry>();
                foreach (var file in Directory.EnumerateFiles(assetsPath, "*.prefab", SearchOption.AllDirectories))
                {
                    string rel = "Assets" + file.Substring(assetsPath.Length).Replace('\\', '/');
                    all.Add(new PrefabEntry { Name = Path.GetFileNameWithoutExtension(file), Path = rel });

                    string text;
                    try { text = File.ReadAllText(file); }
                    catch { continue; }
                    foreach (Match m in _scriptRef.Matches(text))
                    {
                        string guid = m.Groups[1].Value;
                        if (!map.TryGetValue(guid, out var list)) { list = new List<string>(); map[guid] = list; }
                        if (!list.Contains(rel)) list.Add(rel);
                    }
                }
                _all = all.OrderBy(e => e.Name).ToList();
                _map = map;
            }
            catch { _map = new Dictionary<string, List<string>>(); }
            finally { _building = false; }
        }

        public static List<string> PrefabsUsing(string scriptGuid)
        {
            if (_map == null || string.IsNullOrEmpty(scriptGuid)) return new List<string>();
            return _map.TryGetValue(scriptGuid, out var list) ? new List<string>(list) : new List<string>();
        }

        public static List<PrefabEntry> Search(string query, int max = 8)
        {
            if (_all == null) return new List<PrefabEntry>();
            if (string.IsNullOrEmpty(query)) return _all.Take(max).ToList();
            string q = query.ToLowerInvariant();
            return _all
                .Where(e => e.Name.ToLowerInvariant().Contains(q))
                .OrderBy(e => e.Name.ToLowerInvariant().IndexOf(q))
                .ThenBy(e => e.Name.Length)
                .Take(max)
                .ToList();
        }

        public static string ResolvePath(string name)
        {
            if (_all == null || string.IsNullOrEmpty(name)) return null;
            var hit = _all.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
            return string.IsNullOrEmpty(hit.Path) ? null : hit.Path;
        }
    }
}
