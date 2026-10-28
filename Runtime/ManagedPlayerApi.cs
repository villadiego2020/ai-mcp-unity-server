using System;
using System.IO;

namespace AIUnityMCPServer.Runtime
{
    /// <summary>Opt-in semantic input for managed test Players, not global desktop input.</summary>
    public interface IManagedPlayerInputAdapter
    {
        string[] SupportedInputs { get; }
        string Execute(string name, string value);
    }

    public static class ManagedPlayerApi
    {
        internal static IManagedPlayerInputAdapter InputAdapter;
        public static string SaveDirectory { get; internal set; }
        public static bool IsManagedPlayer => !string.IsNullOrEmpty(SaveDirectory);

        public static void RegisterInputAdapter(IManagedPlayerInputAdapter adapter)
        {
            if (adapter == null) throw new ArgumentNullException(nameof(adapter));
            if (InputAdapter != null && !ReferenceEquals(InputAdapter, adapter)) throw new InvalidOperationException("A managed input adapter is already registered.");
            InputAdapter = adapter;
        }
        public static void UnregisterInputAdapter(IManagedPlayerInputAdapter adapter)
        { if (ReferenceEquals(InputAdapter, adapter)) InputAdapter = null; }
    }
}
