using System;
using System.Collections.Generic;
using Urc.Discovery;
using Urc.Protocol;

namespace Urc
{
    public static class ExitCode
    {
        public const int Ok = 0;
        /// <summary>The command ran and reported failure.</summary>
        public const int Failed = 1;
        /// <summary>Timed out, or the editor died mid-wait.</summary>
        public const int Unavailable = 2;
        /// <summary>Bad usage, or the environment is not set up.</summary>
        public const int Usage = 3;
    }

    public static class Program
    {
        public static int Main(string[] rawArgs)
        {
            var args = new Args(rawArgs);

            // Before the no-command check below, which would otherwise swallow a bare `--version`.
            if (args.Has("version"))
            {
                Console.WriteLine($"urc (protocol v{UrcProtocol.Version})");
                return ExitCode.Ok;
            }

            if (args.Has("help") || args.Has("h") || args.Command == null || args.Command == "help")
                return PrintUsage(args.Command == null && !args.Has("help") && !args.Has("h"));

            try
            {
                switch (args.Command)
                {
                    case "status": return StatusCommand.Run(args);
                    case "exec": return ExecCommand.Run(args);
                    case "compile": return ExecCommand.Compile(args);
                    case "resume": return ExecCommand.Resume(args);
                    case "logs": return LogsCommand.Run(args);
                    default:
                        Error($"unknown command '{args.Command}'. Run `urc help` for usage.");
                        return ExitCode.Usage;
                }
            }
            catch (Exception ex)
            {
                Error(ex.Message);
                return ExitCode.Failed;
            }
        }

        public static void Error(string message) => Console.Error.WriteLine("urc: " + message);

        private static int PrintUsage(bool noCommand)
        {
            var text = @"urc — drive a running Unity editor

USAGE
  urc <command> [options]

COMMANDS
  exec                Run C# in the editor and block until it answers
  compile             Tell Unity that files changed; report whether it still builds.
                      Exit 0 only if the project compiles.
  resume [<jobId>]    Pick up a job whose CLI was killed or timed out. With no id,
                      the job currently in flight (there is only ever one).
  logs                Captured console output, read straight off disk. Works while
                      the editor is wedged, mid-reload, or dead.
                        --since <cursor>  everything after a result's logs.since
                        --errors          errors only  (--level log|warning|error)
                        --tail <n>        last n lines (default 50 without --since)
  status              State of the editor for this project
  status --all        Every running editor, whatever project it serves

EXEC
  urc exec --code 'Debug.Log(""hi""); return 2 + 2;'
  urc exec --file snippet.cs
  cat snippet.cs | urc exec -

  --code <c#>         The snippet. Preferred for anything short: it shows a human
                      approving the call exactly what will run.
  --file <path>       Read the snippet from a file.
  --using <ns,ns>     Extra namespaces. Unambiguous ones are resolved automatically.
  --timeout <secs>    Bound the CLI's wait (default 120). Never aborts the job —
                      main-thread work cannot be cancelled; use `urc resume` after.

  BATCH AGGRESSIVELY: do setup, action and verification in ONE exec and return a
  single concise value. Each call re-sends the whole conversation, so fewer calls
  cost far less. Reusable logic belongs in a static class in the project, invoked
  as `urc exec --code 'return ProjectTools.Thing();'`.

  Results persist in an agent's context for the entire session, so return a narrow
  value — a scalar or a few fields, not an object graph. Console output and stack
  traces are NOT returned; they go to the editor log.

OPTIONS
  --project <path>    Unity project root. Defaults to $URC_PROJECT, then a walk up from
                      the working directory (like git finding .git).
  --json              Emit the raw result document on stdout and nothing else.
  --verbose           Report reloads and state changes while waiting.
  --no-settle         Return as soon as the command finishes, without waiting for the
                      editor to go quiet. Faster for a read-only probe; the trade is
                      that your next call may hit an importing or compiling editor,
                      and a compile your command triggered goes unreported.
  --help              This text.

EXIT CODES
  0 ok · 1 failed · 2 timed out or editor gone · 3 usage or environment
";
            Console.WriteLine(text.TrimEnd());
            return noCommand ? ExitCode.Usage : ExitCode.Ok;
        }
    }

    /// <summary>
    /// Minimal argument parsing. Hand-rolled rather than pulled from a package because the CLI is
    /// published with NativeAOT, where every dependency is a potential reflection surprise, and the
    /// surface here is five commands.
    /// </summary>
    public sealed class Args
    {
        private readonly Dictionary<string, string> _options =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _positional = new List<string>();

