using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json;

namespace AIUnityMCPServer
{
    internal sealed class ManagedIntegrationException : InvalidOperationException
    {
        internal readonly string Code;
        internal ManagedIntegrationException(string code, string message) : base(message) { Code = code; }
    }

    internal sealed class ManagedSnapshotResult
    {
        internal readonly string Revision, BaselineHash;
        internal ManagedSnapshotResult(string revision, string baselineHash = null) { Revision = revision; BaselineHash = baselineHash; }
    }

    [Serializable] internal sealed class ManagedIntegrationFile
    {
        public string path, kind, hash, guid;
        public long bytes;
    }
    [Serializable] internal sealed class ManagedIntegrationBaseline
    {
        public int schemaVersion = 1;
        public List<ManagedIntegrationFile> files;
    }
    [Serializable] internal sealed class ManagedIntegrationChange
    {
        public string path, kind, operation, status, baselineHash, privateHash, mainHash, payloadHash, reason;
        public long bytes;
    }
    [Serializable] internal sealed class ManagedIntegrationPlan
    {
        public int schemaVersion = 1;
        public string id, sessionId, baselineHash, mainSnapshotHash, privateSnapshotHash;
        public long created;
        public int mainOnlyChanges;
        public List<ManagedIntegrationChange> entries = new List<ManagedIntegrationChange>();
    }

    // Filesystem-only planning. Every write is confined to a new private sidecar;
    // there is deliberately no Main mutation, import, refresh or apply operation here.
    internal static partial class ManagedSessionIntegration
    {
        internal const int MaximumEntries = ManagedSessionFiles.MaximumFiles;
        internal const int MaximumMetadataBytes = 32 * 1024 * 1024;
        static readonly string[] Scopes = { "Assets", "Packages", "ProjectSettings", "ManagedVendor" };
        static readonly Regex HashPattern = new Regex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);
        static readonly Regex GuidPattern = new Regex(@"(?m)^guid: ([0-9a-fA-F]{32})\r?$", RegexOptions.CultureInvariant);

        internal static ManagedSnapshotResult Snapshot(string main, string workspace, string sidecar, CancellationToken token)
        {
            EnsureSeparate(main, workspace, sidecar);
            string revision = ManagedSessionFiles.Snapshot(main, workspace, true, token);
            var originalAssets = Scan(main, true, false, token);
            var privateFiles = Scan(workspace, false, true, token);
            RequireSame(originalAssets, Assets(privateFiles), "The copied Assets do not match Main; retry after source edits finish.");
            RequireSame(originalAssets, Scan(main, true, false, token), "Main Assets changed during baseline capture.");
            RequireSame(privateFiles, Scan(workspace, false, true, token), "Private source changed during baseline capture.");
            var baseline = new ManagedIntegrationBaseline { files = privateFiles };
            string content = Serialize(baseline);
            string hash = ManagedSessionFiles.HashText(content);
            CreateDirectory(sidecar);
            string path = BaselinePath(sidecar, hash);
            if (!File.Exists(path)) WriteNew(path, content);
            else ReadDocument<ManagedIntegrationBaseline>(path, hash, "BASELINE_UNAVAILABLE", "BASELINE_INTEGRITY");
            return new ManagedSnapshotResult(revision, hash);
        }

        internal static string BaselinePath(string sidecar, string hash)
        {
            if (string.IsNullOrEmpty(hash)) throw new ManagedIntegrationException("BASELINE_UNAVAILABLE", "This session has no captured integration baseline. Its private source remains available; a baseline cannot be reconstructed from current Main.");
            if (!HashPattern.IsMatch(hash)) throw new ManagedIntegrationException("BASELINE_INTEGRITY", "The integration baseline identity is invalid.");
            return Path.Combine(sidecar, "b-" + hash + ".json");
        }

