using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Newtonsoft.Json;

namespace AIUnityMCPServer
{
    [Serializable] internal sealed class ManagedApplyEntry
    {
        public string path, kind, beforeHash, afterHash, privateHash, staged, backup;
        public bool attempted, completed, skipWrite;
    }
    [Serializable] internal sealed class ManagedApplyPreparation
    {
        public int schemaVersion = 1;
        public string baselineHash, planHash, privateSnapshotHash;
        public List<ManagedApplyEntry> entries = new List<ManagedApplyEntry>();
        public ManagedIntegrationBaseline nextBaseline;
    }
    [Serializable] internal sealed class ManagedApplyJournal
    {
        public string state = "prepared", error, baselineAfterHash;
        public List<ManagedApplyEntry> entries;
        public List<string> recoveryConflicts = new List<string>();
    }
    internal sealed class ManagedApplyFailure : IOException
    {
        internal readonly bool RecoveryRequired;
        internal ManagedApplyFailure(string message, bool recoveryRequired, Exception inner) : base(message, inner) { RecoveryRequired = recoveryRequired; }
    }

    internal static partial class ManagedSessionIntegration
    {
        internal const int MaximumApplyEntries = 512;
        internal const long MaximumApplyBytes = 512L * 1024 * 1024;

        // Raw descendant enumeration deliberately does not use the snapshot exclusion policy:
        // a hidden/secret Main child must prevent deletion of its enclosing directory too.
        internal static bool HasUnreviewedDescendants(string main, string relative, List<ManagedIntegrationChange> changes, CancellationToken token)
        {
            string directory = ManagedSessionFiles.AssetPath(main, relative);
            if (!Directory.Exists(directory)) return false;
            var reviewed = new HashSet<string>(changes.Where(item => item.operation == "deleted" && item.path.StartsWith("Assets/", StringComparison.Ordinal)).Select(item => item.path), StringComparer.OrdinalIgnoreCase);
            var pending = new Stack<string>(); pending.Push(directory);
            int count = 0;
            while (pending.Count > 0)
            {
                string parent = pending.Pop(); ManagedSessionFiles.RejectLink(parent);
                foreach (string child in Directory.EnumerateFileSystemEntries(parent))
                {
                    token.ThrowIfCancellationRequested(); ManagedSessionFiles.RejectLink(child); ManagedSessionFiles.CheckPathLength(child);
                    if (++count > MaximumEntries) throw new ManagedIntegrationException("INTEGRATION_LIMIT", "Directory deletion preflight exceeds 50,000 descendants.");
                    string path = child.Substring(Path.GetFullPath(main).TrimEnd(Path.DirectorySeparatorChar).Length + 1).Replace('\\', '/');
                    if (!reviewed.Contains(path)) return true;
                    if (Directory.Exists(child)) pending.Push(child);
                }
            }
            return false;
        }

