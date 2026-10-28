using System;
using System.ComponentModel;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace AIUnityMCPServer
{
    internal static class MCPListenerSocket
    {
        const uint HandleFlagInherit = 1;

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);

        internal static void PreventInheritance(Socket socket)
        {
            if (socket == null) throw new ArgumentNullException(nameof(socket));
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) return;
            // Unity's Windows Mono can pass inheritable sockets to Process.Start children.
            // A child would then retain this listener after the Editor stops it or reloads.
            // The Socket retains ownership of its handle; only the inheritance bit changes.
            if (!SetHandleInformation(socket.Handle, HandleFlagInherit, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not prevent MCP listener inheritance by child processes.");
        }
    }
}
