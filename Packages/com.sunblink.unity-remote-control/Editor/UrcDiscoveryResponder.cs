using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using Sunblink.Urc.Protocol;

namespace Sunblink.Urc.Editor
{
    /// <summary>
    /// Answers discovery queries on a background thread.
    ///
    /// This thread is what replaces per-project port allocation: the editor binds an ephemeral TCP
    /// port and advertises it here, so nothing is assigned, registered, or configured, and any
    /// number of editors coexist with no setup.
    ///
    /// It must never touch a Unity API. Everything it reports comes from <see cref="UrcEditorState"/>
    /// volatiles, which is what keeps `urc status` answering through compiles and imports.
    /// </summary>
    internal sealed class UrcDiscoveryResponder : IDisposable
    {
        private readonly int _tcpPort;
        private UdpClient _socket;
        private Thread _thread;
        private volatile bool _running;

        public UrcDiscoveryResponder(int tcpPort)
        {
            _tcpPort = tcpPort;
        }

        public string LastError { get; private set; }

        public bool Start()
        {
            try
            {
                _socket = new UdpClient(AddressFamily.InterNetwork);

                // Several editors share this port; without ReuseAddress the second one to launch
                // would fail to bind and become undiscoverable.
                _socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _socket.Client.Bind(new IPEndPoint(IPAddress.Any, UrcProtocol.MulticastPort));
                UrcSockets.DisableHandleInheritance(_socket.Client);

                _socket.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive,
                    UrcProtocol.MulticastTtl);
                _socket.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);

                var joined = JoinOnAllInterfaces();
                if (joined == 0)
                {
                    LastError = "could not join the discovery multicast group on any interface.";
                    Dispose();
                    return false;
                }

                _running = true;
                _thread = new Thread(Loop) { IsBackground = true, Name = "urc-discovery" };
                _thread.Start();
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Dispose();
                return false;
            }
        }

        /// <summary>
        /// Joins the group on loopback and on every operational IPv4 interface.
        ///
        /// Loopback alone ought to be sufficient for a TTL-0 group, but Windows is not consistent
        /// about which interface carries a host-scoped group — which is why real mDNS implementations
        /// bind every interface rather than reasoning about it. TTL 0 means the extra memberships
        /// still cannot put a datagram on the wire.
        /// </summary>
        private int JoinOnAllInterfaces()
        {
            var group = IPAddress.Parse(UrcProtocol.MulticastGroup);
            var joined = 0;

            foreach (var local in LocalAddresses())
            {
                try
                {
                    _socket.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership,
                        new MulticastOption(group, local));
                    joined++;
                }
                catch (SocketException)
                {
                    // Already joined, or this interface will not carry the group. Neither is fatal.
                }
            }

            return joined;
        }

        private static IEnumerable<IPAddress> LocalAddresses()
        {
            yield return IPAddress.Loopback;

            NetworkInterface[] interfaces;
            try { interfaces = NetworkInterface.GetAllNetworkInterfaces(); }
            catch (NetworkInformationException) { yield break; }

            foreach (var nic in interfaces)
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

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

        private void Loop()
        {
            while (_running)
            {
                IPEndPoint from = null;
                byte[] data;

                try
                {
                    data = _socket.Receive(ref from);
                }
                catch (ObjectDisposedException) { return; }   // Dispose closed the socket
                catch (SocketException)
                {
                    // A transient receive error must not kill discovery for the rest of the session.
                    if (!_running) return;
                    continue;
                }

                // Even with TTL 0, judge by the datagram's origin rather than trusting the group's
                // scope to hold across every network stack and VPN adapter.
                if (from == null || !IPAddress.IsLoopback(from.Address)) continue;

                if (!DiscoveryQuery.TryParse(data, data.Length, out var query)) continue;

                try
                {
                    var reply = UrcEditorState.Snapshot(_tcpPort);
                    reply.Nonce = query.Nonce;
                    var payload = reply.ToBytes();

                    // Unicast straight back to the querying socket, so replies never fan out to
                    // other clients listening on the group.
                    _socket.Send(payload, payload.Length, from);
                }
                catch (ObjectDisposedException) { return; }
                catch (Exception)
                {
                    // A malformed or oversized reply is a bug worth surfacing, but not at the cost
                    // of taking the responder down mid-session.
                }
            }
        }

        public void Dispose()
        {
            _running = false;

            try { _socket?.Close(); } catch (Exception) { }
            _socket = null;

            // The loop exits on the socket close; joining briefly keeps a dying domain tidy without
            // risking a hang if the thread is wedged in a syscall.
            try { _thread?.Join(TimeSpan.FromMilliseconds(250)); } catch (Exception) { }
            _thread = null;
        }
    }
}