        internal static string PrepareApply(string main, string workspace, string sidecar, string baselineHash, string planDirectory, string planHash, string operationDirectory, CancellationToken token)
        {
            EnsureOutside(workspace, operationDirectory);
            var plan = ReadPlan(planDirectory, planHash);
            if (plan.baselineHash != baselineHash) throw new ManagedIntegrationException("STALE_BASELINE", "This preview references an older published baseline. Create a new preview.");
            if (plan.entries.Any(item => item.status == "conflict" || item.status == "unsupported")) throw new ManagedIntegrationException("PLAN_BLOCKED", "Resolve all conflicting and unsupported changes before publication.");
            if (plan.entries.Count > MaximumApplyEntries || plan.entries.Sum(item => item.bytes) > MaximumApplyBytes)
                throw new ManagedIntegrationException("APPLY_LIMIT", "Publication is limited to 512 changed entries and 512 MiB of desired file bytes. Split larger changes into separate reviews.");
            var privateFiles = Scan(workspace, false, true, token);
            if (ManifestHash(privateFiles) != plan.privateSnapshotHash) throw new ManagedIntegrationException("STALE_PRIVATE", "Private source changed after review. Create a new preview.");
            var mainFiles = Scan(main, true, false, token);
            var mainIndex = Index(mainFiles); var privateIndex = Index(privateFiles);
            var baseline = ReadDocument<ManagedIntegrationBaseline>(BaselinePath(sidecar, baselineHash), baselineHash, "BASELINE_UNAVAILABLE", "BASELINE_INTEGRITY");
            var next = Index(baseline.files);
            var projected = new Dictionary<string, ManagedIntegrationFile>(mainIndex, StringComparer.OrdinalIgnoreCase);
            foreach (var change in plan.entries)
            {
                token.ThrowIfCancellationRequested(); ValidateRelative(change.path);
                if (!change.path.StartsWith("Assets/", StringComparison.Ordinal)) throw new ManagedIntegrationException("PLAN_BLOCKED", "Only reviewed Assets changes can be published.");
                mainIndex.TryGetValue(change.path, out var current); privateIndex.TryGetValue(change.path, out var desired);
                if (Value(current) != change.mainHash || Value(desired) != change.privateHash) throw new ManagedIntegrationException("STALE_SOURCE", "Reviewed source changed: " + change.path);
                if (change.kind == "directory" && change.operation == "deleted" && HasUnreviewedDescendants(main, change.path, plan.entries, token))
                    throw new ManagedIntegrationException("UNREVIEWED_DESCENDANTS", "Main directory contains unreviewed descendants: " + change.path);
                if (desired == null) { projected.Remove(change.path); next.Remove(change.path); }
                else { projected[change.path] = desired; next[change.path] = desired; }
            }
            ValidatePairs(projected.Values.ToList());
            if (Directory.Exists(operationDirectory)) throw new ManagedIntegrationException("OPERATION_EXISTS", "Operation staging must be new.");
            CreateDirectory(operationDirectory);
            var prepared = new ManagedApplyPreparation { baselineHash = baselineHash, planHash = planHash, privateSnapshotHash = plan.privateSnapshotHash,
                nextBaseline = new ManagedIntegrationBaseline { files = next.Values.OrderBy(item => item.path, StringComparer.Ordinal).ToList() } };
            foreach (var change in plan.entries)
            {
                token.ThrowIfCancellationRequested();
                int index = prepared.entries.Count;
                var entry = new ManagedApplyEntry { path = change.path, kind = change.kind, beforeHash = change.mainHash, afterHash = change.privateHash, privateHash = change.privateHash,
                    staged = index.ToString("D4") + ".new", backup = index.ToString("D4") + ".old", skipWrite = change.status == "already_applied" };
                if (!entry.skipWrite && entry.kind == "file" && entry.beforeHash != "missing" && (File.GetAttributes(ManagedSessionFiles.AssetPath(main, entry.path)) & FileAttributes.ReadOnly) != 0)
                    throw new ManagedIntegrationException("TARGET_READ_ONLY", "Main target is read-only: " + entry.path);
                if (!entry.skipWrite && entry.kind == "file" && entry.afterHash != "missing")
                {
                    string payload = Path.Combine(planDirectory, change.payloadHash + ".bin");
                    if (change.payloadHash != change.privateHash || !File.Exists(payload) || HashFile(payload, token) != change.privateHash)
                        throw new ManagedIntegrationException("PAYLOAD_INTEGRITY", "Reviewed payload changed: " + entry.path);
                    CopyPayload(payload, Path.Combine(operationDirectory, entry.staged), entry.afterHash, change.bytes, token);
                }
                if (!entry.skipWrite && entry.kind == "file" && entry.beforeHash != "missing")
                {
                    string path = ManagedSessionFiles.AssetPath(main, entry.path);
                    CopyPayload(path, Path.Combine(operationDirectory, entry.backup), entry.beforeHash, new FileInfo(path).Length, token);
                }
                prepared.entries.Add(entry);
            }
            string content = Serialize(prepared); WriteNew(Path.Combine(operationDirectory, "prepared.json"), content);
            WriteJournal(operationDirectory, new ManagedApplyJournal { entries = prepared.entries });
            return ManagedSessionFiles.HashText(content);
        }

