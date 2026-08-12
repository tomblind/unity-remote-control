<#
.SYNOPSIS
  Installs urc into a Unity project with zero git footprint.

.DESCRIPTION
  Copies the editor package into <project>/Assets/UnityRemoteControl/ and the CLI into
  <project>/.urc/, then hides both from git via a sentinel-fenced block in .git/info/exclude.

  WHY ASSETS-MODE. The goal is dropping this into a SHARED repo without imposing it on teammates,
  and .git/info/exclude is the only ignore mechanism that is itself invisible: .gitignore is
  tracked, so editing it IS the diff you are trying to avoid. An embedded package under Packages/
  would also avoid manifest.json, but Unity records it in the tracked packages-lock.json, which is
  why Assets-mode is the only footprint-free load path.

  KNOWN LIMITS, inherent to the approach:
    - .git/info/exclude is per-clone, so a fresh clone needs a re-install.
    - `git clean -xdf` deletes the install (its files are ignored files).
    - The source compiles as part of the project.

.EXAMPLE
  .\install.ps1 F:\projects\MyGame
  .\install.ps1 F:\projects\MyGame -Uninstall
#>

[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string] $ProjectPath,

    [switch] $Uninstall,

    # Install visibly even when git is unavailable or the exclude cannot be written.
    [switch] $Force
)

$ErrorActionPreference = 'Stop'

$SourceRoot   = $PSScriptRoot
$PackageDir   = Join-Path $SourceRoot 'Packages\com.tomblind.unity-remote-control'
$RoslynDir    = Join-Path $SourceRoot 'Packages\com.tomblind.unity-remote-control.roslyn\Plugins\Roslyn'
$SkillFile    = Join-Path $SourceRoot 'skill\SKILL.md'
$CliBinary    = Join-Path $SourceRoot 'cli\bin\Release\net10.0\win-x64\publish\urc.exe'

$InstallName  = 'UnityRemoteControl'
$SentinelOpen = '# >>> unity-remote-control ({0}) >>>'
$SentinelShut = '# <<< unity-remote-control ({0}) <<<'

# ---------------------------------------------------------------------------------------------

function Fail([string] $Message) { Write-Host "error: $Message" -ForegroundColor Red; exit 1 }
function Note([string] $Message) { Write-Host "  $Message" -ForegroundColor DarkGray }

function Resolve-Project([string] $Path) {
    if (-not $Path) {
        Fail "no project given.`n  usage: .\install.ps1 <path to Unity project> [-Uninstall]"
    }
    if (-not (Test-Path $Path)) { Fail "no such directory: $Path" }

    $full = (Resolve-Path $Path).Path
    if (-not (Test-Path (Join-Path $full 'ProjectSettings\ProjectVersion.txt'))) {
        Fail "not a Unity project (no ProjectSettings/ProjectVersion.txt): $full"
    }
    return $full
}

<#
  Locates the git root, the path prefix from that root to the project, and the exclude file.

  Three traps live here, all previously hit:
    - Under $ErrorActionPreference='Stop', a NATIVE command writing to stderr becomes a TERMINATING
      error even with 2>$null, so a non-repo `git` call would abort the whole script. Hence the
      local override.
    - `git rev-parse` must not be gated on $LASTEXITCODE: the cmd\git.exe shim reports -1 on a
      pipeline short-circuit even on success. Judge by the output instead.
    - The exclude file is NOT always .git/info/exclude - in a worktree, .git is a FILE pointing
      elsewhere - so it is resolved with --git-path.
