using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace AIUnityMCPServer
{
    internal static class ManagedProcessImage
    {
        const uint QueryLimitedInformation = 0x1000;
        const int MaximumPathCharacters = 32768;

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

        [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder name, ref uint characters);

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool CloseHandle(IntPtr handle);

        internal static bool Matches(int pid, string expectedExecutable)
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT || pid <= 0 || string.IsNullOrWhiteSpace(expectedExecutable)) return false;
            // Avoid Mono's MainModule enumeration: Unity's embedded runtime can corrupt the
            // native heap when enumerating modules after the creator Process was disposed.
            IntPtr process = OpenProcess(QueryLimitedInformation, false, (uint)pid);
            if (process == IntPtr.Zero) return false;
            try
            {
                var buffer = new StringBuilder(MaximumPathCharacters);
                uint characters = MaximumPathCharacters;
                // Flags=0 requests the Win32 path; size is in UTF-16 characters, excluding
                // the terminator on success. Never accept a missing or truncated image.
                if (!QueryFullProcessImageNameW(process, 0, buffer, ref characters) || characters == 0 || characters >= MaximumPathCharacters) return false;
                return string.Equals(Path.GetFullPath(buffer.ToString()), Path.GetFullPath(expectedExecutable), StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                // This helper owns only this OpenProcess handle, never a Process.Handle.
                if (!CloseHandle(process)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Managed process-query handle could not be closed.");
            }
        }
    }
}