        internal static ManagedApplyPreparation ReadPreparation(string directory, string hash) =>
            ReadDocument<ManagedApplyPreparation>(Path.Combine(directory, "prepared.json"), hash, "PREPARATION_UNAVAILABLE", "PREPARATION_INTEGRITY");

        internal static string Identity(string project, string relative, CancellationToken token)
        {
            string path = ManagedSessionFiles.AssetPath(project, relative);
            return Directory.Exists(path) ? "directory" : File.Exists(path) ? HashFile(path, token) : "missing";
        }

        // Unity guards are owned by the caller. All expensive whole-project hashing/staging
        // happened asynchronously; the final transaction rechecks reviewed targets and payloads.
        internal static string CommitApply(string main, string workspace, string sidecar, string operationDirectory, string preparationHash, Action<int, string> fault = null, Action beforeFirstWrite = null)
        {
            var prepared = ReadPreparation(operationDirectory, preparationHash);
            var journal = new ManagedApplyJournal { entries = prepared.entries };
            var changes = prepared.entries.Select(item => new ManagedIntegrationChange { path = item.path, kind = item.kind, operation = item.afterHash == "missing" ? "deleted" : "modified" }).ToList();
            foreach (var entry in prepared.entries)
            {
                if (Identity(main, entry.path, CancellationToken.None) != entry.beforeHash || Identity(workspace, entry.path, CancellationToken.None) != entry.privateHash)
                    throw new ManagedIntegrationException("STALE_SOURCE", "Source changed before publication: " + entry.path);
                if (entry.kind == "directory" && entry.afterHash == "missing" && HasUnreviewedDescendants(main, entry.path, changes, CancellationToken.None))
                    throw new ManagedIntegrationException("UNREVIEWED_DESCENDANTS", "Main directory contains unreviewed descendants: " + entry.path);
                if (!entry.skipWrite && entry.kind == "file" && entry.afterHash != "missing" && HashFile(Path.Combine(operationDirectory, entry.staged), CancellationToken.None) != entry.afterHash)
                    throw new ManagedIntegrationException("PAYLOAD_INTEGRITY", "Staged payload changed: " + entry.path);
                if (!entry.skipWrite && entry.kind == "file" && entry.beforeHash != "missing" && HashFile(Path.Combine(operationDirectory, entry.backup), CancellationToken.None) != entry.beforeHash)
                    throw new ManagedIntegrationException("BACKUP_INTEGRITY", "Staged preimage changed: " + entry.path);
            }
            ValidateLiveProjection(main, prepared);
            var ordered = prepared.entries.Where(entry => !entry.skipWrite).OrderBy(entry => entry.kind == "directory" ? (entry.afterHash == "missing" ? 2 : 0) : 1)
                .ThenBy(entry => entry.kind == "directory" && entry.afterHash == "missing" ? -entry.path.Count(ch => ch == '/') : entry.path.Count(ch => ch == '/')).ToList();
            journal.entries = ordered; journal.state = "applying"; WriteJournal(operationDirectory, journal);
            bool firstWrite = true;
            try
            {
                for (int index = 0; index < ordered.Count; index++)
                {
                    var entry = ordered[index];
                    fault?.Invoke(index, "before_intent");
                    if (Identity(main, entry.path, CancellationToken.None) != entry.beforeHash) throw new ManagedIntegrationException("STALE_MAIN", "Main changed during publication: " + entry.path);
                    entry.attempted = true; WriteJournal(operationDirectory, journal);
                    fault?.Invoke(index, "after_intent");
                    if (Identity(main, entry.path, CancellationToken.None) != entry.beforeHash)
                        throw new ManagedIntegrationException("STALE_MAIN", "Main changed after publication intent: " + entry.path);
                    if (firstWrite) beforeFirstWrite?.Invoke();
                    string path = ManagedSessionFiles.AssetPath(main, entry.path);
                    if (entry.kind == "directory")
                    {
                        if (entry.afterHash == "missing") Directory.Delete(path, false);
                        else Directory.CreateDirectory(path);
                    }
                    else if (entry.afterHash == "missing") File.Delete(path);
                    else if (entry.beforeHash == "missing") File.Move(Path.Combine(operationDirectory, entry.staged), path);
                    else File.Replace(Path.Combine(operationDirectory, entry.staged), path, null);
                    firstWrite = false;
                    fault?.Invoke(index, "after_write");
                    if (Identity(main, entry.path, CancellationToken.None) != entry.afterHash) throw new IOException("Published postimage verification failed: " + entry.path);
                    entry.completed = true; WriteJournal(operationDirectory, journal);
                }
                fault?.Invoke(ordered.Count, "before_sources_applied");
                if (firstWrite) beforeFirstWrite?.Invoke();
                string baseline = Serialize(prepared.nextBaseline); string hash = ManagedSessionFiles.HashText(baseline);
                string baselinePath = BaselinePath(sidecar, hash);
                if (!File.Exists(baselinePath)) WriteNew(baselinePath, baseline);
                else ReadDocument<ManagedIntegrationBaseline>(baselinePath, hash, "BASELINE_UNAVAILABLE", "BASELINE_INTEGRITY");
                journal.baselineAfterHash = hash; journal.state = "sources_applied"; WriteJournal(operationDirectory, journal);
                return hash;
            }
            catch (Exception error)
            {
                journal.error = error.Message;
                try { Rollback(main, operationDirectory, journal, fault); }
                catch (Exception rollbackError)
                {
                    throw new ManagedApplyFailure("Publication failed: " + error.Message + "; rollback evidence could not be persisted: " + rollbackError.Message,
                        true, new AggregateException(error, rollbackError));
                }
                throw new ManagedApplyFailure("Publication failed: " + error.Message + "; " + (journal.recoveryConflicts.Count == 0 ? "verified transaction postimages were rolled back." : "external changes require recovery review."), journal.recoveryConflicts.Count != 0, error);
            }
        }

