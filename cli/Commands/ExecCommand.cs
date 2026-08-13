using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Urc.Discovery;
using Urc.Protocol;

namespace Urc
{
    /// <summary>
    /// Runs C# in the editor and blocks until the real answer arrives — across domain reloads,
    /// invisibly.
    ///
    /// This is the command the whole design exists to make possible. Both prior tools forced the
    /// caller to poll: one because MCP capped tool calls at ~60s and had to hand off to a result
    /// file, the other because its protocol was files being polled from the editor's update loop.
    /// Here the CLI holds a socket, and when a reload kills it, reconnects and re-attaches on the
    /// caller's behalf. One command, one answer.
    /// </summary>
    internal static class ExecCommand
    {
        public static int Run(Args args)
        {
            var snippet = ReadCode(args, out var codeError);
            if (snippet == null) { Program.Error(codeError); return ExitCode.Usage; }

            var project = ProjectResolver.Resolve(args.Get("project"), out _);
            var timeout = ParseTimeout(args.Get("timeout"), TimeSpan.FromSeconds(120));

            var replies = DiscoveryClient.Locate(
                ProjectResolver.Satisfies(project, args.Pid),
                ProjectResolver.Present(project, args.Pid),
                project);

            if (!ProjectResolver.TrySelect(replies, project, out var editor, out var error, args.Pid))
            {
                Program.Error(error);
                return project == null ? ExitCode.Usage : ExitCode.Failed;
            }

            var usings = new List<string>();
            var usingArg = args.Get("using");
            if (!string.IsNullOrEmpty(usingArg)) usings.AddRange(usingArg.Split(','));

            // Client-generated, which is what makes re-attach idempotent: the same id identifies the
            // job whether we are submitting it or reconnecting to it.
            var jobId = "J" + Guid.NewGuid().ToString("N").Substring(0, 8);

            return new Session(editor, jobId, snippet.Code, usings, timeout, args)
            {
                SourceMap = snippet.Map
            }.Run();
        }

        /// <summary>
        /// `urc compile` — tell Unity that files on disk changed, and report whether it still builds.
        ///
        /// Thin by design: the ambient machinery observes the resulting import, compile and reload.
        /// It exists as a command because ambient handling OBSERVES reloads, it does not CAUSE them —
        /// a focused editor auto-refreshes, an unfocused one (the normal state for agent work) will
        /// sit there indefinitely after an edit.
        /// </summary>
        public static int Compile(Args args)
        {
            var project = ProjectResolver.Resolve(args.Get("project"), out _);
            var timeout = ParseTimeout(args.Get("timeout"), TimeSpan.FromMinutes(5));

            var replies = DiscoveryClient.Locate(
                ProjectResolver.Satisfies(project, args.Pid),
                ProjectResolver.Present(project, args.Pid),
                project);

            if (!ProjectResolver.TrySelect(replies, project, out var editor, out var error, args.Pid))
            {
                Program.Error(error);
                return project == null ? ExitCode.Usage : ExitCode.Failed;
            }

            var jobId = "J" + Guid.NewGuid().ToString("N").Substring(0, 8);
            return new Session(editor, jobId, null, null, timeout, args)
            {
                Op = UrcProtocol.Op.Compile
            }.Run();
        }

        /// <summary>
        /// `urc resume` — pick up a job whose CLI was killed or timed out.
        ///
        /// Shares exec's session machinery: a resume is exactly the state exec is already in after a
        /// reload, minus the initial submission. With no job id it means "the pending job, or the
        /// last one" — there is only ever one in flight, so the id is usually unnecessary.
        /// </summary>
        public static int Resume(Args args)
        {
            var project = ProjectResolver.Resolve(args.Get("project"), out _);
            var timeout = ParseTimeout(args.Get("timeout"), TimeSpan.FromSeconds(120));

            var replies = DiscoveryClient.Locate(
                ProjectResolver.Satisfies(project, args.Pid),
                ProjectResolver.Present(project, args.Pid),
                project);

            if (!ProjectResolver.TrySelect(replies, project, out var editor, out var error, args.Pid))
            {
                Program.Error(error);
                return project == null ? ExitCode.Usage : ExitCode.Failed;
            }

            // Positional id, else whatever the editor reports as pending, else let the editor decide.
            var jobId = args.Positional.Count > 1 ? args.Positional[1] : editor.PendingJobId;

            return new Session(editor, jobId, null, null, timeout, args) { AlreadySubmitted = true }.Run();
        }

