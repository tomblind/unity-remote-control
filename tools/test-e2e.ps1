# End-to-end tests for the CLI against the scriptable fake editor.
#
# Every scenario here is one that broke a prior tool. They run without Unity because the fake
# speaks the real protocol - which is the point: provoking a domain reload at a precise moment
# inside a real editor is slow and unreliable, while here it is a flag.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\test-e2e.ps1
#
# The real editor still has to be tested separately; see docs/validation.md.
#
# Two PowerShell traps this file works around, both previously hit by the prior project:
#  - Under $ErrorActionPreference='Stop', a NATIVE command writing to stderr becomes a terminating
#    error. Every urc invocation here expects stderr, so Invoke-Urc drops to 'Continue' locally.
#  - PowerShell 5.1 reads a .ps1 as ANSI unless it has a BOM, so this file stays pure ASCII.

$ErrorActionPreference = 'Stop'

$root  = Split-Path -Parent $PSScriptRoot
$urc   = Join-Path $root 'cli\bin\Debug\net10.0\urc.exe'
$fake  = Join-Path $root 'tools\FakeEditor\bin\Debug\net10.0\fake-editor.exe'
$projA = 'C:\urc-test\ProjectA'
$projB = 'C:\urc-test\ProjectB'

foreach ($exe in @($urc, $fake)) {
    if (-not (Test-Path $exe)) {
        throw ("missing {0}. Build first: dotnet build cli\Urc.csproj; dotnet build tools\FakeEditor\FakeEditor.csproj" -f $exe)
    }
}

$script:pass = 0
$script:fail = 0

function Invoke-Urc {
    param([string[]]$Arguments)
    # Local scope only: native stderr must not become a terminating error here.
    $ErrorActionPreference = 'Continue'
    $output = (& $urc @Arguments 2>&1 | Out-String)
    return [pscustomobject]@{ Output = $output; ExitCode = $LASTEXITCODE }
}

function Invoke-UrcPiped {
    param([string]$Stdin, [string[]]$Arguments)
    $ErrorActionPreference = 'Continue'
    $output = ($Stdin | & $urc @Arguments 2>&1 | Out-String)
    return [pscustomobject]@{ Output = $output; ExitCode = $LASTEXITCODE }
}

function Start-Fake {
    param([string[]]$Arguments)
    $log = [IO.Path]::GetTempFileName()
    $p = Start-Process $fake -ArgumentList $Arguments -PassThru -NoNewWindow -RedirectStandardOutput $log
    Start-Sleep -Milliseconds 800   # let it bind and join the group
    return @{ Process = $p; Log = $log }
}

function Stop-Fake {
    param($Fake)
    if ($Fake.Process -and -not $Fake.Process.HasExited) {
        Stop-Process -Id $Fake.Process.Id -Force -ErrorAction SilentlyContinue
    }
    Start-Sleep -Milliseconds 250
}

function Check {
    param([string]$Name, [scriptblock]$Body)
    try {
        & $Body
        Write-Host "  PASS  $Name" -ForegroundColor Green
        $script:pass++
    } catch {
        Write-Host "  FAIL  $Name" -ForegroundColor Red
        Write-Host "        $($_.Exception.Message)" -ForegroundColor DarkGray
        $script:fail++
    }
}

