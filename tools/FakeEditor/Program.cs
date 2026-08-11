using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Sunblink.Urc.Protocol;

namespace Sunblink.Urc.FakeEditor
{
    /// <summary>
    /// A scriptable stand-in for a Unity editor, speaking the real protocol.
    ///
    /// It exists to test the CLI's reconnect/re-attach state machine, which is the least testable and
    /// most important part of the design: provoking a domain reload at a precise moment inside a real
    /// editor is slow and unreliable, while here it is a flag. The crash path is likewise a flag
    /// rather than a `taskkill` race.
    ///
    /// It compiles the same shared protocol sources as both real sides, so a wire-format change
    /// breaks it too.
    /// </summary>
    public static class Program
    {
        private sealed class Job
        {
            public string Id;
            public bool Done;
            public string Status = UrcProtocol.Status.Ok;
            public Json Value;
        }

        private static readonly ConcurrentDictionary<string, Job> Jobs = new ConcurrentDictionary<string, Job>();

        private static volatile int _generation = 1;
        private static volatile string _state = UrcProtocol.State.Idle;
        private static volatile string _pendingJobId;
        private static volatile string _lastJobId;
        private static volatile bool _running = true;

        private static string _projectPath;
        private static string _unityVersion;
        private static double _stall;
        private static int _execDelayMs;
        private static int _reloadAfterMs;
        private static int _dieAfterMs;

        public static int Main(string[] args)
        {
            _projectPath = ProjectPaths.Canonicalize(Arg(args, "--project") ?? Environment.CurrentDirectory);
            _unityVersion = Arg(args, "--unity") ?? "6000.3.9f1";
            _state = Arg(args, "--state") ?? UrcProtocol.State.Idle;
            _generation = Int(args, "--generation", 1);
            _stall = Double(args, "--stall", 0);
            _execDelayMs = Int(args, "--exec-delay", 50);
            _reloadAfterMs = Int(args, "--reload-after", 0);
            _dieAfterMs = Int(args, "--die-after", 0);
            var seconds = Int(args, "--seconds", 20);

            if (_projectPath == null)
            {
                Console.Error.WriteLine("fake-editor: bad --project");
                return 2;
            }

            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var tcpPort = ((IPEndPoint)listener.LocalEndpoint).Port;

            var discovery = new Thread(() => DiscoveryLoop(tcpPort)) { IsBackground = true };
            discovery.Start();

            var accept = new Thread(() => AcceptLoop(listener)) { IsBackground = true };
            accept.Start();

            Log($"{_projectPath}");
            Log($"tcp {tcpPort} · generation {_generation} · state {_state}" +
                (_reloadAfterMs > 0 ? $" · reload after {_reloadAfterMs}ms" : "") +
                (_dieAfterMs > 0 ? $" · die after {_dieAfterMs}ms" : ""));

            if (_dieAfterMs > 0)
            {
                Thread.Sleep(_dieAfterMs);
                Log("simulating a crash (exiting without notice)");
                Environment.Exit(9);
            }

            var clock = Stopwatch.StartNew();
            while (clock.Elapsed < TimeSpan.FromSeconds(seconds)) Thread.Sleep(50);

            _running = false;
            listener.Stop();
            Log("exiting");
            return 0;
        }

        // ---- discovery ----------------------------------------------------------------------

        private static void DiscoveryLoop(int tcpPort)
        {
            using var socket = new UdpClient(AddressFamily.InterNetwork);
            socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.Client.Bind(new IPEndPoint(IPAddress.Any, UrcProtocol.MulticastPort));
            socket.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive,
                UrcProtocol.MulticastTtl);
            socket.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);