        private sealed class Session
        {
            private readonly DiscoveryReply _editor;
            private readonly string _jobId;
            private readonly string _code;
            private readonly List<string> _usings;
            private readonly TimeSpan _timeout;
            private readonly Args _args;
            private readonly Stopwatch _clock = Stopwatch.StartNew();

            private int _generation;
            private bool _submitted;

            /// <summary>Held until the editor settles, so one command prints one coherent answer.</summary>
            private Json _result;
            private Json _settled;

            /// <summary>
            /// Whether the editor announced a reload on this connection.
            ///
            /// This is what separates "the socket dropped because the domain is reloading — reconnect
            /// and re-attach" from "the peer simply closed". Without it, a server that never sends a
            /// settle frame (an older package, or any protocol drift) turns into a reconnect loop
            /// that spins until the timeout, even though the result is already in hand.
            /// </summary>
            private bool _sawReloading;

            /// <summary>Set by `resume`: there is nothing to submit, only a job to re-attach to.</summary>
            public bool AlreadySubmitted { set { _submitted = value; } }

            /// <summary>`compile` for the compile command; `exec` otherwise.</summary>
            public string Op = UrcProtocol.Op.Exec;

            /// <summary>
            /// Which source each line range of a combined snippet came from.
            ///
            /// Compiler diagnostics number lines in the COMBINED text, which is meaningless to a
            /// caller who passed three files — so the mapping is printed alongside a compile failure.
            /// Empty when only one source was given, where the numbers already line up.
            /// </summary>
            public List<string> SourceMap;

            public Session(DiscoveryReply editor, string jobId, string code, List<string> usings,
                TimeSpan timeout, Args args)
            {
                _editor = editor;
                _jobId = jobId;
                _code = code;
                _usings = usings;
                _timeout = timeout;
                _args = args;
                _generation = editor.Generation;
            }

            public int Run()
            {
                var current = _editor;

                while (true)
                {
                    if (_clock.Elapsed > _timeout) return Timeout();

                    var connection = EditorConnection.Connect(current, TimeSpan.FromSeconds(5), out var error);
                    if (connection == null)
                    {
                        // Could not connect. If the editor is mid-reload its listener is simply gone
                        // for a moment, so this is only fatal once the process itself is gone.
                        if (!ProcessLiveness.IsAlive(current.Pid)) return EditorGone();
                        var next = Rediscover(current);
                        if (next == null) return EditorGone();
                        current = next;
                        continue;
                    }

                    using (connection)
                    {
                        _generation = connection.Greeting["generation"].AsInt(_generation);
                        _sawReloading = false;   // per-connection, not per-session

                        var request = _submitted ? AttachFrame() : ExecFrame();
                        if (!connection.Send(request))
                        {
                            if (!ProcessLiveness.IsAlive(current.Pid)) return EditorGone();
                            continue;
                        }
                        _submitted = true;

                        var outcome = Pump(connection);
                        if (outcome.HasValue) return outcome.Value;

                        // Reload, or the socket dropped. Wait for the editor to come back at a higher
                        // generation, then re-attach. Invisible to the caller.
                        var next = Rediscover(current);
                        if (next == null) return EditorGone();
                        current = next;
                    }
                }
            }

            /// <summary>Reads frames until the job resolves. Null means "reconnect and re-attach".</summary>
            private int? Pump(EditorConnection connection)
            {
                while (true)
                {
                    if (_clock.Elapsed > _timeout) return Timeout();

                    // Bounded wait so the deadline is enforced even during a long silent job —
                    // without it the read blocks forever and --timeout never fires.
                    var read = connection.TryReadFrame(TimeSpan.FromMilliseconds(200), out var frame);

                    if (read == EditorConnection.Read.Closed)
                    {
                        // Closed after a reload was announced, or before we have anything: reconnect.
                        if (_sawReloading || _result == null) return null;

                        // Closed with a result already in hand and no reload announced: the peer just
                        // hung up. Report what we have rather than chasing a settle frame forever.
                        return Report();
                    }

                    if (read == EditorConnection.Read.Timeout) continue;

                    switch (frame["ev"].AsString())
                    {
                        case UrcProtocol.Ev.Accepted:
                            continue;

                        case UrcProtocol.Ev.Log:
                            // Streamed, so a long job is not silent. Stderr keeps --json parseable.
                            Console.Error.WriteLine("  " + frame["message"].AsString(""));
                            continue;

                        case UrcProtocol.Ev.State:
                            // The frame carrying `settled` is the end of the settle window: the
                            // editor is quiet (or gave up being quiet) and the answer is complete.
                            if (frame.Has("settled")) { _settled = frame; return Report(); }

                            if (_args.Has("verbose"))
                                Console.Error.WriteLine("  [" + frame["phase"].AsString("") + "]");
                            continue;

                        case UrcProtocol.Ev.Reloading:
                            _sawReloading = true;
                            if (_args.Has("verbose"))
                                Console.Error.WriteLine("  [domain reloading]");
                            return null;

                        case UrcProtocol.Ev.Busy:
                            Program.Error(frame["message"].AsString("the editor is busy with another job."));
                            return ExitCode.Failed;

                        case UrcProtocol.Ev.Error:
                            Program.Error(frame["message"].AsString("the editor rejected the request."));
                            return ExitCode.Failed;

                        case UrcProtocol.Ev.Result:
                            _result = frame;
                            // Without settling, a command returns success and the caller's very next
                            // call hits an editor that is importing or compiling because of what this
                            // one did — and never learns the pulled code does not build.
                            if (_args.Has("no-settle")) return Report();
                            continue;

                        default:
                            continue;   // unknown frames are ignored, so additive changes stay compatible
                    }
                }
            }

