using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace AIUnityMCPServer
{
    internal static class ManagedSessionFiles
    {
        internal const long MaximumSnapshotBytes = 16L * 1024 * 1024 * 1024;
        internal const long MaximumRetainedBytes = 64L * 1024 * 1024 * 1024;
        internal const long MaximumSourceFileBytes = 256L * 1024 * 1024;
        internal const int MaximumFiles = 50000;
        static readonly HashSet<string> Excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".git", ".svn", ".agent-memory", ".agent-coordination", "Library", "Temp", "Logs", "obj", "node_modules", ".vs", ".idea" };
        static readonly object IdentityLock = new object();
        static string currentUserSid;

        internal static string Hash(byte[] data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (ManagedSourceHash.UseNative(data.LongLength)) return BitConverter.ToString(ManagedSourceHash.NativeDigest(data)).Replace("-", "").ToLowerInvariant();
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "").ToLowerInvariant();
        }

        internal static string HashText(string value) => Hash(Encoding.UTF8.GetBytes(value));
        internal static string NewCapability()
        {
            var bytes = new byte[32];
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(bytes);
            return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
        }

        internal static bool CapabilityMatches(string capability, string expectedHash)
        {
            string actual = HashText(capability ?? "");
            if (actual.Length != (expectedHash ?? "").Length) return false;
            int difference = 0;
            for (int index = 0; index < actual.Length; index++) difference |= actual[index] ^ expectedHash[index];
            return difference == 0;
        }

        internal static void RejectLink(string path)
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("LINK_UNSUPPORTED: snapshots and workspaces cannot contain reparse points: " + path);
        }

        internal static void RestrictDirectory(string path)
        {
            var identity = new SecurityIdentifier(CurrentUserSid());
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new FileSystemAccessRule(identity, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            Directory.SetAccessControl(path, security);
        }

        static string CurrentUserSid()
        {
            lock (IdentityLock)
            {
                if (currentUserSid != null) return currentUserSid;
                // Unity's Mono implements DirectorySecurity, but WindowsIdentity.User throws.
                string executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "whoami.exe");
                if (!Path.IsPathRooted(executable) || !File.Exists(executable)) throw new UnauthorizedAccessException("Trusted Windows SID lookup executable is unavailable.");
                var start = new System.Diagnostics.ProcessStartInfo(executable, "/user /fo csv /nh")
                { UseShellExecute = false, CreateNoWindow = true, WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true };
                using (var process = System.Diagnostics.Process.Start(start))
                {
                    if (process == null) throw new UnauthorizedAccessException("Windows SID lookup did not start.");
                    if (!process.WaitForExit(3000)) { process.Kill(); throw new UnauthorizedAccessException("Windows SID lookup exceeded its deadline."); }
                    if (process.ExitCode != 0) throw new UnauthorizedAccessException("Windows SID lookup failed.");
                    string output = process.StandardOutput.ReadToEnd().Trim();
                    int comma = output.LastIndexOf(',');
                    string sid = comma < 0 ? "" : output.Substring(comma + 1).Trim().Trim('"');
                    if (!System.Text.RegularExpressions.Regex.IsMatch(sid, @"^S-1-\d+(?:-\d+)+$")) throw new UnauthorizedAccessException("Windows SID lookup returned an invalid identity.");
                    currentUserSid = sid;
                    return sid;
                }
            }
        }

        internal static void CheckPathLength(string path)
        {
            if (Path.GetFullPath(path).Length > 250)
                throw new InvalidDataException("PATH_TOO_LONG: private source paths must fit within 250 characters for Unity Mono file reads. Use a shorter source/package layout.");
        }

        internal static void ValidateSnapshotMetrics(long totalBytes, long largestFileBytes, int fileCount)
        {
            if (totalBytes < 0 || largestFileBytes < 0 || fileCount < 0) throw new ArgumentOutOfRangeException("Snapshot metrics cannot be negative.");
            if (totalBytes > MaximumSnapshotBytes || largestFileBytes > MaximumSourceFileBytes || fileCount > MaximumFiles)
                throw new InvalidDataException("SNAPSHOT_LIMIT: saved source exceeds 16 GiB, 50,000 files, or the 256 MiB single-file budget.");
        }

        internal static bool TryReadMailboxText(string path, int maximumBytes, out string text, out string detail)
        {
            text = null; detail = "temporarily_unavailable";
            if (maximumBytes < 1 || maximumBytes > 1048576) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var bytes = new MemoryStream())
                {
                    if (stream.Length > maximumBytes) { detail = "size_limit"; return false; }
                    var buffer = new byte[Math.Min(4096, maximumBytes + 1)];
                    int count;
                    while ((count = stream.Read(buffer, 0, Math.Min(buffer.Length, maximumBytes + 1 - (int)bytes.Length))) > 0)
                    {
                        bytes.Write(buffer, 0, count);
                        if (bytes.Length > maximumBytes) { detail = "size_limit"; return false; }
                    }
                    text = Encoding.UTF8.GetString(bytes.ToArray());
                    detail = null;
                    return true;
                }
            }
            catch (IOException) { detail = "io_unavailable"; return false; }
            catch (UnauthorizedAccessException) { detail = "access_unavailable"; return false; }
        }

        internal static bool IsExcluded(string name)
        {
            // Unity metadata follows its asset's policy. Copying metadata for an excluded
            // asset would leave an orphan and can expose sensitive asset names in snapshots.
            if (name.EndsWith(".meta", StringComparison.OrdinalIgnoreCase) && IsExcluded(name.Substring(0, name.Length - 5))) return true;
            string extension = Path.GetExtension(name);
            return Excluded.Contains(name) || name.StartsWith(".env", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("credentials.json", StringComparison.OrdinalIgnoreCase) || name.Equals("secrets.json", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals(".npmrc", StringComparison.OrdinalIgnoreCase) || name.Equals("id_rsa", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".pfx", StringComparison.OrdinalIgnoreCase) || extension.Equals(".pem", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".key", StringComparison.OrdinalIgnoreCase) || extension.Equals(".keystore", StringComparison.OrdinalIgnoreCase);
        }

        internal static string AssetPath(string project, string relative)
        {
            if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)) throw new InvalidDataException("INVALID_PATH: use a relative Assets path.");
            string normalized = relative.Replace('\\', '/');
            if (!normalized.StartsWith("Assets/", StringComparison.Ordinal) || normalized.Split('/').Any(part => part == ".." || part == "." || part.Length == 0 || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || IsExcluded(part)))
                throw new InvalidDataException("INVALID_PATH: only bounded non-secret files under Assets are supported.");
            string root = Path.GetFullPath(project).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string full = Path.GetFullPath(Path.Combine(root, relative));
            CheckPathLength(full);
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("INVALID_PATH: path escapes workspace.");
            string current = root.TrimEnd(Path.DirectorySeparatorChar);
            RejectLink(current);
            foreach (string part in normalized.Split('/'))
            {
                current = Path.Combine(current, part);
                if (File.Exists(current) || Directory.Exists(current)) RejectLink(current);
            }
            return full;
        }

        internal static void WriteAtomic(string path, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            CheckPathLength(path);
            string temporary = Path.Combine(Path.GetDirectoryName(path), "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp");
            CheckPathLength(temporary);
            try
            {
                File.WriteAllText(temporary, text, new UTF8Encoding(false));
                for (int attempt = 1; ; attempt++)
                {
                    try
                    {
                        if (File.Exists(path)) File.Replace(temporary, path, null);
                        else File.Move(temporary, path);
                        break;
                    }
                    catch (Exception exception) when ((exception is IOException || exception is UnauthorizedAccessException) && attempt < 3)
                    { Thread.Sleep(5 * attempt); }
                }
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        internal static string ProjectFingerprint(string project, CancellationToken cancellation)
        {
            var records = new List<string>();
            long total = 0;
            foreach (string name in new[] { "Assets", "Packages", "ProjectSettings", "ManagedVendor" })
            {
                string directory = Path.Combine(project, name);
                if (Directory.Exists(directory)) Collect(directory, name, records, ref total, cancellation);
            }
            records.Sort(StringComparer.Ordinal);
            return HashText(string.Join("\n", records));
        }

        static void Collect(string directory, string relative, List<string> records, ref long total, CancellationToken cancellation)
        {
            RejectLink(directory);
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory).OrderBy(item => item, StringComparer.Ordinal))
            {
                cancellation.ThrowIfCancellationRequested();
                string name = Path.GetFileName(entry);
                if (IsExcluded(name)) continue;
                RejectLink(entry);
                string key = relative + "/" + name;
                if (Directory.Exists(entry)) Collect(entry, key, records, ref total, cancellation);
                else
                {
                    CheckPathLength(entry);
                    long size = new FileInfo(entry).Length;
                    total += size;
                    ValidateSnapshotMetrics(total, size, records.Count + 1);
                    records.Add(key + "=" + Hash(File.ReadAllBytes(entry)));
                }
            }
        }

        internal static string Snapshot(string source, string destination, bool vendorLocalPackages, CancellationToken cancellation)
        {
            if (Directory.Exists(destination)) throw new InvalidDataException("SNAPSHOT_EXISTS: destination must be new.");
            string before = ProjectFingerprint(source, cancellation);
            Directory.CreateDirectory(destination);
            foreach (string name in new[] { "Assets", "Packages", "ProjectSettings", "ManagedVendor" })
            {
                string origin = Path.Combine(source, name);
                if (Directory.Exists(origin)) CopyTree(origin, Path.Combine(destination, name), cancellation);
            }
            if (before != ProjectFingerprint(source, cancellation)) throw new InvalidDataException("SOURCE_CHANGED: source changed during the snapshot; retry when edits finish.");
            if (vendorLocalPackages) VendorDependencies(source, destination, cancellation);
            WriteAtomic(Path.Combine(destination, "managed-snapshot-report.json"), new JObject
            {
                ["scope"] = "Saved files in Assets, Packages, ProjectSettings and private ManagedVendor only; unsaved Editor state is not captured.",
                ["excludedPaths"] = new JArray(ExcludedPaths(source, cancellation))
            }.ToString());
            return ProjectFingerprint(destination, cancellation);
        }

        internal static IEnumerable<string> ExcludedPaths(string project, CancellationToken cancellation)
        {
            var result = new List<string>();
            var pending = new Stack<string>();
            foreach (string name in new[] { "Assets", "Packages", "ProjectSettings", "ManagedVendor" })
                if (Directory.Exists(Path.Combine(project, name))) pending.Push(Path.Combine(project, name));
            while (pending.Count > 0)
            {
                string directory = pending.Pop(); RejectLink(directory);
                foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    cancellation.ThrowIfCancellationRequested(); RejectLink(entry);
                    if (IsExcluded(Path.GetFileName(entry)))
                    {
                        if (result.Count >= 1000) throw new InvalidDataException("EXCLUSION_LIMIT: more than 1000 excluded paths; prepare a smaller saved-source project.");
                        result.Add(entry.Substring(project.Length + 1).Replace('\\', '/'));
                    }
                    else if (Directory.Exists(entry)) pending.Push(entry);
                }
            }
            return result;
        }

        static void CopyTree(string source, string destination, CancellationToken cancellation)
        {
            RejectLink(source);
            Directory.CreateDirectory(destination);
            foreach (string entry in Directory.EnumerateFileSystemEntries(source))
            {
                cancellation.ThrowIfCancellationRequested();
                if (IsExcluded(Path.GetFileName(entry))) continue;
                RejectLink(entry);
                string target = Path.Combine(destination, Path.GetFileName(entry));
                if (Directory.Exists(entry)) CopyTree(entry, target, cancellation);
                else { CheckPathLength(target); File.Copy(entry, target, false); }
            }
        }

        static void VendorDependencies(string source, string destination, CancellationToken cancellation)
        {
            string manifestPath = Path.Combine(destination, "Packages", "manifest.json");
            if (!File.Exists(manifestPath)) throw new InvalidDataException("INVALID_PROJECT: Packages/manifest.json is missing.");
            var manifest = JObject.Parse(File.ReadAllText(manifestPath));
            var dependencies = manifest["dependencies"] as JObject ?? throw new InvalidDataException("INVALID_PROJECT: dependencies are missing.");
            foreach (var dependency in dependencies.Properties().ToArray())
            {
                string value = dependency.Value.Value<string>();
                if (value == null || !value.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) continue;
                string path = value.Substring(5);
                string origin = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(source, "Packages", path));
                string packageJson = Path.Combine(origin, "package.json");
                if (!Directory.Exists(origin) || !File.Exists(packageJson)) throw new InvalidDataException("LOCAL_DEPENDENCY_UNSUPPORTED: local package must be an unpacked directory.");
                var package = JObject.Parse(File.ReadAllText(packageJson));
                if ((package["dependencies"] as JObject)?.Properties().Any(item => item.Value.ToString().StartsWith("file:", StringComparison.OrdinalIgnoreCase)) == true)
                    throw new InvalidDataException("LOCAL_DEPENDENCY_UNSUPPORTED: transitive local file dependencies must be embedded before registration.");
                if (dependency.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || dependency.Name.Contains("..")) throw new InvalidDataException("INVALID_PACKAGE_NAME");
                string target = Path.Combine(destination, "ManagedVendor", dependency.Name);
                var packageRecords = new List<string>(); long packageBytes = 0;
                Collect(origin, dependency.Name, packageRecords, ref packageBytes, cancellation);
                string before = HashText(string.Join("\n", packageRecords.OrderBy(item => item, StringComparer.Ordinal)));
                CopyTree(origin, target, cancellation);
                var afterRecords = new List<string>(); long afterBytes = 0;
                Collect(origin, dependency.Name, afterRecords, ref afterBytes, cancellation);
                if (before != HashText(string.Join("\n", afterRecords.OrderBy(item => item, StringComparer.Ordinal))))
                    throw new InvalidDataException("SOURCE_CHANGED: local package changed while being privately vendored; retry after edits finish.");
                dependency.Value = "file:../ManagedVendor/" + dependency.Name;
            }
            // The original lock contains absolute local paths; resolve it privately again.
            string packageLock = Path.Combine(destination, "Packages", "packages-lock.json");
            if (File.Exists(packageLock)) File.Delete(packageLock);
            WriteAtomic(manifestPath, manifest.ToString());
            ProjectFingerprint(destination, cancellation);
        }
    }
}
