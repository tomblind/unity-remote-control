using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEngine;
using Urc.Protocol;

// Aliased rather than `using System.Diagnostics`, which would make `Debug` ambiguous with UnityEngine's.
using Stopwatch = System.Diagnostics.Stopwatch;

namespace Urc.Editor
{
    /// <summary>
    /// Owns the TCP control channel and the discovery responder for this editor.
    ///
    /// Lifecycle rule that must not be relaxed: the server starts SYNCHRONOUSLY inside the
    /// [InitializeOnLoad] static constructor, never deferred through EditorApplication.delayCall or
    /// update. An unfocused editor may not tick for minutes after a reload, and delayCall registered
    /// from InitializeOnLoad has been observed to silently never fire after unfocused reloads on
    /// Unity 6000.3. Starting here is safe — binding a socket and starting threads needs no further
    /// editor initialization, and incoming requests simply queue on the main-thread pump.
    /// </summary>
    [InitializeOnLoad]
    internal static class UrcServer
    {
        private static TcpListener _listener;
        private static UrcDiscoveryResponder _discovery;
        private static Thread _acceptThread;
        private static volatile bool _running;

        /// <summary>The connection currently holding the slot, if any. Read by the main thread on reload.</summary>
        private static volatile UrcConnection _active;

        public static int Port { get; private set; }
        public static string LastError { get; private set; }
        public static bool IsRunning => _running;

        static UrcServer()
        {
            // BATCH MODE SERVES NOTHING. Unity spawns AssetImportWorker processes that are full
            // Unity.exe instances running this same package, with the SAME -projectPath. Without this
            // guard each worker binds a port and answers discovery, so the CLI sees several
            // indistinguishable editors for one project and picks one at random — and a worker never
            // runs EditorApplication.update, so a command landing on one hangs until the timeout.
            // Observed as random flakiness that "fixes itself" on retry, worst during heavy imports,
            // which is exactly when the workers exist.
            //
            // No interactive editor means nothing to drive, so batch mode simply does not serve.
            if (Application.isBatchMode) return;

            // Must come first: its constructor calls SessionState and Application.dataPath, and every
            // thread started below reads from it. Forcing it here guarantees those Unity APIs run on
            // the main thread rather than on whichever background thread happens to touch it first.
            UrcEditorState.EnsureInitialized();

            UrcMainThread.EnablePump();

            // Reconcile the machine-global throttle on the first update tick, never here: writes
            // during [InitializeOnLoad] half-apply (the pref store updates but the live session keeps
            // the old value). This is also what restores the pref after a reload killed a bracket.
            UrcThrottle.ScheduleSync();

            // A job left running by the previous domain had its continuation destroyed with that
            // domain. Settle it now, so a reconnecting client gets `interrupted` rather than waiting
            // on a result that can never arrive.
            UrcJobStore.ReconcileAfterReload();

            Start();

            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            EditorApplication.quitting += Shutdown;
        }

        private static void Start()
        {
            try
            {
                // Port 0: the OS picks a free port, advertised via discovery. Nothing is ever
                // allocated, recorded, or configured — and a zombie inherited handle can no longer
                // collide with us, because we never rebind a fixed number.
                _listener = new TcpListener(IPAddress.Loopback, 0);
                _listener.Start();
                UrcSockets.DisableHandleInheritance(_listener.Server);

                Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

                _discovery = new UrcDiscoveryResponder(Port);
                if (!_discovery.Start())
                {
                    LastError = "discovery unavailable: " + _discovery.LastError;
                    Debug.LogWarning($"[urc] {LastError} The editor is running but cannot be found by the CLI.");
                }

                _running = true;
                _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "urc-accept" };
                _acceptThread.Start();
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Debug.LogError($"[urc] failed to start: {ex.Message}");
                Shutdown();
            }
        }