            private Json ExecFrame()
            {
                var frame = Json.Object()
                    .Set("op", Op)
                    .Set("jobId", _jobId)
                    .SetIf("code", _code == null ? null : Json.String(_code))
                    .Set("client", Json.Object()
                        .Set("name", "urc")
                        .Set("pid", Process.GetCurrentProcess().Id)
                        .Set("protocol", UrcProtocol.Version));

                if (_args.Has("no-settle")) frame.Set("noSettle", true);

                var snippetArgs = SnippetArgs(_args);
                if (snippetArgs.Count > 0) frame.Set("args", snippetArgs);

                if (_usings != null && _usings.Count > 0)
                {
                    var array = Json.Array();
                    foreach (var u in _usings) array.Add(Json.String(u.Trim()));
                    frame.Set("usings", array);
                }

                return frame;
            }

            // jobId is omitted rather than sent empty when unknown: the server reads that as
            // "whatever is pending, else the most recent", which is what `urc resume` with no
            // argument means.
            private Json AttachFrame() =>
                Json.Object()
                    .Set("op", UrcProtocol.Op.Attach)
                    .SetIf("jobId", string.IsNullOrEmpty(_jobId) ? null : Json.String(_jobId))
                    .SetIf("noSettle", _args.Has("no-settle") ? Json.Bool(true) : null)
                    .Set("client", Json.Object()
                        .Set("name", "urc")
                        .Set("pid", Process.GetCurrentProcess().Id)
                        .Set("protocol", UrcProtocol.Version));

            /// <summary>
            /// Waits for the editor to reappear at a HIGHER generation — proof that a specific reload
            /// completed, rather than a guess from timing. Returns null once the process is gone.
            /// </summary>
            private DiscoveryReply Rediscover(DiscoveryReply previous)
            {
                while (_clock.Elapsed <= _timeout)
                {
                    if (!ProcessLiveness.IsAlive(previous.Pid)) return null;

                    var replies = DiscoveryClient.Query(
                        satisfied: r => r.Pid == previous.Pid &&
                                        r.Generation > _generation &&
                                        r.State != UrcProtocol.State.Reloading);

                    foreach (var reply in replies)
                    {
                        if (reply.Pid != previous.Pid) continue;
                        if (reply.Generation <= _generation) continue;
                        if (reply.State == UrcProtocol.State.Reloading) continue;
                        return reply;
                    }

                    Thread.Sleep(50);
                }

                return previous;   // timed out waiting; the caller's clock check reports it
            }

            /// <summary>
            /// Prints the command's outcome and the editor's health as SEPARATE facts.
            ///
            /// "my command failed" and "the project no longer compiles" are different things, and an
            /// agent that cannot tell them apart debugs the wrong one.
            /// </summary>
            private int Report()
            {
                if (_result == null && _settled == null) return ExitCode.Failed;

                var compile = _settled?["compile"] ?? Json.Null;
                var compileFailed = compile["status"].AsString() == "failed";

                if (_args.Json)
                {
                    var doc = _result ?? Json.Object().Set("status", UrcProtocol.Status.Ok);
                    if (_settled != null)
                    {
                        doc.Set("editor", Json.Object()
                            .Set("settled", _settled["settled"].AsBool())
                            .Set("generation", _settled["generation"].AsInt())
                            .SetIf("compile", compile));
                    }
                    Console.WriteLine(doc.ToString());
                    return ExitCodeFor(_result, compileFailed);
                }

                var status = _result?["status"].AsString(UrcProtocol.Status.Failed) ?? UrcProtocol.Status.Ok;
                var summary = _result?["summary"].AsString();
                var value = _result?["value"] ?? Json.Null;

                switch (status)
                {
                    case UrcProtocol.Status.Ok:
                        if (!value.IsNull)
                            Console.WriteLine(value.ValueKind == Json.Kind.String ? value.AsString() : value.ToString());
                        else if (!string.IsNullOrEmpty(summary) && Op != UrcProtocol.Op.Compile)
                            Console.WriteLine(summary);
                        break;

                    case UrcProtocol.Status.Interrupted:
                        Program.Error(summary ?? "the job was interrupted by a domain reload.");
                        return ExitCode.Unavailable;

                    default:
                        Program.Error(summary ?? "the snippet failed.");
                        PrintSourceMap();
                        PrintCompile(compile);
                        return ExitCode.Failed;
                }

                PrintCompile(compile);

                if (_settled != null && !_settled["settled"].AsBool())
                    Console.Error.WriteLine("! editor is still busy (" + _settled["phase"].AsString("?") + ")");

                return ExitCodeFor(_result, compileFailed);
            }

