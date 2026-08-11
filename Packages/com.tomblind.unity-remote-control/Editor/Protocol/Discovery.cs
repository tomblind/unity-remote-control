using System;
using System.Text;

namespace Urc.Protocol
{
    /// <summary>
    /// Sent by the CLI to the multicast group. Every running editor replies unicast to the query's
    /// source port, so replies never fan out to other clients.
    /// </summary>
    public sealed class DiscoveryQuery
    {
        /// <summary>
        /// Correlates replies with this query. Without it a datagram left over from a previous run —
        /// or from another CLI sharing the group — could be mistaken for an answer to this one.
        /// </summary>
        public string Nonce;

        public int Protocol = UrcProtocol.Version;

        public byte[] ToBytes()
        {
            var json = Json.Object()
                .Set("urc", UrcProtocol.Magic)
                .Set("op", UrcProtocol.Op.Query)
                .Set("protocol", Protocol)
                .Set("nonce", Nonce);
            return Encoding.UTF8.GetBytes(json.ToString());
        }

        public static bool TryParse(byte[] data, int length, out DiscoveryQuery query)
        {
            query = null;
            if (data == null || length <= 0 || length > UrcProtocol.MaxDatagramBytes) return false;

            string text;
            try { text = Encoding.UTF8.GetString(data, 0, length); }
            catch (Exception) { return false; }

            if (!Json.TryParse(text, out var json)) return false;
            if (json["urc"].AsString() != UrcProtocol.Magic) return false;
            if (json["op"].AsString() != UrcProtocol.Op.Query) return false;

            query = new DiscoveryQuery
            {
                Nonce = json["nonce"].AsString(),
                Protocol = json["protocol"].AsInt(),
            };
            return true;
        }
    }

    /// <summary>
    /// One editor's answer to a discovery query, and the entire payload behind `urc status`.
    ///
    /// Status is answered from this datagram rather than over TCP for a specific reason: only one TCP
    /// connection is served at a time, so a status that needed the socket would be unanswerable
    /// exactly when a long job is running — which is when you most want to ask.
    ///
    /// Everything here is stamped by the main-thread pump and read off a volatile, so the responder
    /// touches no Unity API and keeps answering through compiles and imports.
    /// </summary>
    public sealed class DiscoveryReply
    {
        public int Protocol = UrcProtocol.Version;
        public string Nonce;

        public string ProjectPath;
        public string UnityVersion;
        public string PackageVersion;

        public int Pid;
        public int TcpPort;

        /// <summary>
        /// Increments once per domain load. Comparing it across a reload is how the client proves a
        /// specific reload finished rather than guessing from timing.
        /// </summary>
        public int Generation;

        /// <summary>One of <see cref="UrcProtocol.State"/>.</summary>
        public string State = UrcProtocol.State.Idle;

        /// <summary>
        /// Time since the main thread last ticked. The one number that exposes a stalled or wedged
        /// main thread — a modal dialog, a long synchronous import — where `exec` would block.
        /// </summary>
        public double SecondsSinceLastTick;

        /// <summary>Identifies the editor session; dies with the process, unlike <see cref="Pid"/> which can be recycled.</summary>
        public string SessionId;

        /// <summary>Job currently in flight, if any. Feeds the `urc resume` recovery path.</summary>
        public string PendingJobId;

        /// <summary>Snippet assemblies loaded since the last domain reload — see the assembly-leak note.</summary>
        public int LoadedSnippets;

        public byte[] ToBytes()
        {
            var json = Json.Object()
                .Set("urc", UrcProtocol.Magic)
                .Set("ev", UrcProtocol.Ev.Reply)
                .Set("protocol", Protocol)
                .SetIf("nonce", Nonce)
                .Set("projectPath", ProjectPath)
                .Set("unityVersion", UnityVersion)
                .SetIf("packageVersion", PackageVersion)
                .Set("pid", Pid)
                .Set("tcpPort", TcpPort)
                .Set("generation", Generation)
                .Set("state", State)
                .Set("secondsSinceLastTick", Math.Round(SecondsSinceLastTick, 2))
                .SetIf("sessionId", SessionId)
                .SetIf("pendingJobId", PendingJobId)
                .Set("loadedSnippets", LoadedSnippets);

            var bytes = Encoding.UTF8.GetBytes(json.ToString());

            // A fragmented reply is a lost reply on a busy stack. Nothing here is unbounded except
            // the project path, so overflow means someone added a field that does not belong on the
            // datagram — fail loudly at the source rather than shipping a reply that vanishes.
            if (bytes.Length > UrcProtocol.MaxDatagramBytes)
                throw new InvalidOperationException(
                    $"Discovery reply is {bytes.Length} bytes, over the {UrcProtocol.MaxDatagramBytes}-byte limit. " +
                    "Discovery carries counts and states only.");

            return bytes;
        }

        public static bool TryParse(byte[] data, int length, out DiscoveryReply reply)
        {
            reply = null;
            if (data == null || length <= 0 || length > UrcProtocol.MaxDatagramBytes) return false;

            string text;
            try { text = Encoding.UTF8.GetString(data, 0, length); }
            catch (Exception) { return false; }

            if (!Json.TryParse(text, out var json)) return false;
            if (json["urc"].AsString() != UrcProtocol.Magic) return false;
            if (json["ev"].AsString() != UrcProtocol.Ev.Reply) return false;

            var projectPath = ProjectPaths.Canonicalize(json["projectPath"].AsString());
            if (projectPath == null) return false;

            reply = new DiscoveryReply
            {
                Protocol = json["protocol"].AsInt(),
                Nonce = json["nonce"].AsString(),
                ProjectPath = projectPath,
                UnityVersion = json["unityVersion"].AsString(""),
                PackageVersion = json["packageVersion"].AsString(),
                Pid = json["pid"].AsInt(),
                TcpPort = json["tcpPort"].AsInt(),
                Generation = json["generation"].AsInt(),
                State = json["state"].AsString(UrcProtocol.State.Idle),
                SecondsSinceLastTick = json["secondsSinceLastTick"].AsDouble(),
                SessionId = json["sessionId"].AsString(),
                PendingJobId = json["pendingJobId"].AsString(),
                LoadedSnippets = json["loadedSnippets"].AsInt(),
            };
            return true;
        }

        /// <summary>True when this editor can actually be driven by this build of the CLI.</summary>
        public bool IsCompatible => Protocol == UrcProtocol.Version;
    }
}