        static void Rollback(string main, string operationDirectory, ManagedApplyJournal journal, Action<int, string> fault)
        {
            foreach (var entry in journal.entries.AsEnumerable().Reverse().Where(item => item.attempted))
            {
                try
                {
                    string actual = Identity(main, entry.path, CancellationToken.None);
                    if (actual == entry.beforeHash) continue;
                    if (actual != entry.afterHash) { journal.recoveryConflicts.Add(entry.path); continue; }
                    string path = ManagedSessionFiles.AssetPath(main, entry.path);
                    if (entry.kind == "directory")
                    {
                        if (entry.beforeHash == "missing") Directory.Delete(path, false);
                        else Directory.CreateDirectory(path);
                    }
                    else if (entry.beforeHash == "missing") File.Delete(path);
                    else
                    {
                        string backup = Path.Combine(operationDirectory, entry.backup);
                        if (HashFile(backup, CancellationToken.None) != entry.beforeHash) { journal.recoveryConflicts.Add(entry.path); continue; }
                        string restore = Path.Combine(operationDirectory, entry.backup + ".restore");
                        File.Copy(backup, restore, false);
                        fault?.Invoke(journal.entries.IndexOf(entry), "after_restore_staging");
                        if (Identity(main, entry.path, CancellationToken.None) != actual) { journal.recoveryConflicts.Add(entry.path); continue; }
                        if (actual == "missing") File.Move(restore, path); else File.Replace(restore, path, null);
                    }
                    if (Identity(main, entry.path, CancellationToken.None) != entry.beforeHash) journal.recoveryConflicts.Add(entry.path);
                }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is ManagedIntegrationException)
                { journal.recoveryConflicts.Add(entry.path + ": " + error.Message); }
            }
            journal.state = journal.recoveryConflicts.Count == 0 ? "rolled_back" : "recovery_required";
            WriteJournal(operationDirectory, journal);
        }

        internal static ManagedApplyJournal InspectJournal(string directory)
        {
            string path = Path.Combine(directory, "journal.json");
            if (!File.Exists(path)) return null;
            RejectPathLinks(path);
            if (new FileInfo(path).Length > MaximumMetadataBytes) throw new ManagedIntegrationException("JOURNAL_INTEGRITY", "Operation journal exceeds its bound.");
            return JsonConvert.DeserializeObject<ManagedApplyJournal>(File.ReadAllText(path));
        }