            /// <summary>
            /// The exit code reflects THIS COMMAND's outcome. An exec that succeeded exits 0 even if
            /// the project is now broken — failing it would break every read-only probe whenever a
            /// colleague's edit broke the build. The banner keeps it visible instead.
            ///
            /// `compile` is the exception: there, compile errors ARE the outcome.
            /// </summary>
            private int ExitCodeFor(Json result, bool compileFailed)
            {
                if (Op == UrcProtocol.Op.Compile) return compileFailed ? ExitCode.Failed : ExitCode.Ok;

                var status = result?["status"].AsString(UrcProtocol.Status.Ok) ?? UrcProtocol.Status.Ok;
                return status == UrcProtocol.Status.Ok ? ExitCode.Ok : ExitCode.Failed;
            }

            /// <summary>Turns "error on line 34" into something locatable when sources were combined.</summary>
            private void PrintSourceMap()
            {
                if (SourceMap == null || SourceMap.Count < 2) return;

                Console.Error.WriteLine("  combined from:");
                foreach (var entry in SourceMap) Console.Error.WriteLine("    " + entry);
            }

            private void PrintCompile(Json compile)
            {
                if (compile == null || compile.IsNull) return;
                if (compile["status"].AsString() != "failed") return;

                var count = compile["errorCount"].AsInt();
                Console.Error.WriteLine($"! project has {count} compile error{(count == 1 ? "" : "s")} " +
                                        "- old code is still live");

                var shown = 0;
                foreach (var error in compile["errors"].Items)
                {
                    shown++;
                    var file = error["file"].AsString("?");
                    var line = error["line"].AsInt();
                    var code = error["code"].AsString("");
                    var repeats = error["count"].AsInt(1);
                    var suffix = repeats > 1 ? $"  (x{repeats})" : "";
                    Console.Error.WriteLine($"    {file}:{line}  {code}  {error["message"].AsString("")}{suffix}");
                }

                if (count > shown) Console.Error.WriteLine($"    +{count - shown} more");
            }

            private int Timeout()
            {
                Program.Error(
                    $"timed out after {_timeout.TotalSeconds:0}s — job {_jobId} may still be running.\n" +
                    $"  urc resume {_jobId}");
                return ExitCode.Unavailable;
            }

            private int EditorGone()
            {
                Program.Error(
                    $"the editor (pid {_editor.Pid}) exited" +
                    (_submitted ? $" before job {_jobId} finished." : "."));
                return ExitCode.Unavailable;
            }
        }

