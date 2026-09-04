using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace AIUnityMCPServer
{
    internal sealed class UIToolkitFileSnapshot
    {
        public string AssetPath;
        public string AbsolutePath;
        public byte[] Bytes;
        public string Text;
        public string Hash;
        public bool Exists;
    }

    internal static class UIToolkitPathGuard
    {
        internal const int MaximumSourceBytes = 1024 * 1024;
        internal const int MaximumApplyFileBytes = 512 * 1024;
        internal const int MaximumApplyTotalBytes = 1024 * 1024;

        static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        internal static bool TryRead(string assetPath, int maximumBytes, out UIToolkitFileSnapshot snapshot, out string code, out string message)
        {
            if (!TryResolve(assetPath, true, out snapshot, out code, out message))
                return false;

            var fileInfo = new FileInfo(snapshot.AbsolutePath);
            if (fileInfo.Length > maximumBytes)
            {
                code = "SOURCE_TOO_LARGE";
                message = $"'{snapshot.AssetPath}' is {fileInfo.Length} bytes; the limit is {maximumBytes} bytes.";
                return false;
            }

            try
            {
                snapshot.Bytes = File.ReadAllBytes(snapshot.AbsolutePath);
                snapshot.Text = StrictUtf8.GetString(snapshot.Bytes);
                snapshot.Hash = ComputeHash(snapshot.Bytes);
                return true;
            }
            catch (DecoderFallbackException)
            {
                code = "INVALID_ENCODING";
                message = $"'{snapshot.AssetPath}' must be valid UTF-8 text.";
                return false;
            }
            catch (Exception exception)
            {
                code = "READ_FAILED";
                message = $"Could not read '{snapshot.AssetPath}': {exception.Message}";
                return false;
            }
        }

        internal static bool TryResolve(string assetPath, bool mustExist, out UIToolkitFileSnapshot snapshot, out string code, out string message)
        {
            snapshot = null;
            code = null;
            message = null;

            if (!TryNormalizeAssetPath(assetPath, out string normalizedPath, out code, out message))
                return false;

            string assetsRoot = Path.GetFullPath(Application.dataPath);
            string absolutePath = Path.GetFullPath(Path.Combine(ProjectRoot(), normalizedPath.Replace('/', Path.DirectorySeparatorChar)));
            if (!IsInside(absolutePath, assetsRoot))
            {
                code = "UNSAFE_PATH";
                message = "The resolved path leaves the project's Assets directory.";
                return false;
            }

            if (ContainsReparsePoint(absolutePath, assetsRoot, out string reparsePath))
            {
                code = "UNSAFE_PATH";
                message = $"The path crosses the reparse point '{reparsePath}'. UI Toolkit tools do not follow links or junctions.";
                return false;
            }

            bool exists = File.Exists(absolutePath);
            if (mustExist && !exists)
            {
                code = "NOT_FOUND";
                message = $"UI Toolkit source not found: '{normalizedPath}'.";
                return false;
            }

            if (!mustExist && !Directory.Exists(Path.GetDirectoryName(absolutePath)))
            {
                code = "NOT_FOUND";
                message = $"The parent directory for '{normalizedPath}' does not exist.";
                return false;
            }

            snapshot = new UIToolkitFileSnapshot
            {
                AssetPath = normalizedPath,
                AbsolutePath = absolutePath,
                Exists = exists,
                Bytes = Array.Empty<byte>(),
                Text = string.Empty,
                Hash = exists ? null : "missing",
            };
            return true;
        }

        internal static bool TryResolveReference(string ownerAssetPath, string reference, out string assetPath)
        {
            assetPath = null;
            if (string.IsNullOrWhiteSpace(reference))
                return false;

            string cleanReference = reference.Trim().Replace("&amp;", "&");
            int suffixIndex = cleanReference.IndexOfAny(new[] { '?', '#' });
            if (suffixIndex >= 0)
                cleanReference = cleanReference.Substring(0, suffixIndex);

            const string ProjectPrefix = "project://database/";
            if (cleanReference.StartsWith(ProjectPrefix, StringComparison.OrdinalIgnoreCase))
                cleanReference = cleanReference.Substring(ProjectPrefix.Length);

            string candidate;
            if (cleanReference.StartsWith("Assets/", StringComparison.Ordinal))
            {
                candidate = cleanReference;
            }
            else
            {
                string ownerDirectory = Path.GetDirectoryName(ownerAssetPath)?.Replace('\\', '/') ?? "Assets";
                string combined = Path.GetFullPath(Path.Combine(ProjectRoot(), ownerDirectory, cleanReference));
                string assetsRoot = Path.GetFullPath(Application.dataPath);
                if (!IsInside(combined, assetsRoot))
                    return false;
                candidate = "Assets" + combined.Substring(assetsRoot.Length).Replace('\\', '/');
            }

            if (!TryNormalizeAssetPath(candidate, out string normalized, out _, out _))
                return false;
            assetPath = normalized;
            return true;
        }

        internal static byte[] Encode(string content)
        {
            return new UTF8Encoding(false).GetBytes(content ?? string.Empty);
        }

        internal static string ComputeHash(byte[] bytes)
        {
            using var sha256 = SHA256.Create();
            byte[] hash = sha256.ComputeHash(bytes ?? Array.Empty<byte>());
            var builder = new StringBuilder(hash.Length * 2);
            foreach (byte value in hash)
                builder.Append(value.ToString("x2"));
            return builder.ToString();
        }

        internal static string ProjectRoot()
        {
            return Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        }

        static bool TryNormalizeAssetPath(string assetPath, out string normalizedPath, out string code, out string message)
        {
            normalizedPath = null;
            code = null;
            message = null;

            if (string.IsNullOrWhiteSpace(assetPath))
            {
                code = "INVALID_REQUEST";
                message = "A UI Toolkit source path is required.";
                return false;
            }

            if (assetPath.Contains("\\") || Path.IsPathRooted(assetPath) || assetPath.Contains(":"))
            {
                code = "UNSAFE_PATH";
                message = "Use a canonical Unity asset path with forward slashes, beginning with 'Assets/'.";
                return false;
            }

            string[] segments = assetPath.Split('/');
            if (segments.Length < 2 || segments[0] != "Assets")
            {
                code = "UNSAFE_PATH";
                message = "UI Toolkit tools accept only paths beneath 'Assets/'.";
                return false;
            }

            foreach (string segment in segments)
            {
                if (string.IsNullOrEmpty(segment) || segment == "." || segment == "..")
                {
                    code = "UNSAFE_PATH";
                    message = "The path must be canonical and cannot contain empty, current-directory, or parent-directory segments.";
                    return false;
                }
            }

            string extension = Path.GetExtension(assetPath);
            if (!extension.Equals(".uxml", StringComparison.OrdinalIgnoreCase) &&
                !extension.Equals(".uss", StringComparison.OrdinalIgnoreCase))
            {
                code = "INVALID_REQUEST";
                message = "The path must identify a .uxml or .uss file.";
                return false;
            }

            normalizedPath = string.Join("/", segments);
            return true;
        }

        static bool IsInside(string candidate, string root)
        {
            string rootWithSeparator = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return candidate.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
        }

        static bool ContainsReparsePoint(string targetPath, string assetsRoot, out string reparsePath)
        {
            reparsePath = null;
            string currentPath = assetsRoot;
            if (HasReparsePoint(currentPath))
            {
                reparsePath = currentPath;
                return true;
            }

            string relativePath = targetPath.Substring(assetsRoot.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            foreach (string segment in relativePath.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries))
            {
                currentPath = Path.Combine(currentPath, segment);
                if (!Directory.Exists(currentPath) && !File.Exists(currentPath))
                    break;
                if (!HasReparsePoint(currentPath))
                    continue;
                reparsePath = currentPath;
                return true;
            }
            return false;
        }

        static bool HasReparsePoint(string path)
        {
            try
            {
                return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
            }
            catch (FileNotFoundException)
            {
                return false;
            }
            catch (DirectoryNotFoundException)
            {
                return false;
            }
        }
    }
}