#>
function Resolve-GitInfo([string] $ProjectRoot) {
    $ErrorActionPreference = 'Continue'

    $info = @{ IsRepo = $false; Prefix = ''; ExcludeFile = $null }

    Push-Location $ProjectRoot
    try {
        $top = & git rev-parse --show-toplevel 2>$null
        if (-not $top) { return $info }

        $prefix = & git rev-parse --show-prefix 2>$null
        $exclude = & git rev-parse --git-path info/exclude 2>$null
        if (-not $exclude) { return $info }

        # --git-path may return a RELATIVE path, and it is relative to the CURRENT DIRECTORY, not
        # the repository root. Resolving it against the root instead produces a path outside the
        # repo entirely (verified: a project one level down yielded "<repo>/../.git/info/exclude"),
        # and the block then lands in a file git never reads.
        if (-not [System.IO.Path]::IsPathRooted($exclude)) {
            $exclude = [System.IO.Path]::GetFullPath((Join-Path $ProjectRoot $exclude))
        }

        $info.IsRepo = $true
        $info.Prefix = if ($prefix) { $prefix.Trim() } else { '' }
        $info.ExcludeFile = $exclude
    }
    finally { Pop-Location }

    return $info
}

# Every path the install creates, root-anchored. Enumerated up front so nothing leaks on first use.
function Get-ExcludePaths([string] $Prefix) {
    $paths = @(
        "/$Prefix" + "Assets/$InstallName/"
        # Unity generates a .meta for the FOLDER ITSELF, which sits outside the directory pattern.
        # Missing this line is the single most common way a "zero footprint" install leaks.
        "/$Prefix" + "Assets/$InstallName.meta"
        "/$Prefix" + ".urc/"
        "/$Prefix" + ".claude/skills/urc/"
    )
    return ,$paths   # comma: a bare return unrolls a single-element array to a scalar
}

function Write-ExcludeBlock([string] $ExcludeFile, [string] $Prefix) {
    $open = $SentinelOpen -f $Prefix
    $shut = $SentinelShut -f $Prefix

    $lines = if (Test-Path $ExcludeFile) { @(Get-Content $ExcludeFile) } else { @() }
    $kept = Remove-Block $lines $open $shut

    $block = @($open) + (Get-ExcludePaths $Prefix) + @($shut)
    $final = @($kept) + $block

    $dir = Split-Path -Parent $ExcludeFile
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }

    Set-Content -Path $ExcludeFile -Value $final -Encoding utf8
}

# Drops a previously written block, so refresh and uninstall are idempotent and never touch lines
# the user added by hand.
function Remove-Block([string[]] $Lines, [string] $Open, [string] $Shut) {
    $kept = New-Object System.Collections.Generic.List[string]
    $inside = $false

    foreach ($line in $Lines) {
        if ($line -eq $Open) { $inside = $true; continue }
        if ($line -eq $Shut) { $inside = $false; continue }
        if (-not $inside) { $kept.Add($line) }
    }

    # Trailing blank lines accumulate across rewrites otherwise.
    while ($kept.Count -gt 0 -and [string]::IsNullOrWhiteSpace($kept[$kept.Count - 1])) {
        $kept.RemoveAt($kept.Count - 1)
    }

    return ,$kept.ToArray()
}

