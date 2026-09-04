using System;
using System.IO;
using UnityEditor;

namespace AIUnityMCPServer
{
    internal static class MCPPackagePaths
    {
        const string AssemblyDefinitionName = "AIUnityMCPServer.Editor";
        const string AssemblyDefinitionFileName = AssemblyDefinitionName + ".asmdef";
        static string _packageVersion = "";

        public static string ProjectRoot() =>
            Path.GetFullPath(Path.Combine(UnityEngine.Application.dataPath, ".."));

        public static string PackageRoot()
        {
            var package = CurrentPackage();
            if (package != null
                && !string.IsNullOrEmpty(package.resolvedPath)
                && Directory.Exists(package.resolvedPath))
            {
                return package.resolvedPath;
            }

            return FindPackageRootFromAssemblyDefinition();
        }

        public static string ServerDirectory()
        {
            string root = PackageRoot();
            return string.IsNullOrEmpty(root) ? "" : Path.Combine(root, "Server~");
        }

        public static string ServerEntryPath()
        {
            string serverDirectory = ServerDirectory();
            return string.IsNullOrEmpty(serverDirectory) ? "" : Path.Combine(serverDirectory, "index.js");
        }

        public static string CommandManifestPath()
        {
            string serverDirectory = ServerDirectory();
            return string.IsNullOrEmpty(serverDirectory) ? "" : Path.Combine(serverDirectory, "commands.json");
        }

        public static string RuntimeCacheRoot()
        {
            string localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(localApplicationData))
            {
                localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }

            return string.IsNullOrEmpty(localApplicationData)
                ? ""
                : Path.Combine(localApplicationData, "AIUnityMCPServer", "runtime");
        }

        public static bool IsImmutableInstall()
        {
            var package = CurrentPackage();
            return package != null
                && package.source != UnityEditor.PackageManager.PackageSource.Local
                && package.source != UnityEditor.PackageManager.PackageSource.Embedded;
        }

        public static string PackageVersion()
        {
            if (!string.IsNullOrEmpty(_packageVersion))
            {
                return _packageVersion;
            }

            var package = CurrentPackage();
            _packageVersion = package == null || string.IsNullOrEmpty(package.version) ? "unknown" : package.version;
            return _packageVersion;
        }

        static UnityEditor.PackageManager.PackageInfo CurrentPackage() =>
            UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(MCPPackagePaths).Assembly);

        static string FindPackageRootFromAssemblyDefinition()
        {
            foreach (string guid in AssetDatabase.FindAssets($"{AssemblyDefinitionName} t:AssemblyDefinitionAsset"))
            {
                string assetPath = AssetDatabase.GUIDToAssetPath(guid);
                if (!assetPath.EndsWith("/" + AssemblyDefinitionFileName, StringComparison.Ordinal))
                {
                    continue;
                }

                string editorDirectory = Directory.GetParent(Path.GetFullPath(assetPath))?.FullName ?? "";
                return string.IsNullOrEmpty(editorDirectory)
                    ? ""
                    : Directory.GetParent(editorDirectory)?.FullName ?? "";
            }

            return "";
        }
    }
}