            var group = IPAddress.Parse(UrcProtocol.MulticastGroup);
            foreach (var local in LocalAddresses())
            {
                try
                {
                    socket.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership,
                        new MulticastOption(group, local));
                }
                catch (SocketException) { }
            }

            while (_running)
            {
                socket.Client.ReceiveTimeout = 250;
                IPEndPoint from = null;
                byte[] data;
                try { data = socket.Receive(ref from); }
                catch (SocketException) { continue; }
                catch (ObjectDisposedException) { return; }

                if (from == null || !IPAddress.IsLoopback(from.Address)) continue;
                if (!DiscoveryQuery.TryParse(data, data.Length, out var query)) continue;

                var reply = new DiscoveryReply
                {
                    Nonce = query.Nonce,
                    ProjectPath = _projectPath,
                    UnityVersion = _unityVersion,
                    PackageVersion = "0.1.0-fake",
                    Pid = Process.GetCurrentProcess().Id,
                    TcpPort = tcpPort,
                    Generation = _generation,
                    State = _state,
                    SecondsSinceLastTick = _stall,
                    SessionId = "fake0001",
                    PendingJobId = _pendingJobId,
                };

                try
                {
                    var payload = reply.ToBytes();
                    socket.Send(payload, payload.Length, from);
                }
                catch (Exception) { }
            }
        }

        // ---- control channel ----------------------------------------------------------------

        private static void AcceptLoop(TcpListener listener)
        {
            while (_running)
            {
                TcpClient client;
                try { client = listener.AcceptTcpClient(); }
                catch (Exception) { return; }

                using (client)
                {
                    try { Serve(client); }
                    catch (Exception) { }
                }
            }
        }

        private static void Serve(TcpClient client)
        {
            client.NoDelay = true;
            var stream = client.GetStream();
            var writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = true };
            var reader = new StreamReader(stream, new UTF8Encoding(false));

            Write(writer, Json.Object()
                .Set("ev", UrcProtocol.Ev.Hello)
                .Set("protocol", UrcProtocol.Version)
                .Set("pid", Process.GetCurrentProcess().Id)
                .Set("generation", _generation)
                .Set("projectPath", _projectPath)
                .Set("unityVersion", _unityVersion)
                .Set("sessionId", "fake0001")
                .Set("state", _state));

            client.ReceiveTimeout = (int)UrcProtocol.RequestDeadline.TotalMilliseconds;
            string line;
            try { line = reader.ReadLine(); }
            catch (IOException) { return; }
            client.ReceiveTimeout = 0;

            if (string.IsNullOrEmpty(line) || !Json.TryParse(line, out var request)) return;

            var op = request["op"].AsString();
            var jobId = request["jobId"].AsString();
            Log($"<- {op} {jobId}");

            switch (op)
            {
                case UrcProtocol.Op.Exec: HandleExec(writer, jobId); break;
                case UrcProtocol.Op.Attach: HandleAttach(writer, jobId); break;
                default:
                    Write(writer, Json.Object().Set("ev", UrcProtocol.Ev.Error).Set("message", $"unknown op '{op}'"));
                    break;
            }
        }

        private static void HandleExec(StreamWriter writer, string jobId)
        {
            var job = Jobs.GetOrAdd(jobId, id => new Job { Id = id });
            _pendingJobId = jobId;
            _lastJobId = jobId;
            _state = UrcProtocol.State.Busy;

            Write(writer, Json.Object().Set("ev", UrcProtocol.Ev.Accepted).Set("jobId", jobId));

            var clock = Stopwatch.StartNew();

            // Simulated domain reload: announce it, drop the connection, bump the generation. The
            // job survives in memory exactly as a real one survives in SessionState.
            if (_reloadAfterMs > 0 && !job.Done)
            {
                while (clock.ElapsedMilliseconds < _reloadAfterMs && !job.Done) Thread.Sleep(5);

                Log("simulating a domain reload");
                _state = UrcProtocol.State.Reloading;
                Write(writer, Json.Object()
                    .Set("ev", UrcProtocol.Ev.Reloading)
                    .Set("generation", _generation)
                    .Set("jobId", jobId));

                // Finish the job in the background, as a pre-reload completion would.
                var remaining = Math.Max(0, _execDelayMs - (int)clock.ElapsedMilliseconds);
                new Thread(() =>
                {
                    Thread.Sleep(remaining);
                    Finish(job);
                    Thread.Sleep(150);
                    _generation++;
                    _state = UrcProtocol.State.Idle;
                    Log($"reload complete, generation {_generation}");
                }) { IsBackground = true }.Start();

                return;   // connection closes; the client is expected to re-attach
            }

            while (clock.ElapsedMilliseconds < _execDelayMs) Thread.Sleep(5);
            Finish(job);
            Write(writer, ResultFrame(job));
        }

        private static void HandleAttach(StreamWriter writer, string jobId)
        {
            // No id means "whatever is pending, else the most recent" — matching the real server, so
            // `urc resume` with no argument is exercised here too.
            if (string.IsNullOrEmpty(jobId)) jobId = _pendingJobId ?? _lastJobId;

            if (string.IsNullOrEmpty(jobId) || !Jobs.TryGetValue(jobId, out var job))
            {
                Write(writer, Json.Object()
                    .Set("ev", UrcProtocol.Ev.Error)
                    .Set("message", $"job '{jobId}' is unknown to this editor session."));
                return;
            }

            Write(writer, Json.Object().Set("ev", UrcProtocol.Ev.Accepted).Set("jobId", jobId));

            while (!job.Done && _running) Thread.Sleep(10);
            Write(writer, ResultFrame(job));
        }

        private static void Finish(Job job)
        {
            job.Value = Json.String("fake-result");
            job.Done = true;
            _pendingJobId = null;
            _state = UrcProtocol.State.Idle;
        }

        private static Json ResultFrame(Job job) =>
            Json.Object()
                .Set("ev", UrcProtocol.Ev.Result)
                .Set("jobId", job.Id)
                .Set("status", job.Status)
                .SetIf("value", job.Value)
                .Set("generation", _generation);

        private static void Write(StreamWriter writer, Json frame)
        {
            try { writer.WriteLine(frame.ToString()); }
            catch (IOException) { }
        }

        // ---- helpers ------------------------------------------------------------------------

        private static void Log(string message) => Console.WriteLine("fake-editor: " + message);

        private static string Arg(string[] args, string name)
        {
            for (var i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            return null;
        }

        private static int Int(string[] args, string name, int fallback) =>
            int.TryParse(Arg(args, name), out var value) ? value : fallback;

        private static double Double(string[] args, string name, double fallback) =>
            double.TryParse(Arg(args, name), out var value) ? value : fallback;

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
                foreach (var addr in nic.GetIPProperties().UnicastAddresses)
                    if (addr.Address.AddressFamily == AddressFamily.InterNetwork) yield return addr.Address;
            }
        }
    }
}
