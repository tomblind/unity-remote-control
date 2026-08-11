using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Sunblink.Urc.Protocol;

namespace Sunblink.Urc
{
    /// <summary>A live TCP connection to one editor, framed as NDJSON.</summary>
    internal sealed class EditorConnection : IDisposable
    {
        private readonly TcpClient _client;
        private readonly StreamWriter _writer;
        private readonly StreamReader _reader;

        public Json Greeting { get; private set; }

        private EditorConnection(TcpClient client, StreamWriter writer, StreamReader reader)
        {
            _client = client;
            _writer = writer;
            _reader = reader;
        }

        /// <summary>
        /// Connects and validates the greeting.
        ///
        /// The greeting is not informational — the CLI already knows everything in it from discovery.
        /// It guards a narrow but real race: discovery reports port 51734, the editor quits, another
        /// process binds 51734, and we connect to a stranger. Checking pid and project BEFORE sending
        /// anything means source code never reaches an unknown process.
        /// </summary>
        public static EditorConnection Connect(DiscoveryReply editor, TimeSpan timeout, out string error)
        {
            error = null;
            TcpClient client = null;

            try
            {
                client = new TcpClient { NoDelay = true };
                var connect = client.BeginConnect(System.Net.IPAddress.Loopback, editor.TcpPort, null, null);
                if (!connect.AsyncWaitHandle.WaitOne(timeout))
                {
                    error = $"timed out connecting to 127.0.0.1:{editor.TcpPort}.";
                    client.Close();
                    return null;
                }
                client.EndConnect(connect);

                var stream = client.GetStream();
                var writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = true };
                var reader = new StreamReader(stream, new UTF8Encoding(false));

                client.ReceiveTimeout = (int)timeout.TotalMilliseconds;
                var line = reader.ReadLine();
                client.ReceiveTimeout = 0;

                if (string.IsNullOrEmpty(line) || !Json.TryParse(line, out var greeting) ||
                    greeting["ev"].AsString() != UrcProtocol.Ev.Hello)
                {
                    error = $"port {editor.TcpPort} did not greet as a Unity editor — it may have been " +
                            "reused by another process since discovery.";
                    client.Close();
                    return null;
                }

                if (greeting["pid"].AsInt() != editor.Pid)
                {
                    error = $"port {editor.TcpPort} is now owned by pid {greeting["pid"].AsInt()}, " +
                            $"not the editor discovery reported (pid {editor.Pid}).";
                    client.Close();
                    return null;
                }

                var connection = new EditorConnection(client, writer, reader) { Greeting = greeting };
                connection.StartReader();
                return connection;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                try { client?.Close(); } catch (Exception) { }
                return null;
            }
        }

        public bool Send(Json frame)
        {
            try { _writer.WriteLine(frame.ToString()); return true; }
            catch (IOException) { return false; }
            catch (ObjectDisposedException) { return false; }
        }

        public enum Read { Frame, Timeout, Closed }

        private System.Collections.Concurrent.BlockingCollection<Json> _frames;
        private volatile bool _closed;

        /// <summary>
        /// Frames are read on their own thread rather than with a socket receive timeout.
        ///
        /// A timeout that fires partway through a line would leave the StreamReader holding half a
        /// frame with no way to resume it — so the caller's deadline is enforced by waiting on a
        /// queue instead, which cannot tear the stream.
        /// </summary>
        private void StartReader()
        {
            _frames = new System.Collections.Concurrent.BlockingCollection<Json>();

            new Thread(() =>
            {
                try
                {
                    while (true)
                    {
                        var line = _reader.ReadLine();
                        if (line == null) break;                       // peer closed
                        if (line.Length == 0) continue;
                        if (Json.TryParse(line, out var frame)) _frames.Add(frame);
                    }
                }
                catch (Exception) { /* closed, reset, or disposed — all mean the same thing here */ }
                finally
                {
                    _closed = true;
                    try { _frames.CompleteAdding(); } catch (Exception) { }
                }
            })
            { IsBackground = true, Name = "urc-reader" }.Start();
        }

        /// <summary>
        /// Waits up to <paramref name="timeout"/> for the next frame.
        /// <see cref="Read.Closed"/> means the socket went away — which a domain reload also looks
        /// like, and which the caller distinguishes by checking whether the pid is still alive.
        /// </summary>
        public Read TryReadFrame(TimeSpan timeout, out Json frame)
        {
            frame = null;
            try
            {
                if (_frames.TryTake(out frame, (int)Math.Max(1, timeout.TotalMilliseconds)))
                    return Read.Frame;
            }
            // Covers ObjectDisposedException too, which derives from it.
            catch (InvalidOperationException) { return Read.Closed; }   // CompleteAdding + drained

            return _closed && _frames.IsCompleted ? Read.Closed : Read.Timeout;
        }

        public void Dispose()
        {
            _closed = true;
            try { _client?.Close(); } catch (Exception) { }
            try { _frames?.Dispose(); } catch (Exception) { }
        }
    }

    internal static class ProcessLiveness
    {
        /// <summary>
        /// Whether a pid is still running. This is what separates "the editor is reloading, wait for
        /// it" from "the editor died, stop waiting" — and it is why a crash fails in seconds instead
        /// of burning the full timeout.
        ///
        /// It is a check on ONE known pid, not an enumeration, so it needs no special privileges and
        /// no platform-specific code.
        /// </summary>
        public static bool IsAlive(int pid)
        {
            if (pid <= 0) return false;
            try
            {
                using var process = Process.GetProcessById(pid);
                return !process.HasExited;
            }
            catch (ArgumentException) { return false; }      // no such process
            catch (InvalidOperationException) { return false; }
            catch (Exception) { return true; }               // can't tell: assume alive, let the timeout decide
        }
    }
}
