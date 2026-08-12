using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using Urc.Protocol;

namespace Urc.Discovery
{
    /// <summary>
    /// Finds running editors by multicast query.
    ///
    /// This replaces the port allocation both prior tools needed. Editors bind an ephemeral TCP port
    /// and advertise it here, so nothing is ever assigned, recorded, or configured — which is what
    /// makes several editors work at once with no setup.
    /// </summary>
    public static class DiscoveryClient
    {
        /// <summary>
        /// How long to collect replies. Loopback datagrams arrive in well under a millisecond; this
        /// is slack for a busy machine, and it is the dominant cost of a `urc status`.
        /// </summary>
        public static readonly TimeSpan DefaultWindow = TimeSpan.FromMilliseconds(250);

        /// <summary>
        /// Queries the group and returns every editor that answered, deduplicated by pid.
        ///
        /// The query goes out once per candidate interface rather than once overall. Multicast
        /// interface selection is the least portable part of this design — Windows in particular is
        /// inconsistent about which interface carries a loopback-scoped group, which is why real
        /// mDNS implementations bind every interface instead of reasoning about it. Duplicate
        /// replies are expected and harmless; they collapse on pid.
        /// </summary>
        /// <param name="satisfied">
        /// Optional early exit. A targeted command knows which project it wants, and loopback replies
        /// arrive in well under a millisecond, so waiting out the full window is pure latency — it
        /// measured as ~285ms per command versus ~10ms of actual startup. Returning as soon as the
        /// wanted editor answers removes that from every invocation.
        ///
        /// The failure path still waits the whole window: concluding "not running" early would be
        /// wrong, and the full set is needed to tell the caller which editors ARE running.
        /// </param>
        /// <summary>
        /// How long to keep re-querying when the editor we want does not answer at all.
        ///
        /// A single query is ONE SAMPLE, and there are several ways to miss: during a domain reload
        /// the responder genuinely does not exist (measured at roughly half a second), and a loopback
        /// datagram can be dropped outright when the editor is saturated by an import. Both present
        /// identically — "no editor running" for an editor that is plainly there — and both clear on
        /// the next call, which is why they read as flakiness.
        ///
        /// So retry here instead of making the caller do it. The cost is paid only on a miss: a
        /// successful lookup still returns in milliseconds.
        /// </summary>
        /// Sized against the gap it exists to cover — a domain reload leaves no responder for roughly
        /// half a second — not against how long we are willing to wait. Every miss costs this in
        /// full, INCLUDING the common, legitimate case of "Unity simply is not running", so a
        /// generous budget would tax the wrong thing.
        private static readonly TimeSpan MissRetryBudget = TimeSpan.FromMilliseconds(1500);

        private static readonly TimeSpan MissRetryGap = TimeSpan.FromMilliseconds(120);

        /// <summary>
        /// Queries, retrying while <paramref name="present"/> finds nothing.
        ///
        /// `satisfied` ends the listening window early (the fast path); `present` decides whether the
        /// thing we came for answered AT ALL. They differ deliberately: a busy editor may fail
        /// `satisfied` (its tick is stale) yet still be present, and retrying that would add seconds
        /// to every command issued during an import.
        /// </summary>
        public static List<DiscoveryReply> Locate(
            Func<DiscoveryReply, bool> satisfied,
            Func<DiscoveryReply, bool> present,
            string projectPath = null,
            TimeSpan? patience = null)
        {
            var started = DateTime.UtcNow;
            var shortDeadline = started + MissRetryBudget;
            var reloadDeadline = started + (patience ?? ReloadPatience);
            var announced = false;
            List<DiscoveryReply> last;

            while (true)
            {
                last = Query(satisfied: satisfied);

                if (present == null) return last;

                foreach (var reply in last)
                {
                    if (!present(reply)) continue;
                    EditorHint.Remember(reply);      // so a future miss can be diagnosed
                    return last;
                }

                // Nothing answered for this project. Whether that is worth waiting out depends
                // entirely on whether the editor still EXISTS.
                var hintedPid = 0;
                var reloading = projectPath != null &&
                                EditorHint.LastEditorStillAlive(projectPath, out hintedPid);

                if (reloading)
                {
                    if (DateTime.UtcNow >= reloadDeadline) return last;

                    if (!announced)
                    {
                        announced = true;
                        // Say something: a silent 30s wait is indistinguishable from a hang.
                        Console.Error.WriteLine(
                            $"note: editor (pid {hintedPid}) is alive but not answering — probably reloading. Waiting…");
                    }

                    Thread.Sleep(MissRetryGap);
                    continue;
                }

                // No known editor process, or none running at all: the answer will not change.
                if (!AnyUnityProcess()) return last;
                if (DateTime.UtcNow >= shortDeadline) return last;

                Thread.Sleep(MissRetryGap);
            }
        }

        /// <summary>
        /// How long to wait on an editor that is provably alive but silent.
        ///
        /// Generous, because the thing being waited on is genuinely slow: a domain reload in a large
        /// project was measured at roughly 30 seconds of continuous silence. This only ever applies
        /// when the process is confirmed alive, so it cannot be spent on an editor that is gone.
        /// </summary>
        private static readonly TimeSpan ReloadPatience = TimeSpan.FromSeconds(90);