        internal static string CreatePlan(string main, string workspace, string sidecar, string baselineHash, string planDirectory,
            string planId, string sessionId, CancellationToken token)
        {
            EnsureSeparate(main, workspace, sidecar);
            EnsureOutside(main, planDirectory); EnsureOutside(workspace, planDirectory);
            var baseline = ReadDocument<ManagedIntegrationBaseline>(BaselinePath(sidecar, baselineHash), baselineHash, "BASELINE_UNAVAILABLE", "BASELINE_INTEGRITY");
            if (baseline == null || baseline.schemaVersion != 1 || baseline.files == null) throw new ManagedIntegrationException("BASELINE_INTEGRITY", "Unsupported or incomplete integration baseline.");
            var original = Index(baseline.files);
            var privateFiles = Scan(workspace, false, true, token);
            var mainFiles = Scan(main, true, false, token);
            var current = Index(privateFiles);
            var mainIndex = Index(mainFiles);
            ValidatePairs(Assets(privateFiles)); ValidatePairs(mainFiles);
            var plan = new ManagedIntegrationPlan
            {
                id = planId, sessionId = sessionId, baselineHash = baselineHash, created = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                mainSnapshotHash = ManifestHash(mainFiles), privateSnapshotHash = ManifestHash(privateFiles)
            };
            var projected = new Dictionary<string, ManagedIntegrationFile>(mainIndex, StringComparer.OrdinalIgnoreCase);
            foreach (string path in original.Keys.Union(current.Keys, StringComparer.OrdinalIgnoreCase).Union(mainIndex.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(item => item, StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested();
                original.TryGetValue(path, out var before); current.TryGetValue(path, out var desired); mainIndex.TryGetValue(path, out var actual);
                bool asset = path.StartsWith("Assets/", StringComparison.Ordinal);
                if (Same(before, desired)) { if (asset && !Same(before, actual)) plan.mainOnlyChanges++; continue; }
                if (asset && new[] { before, desired, actual }.Where(item => item != null).Select(item => item.kind).Distinct().Count() > 1)
                    throw new ManagedIntegrationException("ASSET_TYPE_CONFLICT", "File/directory replacement requires manual review: " + path);
                string status = !asset ? "unsupported" : Same(desired, actual) ? "already_applied" : Same(before, actual) ? "ready" : "conflict";
                var change = new ManagedIntegrationChange
                {
                    path = path, kind = (desired ?? before).kind, operation = before == null ? "added" : desired == null ? "deleted" : "modified",
                    status = status, baselineHash = Value(before), privateHash = Value(desired), mainHash = asset ? Value(actual) : "unsupported_scope", bytes = desired?.bytes ?? 0
                };
                if (plan.entries.Count >= MaximumEntries) throw new ManagedIntegrationException("INTEGRATION_LIMIT", "Integration preview exceeds 50,000 changed file/directory entries; split the work before planning.");
                plan.entries.Add(change);
                if (asset) { if (desired == null) projected.Remove(path); else projected[path] = desired; }
            }
            foreach (var change in plan.entries.Where(item => item.kind == "directory" && item.operation == "deleted"))
                if (HasUnreviewedDescendants(main, change.path, plan.entries, token))
                {
                    change.status = "conflict"; change.reason = "Main contains unreviewed descendants, including any excluded or hidden entries.";
                    // Preserve the conflicting Main parent in the validation projection so
                    // its children remain an inspectable conflict rather than an orphan error.
                    if (mainIndex.TryGetValue(change.path, out var folder)) projected[change.path] = folder;
                    if (mainIndex.TryGetValue(change.path + ".meta", out var metadata)) projected[change.path + ".meta"] = metadata;
                }
            ValidatePairs(projected.Values.OrderBy(item => item.path, StringComparer.Ordinal).ToList());
            if (Directory.Exists(planDirectory)) throw new ManagedIntegrationException("PLAN_EXISTS", "Integration plans are immutable; use a new request ID.");
            CreateDirectory(planDirectory);
            foreach (var change in plan.entries.Where(item => item.path.StartsWith("Assets/", StringComparison.Ordinal) && item.kind == "file" && item.operation != "deleted"))
            {
                token.ThrowIfCancellationRequested();
                string payload = Path.Combine(planDirectory, change.privateHash + ".bin");
                if (!File.Exists(payload)) CopyPayload(ManagedSessionFiles.AssetPath(workspace, change.path), payload, change.privateHash, change.bytes, token);
                change.payloadHash = change.privateHash;
            }
            RequireSame(privateFiles, Scan(workspace, false, true, token), "Private source changed during integration planning; request a new preview.");
            RequireSame(mainFiles, Scan(main, true, false, token), "Main Assets changed during integration planning; request a new preview.");
            ReadDocument<ManagedIntegrationBaseline>(BaselinePath(sidecar, baselineHash), baselineHash, "BASELINE_UNAVAILABLE", "BASELINE_INTEGRITY");
            string content = Serialize(plan);
            WriteNew(Path.Combine(planDirectory, "plan.json"), content);
            return ManagedSessionFiles.HashText(content);
        }

        internal static ManagedIntegrationPlan ReadPlan(string directory, string expectedHash)
        {
            var plan = ReadDocument<ManagedIntegrationPlan>(Path.Combine(directory, "plan.json"), expectedHash, "PLAN_UNAVAILABLE", "PLAN_INTEGRITY");
            if (plan == null || plan.schemaVersion != 1 || plan.entries == null || plan.entries.Count > MaximumEntries)
                throw new ManagedIntegrationException("PLAN_INTEGRITY", "Unsupported integration plan.");
            return plan;
        }

        static T ReadDocument<T>(string path, string expectedHash, string missingCode, string integrityCode)
        {
            if (!File.Exists(path)) throw new ManagedIntegrationException(missingCode, "The private integration document is unavailable; source was preserved.");
            RejectPathLinks(path);
            byte[] bytes;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var memory = new MemoryStream())
            {
                if (stream.Length > MaximumMetadataBytes) throw new ManagedIntegrationException(integrityCode, "Integration metadata exceeds its 32 MiB limit.");
                var buffer = new byte[65536]; int count;
                while ((count = stream.Read(buffer, 0, buffer.Length)) != 0)
                {
                    if (memory.Length + count > MaximumMetadataBytes) throw new ManagedIntegrationException(integrityCode, "Integration metadata exceeds its 32 MiB limit.");
                    memory.Write(buffer, 0, count);
                }
                bytes = memory.ToArray();
            }
            if (ManagedSessionFiles.Hash(bytes) != expectedHash) throw new ManagedIntegrationException(integrityCode, "The private integration document changed after capture.");
            try { return JsonConvert.DeserializeObject<T>(new UTF8Encoding(false, true).GetString(bytes)); }
            catch (JsonException exception) { throw new ManagedIntegrationException(integrityCode, "Invalid integration metadata: " + exception.Message); }
        }

        static List<ManagedIntegrationFile> Assets(IEnumerable<ManagedIntegrationFile> files) => files.Where(item => item.path.StartsWith("Assets/", StringComparison.Ordinal)).ToList();
        static string Value(ManagedIntegrationFile item) => item?.hash ?? "missing";
        static bool Same(ManagedIntegrationFile a, ManagedIntegrationFile b) => a == null ? b == null : b != null && a.kind == b.kind && a.hash == b.hash;
        static string ManifestHash(List<ManagedIntegrationFile> files) => ManagedSessionFiles.HashText(Serialize(files));
        static void RequireSame(List<ManagedIntegrationFile> before, List<ManagedIntegrationFile> after, string message)
        { if (ManifestHash(before) != ManifestHash(after)) throw new ManagedIntegrationException("SOURCE_CHANGED", message); }

        static Dictionary<string, ManagedIntegrationFile> Index(List<ManagedIntegrationFile> files, bool verifyHashes = true)
        {
            if (files.Count > MaximumEntries) throw new ManagedIntegrationException("INTEGRATION_LIMIT", "Integration inventory exceeds 50,000 file/directory entries.");
            var index = new Dictionary<string, ManagedIntegrationFile>(StringComparer.OrdinalIgnoreCase);
            long total = 0;
            foreach (var file in files)
            {
                ValidateRelative(file.path);
                if ((file.kind != "file" && file.kind != "directory") || file.bytes < 0 || (file.kind == "file" && verifyHashes && !HashPattern.IsMatch(file.hash ?? "")) || (file.kind == "directory" && (file.hash != "directory" || file.bytes != 0)))
                    throw new ManagedIntegrationException("BASELINE_INTEGRITY", "Invalid file identity in integration metadata.");
                if (index.ContainsKey(file.path)) throw new ManagedIntegrationException("PATH_COLLISION", "Case-insensitive path collision: " + file.path);
                total += file.bytes;
                ManagedSessionFiles.ValidateSnapshotMetrics(total, file.bytes, index.Count + 1);
                index.Add(file.path, file);
            }
            return index;
        }

        static void ValidateRelative(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.Contains('\\') || !Scopes.Any(scope => path.StartsWith(scope + "/", StringComparison.Ordinal)) ||
                path.Split('/').Any(part => part.Length == 0 || part == "." || part == ".." || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || Excluded(part)))
                throw new ManagedIntegrationException("UNSAFE_PATH", "Integration supports only safe, non-secret relative source paths.");
        }

