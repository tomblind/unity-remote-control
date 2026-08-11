using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
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