        /// <summary>
        /// Announce the reload BEFORE the listener dies. Both prior tools left the client to infer a
        /// reload from a dropped socket, which is indistinguishable from a crash; an explicit frame
        /// turns an ambiguous failure into a "reconnect and re-attach" instruction.
        ///
        /// Runs on the main thread, hence the write lock inside UrcConnection.
        /// </summary>
        private static void OnBeforeAssemblyReload()
        {
            UrcEditorState.MarkReloading();

            _active?.Write(Json.Object()
                .Set("ev", UrcProtocol.Ev.Reloading)
                .Set("generation", UrcEditorState.Generation)
                .SetIf("jobId", UrcJobStore.PendingJobId));

            Shutdown();
        }

        private static void Shutdown()
        {
            _running = false;

            UrcMainThread.DisablePump();

            try { _discovery?.Dispose(); } catch (Exception) { }
            _discovery = null;

            try { _listener?.Stop(); } catch (Exception) { }
            _listener = null;

            try { _acceptThread?.Join(TimeSpan.FromMilliseconds(250)); } catch (Exception) { }
            _acceptThread = null;
        }

        /// <summary>
        /// Accepts connections and hands each to its own short-lived thread.
        ///
        /// ONE JOB at a time is still the rule, but it is enforced by the pending-job check in the
        /// handlers — NOT by blocking here. Serving inline used to mean a long job (or a settle
        /// window) stopped the accept loop entirely, so a second client never received the greeting
        /// and could not be told `busy`; it just timed out, and every retry timed out behind it. A
        /// single stalled settle therefore read as a wedged editor.
        ///
        /// Handler threads are cheap and short: an un-handshaked connection is dropped within
        /// UrcProtocol.RequestDeadline, and a client that just wants `busy` is answered immediately.
        /// </summary>
        private static void AcceptLoop()
        {
            while (_running)
            {
                TcpClient client;
                try { client = _listener.AcceptTcpClient(); }
                catch (ObjectDisposedException) { return; }
                catch (InvalidOperationException) { return; }   // listener stopped
                catch (SocketException) { if (!_running) return; continue; }

                var accepted = client;
                var thread = new Thread(() =>
                {
                    using (accepted)
                    {
                        UrcConnection connection = null;
                        try
                        {
                            UrcSockets.DisableHandleInheritance(accepted.Client);
                            connection = Serve(accepted);
                        }
                        catch (Exception)
                        {
                            // A client that dies mid-conversation is routine, not an error.
                        }
                        finally
                        {
                            // Only the slot holder clears the slot; a concurrent `busy` responder
                            // must not evict the connection actually running the job.
                            if (connection != null && ReferenceEquals(_active, connection)) _active = null;
                        }
                    }
                })
                { IsBackground = true, Name = "urc-conn" };

                thread.Start();
            }
        }

        /// <summary>Returns the connection if it claimed the job slot, else null.</summary>
        private static UrcConnection Serve(TcpClient client)
        {
            client.NoDelay = true;

            var stream = client.GetStream();
            var writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = true };
            var reader = new StreamReader(stream, new UTF8Encoding(false));
            var connection = new UrcConnection(writer);

            // The server speaks first, so probing is passive: a client reads one line and disconnects
            // if it is not us, and we never write bytes into a foreign service's socket.
            connection.Write(Greeting());

            // A connection has a bounded window to send a valid request. Without this, any local
            // process — a webpage doing fetch('http://127.0.0.1:<port>') — would occupy the only slot
            // and lock the editor out with no useful error. It cannot read our reply (this is not
            // HTTP), but holding the connection alone would be enough.
            client.ReceiveTimeout = (int)UrcProtocol.RequestDeadline.TotalMilliseconds;

            string line;
            try { line = reader.ReadLine(); }
            catch (IOException) { return null; }   // deadline elapsed, or the peer vanished

            if (string.IsNullOrEmpty(line)) return null;
            if (!Json.TryParse(line, out var request)) { connection.WriteError("malformed request frame."); return null; }