        static bool Excluded(string name) => ManagedSessionFiles.IsExcluded(name) || (name.EndsWith(".meta", StringComparison.OrdinalIgnoreCase) && ManagedSessionFiles.IsExcluded(name.Substring(0, name.Length - 5)));

        internal static List<ManagedIntegrationFile> Scan(string project, bool assetsOnly, bool rejectExcluded, CancellationToken token)
        {
            RejectPathLinks(project);
            var files = new List<ManagedIntegrationFile>();
            long total = 0;
            foreach (string scope in assetsOnly ? new[] { "Assets" } : Scopes)
            {
                string directory = Path.Combine(project, scope);
                if (!Directory.Exists(directory)) continue;
                Collect(directory, scope, files, ref total, rejectExcluded, token);
            }
            files.Sort((a, b) => StringComparer.Ordinal.Compare(a.path, b.path));
            Index(files);
            return files;
        }

        static void Collect(string directory, string relative, List<ManagedIntegrationFile> files, ref long total, bool rejectExcluded, CancellationToken token)
        {
            ManagedSessionFiles.RejectLink(directory);
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory).OrderBy(item => item, StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested();
                ManagedSessionFiles.RejectLink(entry);
                string name = Path.GetFileName(entry);
                if (Excluded(name))
                {
                    if (rejectExcluded) throw new ManagedIntegrationException("UNSAFE_PATH", "Excluded or potentially sensitive private source cannot be published: " + relative + "/" + name);
                    continue;
                }
                string path = relative + "/" + name;
                ValidateRelative(path); ManagedSessionFiles.CheckPathLength(entry);
                bool directoryEntry = Directory.Exists(entry);
                long length = directoryEntry ? 0 : new FileInfo(entry).Length;
                total += length;
                ManagedSessionFiles.ValidateSnapshotMetrics(total, length, files.Count + 1);
                var file = new ManagedIntegrationFile { path = path, kind = directoryEntry ? "directory" : "file", bytes = length, hash = directoryEntry ? "directory" : HashFile(entry, token) };
                if (!directoryEntry && path.StartsWith("Assets/", StringComparison.Ordinal) && name.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) file.guid = ReadGuid(entry);
                files.Add(file);
                if (directoryEntry) Collect(entry, path, files, ref total, rejectExcluded, token);
            }
        }

        static string ReadGuid(string path)
        {
            var header = new byte[4096];
            int count;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) count = stream.Read(header, 0, header.Length);
            var match = GuidPattern.Match(Encoding.ASCII.GetString(header, 0, count));
            return match.Success ? match.Groups[1].Value.ToLowerInvariant() : null;
        }

        static void ValidatePairs(List<ManagedIntegrationFile> files, bool verifyHashes = true)
        {
            var index = Index(files, verifyHashes);
            var guids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in files)
            {
                string parent = file.path.Substring(0, file.path.LastIndexOf('/'));
                if (parent != "Assets" && (!index.TryGetValue(parent, out var directory) || directory.kind != "directory"))
                    throw new ManagedIntegrationException("ASSET_PARENT_MISSING", "Reviewed asset parent is missing or is not a directory: " + parent);
                if (file.path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                {
                    string asset = file.path.Substring(0, file.path.Length - 5);
                    if (file.kind != "file" || !index.ContainsKey(asset) || string.IsNullOrEmpty(file.guid))
                        throw new ManagedIntegrationException("ASSET_META_INCOMPLETE", "Asset/meta pair or GUID is incomplete: " + file.path);
                    if (guids.TryGetValue(file.guid, out string other)) throw new ManagedIntegrationException("GUID_CONFLICT", "Duplicate asset GUID: " + other + " and " + asset);
                    guids.Add(file.guid, asset);
                }
                else if (!index.TryGetValue(file.path + ".meta", out var meta) || meta.kind != "file")
                    throw new ManagedIntegrationException("ASSET_META_INCOMPLETE", "Asset/meta pair is incomplete: " + file.path);
            }
        }

        internal static string HashFile(string path, CancellationToken token)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (ManagedSourceHash.UseNative(stream.Length)) return BitConverter.ToString(ManagedSourceHash.NativeDigest(stream, token)).Replace("-", "").ToLowerInvariant();
                using (var sha = SHA256.Create())
                {
                    var buffer = new byte[65536]; int count;
                    while ((count = stream.Read(buffer, 0, buffer.Length)) != 0) { token.ThrowIfCancellationRequested(); sha.TransformBlock(buffer, 0, count, buffer, 0); }
                    sha.TransformFinalBlock(new byte[0], 0, 0);
                    return BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();
                }
            }
        }

        static void CopyPayload(string source, string destination, string expectedHash, long expectedBytes, CancellationToken token)
        {
            ManagedSessionFiles.CheckPathLength(destination);
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                if (input.Length != expectedBytes) throw new ManagedIntegrationException("SOURCE_CHANGED", "Private payload length changed during planning.");
                var buffer = new byte[65536]; int count;
                while ((count = input.Read(buffer, 0, buffer.Length)) != 0) { token.ThrowIfCancellationRequested(); output.Write(buffer, 0, count); }
            }
            if (HashFile(destination, token) != expectedHash) throw new ManagedIntegrationException("SOURCE_CHANGED", "Private payload changed during planning.");
        }

        static string Serialize(object value)
        {
            string result = JsonConvert.SerializeObject(value, Formatting.None);
            if (Encoding.UTF8.GetByteCount(result) > MaximumMetadataBytes) throw new ManagedIntegrationException("INTEGRATION_LIMIT", "Integration metadata exceeds its 32 MiB budget.");
            return result;
        }

        static void WriteNew(string path, string content)
        {
            ManagedSessionFiles.CheckPathLength(path); RejectPathLinks(Path.GetDirectoryName(path));
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false))) writer.Write(content);
        }

        static void CreateDirectory(string path) { RejectPathLinks(path); Directory.CreateDirectory(path); ManagedSessionFiles.RejectLink(path); }
        static void RejectPathLinks(string path)
        {
            string current = Path.GetFullPath(path);
            while (!string.IsNullOrEmpty(current))
            {
                if (File.Exists(current) || Directory.Exists(current)) ManagedSessionFiles.RejectLink(current);
                current = Path.GetDirectoryName(current);
            }
        }
        static void EnsureSeparate(string main, string workspace, string sidecar)
        { EnsureOutside(main, workspace); EnsureOutside(workspace, main); EnsureOutside(main, sidecar); EnsureOutside(workspace, sidecar); }
        static void EnsureOutside(string parent, string candidate)
        {
            string root = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string path = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (path.Equals(root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new ManagedIntegrationException("UNSAFE_STORAGE", "Integration sidecars and source projects must have separate roots.");
        }
    }
}