        /// <summary>
        /// Whether any Unity editor process exists. Deliberately conservative: an enumeration that
        /// fails reports true, so a permissions problem degrades into retrying rather than into a
        /// false "not running".
        /// </summary>
        private static bool AnyUnityProcess()
        {
            try { return Process.GetProcessesByName("Unity").Length > 0; }
            catch (Exception) { return true; }
        }

        public static List<DiscoveryReply> Query(
            TimeSpan? window = null,
            Func<DiscoveryReply, bool> satisfied = null)
        {
            var deadline = window ?? DefaultWindow;
            var nonce = Guid.NewGuid().ToString("N").Substring(0, 12);
            var found = new Dictionary<int, DiscoveryReply>();

            using var socket = new UdpClient(AddressFamily.InterNetwork);
            socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.Client.Bind(new IPEndPoint(IPAddress.Any, 0));

            // Hop limit 0 confines every datagram to this host.
            socket.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive,
                UrcProtocol.MulticastTtl);
            socket.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);

            var group = new IPEndPoint(IPAddress.Parse(UrcProtocol.MulticastGroup), UrcProtocol.MulticastPort);
            var payload = new DiscoveryQuery { Nonce = nonce }.ToBytes();
            var clock = Stopwatch.StartNew();

            // Loopback first, on its own. Enumerating network interfaces costs tens of milliseconds
            // on Windows — measured at ~40ms, several times the entire rest of a targeted command —
            // and a host-scoped group should never have needed the other interfaces anyway. So pay
            // for them only if loopback alone turns up nothing.
            SendVia(socket, IPAddress.Loopback, payload, group);
            if (Collect(socket, found, nonce, satisfied, clock, LoopbackGrace)) return Sorted(found);

            // Fallback: Windows is inconsistent about which interface carries a host-scoped group,
            // which is why real mDNS implementations bind them all rather than reasoning about it.
            foreach (var local in OtherInterfaces())
                SendVia(socket, local, payload, group);

            Collect(socket, found, nonce, satisfied, clock, deadline);
            return Sorted(found);
        }

        /// <summary>
        /// How long loopback alone gets before the other interfaces are tried. Replies arrive in well
        /// under a millisecond; this is slack for a busy editor, not an expected wait.
        /// </summary>
        private static readonly TimeSpan LoopbackGrace = TimeSpan.FromMilliseconds(40);

        private static void SendVia(UdpClient socket, IPAddress local, byte[] payload, IPEndPoint group)
        {
            try
            {
                socket.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface,
                    local.GetAddressBytes());
                socket.Send(payload, payload.Length, group);
            }
            catch (SocketException)
            {
                // An interface that refuses the send is simply not a path to any editor.
            }
        }

        /// <summary>Receives until <paramref name="until"/> elapses. Returns true if the caller's predicate was met.</summary>
        private static bool Collect(
            UdpClient socket,
            Dictionary<int, DiscoveryReply> found,
            string nonce,
            Func<DiscoveryReply, bool> satisfied,
            Stopwatch clock,
            TimeSpan until)
        {
            while (true)
            {
                var remaining = until - clock.Elapsed;
                if (remaining <= TimeSpan.Zero) return false;

                socket.Client.ReceiveTimeout = Math.Max(1, (int)remaining.TotalMilliseconds);
                IPEndPoint from = null;
                byte[] data;
                try { data = socket.Receive(ref from); }
                catch (SocketException) { return false; }   // timeout, or the socket went away

                // Even with TTL 0, trust the datagram's origin rather than the group's scope holding
                // across every network stack and VPN adapter.
                if (from == null || !IPAddress.IsLoopback(from.Address)) continue;

                if (!DiscoveryReply.TryParse(data, data.Length, out var reply)) continue;
                if (reply.Nonce != null && reply.Nonce != nonce) continue;   // stale or another client's
                if (reply.Pid <= 0) continue;

                found[reply.Pid] = reply;

                if (satisfied != null && satisfied(reply)) return true;
            }
        }

        private static List<DiscoveryReply> Sorted(Dictionary<int, DiscoveryReply> found)
        {
            var result = new List<DiscoveryReply>(found.Values);
            result.Sort((a, b) => string.CompareOrdinal(a.ProjectPath, b.ProjectPath));
            return result;
        }

        /// <summary>
        /// Every operational IPv4 interface except loopback, which is queried first and separately.
        /// Enumerating these is the expensive part, so it happens only on the fallback path.
        /// </summary>
        private static IEnumerable<IPAddress> OtherInterfaces()
        {
            NetworkInterface[] interfaces;
            try { interfaces = NetworkInterface.GetAllNetworkInterfaces(); }
            catch (NetworkInformationException) { yield break; }

            foreach (var nic in interfaces)
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                if (!nic.Supports(NetworkInterfaceComponent.IPv4)) continue;

                IPInterfaceProperties props;
                try { props = nic.GetIPProperties(); }
                catch (Exception) { continue; }

                foreach (var addr in props.UnicastAddresses)
                {
                    if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                        yield return addr.Address;
                }
            }
        }
    }
}