# True when the project already provides Roslyn - its own copy, or another package's. Installing a
# second one causes duplicate-assembly errors, so ours is skipped.
function Test-ProjectHasRoslyn([string] $ProjectRoot) {
    $hits = @(Get-ChildItem -Path (Join-Path $ProjectRoot 'Assets'), (Join-Path $ProjectRoot 'Packages') `
        -Filter 'Microsoft.CodeAnalysis.CSharp.Scripting.dll' -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notmatch [regex]::Escape("Assets\$InstallName") })

    return $hits.Count -gt 0
}

function Copy-Payload([string] $ProjectRoot) {
    $dest = Join-Path $ProjectRoot "Assets\$InstallName"
    $editorDest = Join-Path $dest 'Editor'

    # Refresh replaces only the .cs and .asmdef; see the Roslyn note below for why the DLLs are left
    # alone once present.
    if (Test-Path $editorDest) { Remove-Item $editorDest -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $editorDest | Out-Null

    Copy-Item -Path (Join-Path $PackageDir 'Editor\*') -Destination $editorDest -Recurse -Force
    # Unity regenerates .meta for source; copying the package's would duplicate GUIDs in a project
    # that also references the package.
    Get-ChildItem $editorDest -Filter '*.meta' -Recurse -File | Remove-Item -Force

    $roslynDest = Join-Path $dest 'Plugins\Roslyn'
    if (Test-ProjectHasRoslyn $ProjectRoot) {
        Note 'project already provides Roslyn - skipping the bundled copy.'
    }
    elseif (Test-Path $roslynDest) {
        # A RUNNING editor file-locks the loaded Roslyn DLLs, so a wipe-and-recopy fails mid-refresh
        # and leaves a half-installed Plugins folder. Existing DLLs are therefore never replaced.
        Note 'Roslyn already installed - left untouched (a running editor file-locks these DLLs).'
    }
    else {
        New-Item -ItemType Directory -Force -Path $roslynDest | Out-Null
        # The .meta files ARE copied here: they carry Editor-only platform settings and
        # isExplicitlyReferenced:0, which is what lets the asmdef bind Roslyn without naming it.
        Copy-Item -Path (Join-Path $RoslynDir '*') -Destination $roslynDest -Recurse -Force
        Note 'installed the bundled Roslyn.'
    }

    # CLI. On Windows the .exe is locked while running, so write beside it and swap.
    if (-not (Test-Path $CliBinary)) {
        Fail "the CLI is not built. Run:`n  dotnet publish cli\Urc.csproj -r win-x64 -c Release"
    }

    $cliDir = Join-Path $ProjectRoot '.urc'
    New-Item -ItemType Directory -Force -Path $cliDir | Out-Null
    $cliDest = Join-Path $cliDir 'urc.exe'

    try {
        Copy-Item $CliBinary $cliDest -Force
    }
    catch {
        $staged = "$cliDest.new"
        Copy-Item $CliBinary $staged -Force
        try {
            Move-Item $staged $cliDest -Force
        }
        catch {
            Remove-Item $staged -Force -ErrorAction SilentlyContinue
            Fail "could not replace $cliDest - close any running urc and re-run."
        }
    }

    # Skill, so agents learn to batch rather than issuing one call per thought.
    if (Test-Path $SkillFile) {
        $skillDest = Join-Path $ProjectRoot '.claude\skills\urc'
        New-Item -ItemType Directory -Force -Path $skillDest | Out-Null
        Copy-Item $SkillFile (Join-Path $skillDest 'SKILL.md') -Force
    }

    return $dest
}

function Write-Manifest([string] $ProjectRoot, [bool] $GitExcluded, [string] $Prefix) {
    $version = '0.1.0'
    $packageJson = Join-Path $PackageDir 'package.json'
    if (Test-Path $packageJson) {
        $parsed = Get-Content $packageJson -Raw | ConvertFrom-Json
        if ($parsed.version) { $version = $parsed.version }
    }

    $manifest = [ordered]@{
        version      = $version
        mode         = 'assets'
        installedAt  = (Get-Date).ToUniversalTime().ToString('o')
        source       = $SourceRoot
        gitExclude   = $GitExcluded
        prefix       = $Prefix
        excludePaths = @(Get-ExcludePaths $Prefix)
    }

    $path = Join-Path $ProjectRoot '.urc\install.json'
    $manifest | ConvertTo-Json -Depth 4 | Set-Content -Path $path -Encoding utf8
}

