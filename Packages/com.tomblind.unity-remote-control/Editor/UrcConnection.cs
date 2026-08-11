using System;
using System.IO;
using Urc.Protocol;

namespace Urc.Editor
{
    /// <summary>
    /// A client connection, with writes serialized.
    ///
    /// The lock is not decoration: the connection thread writes results and log frames, while the
    /// MAIN thread writes the `reloading` frame from beforeAssemblyReload. Those genuinely race, and
    /// an interleaved write would produce a torn line that the client cannot parse — at exactly the
    /// moment it most needs to understand what happened.
    /// </summary>
    internal sealed class UrcConnection
    {
        private readonly StreamWriter _writer;
        private readonly object _gate = new object();
        private bool _closed;

        public UrcConnection(StreamWriter writer)
        {
            _writer = writer;
        }

        /// <summary>Pid of the client that owns this connection, used to reclaim a slot whose owner died.</summary>
        public int ClientPid { get; set; }

        public bool Write(Json frame)
        {
            if (frame == null) return false;

            lock (_gate)
            {
                if (_closed) return false;
                try
                {
                    _writer.WriteLine(frame.ToString());
                    return true;
                }
                catch (Exception)
                {
                    // A client that vanished mid-job is routine. Mark closed so the job's remaining
                    // frames don't each pay for another failed write.
                    _closed = true;
                    return false;
                }
            }
        }

        public void Close()
        {
            lock (_gate) { _closed = true; }
        }

        public bool IsClosed
        {
            get { lock (_gate) { return _closed; } }
        }

        public void WriteError(string message) =>
            Write(Json.Object().Set("ev", UrcProtocol.Ev.Error).Set("message", message));
    }
}