function Assert {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

Write-Host "`nurc end-to-end (fake editor)`n" -ForegroundColor Cyan

# --- exec, happy path -----------------------------------------------------------------------
Check 'exec returns a value' {
    $f = Start-Fake @('--project', $projA, '--seconds', '10', '--exec-delay', '100')
    try {
        $r = Invoke-Urc @('exec', '--code', 'return 2+2;', '--project', $projA)
        Assert ($r.ExitCode -eq 0) "expected exit 0, got $($r.ExitCode): $($r.Output)"
        Assert ($r.Output -match 'fake-result') "unexpected output: $($r.Output)"
    } finally { Stop-Fake $f }
}

# --- the documented stdin form, which never actually worked ---------------------------------
# A bare "-" took the flag branch of the arg parser and was silently dropped, so
# `cat snippet.cs | urc exec -` could not run - while --help advertised it. Piping is the only way
# to parameterise a snippet without adding compiled code to the project, so this is load-bearing.
Check 'exec - reads the snippet from stdin' {
    $f = Start-Fake @('--project', $projA, '--seconds', '12', '--exec-delay', '50')
    try {
        $r = Invoke-UrcPiped 'return 1 + 1;' @('exec', '-', '--project', $projA)
        Assert ($r.ExitCode -eq 0) "expected exit 0, got $($r.ExitCode): $($r.Output)"
        Assert ($r.Output -match 'fake-result') "stdin snippet did not run: $($r.Output)"

        # Flags on either side of the dash must both parse.
        $r2 = Invoke-UrcPiped 'return 2;' @('exec', '--project', $projA, '-')
        Assert ($r2.ExitCode -eq 0) "dash after a flag failed: $($r2.Output)"
    } finally { Stop-Fake $f }
}

# --- parameters travel beside the source, not inside it -------------------------------------
# Repeatable flags are the trap here: the parser keeps only the last value per name, so without
# explicit handling `--arg a=1 --arg b=2` silently drops one. And a value may contain '=' or '/'.
Check 'exec --arg passes parameters without touching the source' {
    $f = Start-Fake @('--project', $projA, '--seconds', '12', '--exec-delay', '50')
    try {
        $r = Invoke-Urc @('exec', '--code', 'return 1;', '--project', $projA,
                          '--arg', 'width=1920', '--arg', 'path=C:/a b/img.png', '--arg', 'eq=a=b')
        Assert ($r.ExitCode -eq 0) "expected exit 0, got $($r.ExitCode): $($r.Output)"

        # All three must arrive: repeated flags kept, slashes and spaces intact, and a value
        # containing '=' split only on the FIRST one.
        Assert ($r.Output -match 'width=1920') "width missing: $($r.Output)"
        Assert ($r.Output -match [regex]::Escape('path=C:/a b/img.png')) "path mangled: $($r.Output)"
        Assert ($r.Output -match [regex]::Escape('eq=a=b')) "value containing '=' was split wrongly: $($r.Output)"

        # A malformed --args must fail loudly: falling back to defaults would run the snippet with
        # the wrong inputs and look like it worked.
        $bad = Invoke-Urc @('exec', '--code', 'return 1;', '--project', $projA, '--args', 'not-json')
        Assert ($bad.ExitCode -ne 0) "malformed --args was accepted silently"
    } finally { Stop-Fake $f }
}

# --- composing several snippet files into one call ------------------------------------------
# A skill ships snippets as files; without composition an agent must spend a round trip per
# snippet, or paste their contents together and lose the files. Order is the contract: files in
# command-line order, --code LAST so it can call what they declared.
Check 'multiple --file sources combine in order with --code last' {
    $f = Start-Fake @('--project', $projA, '--seconds', '12', '--exec-delay', '50')
    try {
        $one = Join-Path $env:TEMP 'urc-compose-1.cs'
        $two = Join-Path $env:TEMP 'urc-compose-2.cs'
        Set-Content $one -Encoding utf8 -Value 'int First() { return 1; }'
        Set-Content $two -Encoding utf8 -Value 'int Second() { return 2; }'

        $r = Invoke-Urc @('exec', '--file', $one, '--file', $two,
                          '--code', 'return First() + Second();', '--project', $projA)
        Assert ($r.ExitCode -eq 0) "expected exit 0, got $($r.ExitCode): $($r.Output)"

        # The fake echoes the first and last line it received.
        Assert ($r.Output -match 'int First') "first file is not first: $($r.Output)"
        Assert ($r.Output -match 'return First\(\) \+ Second\(\)') "--code is not last: $($r.Output)"
    } finally { Stop-Fake $f }
}

# --- the line spans that let the editor window undo the concatenation -----------------------
# The window's history is how a person sees what an agent ran, and a composed call arrives there
# as one long concatenation. The CLI sends where each source landed so it can be split back
# apart. Ranges must be exact: they are the same numbering compiler errors use, so an off-by-one
# here misattributes a compile error to the wrong file.
Check 'exec reports where each combined source landed' {
    $f = Start-Fake @('--project', $projA, '--seconds', '14', '--exec-delay', '50')
    try {
        $one = Join-Path $env:TEMP 'urc-span-1.cs'
        $two = Join-Path $env:TEMP 'urc-span-2.cs'
        Set-Content $one -Encoding utf8 -Value @('int A1() { return 1; }', 'int A2() { return 2; }')
        Set-Content $two -Encoding utf8 -Value @('int B1() { return 3; }')

        $r = Invoke-Urc @('exec', '--file', $one, '--file', $two,
                          '--code', 'return A1() + B1();', '--project', $projA)
        Assert ($r.ExitCode -eq 0) "expected exit 0, got $($r.ExitCode): $($r.Output)"

        # 2 lines, then 1 line, then --code: so lines 1-2, 3, and 4.
        Assert ($r.Output -match [regex]::Escape('urc-span-1.cs:1+2')) "wrong span for file 1: $($r.Output)"
        Assert ($r.Output -match [regex]::Escape('urc-span-2.cs:3+1')) "wrong span for file 2: $($r.Output)"
        Assert ($r.Output -match [regex]::Escape('--code:4+1')) "wrong span for --code: $($r.Output)"

        # A single source has nothing to split, so the field stays off the wire entirely rather
        # than shipping a one-element list the window would have to special-case anyway.
        $solo = Invoke-Urc @('exec', '--code', 'return 1;', '--project', $projA)
        Assert ($solo.ExitCode -eq 0) "expected exit 0, got $($solo.ExitCode): $($solo.Output)"
        Assert ($solo.Output -notmatch 'sources\[') "a lone source should send no spans: $($solo.Output)"
    } finally { Stop-Fake $f }
}

# --- a snippet declaring what it needs ------------------------------------------------------
# Composing by hand is a correctness problem, not a convenience one: a snippet calling a helper in
# another file compiles only if the caller remembered to pass that file too, and forgetting
# produces a compile error at a line the caller never wrote. //urc:require moves that from
# something tracked per call to something the file states once.
Check 'a snippet pulls in the files it requires' {
    $f = Start-Fake @('--project', $projA, '--seconds', '16', '--exec-delay', '50')
    try {
        $dir = Join-Path $env:TEMP 'urc-req'
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
        $leaf = Join-Path $dir 'leaf.cs'
        $mid  = Join-Path $dir 'mid.cs'
        $top  = Join-Path $dir 'top.cs'
        Set-Content $leaf -Encoding utf8 -Value @('int Leaf() { return 1; }')
        Set-Content $mid  -Encoding utf8 -Value @('//urc:require ./leaf.cs', 'int Mid() { return Leaf(); }')
        Set-Content $top  -Encoding utf8 -Value @('// header comment', '//urc:require ./mid.cs', 'int Top() { return Mid(); }')

        # Naming only the top file must drag in the whole chain, dependencies first.
        $r = Invoke-Urc @('exec', '--file', $top, '--code', 'return Top();', '--project', $projA)
        Assert ($r.ExitCode -eq 0) "expected exit 0, got $($r.ExitCode): $($r.Output)"
        Assert ($r.Output -match 'leaf\.cs:1\+1') "leaf was not pulled in first: $($r.Output)"
        Assert ($r.Output -match 'mid\.cs:2\+2') "mid is misplaced: $($r.Output)"
        Assert ($r.Output -match 'top\.cs:4\+3') "top is misplaced: $($r.Output)"

        # Naming a file that is ALSO required elsewhere must include it once - twice would be a
        # duplicate-definition error, which is the failure this feature exists to prevent.
        $dup = Invoke-Urc @('exec', '--file', $leaf, '--file', $top, '--code', 'return Top();', '--project', $projA)
        Assert ($dup.ExitCode -eq 0) "expected exit 0, got $($dup.ExitCode): $($dup.Output)"
        $leafCount = ([regex]::Matches($dup.Output, 'leaf\.cs:')).Count
        Assert ($leafCount -eq 1) "leaf.cs appeared $leafCount times, expected once: $($dup.Output)"

        # A cycle must terminate rather than recurse until the stack dies.
        $a = Join-Path $dir 'cycA.cs'
        $b = Join-Path $dir 'cycB.cs'
        Set-Content $a -Encoding utf8 -Value @('//urc:require ./cycB.cs', 'int A() { return B(); }')
        Set-Content $b -Encoding utf8 -Value @('//urc:require ./cycA.cs', 'int B() { return 2; }')
        $cyc = Invoke-Urc @('exec', '--file', $a, '--code', 'return A();', '--project', $projA)
        Assert ($cyc.ExitCode -eq 0) "a require cycle did not terminate cleanly: $($cyc.Output)"

        # A missing requirement must name the file that asked for it, not just the missing path.
        $bad = Join-Path $dir 'bad.cs'
        Set-Content $bad -Encoding utf8 -Value @('//urc:require ./nope.cs', 'int Bad() { return 0; }')
        $miss = Invoke-Urc @('exec', '--file', $bad, '--code', 'return Bad();', '--project', $projA)
        Assert ($miss.ExitCode -ne 0) "a missing requirement was accepted silently"
        Assert ($miss.Output -match 'nope\.cs') "the missing file is not named: $($miss.Output)"
        Assert ($miss.Output -match 'required by') "the requiring file is not named: $($miss.Output)"
    } finally { Stop-Fake $f }
}

# --- the one the whole design exists for ----------------------------------------------------
Check 'exec survives a domain reload mid-job (reconnect + re-attach)' {
    $f = Start-Fake @('--project', $projA, '--seconds', '15', '--exec-delay', '600', '--reload-after', '200')
    try {
        $r = Invoke-Urc @('exec', '--code', 'return 2+2;', '--project', $projA)
        Assert ($r.ExitCode -eq 0) "expected exit 0, got $($r.ExitCode): $($r.Output)"
        Assert ($r.Output -match 'fake-result') "did not recover the result: $($r.Output)"
        $log = Get-Content $f.Log -Raw
        Assert ($log -match 'attach') "the client never re-attached"
        Assert ($log -match 'generation 2') "the fake never completed its reload"
    } finally { Stop-Fake $f }
}

# --- fail fast, do not burn the timeout -----------------------------------------------------
Check 'editor crash is detected in seconds, not at timeout' {
    $f = Start-Fake @('--project', $projA, '--seconds', '20', '--exec-delay', '9000', '--die-after', '1000')
    try {
        $sw = [Diagnostics.Stopwatch]::StartNew()
        $r = Invoke-Urc @('exec', '--code', 'return 1;', '--project', $projA, '--timeout', '30')
        $sw.Stop()
        Assert ($r.ExitCode -eq 2) "expected exit 2 (unavailable), got $($r.ExitCode)"
        Assert ($sw.Elapsed.TotalSeconds -lt 10) "took $($sw.Elapsed.TotalSeconds)s against a 30s timeout"
        Assert ($r.Output -match 'exited') "unexpected message: $($r.Output)"
    } finally { Stop-Fake $f }
}

# --- timeout bounds the CLI, never the job --------------------------------------------------
Check 'timeout leaves the job running and points at resume' {
    $f = Start-Fake @('--project', $projA, '--seconds', '20', '--exec-delay', '4000')
    try {
        $r = Invoke-Urc @('exec', '--code', 'return 1;', '--project', $projA, '--timeout', '1')
        Assert ($r.ExitCode -eq 2) "expected exit 2, got $($r.ExitCode)"
        Assert ($r.Output -match 'urc resume') "no recovery hint: $($r.Output)"

        $r2 = Invoke-Urc @('resume', '--project', $projA)
        Assert ($r2.ExitCode -eq 0) "resume failed: $($r2.Output)"
        Assert ($r2.Output -match 'fake-result') "resume did not deliver the result: $($r2.Output)"
    } finally { Stop-Fake $f }
}

# --- multiple editors, zero configuration ---------------------------------------------------
Check 'two editors are discovered and routed by project' {
    $a = Start-Fake @('--project', $projA, '--seconds', '12')
    $b = Start-Fake @('--project', $projB, '--seconds', '12', '--generation', '7')
    try {
        $all = Invoke-Urc @('status', '--all')
        Assert ($all.Output -match 'ProjectA') "ProjectA missing from status --all"
        Assert ($all.Output -match 'ProjectB') "ProjectB missing from status --all"

        $one = Invoke-Urc @('status', '--project', $projB, '--json')
        Assert ($one.Output -match '"generation":7') "routed to the wrong editor: $($one.Output)"
    } finally { Stop-Fake $a; Stop-Fake $b }
}

# --- never guess which editor to use --------------------------------------------------------
Check 'unknown project errors and names the running editors' {
    $f = Start-Fake @('--project', $projA, '--seconds', '10')
    try {
        $r = Invoke-Urc @('status', '--project', 'C:\nope\Missing')
        Assert ($r.ExitCode -eq 1) "expected exit 1, got $($r.ExitCode)"
        Assert ($r.Output -match 'ProjectA') "did not name the running editors: $($r.Output)"
    } finally { Stop-Fake $f }
}

Check 'no editors at all is a fast clean failure' {
    $r = Invoke-Urc @('status', '--project', $projA)
    Assert ($r.ExitCode -eq 1) "expected exit 1, got $($r.ExitCode)"
    Assert ($r.Output -match 'no editor running') "unexpected message: $($r.Output)"
}

Write-Host ""
Write-Host "$script:pass passed, $script:fail failed"
Write-Host ""
exit ([int]($script:fail -gt 0))