function Invoke-Uninstall([string] $ProjectRoot) {
    Write-Host "Uninstalling urc from $ProjectRoot" -ForegroundColor Cyan

    $git = Resolve-GitInfo $ProjectRoot
    if ($git.IsRepo -and (Test-Path $git.ExcludeFile)) {
        $open = $SentinelOpen -f $git.Prefix
        $shut = $SentinelShut -f $git.Prefix
        $kept = Remove-Block @(Get-Content $git.ExcludeFile) $open $shut
        Set-Content -Path $git.ExcludeFile -Value $kept -Encoding utf8
        Note 'removed the git exclude block.'
    }

    foreach ($relative in @("Assets\$InstallName", "Assets\$InstallName.meta", '.urc', '.claude\skills\urc')) {
        $path = Join-Path $ProjectRoot $relative
        if (-not (Test-Path $path)) { continue }
        try {
            Remove-Item $path -Recurse -Force
            Note "removed $relative"
        }
        catch {
            # Locked Roslyn DLLs are the usual cause, and only closing Unity releases them.
            Write-Host "  could not remove $relative - close Unity and re-run." -ForegroundColor Yellow
        }
    }

    Write-Host "Done." -ForegroundColor Green
}

# ---------------------------------------------------------------------------------------------

$project = Resolve-Project $ProjectPath

if ($Uninstall) { Invoke-Uninstall $project; exit 0 }

Write-Host "Installing urc into $project" -ForegroundColor Cyan

# Refuse the one configuration that produces duplicate types: a project that already references the
# package through UPM would end up compiling two copies of every class.
$manifestJson = Join-Path $project 'Packages\manifest.json'
if ((Test-Path $manifestJson) -and ((Get-Content $manifestJson -Raw) -match 'com\.tomblind\.unity-remote-control')) {
    if (-not $Force) {
        Fail ("this project already references com.tomblind.unity-remote-control in Packages/manifest.json.`n" +
              "  Assets-mode on top of that would compile two copies of every type.`n" +
              "  Remove the manifest entry first, or pass -Force if you know what you are doing.")
    }
    Write-Host "  warning: package is also referenced via manifest.json (-Force)." -ForegroundColor Yellow
}

$git = Resolve-GitInfo $project
$excluded = $false

if ($git.IsRepo) {
    try {
        Write-ExcludeBlock $git.ExcludeFile $git.Prefix
        $excluded = $true
        Note "hidden from git via $($git.ExcludeFile)"
    }
    catch {
        if (-not $Force) { Fail "could not write the git exclude: $($_.Exception.Message)`n  Pass -Force to install visibly." }
        Write-Host "  warning: installing VISIBLY - could not write the git exclude." -ForegroundColor Yellow
    }
}
else {
    if (-not $Force) {
        Fail ("$project is not inside a git repository, so the install cannot be hidden.`n" +
              "  Pass -Force to install visibly.")
    }
    Write-Host "  warning: not a git repository - installing visibly." -ForegroundColor Yellow
}

$dest = Copy-Payload $project
Write-Manifest $project $excluded $git.Prefix

Note "editor package -> $dest"
Note "cli           -> $(Join-Path $project '.urc\urc.exe')"

Write-Host ""
Write-Host "Done. Open the project in Unity, then from the project root:" -ForegroundColor Green
Write-Host "  .\.urc\urc.exe status"
Write-Host ""

# Verify what this install is actually responsible for: that every path it created is hidden.
# NOT that the whole tree is clean - a working project usually has unrelated edits and untracked
# files of its own, and reporting those as though the installer caused them is a false alarm.
if ($excluded) {
    Push-Location $project
    try {
        $ErrorActionPreference = 'Continue'

        $mine = @(
            "Assets/$InstallName"
            "Assets/$InstallName.meta"
            '.urc'
            '.claude/skills/urc'
        ) | Where-Object { Test-Path (Join-Path $project $_) }

        $leaked = @()
        foreach ($path in $mine) {
            & git check-ignore -q -- $path 2>$null
            if ($LASTEXITCODE -ne 0) { $leaked += $path }
        }

        if ($leaked.Count -gt 0) {
            Write-Host "warning: these installed paths are NOT hidden from git:" -ForegroundColor Yellow
            $leaked | ForEach-Object { Write-Host "  $_" -ForegroundColor Yellow }
            Write-Host "  Diagnose with: git check-ignore -v <path>" -ForegroundColor Yellow
        }
        else {
            Note 'every installed path is hidden from git.'
        }
    }
    finally { Pop-Location }
}
