using System;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Sunblink.Urc.Editor
{
    internal static class UrcSockets
    {
        private static readonly bool IsWindows =
            Environment.OSVersion.Platform == PlatformID.Win32NT ||
            Environment.OSVersion.Platform == PlatformID.Win32Windows;

        private const int HANDLE_FLAG_INHERIT = 0x00000001;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetHandleInformation(IntPtr hObject, int dwMask, int dwFlags);

        /// <summary>
        /// Clears the inherit flag on a socket handle. Mandatory on Windows.
        ///
        /// Under the editor's Mono, socket handles are inheritable by default. Any child the editor
        /// spawns with handle inheritance — the external script editor Unity launches when you open a
        /// file, or an agent's own redirected-stdio subprocess — receives a live duplicate of the
        /// socket, which then keeps the port bound after the editor exits: a zombie owned by a dead
        /// pid that no editor restart can free. Only killing the inheriting child releases it.
        ///
        /// Ephemeral ports make this far less damaging than it was for a fixed-port design (we never
        /// try to rebind the old number), but a leaked handle is still a leak, and the discovery
        /// port is NOT ephemeral — it is the one fixed port in the system, so a zombie there would
        /// break discovery for every editor on the machine.
        /// </summary>
        public static void DisableHandleInheritance(Socket socket)
        {
            if (!IsWindows || socket == null) return;
            try { SetHandleInformation(socket.Handle, HANDLE_FLAG_INHERIT, 0); }
            catch (Exception) { /* best effort; a leaked handle is not worth failing startup over */ }
        }
    }
}