        public Args(string[] args)
        {
            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                if (!arg.StartsWith("-", StringComparison.Ordinal)) { _positional.Add(arg); continue; }

                var name = arg.TrimStart('-');
                if (name.Length == 0) continue;

                // --key=value
                var eq = name.IndexOf('=');
                if (eq >= 0)
                {
                    _options[name.Substring(0, eq)] = name.Substring(eq + 1);
                    continue;
                }

                // --key value, unless the next token is itself a flag (then it is a bare switch)
                if (i + 1 < args.Length && !args[i + 1].StartsWith("-", StringComparison.Ordinal))
                {
                    _options[name] = args[++i];
                }
                else
                {
                    _options[name] = "";
                }
            }
        }

        public string Command => _positional.Count > 0 ? _positional[0] : null;
        public IReadOnlyList<string> Positional => _positional;

        public bool Has(string name) => _options.ContainsKey(name);
        public string Get(string name, string fallback = null) =>
            _options.TryGetValue(name, out var value) && value.Length > 0 ? value : fallback;

        public bool Json => Has("json");
    }

    internal static class StatusCommand
    {
        public static int Run(Args args)
        {
            return args.Has("all") ? RunAll(args) : RunOne(args);
        }

        private static int RunOne(Args args)
        {
            var project = ProjectResolver.Resolve(args.Get("project"), out _);

            // Stop listening the moment the wanted editor answers — see DiscoveryClient.Query.
            var replies = DiscoveryClient.Query(
                satisfied: reply => project != null && ProjectPaths.Equal(reply.ProjectPath, project));

            if (!ProjectResolver.TrySelect(replies, project, out var editor, out var error))
            {
                if (args.Json)
                {
                    Console.WriteLine(Json.Object().Set("error", error).Set("editors", Describe(replies)).ToString());
                }
                else
                {
                    Program.Error(error);
                }
                return project == null ? ExitCode.Usage : ExitCode.Failed;
            }

            if (args.Json)
            {
                Console.WriteLine(Describe(editor).ToString());
                return ExitCode.Ok;
            }

            Console.WriteLine(Summarize(editor));
            Console.WriteLine(editor.ProjectPath);
            foreach (var note in Notes(editor)) Console.WriteLine("! " + note);
            return ExitCode.Ok;
        }

        private static int RunAll(Args args)
        {
            // No early exit: --all cannot know how many editors to expect, so it waits out the window.
            var replies = DiscoveryClient.Query();

            if (args.Json)
            {
                Console.WriteLine(Json.Object().Set("editors", Describe(replies)).ToString());
                return ExitCode.Ok;
            }

            if (replies.Count == 0)
            {
                Console.WriteLine("no Unity editors responded.");
                return ExitCode.Ok;
            }

            foreach (var editor in replies)
            {
                Console.WriteLine(Summarize(editor));
                Console.WriteLine("  " + editor.ProjectPath);
                foreach (var note in Notes(editor)) Console.WriteLine("  ! " + note);
            }
            return ExitCode.Ok;
        }

        private static string Summarize(DiscoveryReply e) =>
            $"{ProjectResolver.DisplayName(e.ProjectPath),-24} {e.State,-10} gen {e.Generation,-4} " +
            $"unity {e.UnityVersion,-12} pid {e.Pid}";

        /// <summary>
        /// Only things that change what the caller should do next. A stalled main thread is the one
        /// number that predicts an `exec` hanging, so it is worth a line; nothing else here is.
        /// </summary>
        private static IEnumerable<string> Notes(DiscoveryReply e)
        {
            if (e.SecondsSinceLastTick > 5)
                yield return $"main thread has not ticked for {e.SecondsSinceLastTick:0.#}s — " +
                             "busy, or waiting on a modal dialog. exec would stall.";

            if (!string.IsNullOrEmpty(e.PendingJobId))
                yield return $"job {e.PendingJobId} is in flight — `urc resume {e.PendingJobId}` to follow it.";

            if (!e.IsCompatible)
                yield return $"protocol v{e.Protocol}, this CLI speaks v{UrcProtocol.Version} — re-run the installer.";
        }

        private static Json Describe(IReadOnlyList<DiscoveryReply> replies)
        {
            var array = Json.Array();
            foreach (var reply in replies) array.Add(Describe(reply));
            return array;
        }

        private static Json Describe(DiscoveryReply e) =>
            Json.Object()
                .Set("projectPath", e.ProjectPath)
                .Set("projectName", ProjectResolver.DisplayName(e.ProjectPath))
                .Set("unityVersion", e.UnityVersion)
                .SetIf("packageVersion", e.PackageVersion)
                .Set("state", e.State)
                .Set("generation", e.Generation)
                .Set("pid", e.Pid)
                .Set("tcpPort", e.TcpPort)
                .Set("protocol", e.Protocol)
                .Set("compatible", e.IsCompatible)
                .Set("secondsSinceLastTick", Math.Round(e.SecondsSinceLastTick, 2))
                .SetIf("sessionId", e.SessionId)
                .SetIf("pendingJobId", e.PendingJobId)
                .Set("loadedSnippets", e.LoadedSnippets);
    }
}