            var op = request["op"].AsString();
            if (string.IsNullOrEmpty(op)) { connection.WriteError("request has no 'op'."); return null; }

            var peerProtocol = request["client"]["protocol"].AsInt(UrcProtocol.Version);
            if (peerProtocol != UrcProtocol.Version)
            {
                connection.WriteError(
                    $"protocol mismatch: client speaks v{peerProtocol}, this package speaks " +
                    $"v{UrcProtocol.Version}. The CLI ships beside the package — re-run the installer.");
                return null;
            }

            connection.ClientPid = request["client"]["pid"].AsInt();

            // No further reads are expected, so the socket may now block indefinitely: a long job is
            // normal, and the client's own timeout bounds the wait.
            client.ReceiveTimeout = 0;

            var settle = !request["noSettle"].AsBool();

            switch (op)
            {
                case UrcProtocol.Op.Exec: HandleExec(connection, request, settle); break;
                case UrcProtocol.Op.Compile: HandleCompile(connection, request, settle); break;
                case UrcProtocol.Op.Attach: HandleAttach(connection, request, settle); break;
                default:
                    connection.WriteError($"op '{op}' is not implemented in package v{UrcVersion.Value}.");
                    break;
            }

            return connection;
        }

        /// <summary>
        /// Claims the job slot for this connection.
        ///
        /// Called by a handler only AFTER it has passed the busy check, so a connection that merely
        /// gets told `busy` never becomes the slot holder — and therefore never receives the
        /// `reloading` notification meant for the job actually running.
        /// </summary>
        private static void ClaimSlot(UrcConnection connection) => _active = connection;

        /// <summary>
        /// `compile` is deliberately thin: it triggers a refresh and lets the ambient machinery do
        /// the rest. It exists as a command at all because ambient handling OBSERVES reloads, it does
        /// not CAUSE them — after an agent edits a .cs file on disk, something has to tell Unity, and
        /// an unfocused editor (the normal state for agent work) will not notice on its own.
        /// </summary>
        private static void HandleCompile(UrcConnection connection, Json request, bool settle)
        {
            var jobId = request["jobId"].AsString();
            if (string.IsNullOrEmpty(jobId)) { connection.WriteError("compile requires a client-generated 'jobId'."); return; }

            var pending = UrcEditorState.PendingJobId;
            if (!string.IsNullOrEmpty(pending) && pending != jobId) { WriteBusy(connection, pending); return; }

            ClaimSlot(connection);

            var job = UrcJob.Create(jobId, UrcProtocol.Op.Compile, connection.ClientPid);
            job.State = UrcJobState.Running;
            var handle = UrcJobs.Register(job);

            connection.Write(Json.Object().Set("ev", UrcProtocol.Ev.Accepted).Set("jobId", jobId));

            UrcMainThread.RequestKeepAlive();
            EngageThrottle();

            // Console capture is scoped to the command, not left running: a real project logs
            // constantly, and recording output nobody asked about is pure churn.
            UrcLog.BeginCapture();

            UrcMainThread.Enqueue(() =>
            {
                UrcJobStore.Save(job);

                try
                {
                    AssetDatabase.Refresh();
                    job.Complete(UrcProtocol.Status.Ok, "refresh requested", null);
                }
                catch (Exception ex)
                {
                    job.Complete(UrcProtocol.Status.Failed, $"{ex.GetType().Name}: {ex.Message}", null);
                }
                finally
                {
                    UrcJobStore.Save(job);
                    UrcHistory.Record(job);
                    UrcJobs.MarkComplete(job.Id);
                    UrcMainThread.ReleaseKeepAlive();
                }

                return System.Threading.Tasks.Task.CompletedTask;
            });

            WaitFor(connection, handle, settle);
            ReleaseThrottle();
            UrcLog.EndCapture();
        }

        /// <summary>
        /// Throttle suspension is bracketed by ENQUEUED pairs rather than taken inside the job, so
        /// engage and release stay balanced even if the client disconnects before the job runs.
        /// EditorPrefs is main-thread only, hence the enqueue.
        ///
        /// The bracket covers the settle window too: the compile and reload a job provokes need the
        /// editor moving just as much as the job itself did.
        /// </summary>
        private static void EngageThrottle() =>
            UrcMainThread.Enqueue(() => { UrcThrottle.Engage(); return System.Threading.Tasks.Task.CompletedTask; });

        private static void ReleaseThrottle() =>
            UrcMainThread.Enqueue(() => { UrcThrottle.Release(); return System.Threading.Tasks.Task.CompletedTask; });

        private static void WriteBusy(UrcConnection connection, string pending)
        {
            UrcJobs.TryGet(pending, out var holder);
            connection.Write(Json.Object()
                .Set("ev", UrcProtocol.Ev.Busy)
                .Set("jobId", pending)
                .Set("cmd", holder?.Job.Cmd ?? "?")
                .Set("clientPid", holder?.Job.ClientPid ?? 0)
                .Set("message",
                    $"job {pending} is already in flight. One command runs at a time; " +
                    $"`urc resume {pending}` follows it. Batching setup, action and verification " +
                    $"into a single exec avoids this entirely."));
        }

        /// <summary>Serves a result from the journal — the reconnect path after a domain reload.</summary>
        private static void HandleAttach(UrcConnection connection, Json request, bool settle)
        {
            var jobId = request["jobId"].AsString();

            // A job this domain is running: wait on the same signal as the original caller, which is
            // what makes a re-attach indistinguishable from never having disconnected.
            if (!string.IsNullOrEmpty(jobId) && UrcJobs.TryGet(jobId, out var live))
            {
                ClaimSlot(connection);
                connection.Write(Json.Object().Set("ev", UrcProtocol.Ev.Accepted).Set("jobId", jobId));

                // A re-attach continues the original command, so it re-opens the capture window —
                // the fresh domain after a reload starts with capture off.
                UrcLog.BeginCapture();
                try { WaitFor(connection, live, settle); }
                finally { UrcLog.EndCapture(); }
                return;
            }

            // Otherwise it predates this domain, so the journal is the only record — and reading it
            // means asking the main thread (SessionState is Unity API).
            var job = UrcJobs.LoadViaMainThread(jobId, () => KeepWaiting(connection));
            if (job == null)
            {
                connection.WriteError(string.IsNullOrEmpty(jobId)
                    ? "no job to resume in this editor session."
                    : $"job '{jobId}' is unknown to this editor session.");
                return;
            }

            connection.Write(job.ToResultFrame());

            // A re-attach after a reload continues settling, so the caller still learns whether the
            // compile that caused the reload actually succeeded.
            if (!settle) return;

            UrcLog.BeginCapture();
            try { Settle(connection); }
            finally { UrcLog.EndCapture(); }
        }

        private static void HandleExec(UrcConnection connection, Json request, bool settle)
        {
            var jobId = request["jobId"].AsString();
            if (string.IsNullOrEmpty(jobId)) { connection.WriteError("exec requires a client-generated 'jobId'."); return; }

            var code = request["code"].AsString();
            if (string.IsNullOrEmpty(code)) { connection.WriteError("exec requires 'code'."); return; }

            // Read the pending id from the volatile mirror, not the journal: this is the connection
            // thread, and SessionState is main-thread only.
            var pending = UrcEditorState.PendingJobId;
            if (!string.IsNullOrEmpty(pending) && pending != jobId) { WriteBusy(connection, pending); return; }

            ClaimSlot(connection);

            var usings = new List<string>();
            foreach (var item in request["usings"].Items)
            {
                var value = item.AsString();
                if (!string.IsNullOrEmpty(value)) usings.Add(value);
            }

            // Parameters travel beside the source, never inside it — which is what lets a reusable
            // snippet stay byte-identical across invocations.
            var snippetArgs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var field in request["args"].Fields)
                snippetArgs[field.Key] = field.Value.AsString("");

            // A caller-supplied helper library, compiled to a real assembly and kept resident for the
            // domain. Parsed here on the connection thread — it is only JSON, no Unity API — and
            // built on the main thread below, where Roslyn runs.
            var librarySources = new List<KeyValuePair<string, string>>();
            foreach (var item in request["lib"].Items)
            {
                librarySources.Add(new KeyValuePair<string, string>(
                    item["name"].AsString("?"), item["code"].AsString("")));
            }

            var job = UrcJob.Create(jobId, UrcProtocol.Op.Exec, connection.ClientPid);
            job.State = UrcJobState.Running;
            job.Detail = code;
            job.Sources = request["sources"];

            // Registered before enqueueing, so a re-attach arriving while the job runs finds it.
            var handle = UrcJobs.Register(job);

            connection.Write(Json.Object().Set("ev", UrcProtocol.Ev.Accepted).Set("jobId", jobId));

            UrcMainThread.RequestKeepAlive();
            EngageThrottle();

            // Console capture is scoped to the command, not left running: a real project logs
            // constantly, and recording output nobody asked about is pure churn.
            UrcLog.BeginCapture();

            UrcMainThread.Enqueue(async () =>
            {
                // On the main thread from here: the journal write and the run both need it.
                UrcJobStore.Save(job);

                // Cursor stamped at DISPATCH, so `logs --since` shows everything this command caused,
                // across any number of reloads.
                var cursor = UrcLog.Cursor;
                UrcLog.Snapshot(out var errors0, out var warnings0, out var total0);

                try
                {
                    // Reported as its own failure rather than folded into the snippet's: a broken
                    // helper is a fault in the library, at a line in the library's own file, and
                    // saying "your snippet failed" would send the caller to the wrong place.
                    UrcLibrary.Handle library = null;
                    if (librarySources.Count > 0 && !UrcLibrary.TryGetOrBuild(
                            librarySources, UrcCodeRunner.BaseOptions, UrcCodeRunner.Loader,
                            out library, out var libraryError))
                    {
                        job.Complete(UrcProtocol.Status.Failed,
                            "the --lib library failed to compile:\n  " + libraryError, null);
                    }
                    else
                    {
                        var run = await UrcCodeRunner.RunAsync(code, usings, snippetArgs, library);
                        job.Complete(run.Status, run.Summary, run.Value);
                        job.ValueArtifact = run.ValueArtifact;
                    }
                }
                catch (Exception ex)
                {
                    job.Complete(UrcProtocol.Status.Failed, $"{ex.GetType().Name}: {ex.Message}", null);
                }
                finally
                {
                    job.Logs = UrcLog.SummarySince(errors0, warnings0, total0, cursor);
                    // ORDERING RULE: journal first, signal second. If delivery then loses a race with
                    // a reload, the client re-attaches and reads the result back from the journal.
                    // That safety net is what makes pre-reload delivery an optimization rather than a
                    // correctness requirement — the prior tool had none, and needed a bespoke
                    // compilationFinished hook to avoid losing results outright.
                    UrcJobStore.Save(job);
                    UrcHistory.Record(job);
                    UrcJobs.MarkComplete(job.Id);
                    UrcMainThread.ReleaseKeepAlive();
                }
            });

            WaitFor(connection, handle, settle);
            ReleaseThrottle();
            UrcLog.EndCapture();
        }

        /// <summary>
        /// Blocks the connection thread until the job finishes, then delivers the result and settles.
        /// Reads the in-memory job object — never the SessionState journal, which is main-thread only.
        /// </summary>
        private static void WaitFor(UrcConnection connection, UrcJobs.Handle handle, bool settle)
        {
            if (!UrcJobs.Await(handle, () => KeepWaiting(connection))) return;

            connection.Write(handle.Job.ToResultFrame());
            if (settle) Settle(connection);
        }

        /// <summary>
        /// Streams editor state until things go quiet, so a command never returns into an editor
        /// that is about to be busy.
        ///
        /// This is what the `git pull` case needs: an exec finishes successfully, and only THEN does
        /// the import + compile + reload it provoked begin. Returning immediately would hand the
        /// caller a success followed by a busy editor on its very next call — and, worse, no idea
        /// that the pulled code does not compile.
        ///
        /// Runs entirely on volatiles, so it keeps working while the editor is busy.
        /// </summary>
        private static void Settle(UrcConnection connection)
        {
            var clock = Stopwatch.StartNew();
            var lastPhase = "";
            var idleSince = -1L;

            while (KeepWaiting(connection) && clock.Elapsed < SettleBudget)
            {
                var phase = UrcEditorState.State;

                if (phase != lastPhase)
                {
                    connection.Write(Json.Object()
                        .Set("ev", UrcProtocol.Ev.State)
                        .Set("phase", phase)
                        .Set("generation", UrcEditorState.Generation));
                    lastPhase = phase;
                }

                // Settled means "nothing transient left to wait out" — NOT "idle". Play mode is a
                // stable state, so waiting for idle there waits forever.
                if (!UrcProtocol.State.IsTransient(phase))
                {
                    if (idleSince < 0) idleSince = clock.ElapsedMilliseconds;
                    else if (clock.ElapsedMilliseconds - idleSince >= SettleDebounceMs)
                    {
                        WriteSettled(connection, true);
                        return;
                    }
                }
                else
                {
                    idleSince = -1;
                }

                UrcMainThread.WakeEditor();
                Thread.Sleep(25);
            }

            // Someone editing files in their IDE can keep an editor churning indefinitely, so the
            // budget is a real outcome, not a failure: report the result and say it is still busy.
            if (KeepWaiting(connection)) WriteSettled(connection, false);
        }

        private static void WriteSettled(UrcConnection connection, bool settled)
        {
            // The compile report needs SessionState, so it is fetched from the main thread rather
            // than read directly here.
            var compile = UrcEditorState.CompileErrors > 0
                ? UrcMainThread.Request(() => UrcCompileWatch.Report(), () => KeepWaiting(connection), null)
                : Json.Object().Set("status", "ok").Set("errorCount", 0);

            connection.Write(Json.Object()
                .Set("ev", UrcProtocol.Ev.State)
                // Report the state we actually ended in, never a hardcoded "idle" — settling in play
                // mode is now normal, and mislabelling it would hide that from the caller.
                .Set("phase", UrcEditorState.State)
                .Set("settled", settled)
                .Set("generation", UrcEditorState.Generation)
                .SetIf("compile", compile));
        }

        /// <summary>How long to wait for quiet before giving up and reporting "still busy".</summary>
        private static readonly TimeSpan SettleBudget = TimeSpan.FromMinutes(5);

        /// <summary>Idle must hold this long — a compile often starts a beat after a refresh returns.</summary>
        private const long SettleDebounceMs = 300;

        /// <summary>
        /// False once the client is gone or the domain is going down. A disconnected client needs no
        /// result — and the job keeps running regardless, its answer recoverable via `urc resume`.
        /// </summary>
        private static bool KeepWaiting(UrcConnection connection) => _running && !connection.IsClosed;

        private static Json Greeting() =>
            Json.Object()
                .Set("ev", UrcProtocol.Ev.Hello)
                .Set("protocol", UrcProtocol.Version)
                .Set("pid", UrcProcess.Id)
                .Set("generation", UrcEditorState.Generation)
                .Set("projectPath", UrcEditorState.ProjectPath)
                .Set("unityVersion", UrcEditorState.UnityVersion)
                .Set("sessionId", UrcEditorState.SessionId)
                .Set("state", UrcEditorState.State);
    }
}