        static void WriteJournal(string directory, ManagedApplyJournal journal)
        {
            string path = Path.Combine(directory, "journal.json"), temporary = Path.Combine(directory, "journal.tmp");
            byte[] bytes = Encoding.UTF8.GetBytes(Serialize(journal));
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
            if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
        }

        internal static bool IsPristine(string workspace, string sidecar, string baselineHash, CancellationToken token)
        {
            var baseline = ReadDocument<ManagedIntegrationBaseline>(BaselinePath(sidecar, baselineHash), baselineHash, "BASELINE_UNAVAILABLE", "BASELINE_INTEGRITY");
            return ManifestHash(Scan(workspace, false, true, token)) == ManifestHash(baseline.files);
        }
        internal static void ValidateStoredBaseline(string sidecar, string hash)
        { ReadDocument<ManagedIntegrationBaseline>(BaselinePath(sidecar, hash), hash, "BASELINE_UNAVAILABLE", "BASELINE_INTEGRITY"); }

        internal static string WorkspaceMetadataStamp(string workspace)
        {
            var records = new List<string>();
            foreach (string scope in Scopes)
            {
                string directory = Path.Combine(workspace, scope); if (!Directory.Exists(directory)) continue;
                var pending = new Stack<string>(); pending.Push(directory);
                while (pending.Count > 0)
                {
                    string parent = pending.Pop(); ManagedSessionFiles.RejectLink(parent);
                    foreach (string path in Directory.EnumerateFileSystemEntries(parent))
                    {
                        ManagedSessionFiles.RejectLink(path);
                        if (Excluded(Path.GetFileName(path))) throw new ManagedIntegrationException("UNSAFE_PATH", "Excluded source appeared during workspace verification.");
                        if (records.Count >= MaximumEntries) throw new ManagedIntegrationException("INTEGRATION_LIMIT", "Workspace verification exceeds its entry bound.");
                        bool folder = Directory.Exists(path);
                        records.Add(path.Substring(workspace.Length) + "=" + (folder ? "directory" : new FileInfo(path).Length.ToString()) + ":" + File.GetLastWriteTimeUtc(path).Ticks);
                        if (folder) pending.Push(path);
                    }
                }
            }
            records.Sort(StringComparer.Ordinal); return ManagedSessionFiles.HashText(string.Join("\n", records));
        }

        static void ValidateLiveProjection(string main, ManagedApplyPreparation prepared)
        {
            var structure = new List<ManagedIntegrationFile>();
            CollectStructure(Path.Combine(main, "Assets"), "Assets", structure);
            var projected = Index(structure, false); var desired = Index(prepared.nextBaseline.files);
            foreach (var entry in prepared.entries)
                if (entry.afterHash == "missing") projected.Remove(entry.path); else projected[entry.path] = desired[entry.path];
            ValidatePairs(projected.Values.ToList(), false);
        }

        static void CollectStructure(string directory, string relative, List<ManagedIntegrationFile> records)
        {
            ManagedSessionFiles.RejectLink(directory);
            foreach (string path in Directory.EnumerateFileSystemEntries(directory))
            {
                ManagedSessionFiles.RejectLink(path); string name = Path.GetFileName(path);
                if (Excluded(name)) continue;
                string key = relative + "/" + name; ValidateRelative(key);
                if (records.Count >= MaximumEntries) throw new ManagedIntegrationException("INTEGRATION_LIMIT", "Live asset structure exceeds the integration bound.");
                bool folder = Directory.Exists(path);
                records.Add(new ManagedIntegrationFile { path = key, kind = folder ? "directory" : "file", hash = folder ? "directory" : null,
                    bytes = folder ? 0 : new FileInfo(path).Length, guid = !folder && name.EndsWith(".meta", StringComparison.OrdinalIgnoreCase) ? ReadGuid(path) : null });
                if (folder) CollectStructure(path, key, records);
            }
        }
    }
}
