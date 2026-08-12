---
name: urc
description: Drive a running Unity editor from the command line - execute C#, compile after editing scripts, inspect scene and project state, read the editor console. Use whenever working in a Unity project with the editor open: inspecting or modifying scenes, prefabs, assets or settings; verifying that a script change compiles; checking what the editor logged; or answering any question about live editor state. Also use when the user mentions Unity, the Unity editor, a scene, a prefab, an asmdef, or a compile error.
---

# Driving the Unity editor with `urc`

`urc` runs C# inside a **running** Unity editor and blocks until it answers. Domain reloads,
recompiles and editor restarts are handled underneath — one command, one answer.

The binary sits at `.urc/urc.exe` (Windows) or `.urc/urc` in the Unity project root.

## Batch aggressively

**Do setup, action and verification in ONE `exec` and return a single concise value.** Each call
re-sends the whole conversation, so fewer calls cost far less.

```bash
# Good: one call, one answer.
urc exec --code 'var go = GameObject.Find("Player"); if (go == null) return "missing"; go.transform.position = Vector3.zero; return go.transform.position.ToString();'

# Bad: three calls for one question.
urc exec --code 'return GameObject.Find("Player") != null;'
urc exec --code 'GameObject.Find("Player").transform.position = Vector3.zero;'
urc exec --code 'return GameObject.Find("Player").transform.position.ToString();'
```

**Prefer documented helpers over exploring.** Don't reflection-scan or read source to discover an
API when the project's own guidance names one.

## Output budget

Results persist in your context for the entire session, so keep them small.

- **Return a narrow value** — a scalar, a short string, a few fields. Not an object graph.
- Return values are capped (depth, collection size, string length). Unity objects collapse to
  `name <Type>`. A capped collection says so.
- **Console output and stack traces are NOT returned.** A result carries only counts and a cursor:
  `"logs":{"errors":1,"warnings":0,"total":3,"since":"c7cbd3bc:7:1"}`. Fetch the text only when the
  counts say it is worth it: `urc logs --since c7cbd3bc:7:1`.
- Capture runs **only while a command is running**, so `urc logs` shows what your commands caused,
  not everything the editor has ever printed. For output from outside a command, read Unity's own
  `Editor.log`.
- Need everything? Serialize it yourself and return the string — a large string is written in full
  to an artifact and the result carries the path.

## Commands

```bash
urc exec --code '<C#>'          # run C#, block until it answers
urc exec --file snippet.cs      # multi-line C# (avoids shell quoting pain)
urc exec --file s.cs --arg w=8  # parameters, repeatable — see below
cat snippet.cs | urc exec -     # snippet from stdin
urc compile                     # tell Unity files changed; exit 0 only if it builds
urc status                      # editor state, generation, pending job
urc status --all                # every running editor on this machine
urc logs --since <cursor>       # console output after a command started
urc logs --errors --tail 20     # recent errors only
urc resume                      # pick up a job whose CLI died or timed out
```

Global: `--project <path>` (or `$URC_PROJECT`), `--json`, `--timeout <secs>`, `--verbose`.

Exit codes: `0` ok · `1` failed · `2` timed out or editor gone · `3` usage/environment.

## After editing a .cs file, run `urc compile`

Unity does not notice file changes on its own while unfocused — which is its normal state during
agent work. `urc compile` triggers the import and reports whether the project still builds.

```bash
urc compile          # exit 0 = builds; exit 1 = errors, listed and deduplicated
```

Errors are deduplicated: one missing type produces hundreds of call-site errors, so you see the
*distinct* problems first. **A failed compile reloads nothing — the old code stays live.**

## Parameterise with `--arg`, never by building values into the source

A reusable snippet takes its inputs beside the source, not inside it:

```bash
urc exec --file screenshot.cs --arg width=1920 --arg path=C:/shots/a.png
```

```csharp
// screenshot.cs — byte-identical on every call
var width = ArgInt("width", 1280);
var path  = Arg("path", "Temp/shot.png");
var debug = ArgBool("debug");
```

Available inside every snippet, no `using` required:
`Arg(name, fallback)` · `ArgInt` · `ArgLong` · `ArgFloat` · `ArgBool` · `RequireArg(name)` (throws if
missing) · `HasArg(name)` · `Args` (the whole dictionary).

**Do not interpolate values into the snippet text.** It looks simpler and costs more:

- Quoting differs per shell, and anything with a slash or a quote has to survive two levels of it.
- Every distinct value produces distinct source, so the snippet is recompiled on every call and each
  compile leaks an assembly that cannot be freed until the next domain reload. Identical source is
  what makes reuse possible at all.

`--arg` is repeatable, splits on the first `=` only (so values may contain more), and needs no
quoting for slashes or spaces. `--args '<json object>'` takes the same parameters in one blob, but
your shell may strip its quotes — prefer `--arg`.

## Reusable operations belong in the project, not in a snippet

Don't paste a large snippet repeatedly. Put the logic in an ordinary static class in the project
and call it in one line:

```csharp
// Assets/Editor/ProjectTools.cs  - normal project code, compiles for everyone
public static class ProjectTools
{
    public static object FindBrokenPrefabs() { /* ... */ }
}
```

```bash
urc exec --code 'return ProjectTools.FindBrokenPrefabs();'
```

`exec` references every assembly loaded in the editor, so project code is callable with no setup,
and the compiler checks your arguments.

## Writing snippets

- Bare statements are wrapped for you. `return` a value to get it back.
- Common usings are already imported (System, Linq, IO, Threading, Tasks, UnityEngine, UnityEditor,
  SceneManagement). A missing namespace is resolved automatically **when it is unambiguous** —
  ambiguous names fail and list the candidates.
- **Prefer `--code`** for anything short: it shows a human approving the call exactly what will run.
  Use `--file` when embedded quotes or newlines fight the shell.
- Snippets run on the main thread and **cannot be aborted** — never write an unbounded loop. Give
  any wait a self-deadline:

```csharp
var deadline = EditorApplication.timeSinceStartup + 30;
while (!(CONDITION) && EditorApplication.timeSinceStartup < deadline) await Task.Delay(100);
return (CONDITION) ? "met" : "timeout";
```

## What you do NOT need to do

- **Don't poll.** `exec` blocks until the job genuinely finishes. If a domain reload happens
  underneath, the CLI reconnects and re-attaches by itself.
- **Don't wait for the editor after a reload.** A command returns only once the editor is quiet
  again (`--no-settle` opts out for a read-only probe).
- **Don't check whether the editor is ready first.** Just run the command; if no editor is running
  you get a clear error naming the editors that are.
- **Don't call `AssetDatabase.Refresh()` via `exec`** to apply script edits — use `urc compile`,
  which reports the compiler errors.

## When something looks wrong

- `urc status` answers even while the editor is compiling or importing. A large
  `secondsSinceLastTick` means the main thread is stuck (a modal dialog, a long import) and `exec`
  would stall.
- `urc logs` reads the log file directly with no editor involved, so it still answers when the
  editor is wedged or has crashed — provided the output happened during a command. A crash outside
  one leaves nothing here; Unity's `Editor.log` has it.
- `no editor running for <path>` lists the editors that *are* running; pass `--project` to pick one.
