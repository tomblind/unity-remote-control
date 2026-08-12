# unity-remote-control

Drive a running Unity editor from the command line. Execute C#, compile after editing scripts,
inspect live editor state — for AI agents and for you.

```bash
urc exec --code 'return GameObject.FindObjectsOfType<Camera>().Length;'
urc compile
urc status
```

One command, one answer. Domain reloads, recompiles and editor restarts are handled underneath.

## Install

```powershell
.\install.ps1 <path to your Unity project>
```

That copies the editor package into `Assets/UnityRemoteControl/`, drops the CLI at `.urc/urc.exe`,
installs a Claude skill, and hides all of it from git via `.git/info/exclude` — **nothing appears in
`git status`, and there is nothing to commit.** The installer verifies that itself and tells you if
it failed.

Then open the project in Unity and, from the project root:

```powershell
.\.urc\urc.exe status
```

To remove it: `.\install.ps1 <project> -Uninstall`.

**Roslyn** is bundled but skipped when your project already provides it — two copies of
`Microsoft.CodeAnalysis` cause duplicate-assembly errors. The editor code names no Roslyn assembly,
so it binds to whichever copy is present.

Requires Unity **2021.3+**. macOS installer and prebuilt binaries are not done yet; build the CLI
with `dotnet publish cli/Urc.csproj -r win-x64 -c Release`.

## Commands

| | |
|---|---|
| `urc exec --code '<C#>'` | Run C# on the editor main thread; block until it answers |
| `urc exec --file s.cs --arg w=1920` | Parameters beside the source, so the snippet stays identical |
| `urc compile` | Tell Unity files changed; exit 0 only if the project builds |
| `urc status [--all]` | Editor state, generation, pending job |
| `urc logs [--since \| --errors \| --tail]` | Captured console, read straight off disk |
| `urc resume [<jobId>]` | Pick up a job whose CLI died or timed out |

Global: `--project`, `--json`, `--timeout`, `--verbose`, `--no-settle`.
Exit codes: `0` ok · `1` failed · `2` timed out or editor gone · `3` usage.

## How it works, and why

Five problems drove the design. Each solution is load-bearing:

**No polling.** The CLI holds a TCP connection and blocks; results are pushed. When a domain reload
kills that connection, the CLI reconnects and re-attaches to the same job on your behalf. The
previous MCP-based tool could not do this — MCP clients cap tool calls at ~60s, forcing a handoff to
a result file that the agent then polled.

**Domain reloads.** Job state lives in Unity's `SessionState`, whose lifetime — survives a reload,
dies with the process — exactly matches the window in which "is my job still alive?" is a meaningful
question. The journal is written *before* delivery, so if the flush loses a race with the reload the
client simply reads the result back on re-attach. There are no state files.

**Busy editors.** Discovery and the accept thread answer entirely from volatiles stamped by the
main-thread pump, touching no Unity API. That is why `status` still answers mid-compile, where a
tick-driven poll loop goes deaf exactly when you need it.

**Restart and crash.** Liveness is a check on one known pid, so a crash fails in seconds instead of
at timeout, and is distinguishable from a reload (same pid, higher generation).

**Multiple editors.** Host-confined UDP multicast discovery (`TTL 0`) plus an ephemeral TCP port.
Nothing is allocated, registered or configured, so port collisions and stale-port bugs are
structurally impossible. Several editors coexist with no setup.

## Output is deliberately terse

Results persist in an agent's context for a whole session, so:

- Return values are bounded (depth, collection size, string length); Unity objects collapse to
  `name <Type>`. A large returned string spills in full to an artifact with a preview inline.
- Console output and stack traces are **not** returned. A result carries counts and a cursor;
  `urc logs --since <cursor>` fetches the text when the counts say it is worth it.
- Console capture runs **only while a command is running**. A real project logs constantly, and
  Unity already writes all of it to `Editor.log` — what this adds is structure (cursors, levels,
  trimmed stacks, domain boundaries), which is only useful around a command. A silent command
  writes nothing at all.
- Compile errors are captured structurally and **deduplicated** — one missing type produces hundreds
  of call-site errors, so you see the distinct problems first.
- Snippet stack traces are trimmed at the first runner frame (a single `Debug.LogError` otherwise
  yields ~25 frames, two of which are yours).

A command's exit code reflects **that command's** outcome. An `exec` that succeeded exits 0 even if
the project no longer compiles — the compile state is reported separately, because "my command
failed" and "the project is broken" are different facts. Only `compile` treats compile errors as its
own outcome.

## Known limits

- A snippet runs on the main thread and **cannot be aborted**. An infinite loop wedges the editor;
  `--timeout` only stops the CLI waiting. Always give a wait a self-deadline.
- Each `exec` loads an assembly that stays resident until the next domain reload — Mono cannot unload
  one, and Unity has a single AppDomain. The count is shown in `status` and in the editor window.
- `.git/info/exclude` is per-clone, so a fresh clone needs a re-install, and `git clean -xdf` deletes
  the install.
- Cancellation is deliberately not implemented yet.

## Development

```powershell
dotnet build tools\PackageCompileCheck\PackageCompileCheck.csproj   # compile the package, no Unity
dotnet build cli\Urc.csproj
powershell -File tools\test-e2e.ps1                                 # 7 e2e tests, no Unity
```

`tools/FakeEditor` speaks the real protocol, so the reconnect/re-attach state machine — reload,
crash, restart, timeout — is tested without provoking a real editor. `PackageCompileCheck` builds the
editor sources against the **2021.3 floor** at C# 9, which is what actually constrains them.