        /// <summary>
        /// Collects `--arg name=value` (repeatable) and `--args '<json object>'` into the frame.
        ///
        /// Parameters go BESIDE the source rather than inside it. Interpolating them into the
        /// snippet means the shell has to survive two levels of escaping — different on every
        /// platform, and painful for anything containing a slash or a quote — and, more importantly,
        /// every distinct value produces distinct source, so a compiled-snippet cache could never
        /// hit and each call would leak another assembly. Passed this way, a reusable snippet is
        /// byte-identical on every invocation.
        /// </summary>
        private static Json SnippetArgs(Args args)
        {
            var result = Json.Object();

            // --args '{"width":1920}' first, so an explicit --arg can override a blob entry.
            var blob = args.Get("args");
            if (!string.IsNullOrWhiteSpace(blob))
            {
                if (!Json.TryParse(blob, out var parsed) || parsed.ValueKind != Json.Kind.Object)
                {
                    // Loudly, because the shell is the likely culprit and the symptom is invisible:
                    // PowerShell strips the inner quotes from '{"a":1}', leaving something that is
                    // not JSON, and silently falling back to defaults makes a snippet quietly run
                    // with the wrong inputs. --arg avoids the problem entirely.
                    throw new ArgumentException(
                        "--args is not a JSON object: " + blob + "\n" +
                        "  Your shell probably stripped the quotes. Prefer --arg name=value, which " +
                        "needs no quoting.");
                }

                foreach (var field in parsed.Fields)
                    result.Set(field.Key, Json.String(field.Value.AsString("")));
            }

            foreach (var pair in args.GetAll("arg"))
            {
                if (string.IsNullOrEmpty(pair)) continue;

                // Split on the FIRST '=' only: a value may legitimately contain more of them.
                var eq = pair.IndexOf('=');
                if (eq <= 0)
                {
                    // A bare `--arg name` is almost certainly a typo for a flag-style parameter;
                    // treat it as present-and-empty, which ArgBool reads as true.
                    result.Set(pair, Json.String(""));
                    continue;
                }

                result.Set(pair.Substring(0, eq), Json.String(pair.Substring(eq + 1)));
            }

            return result;
        }

        /// <summary>
        /// --code is the reviewable form: it shows a human approving the call exactly what will run.
        /// --file and stdin exist because multi-line C# through a shell hits real escaping pain.
        ///
        /// SOURCES COMBINE, in command-line order, with --code last. That is what lets a skill ship
        /// snippets as a small library and an agent compose them in ONE call:
        ///
        ///   urc exec --file capture.cs --file report.cs --code "Capture(1920); return Report();"
        ///
        /// Without it a skill's snippets could not be batched at all — an agent had to spend a round
        /// trip per snippet, or paste their contents together and lose the files entirely.
        /// Top-level method declarations are legal in a submission and --arg globals reach inside
        /// them, so a snippet file is naturally a set of callable methods (both verified).
        /// </summary>
        private sealed class Snippet
        {
            public string Code;
            /// <summary>Which source each line range came from; only needed to explain a compile error.</summary>
            public List<string> Map = new List<string>();
        }

        private static Snippet ReadCode(Args args, out string error)
        {
            error = null;

            var parts = new List<KeyValuePair<string, string>>();

            foreach (var file in args.GetAll("file"))
            {
                if (string.IsNullOrEmpty(file)) continue;
                try { parts.Add(new KeyValuePair<string, string>(file, File.ReadAllText(file))); }
                catch (Exception ex) { error = $"could not read '{file}': {ex.Message}"; return null; }
            }

            foreach (var positional in args.Positional)
            {
                if (positional != "-") continue;

                // Without a pipe, ReadToEnd would block on the terminal and look like a hang.
                var piped = Console.IsInputRedirected ? Console.In.ReadToEnd() : null;
                if (string.IsNullOrWhiteSpace(piped))
                {
                    // Empty stdin would otherwise reach the editor as an empty snippet and come back
                    // as a server-side "exec requires 'code'", pointing at the wrong end.
                    error = "`exec -` reads the snippet from stdin, but nothing arrived.\n" +
                            "  Try:  echo 'return 1;' | urc exec -";
                    return null;
                }

                parts.Add(new KeyValuePair<string, string>("<stdin>", piped));
                break;
            }

            // Last, so it can call whatever the files declared.
            var inline = args.Get("code");
            if (!string.IsNullOrEmpty(inline))
                parts.Add(new KeyValuePair<string, string>("--code", inline));

            if (parts.Count == 0)
            {
                error = "exec needs code: --code '<C#>', --file <path> (repeatable), or - to read stdin.";
                return null;
            }

            var snippet = new Snippet();
            var builder = new System.Text.StringBuilder();
            var line = 1;

            foreach (var part in parts)
            {
                // Normalise line endings so the line map matches what the compiler will count.
                var text = part.Value.Replace("\r\n", "\n").TrimEnd('\n');
                var lines = text.Length == 0 ? 0 : text.Split('\n').Length;

                if (parts.Count > 1)
                    snippet.Map.Add($"{part.Key}: lines {line}-{line + Math.Max(0, lines - 1)}");

                builder.Append(text).Append('\n');
                line += lines;
            }

            snippet.Code = builder.ToString();
            return snippet;
        }

        private static TimeSpan ParseTimeout(string value, TimeSpan fallback)
        {
            if (string.IsNullOrEmpty(value)) return fallback;
            if (double.TryParse(value.TrimEnd('s'), out var seconds) && seconds > 0)
                return TimeSpan.FromSeconds(seconds);
            return fallback;
        }
    }
}
