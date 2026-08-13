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

Batching means doing the steps of **one task** in one call. It does not mean building a
general-purpose snippet that does several different jobs selected by flags — see below.

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

## Snippets are methods; compose them in one call

`--file` is repeatable and the sources combine in order, with `--code` last. So write each snippet
as a **method**, and let `--code` call whichever ones the task needs:

```csharp
// capture.cs
string Capture(int width) { /* ... */ }
```
```csharp
// report.cs
string Report(string what) { /* ... */ }
```
```bash
urc exec --file capture.cs --file report.cs --code "return Report(Capture(1920));"
```

That is what makes a skill's snippets batchable. Without it, two snippets meant either two round
trips or pasting their contents together and abandoning the files.

Top-level methods are legal in a snippet, and `--arg` values are readable inside them, so a snippet
file is naturally a small library. If a compile fails, the error's line numbers refer to the
combined text and `urc` prints which file each range came from.

## A snippet declares what it needs

When one snippet calls another, say so in the file rather than remembering it at every call site:

```csharp
// report.cs
//urc:require ./format.cs
string Report(string what) { return Format(what); }
```

```bash
urc exec --file report.cs --code "return Report(Capture(1920));"   # format.cs comes too
```

Requirements resolve relative to the file holding them, apply transitively, and are included **once**
however many files ask for them — so naming a file explicitly that something else also requires is
safe, not a duplicate-definition error.

Use it whenever a snippet calls a method it does not itself declare. It is a plain line comment, so
the C# compiler never sees it, and it turns "the caller must remember to pass three files" into a
fact the file states once. Without it, forgetting one produces a compile error on a line you did not
write.

## One snippet, one job

Prefer several short, focused snippets over one long snippet with flags choosing between paths. A
snippet that branches on `--arg mode=...` is the worst shape available: only one branch ever runs,
the whole thing must still be read and approved by whoever is watching, and it is far easier to get
subtly wrong than three small snippets each doing one thing — which you can now combine per task
anyway.

Keep them short. Compile time scales with snippet length — a handful of lines costs about 76ms,
sixty lines about 350ms — so length is a real cost paid on every call, not just clutter.

This does not conflict with batching. Batching is about not splitting **one** task across round
trips; shortness is about not cramming **several** tasks into one snippet. A batch built from
one-line calls into project code is both short and complete.

## Passing values in

**Numbers, booleans and enums: write them as C# in `--code`.** They are type-checked by the
compiler, need no conversion, and read plainly in the approval prompt:

```bash
urc exec --file capture.cs --code "return Capture(1920, true);"
```

**Strings — especially paths: use `--arg`.** A string literal inside `--code` has to be valid C#
*and* survive your shell, and the two disagree. This fails on PowerShell, because the quotes are
stripped before `urc` ever sees them:

```bash
urc exec --file report.cs --code "return Report(\"C:/a b/c.png\");"   # CS1525
```

```bash
urc exec --file report.cs --file main.cs --arg "path=C:/a b/c.png"    # works
```

`--arg` values travel through argv untouched, so slashes, spaces and quotes are safe. Read them with
`Arg(name, fallback)` · `ArgInt` · `ArgLong` · `ArgFloat` · `ArgBool` · `RequireArg(name)` (throws if
missing) · `HasArg(name)` · `Args`. They work inside declared methods as well as at top level, and
need no `using`.

`--arg` is repeatable and splits on the first `=` only, so values may contain more. `--args '<json>'`
takes several at once but your shell may strip its quotes — prefer `--arg`.

Whichever you use, **never build the snippet body by string substitution.** That is the escaping
problem with extra steps.

Use parameters for genuine inputs — a width, a path, a name. If a parameter is selecting *behaviour*,
write a second snippet instead.

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

This is also what keeps snippets short. Logic that lives in the project is compiled once by Unity,
so a snippet that calls it is a few lines rather than sixty — and stays cheap and readable however
many times you compose it differently.

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
