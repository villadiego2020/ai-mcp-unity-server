using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace AIUnityMCPServer
{
    internal static class MCPRuntimeCache
    {
        const int MarkerSchemaVersion = 1;
        const string MarkerFileName = "AIUnityMCPServer.runtime.json";

        static readonly string[] RuntimeFileNames =
        {
            "index.js",
            "registry.js",
            "commands.json",
            "package.json",
            "package-lock.json",
        };

        [Serializable]
        sealed class RuntimeMarker
        {
            public int schemaVersion;
            public string packageVersion = "";
            public string fingerprint = "";
        }

        internal sealed class Plan
        {
            public string SourceDirectory { get; }
            public string RuntimeRoot { get; }
            public string RuntimeDirectory { get; }
            public string ServerEntryPath { get; }
            public string DependencyPath { get; }
            public string PackageVersion { get; }
            public string Fingerprint { get; }

            public Plan(
                string sourceDirectory,
                string runtimeRoot,
                string runtimeDirectory,
                string packageVersion,
                string fingerprint)
            {
                SourceDirectory = sourceDirectory;
                RuntimeRoot = runtimeRoot;
                RuntimeDirectory = runtimeDirectory;
                ServerEntryPath = Path.Combine(runtimeDirectory, "index.js");
                DependencyPath = Path.Combine(runtimeDirectory, "node_modules", "@modelcontextprotocol", "sdk", "package.json");
                PackageVersion = packageVersion;
                Fingerprint = fingerprint;
            }
        }

        internal sealed class Source
        {
            public string Directory { get; }
            public string RuntimeRoot { get; }
            public string PackageVersion { get; }

            public Source(string directory, string runtimeRoot, string packageVersion)
            {
                Directory = directory;
                RuntimeRoot = runtimeRoot;
                PackageVersion = packageVersion;
            }
        }

        internal enum CacheState
        {
            Missing,
            Ready,
            Corrupt,
        }

        public static bool TryCreatePlan(out Plan plan, out string failure)
        {
            try
            {
                string sourceDirectory = MCPPackagePaths.ServerDirectory();
                string runtimeRoot = MCPPackagePaths.RuntimeCacheRoot();
                string packageVersion = MCPPackagePaths.PackageVersion();
                return TryCreatePlan(new Source(sourceDirectory, runtimeRoot, packageVersion), out plan, out failure);
            }
            catch (Exception exception)
            {
                plan = null;
                failure = "Could not prepare the runtime cache plan: " + exception.Message;
                return false;
            }
        }

        public static bool TryCreatePlan(Source source, out Plan plan, out string failure)
        {
            plan = null;
            failure = "";

            try
            {
                if (source == null || string.IsNullOrEmpty(source.Directory) || !Directory.Exists(source.Directory))
                {
                    failure = "Package Server~ directory was not found.";
                    return false;
                }

                if (!TryValidateRequiredFiles(source.Directory, out failure))
                {
                    return false;
                }

                if (string.IsNullOrEmpty(source.RuntimeRoot))
                {
                    failure = "The per-user application data directory is unavailable.";
                    return false;
                }

                string normalizedVersion = string.IsNullOrEmpty(source.PackageVersion) ? "unknown" : source.PackageVersion;
                string fingerprint = ComputeFingerprint(source.Directory);
                string identity = SanitizePathSegment(normalizedVersion) + "-" + fingerprint.Substring(0, 16);
                string runtimeDirectory = Path.Combine(source.RuntimeRoot, identity);
                plan = new Plan(source.Directory, source.RuntimeRoot, runtimeDirectory, normalizedVersion, fingerprint);
                return true;
            }
            catch (Exception exception)
            {
                failure = "Could not prepare the runtime cache plan: " + exception.Message;
                return false;
            }
        }

        public static CacheState Inspect(Plan plan, out string detail)
        {
            if (!Directory.Exists(plan.RuntimeDirectory))
            {
                detail = "Runtime cache has not been bootstrapped.";
                return CacheState.Missing;
            }

            try
            {
                if (!TryValidateRequiredFiles(plan.RuntimeDirectory, out detail))
                {
                    return CacheState.Corrupt;
                }

                string markerPath = Path.Combine(plan.RuntimeDirectory, MarkerFileName);
                if (!File.Exists(markerPath))
                {
                    detail = "Runtime cache marker is missing.";
                    return CacheState.Corrupt;
                }

                RuntimeMarker marker = JsonUtility.FromJson<RuntimeMarker>(File.ReadAllText(markerPath));
                if (marker == null
                    || marker.schemaVersion != MarkerSchemaVersion
                    || !string.Equals(marker.packageVersion, plan.PackageVersion, StringComparison.Ordinal)
                    || !string.Equals(marker.fingerprint, plan.Fingerprint, StringComparison.Ordinal))
                {
                    detail = "Runtime cache marker does not match this package build.";
                    return CacheState.Corrupt;
                }

                string runtimeFingerprint = ComputeFingerprint(plan.RuntimeDirectory);
                if (!string.Equals(runtimeFingerprint, plan.Fingerprint, StringComparison.Ordinal))
                {
                    detail = "Runtime bridge files do not match this package build.";
                    return CacheState.Corrupt;
                }

                detail = plan.RuntimeDirectory;
                return CacheState.Ready;
            }
            catch (Exception exception)
            {
                detail = "Runtime cache could not be inspected: " + exception.Message;
                return CacheState.Corrupt;
            }
        }

        public static bool TryEnsure(Plan plan, out bool changed, out string failure)
        {
            changed = false;
            failure = "";

            if (Inspect(plan, out _) == CacheState.Ready)
            {
                return true;
            }

            string stagingDirectory = plan.RuntimeDirectory + ".staging-" + Guid.NewGuid().ToString("N");
            try
            {
                Directory.CreateDirectory(plan.RuntimeRoot);
                Directory.CreateDirectory(stagingDirectory);
                CopyRuntimeFiles(plan.SourceDirectory, stagingDirectory);
                WriteMarker(stagingDirectory, plan);

                var stagingPlan = new Plan(
                    stagingDirectory,
                    plan.RuntimeRoot,
                    stagingDirectory,
                    plan.PackageVersion,
                    plan.Fingerprint);
                if (Inspect(stagingPlan, out string stagingFailure) != CacheState.Ready)
                {
                    failure = "Staged runtime validation failed: " + stagingFailure;
                    return false;
                }

                if (!TryMakeDestinationAvailable(plan, out failure))
                {
                    return false;
                }

                try
                {
                    Directory.Move(stagingDirectory, plan.RuntimeDirectory);
                }
                catch (IOException) when (Inspect(plan, out _) == CacheState.Ready)
                {
                    return true;
                }

                if (Inspect(plan, out string promotedDetail) != CacheState.Ready)
                {
                    failure = "Promoted runtime validation failed: " + promotedDetail;
                    return false;
                }

                changed = true;
                return true;
            }
            catch (Exception exception)
            {
                failure = "Runtime bootstrap failed: " + exception.Message;
                return false;
            }
            finally
            {
                TryDeleteStagingDirectory(stagingDirectory, plan.RuntimeRoot);
            }
        }

        public static bool DependenciesReady(Plan plan) => File.Exists(plan.DependencyPath);

        public static bool IsManagedServerEntry(string serverEntry)
        {
            if (string.IsNullOrEmpty(serverEntry))
            {
                return false;
            }

            try
            {
                string fullEntry = Path.GetFullPath(serverEntry);
                if (!string.Equals(Path.GetFileName(fullEntry), "index.js", StringComparison.OrdinalIgnoreCase)
                    || !File.Exists(fullEntry))
                {
                    return false;
                }

                string runtimeDirectory = Directory.GetParent(fullEntry)?.FullName ?? "";
                string runtimeRoot = Directory.GetParent(runtimeDirectory)?.FullName ?? "";
                return PathsEqual(runtimeRoot, MCPPackagePaths.RuntimeCacheRoot())
                    && File.Exists(Path.Combine(runtimeDirectory, MarkerFileName));
            }
            catch (Exception)
            {
                return false;
            }
        }

        static bool TryMakeDestinationAvailable(Plan plan, out string failure)
        {
            failure = "";
            if (!Directory.Exists(plan.RuntimeDirectory))
            {
                return true;
            }

            if (Inspect(plan, out _) == CacheState.Ready)
            {
                return true;
            }

            string quarantineDirectory = plan.RuntimeDirectory + ".invalid-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
            try
            {
                Directory.Move(plan.RuntimeDirectory, quarantineDirectory);
                Debug.LogWarning("[AI Unity MCP Server Setup] Preserved an invalid runtime cache at " + quarantineDirectory);
                return true;
            }
            catch (Exception exception)
            {
                if (Inspect(plan, out _) == CacheState.Ready)
                {
                    return true;
                }

                failure = "Could not quarantine the invalid runtime cache: " + exception.Message;
                return false;
            }
        }

        static void CopyRuntimeFiles(string sourceDirectory, string destinationDirectory)
        {
            foreach (string fileName in RuntimeFileNames)
            {
                File.Copy(
                    Path.Combine(sourceDirectory, fileName),
                    Path.Combine(destinationDirectory, fileName),
                    false);
            }
        }

        static void WriteMarker(string runtimeDirectory, Plan plan)
        {
            var marker = new RuntimeMarker
            {
                schemaVersion = MarkerSchemaVersion,
                packageVersion = plan.PackageVersion,
                fingerprint = plan.Fingerprint,
            };
            File.WriteAllText(Path.Combine(runtimeDirectory, MarkerFileName), JsonUtility.ToJson(marker, true));
        }

        static bool TryValidateRequiredFiles(string directory, out string failure)
        {
            foreach (string fileName in RuntimeFileNames)
            {
                string path = Path.Combine(directory, fileName);
                if (!File.Exists(path))
                {
                    failure = "Required bridge file is missing: " + path;
                    return false;
                }
            }

            failure = "";
            return true;
        }

        static string ComputeFingerprint(string directory)
        {
            using (var content = new MemoryStream())
            {
                foreach (string fileName in RuntimeFileNames)
                {
                    byte[] nameBytes = Encoding.UTF8.GetBytes(fileName);
                    content.Write(nameBytes, 0, nameBytes.Length);
                    content.WriteByte(0);
                    using (var source = File.OpenRead(Path.Combine(directory, fileName)))
                    {
                        source.CopyTo(content);
                    }

                    content.WriteByte(0);
                }

                content.Position = 0;
                using (SHA256 sha256 = SHA256.Create())
                {
                    byte[] hash = sha256.ComputeHash(content);
                    return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
                }
            }
        }

        static string SanitizePathSegment(string value)
        {
            var result = new StringBuilder(value.Length);
            foreach (char character in value)
            {
                bool isSafe = char.IsLetterOrDigit(character)
                    || character == '.'
                    || character == '-'
                    || character == '_';
                result.Append(isSafe ? character : '-');
            }

            return result.Length == 0 ? "unknown" : result.ToString();
        }

        static void TryDeleteStagingDirectory(string stagingDirectory, string runtimeRoot)
        {
            if (!Directory.Exists(stagingDirectory) || !IsManagedChild(stagingDirectory, runtimeRoot))
            {
                return;
            }

            try
            {
                Directory.Delete(stagingDirectory, true);
            }
            catch (Exception exception)
            {
                Debug.LogWarning("[AI Unity MCP Server Setup] Could not clean temporary runtime cache "
                               + stagingDirectory + ": " + exception.Message);
            }
        }

        static bool IsManagedChild(string path, string root)
        {
            string normalizedRoot = Path.GetFullPath(root)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            string normalizedPath = Path.GetFullPath(path);
            StringComparison comparison = Application.platform == RuntimePlatform.WindowsEditor
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            return normalizedPath.StartsWith(normalizedRoot, comparison);
        }

        static bool PathsEqual(string left, string right)
        {
            if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right))
            {
                return false;
            }

            StringComparison comparison = Application.platform == RuntimePlatform.WindowsEditor
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), comparison);
        }
    }
}
