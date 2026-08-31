<#
.SYNOPSIS
    Drives the Unity Editor headlessly for compile checks, asset generation, and test runs.

.DESCRIPTION
    Single entry point used by every work package in CORE_FIXES.md to verify its own
    acceptance criteria without a human clicking through the Editor.

    Unity locks a project folder to one process. If the Editor has this project open,
    every batchmode invocation fails with exit code 1 immediately after "Successfully
    changed project path". This script detects that up front and says so, rather than
    letting you misread a lock rejection as a compile failure.

.PARAMETER Mode
    compile - open the project headless; non-zero exit means scripts failed to compile.
    method  - run a static method via -executeMethod (implies a compile first).
    tests   - run the test framework and parse the NUnit XML result.
    all     - compile, then EditMode tests, then PlayMode tests.

.EXAMPLE
    .\Tools\unity-verify.ps1 -Mode compile

.EXAMPLE
    .\Tools\unity-verify.ps1 -Mode method -Method Skylotus.Editor.SkylotusCI.GenerateCoreSystemsPrefab

.EXAMPLE
    .\Tools\unity-verify.ps1 -Mode tests -TestPlatform EditMode
#>
[CmdletBinding()]
param(
    [ValidateSet('compile', 'method', 'tests', 'all')]
    [string]$Mode = 'compile',

    [string]$Method,

    [ValidateSet('EditMode', 'PlayMode')]
    [string]$TestPlatform = 'EditMode',

    [string]$ProjectPath = (Split-Path $PSScriptRoot -Parent),

    [string]$UnityExe = 'C:\Program Files\Unity\Hub\Editor\6000.3.8f1\Editor\Unity.exe',

    # Omit -nographics. Needed for asset generation that touches the render pipeline
    # (volume profiles, anything that instantiates a renderer).
    [switch]$Graphics,

    [int]$TimeoutMinutes = 30,

    [string]$LogDir = (Join-Path (Split-Path $PSScriptRoot -Parent) 'Logs\ci')
)

$ErrorActionPreference = 'Stop'

# --- Preflight ---------------------------------------------------------------

if (-not (Test-Path $UnityExe)) {
    Write-Error "Unity not found at: $UnityExe`nPass -UnityExe with the correct path."
}

if (-not (Test-Path (Join-Path $ProjectPath 'Assets'))) {
    Write-Error "Not a Unity project (no Assets folder): $ProjectPath"
}

# A GUI Editor holding the project cannot be waited out — it stays open indefinitely and
# every batchmode call would fail. Detect it by process, not by lockfile: our own batchmode
# runs create the same lockfile, and treating those as fatal would break parallel agents.
$guiHolder = Get-CimInstance Win32_Process -Filter "Name='Unity.exe'" |
    Where-Object { $_.CommandLine -match '-projectpath' -and $_.CommandLine -notmatch 'AssetImportWorker' } |
    Where-Object { $_.CommandLine -notmatch '-batchmode' } |
    Where-Object { $_.CommandLine -replace '/', '\' -match [regex]::Escape($ProjectPath) }

if ($guiHolder) {
    Write-Host ''
    Write-Host '  PROJECT IS LOCKED BY THE EDITOR' -ForegroundColor Yellow
    Write-Host '  The Unity Editor GUI has this project open. Batchmode cannot attach to a' -ForegroundColor Yellow
    Write-Host '  running Editor and will fail with exit code 1.' -ForegroundColor Yellow
    Write-Host "  Holding process: PID $($guiHolder.ProcessId | Select-Object -First 1)" -ForegroundColor Yellow
    Write-Host '  Close the Editor (or run this against a project copy) and retry.' -ForegroundColor Yellow
    Write-Host ''
    exit 2
}

New-Item -ItemType Directory -Force -Path $LogDir | Out-Null
$stamp = Get-Date -Format 'yyyyMMdd_HHmmss'

# --- Cross-process serialization ---------------------------------------------
# Unity locks a project folder to one process, so concurrent agents must queue rather
# than fail. A named system mutex serializes every invocation of this script against the
# same project. Without it, parallel work packages would trip over each other's runs and
# report a lock rejection as a build failure.
$mutexName = 'Global\SkylotusUnityVerify_' + ($ProjectPath.ToLowerInvariant() -replace '[^a-z0-9]', '_')
$mutex = New-Object System.Threading.Mutex($false, $mutexName)
$mutexHeld = $false

try {
    if (-not $mutex.WaitOne(0)) {
        Write-Host "  waiting for another verify run to finish (up to $TimeoutMinutes min)..." -ForegroundColor DarkGray
        if (-not $mutex.WaitOne([TimeSpan]::FromMinutes($TimeoutMinutes))) {
            Write-Host '  Timed out waiting for the Unity lock.' -ForegroundColor Red
            exit 2
        }
    }
    $mutexHeld = $true
}
catch [System.Threading.AbandonedMutexException] {
    # A previous run died without releasing. We now own it; carry on.
    $mutexHeld = $true
}

# --- Runner ------------------------------------------------------------------

function Invoke-Unity {
    param([string[]]$ExtraArgs, [string]$Tag)

    $log = Join-Path $LogDir "$Tag`_$stamp.log"

    $unityArgs = @('-batchmode', '-projectPath', $ProjectPath, '-logFile', $log)
    if (-not $Graphics) { $unityArgs += '-nographics' }
    $unityArgs += $ExtraArgs

    Write-Host "  unity $Tag ..." -NoNewline

    $proc = Start-Process -FilePath $UnityExe -ArgumentList $unityArgs -PassThru -NoNewWindow
    $done = $proc.WaitForExit($TimeoutMinutes * 60 * 1000)

    if (-not $done) {
        $proc.Kill()
        Write-Host ' TIMEOUT' -ForegroundColor Red
        Write-Host "  Exceeded $TimeoutMinutes min. Log: $log"
        return [pscustomobject]@{ ExitCode = 124; Log = $log }
    }

    $code = $proc.ExitCode
    if ($code -eq 0) { Write-Host ' ok' -ForegroundColor Green }
    else { Write-Host " exit $code" -ForegroundColor Red }

    [pscustomobject]@{ ExitCode = $code; Log = $log }
}

function Show-CompileErrors {
    param([string]$Log)

    if (-not (Test-Path $Log)) { return }

    # Unity prefixes compiler diagnostics with the file and a CS code.
    $errors = Select-String -Path $Log -Pattern 'error CS\d+' -ErrorAction SilentlyContinue |
        Select-Object -ExpandProperty Line -Unique

    if ($errors) {
        Write-Host ''
        Write-Host "  $($errors.Count) compile error(s):" -ForegroundColor Red
        $errors | Select-Object -First 40 | ForEach-Object { Write-Host "    $_" -ForegroundColor Red }
        if ($errors.Count -gt 40) { Write-Host "    ... and $($errors.Count - 40) more" -ForegroundColor Red }
        Write-Host ''
        return
    }

    # Markers emitted by SkylotusCI. An -executeMethod failure lands here, not above:
    # the project compiled fine and the method itself reported the problem.
    $ci = Select-String -Path $Log -Pattern '\[CI\]' -ErrorAction SilentlyContinue |
        Select-Object -ExpandProperty Line -Unique

    if ($ci) {
        Write-Host ''
        Write-Host '  Reported by SkylotusCI:' -ForegroundColor Red
        $ci | Select-Object -First 40 | ForEach-Object { Write-Host "    $_" -ForegroundColor Red }
        if ($ci.Count -gt 40) { Write-Host "    ... and $($ci.Count - 40) more" -ForegroundColor Red }
        Write-Host ''
        return
    }

    # Unhandled exception thrown by an -executeMethod target.
    $ex = Select-String -Path $Log -Pattern 'Exception:|executeMethod|Unhandled' -ErrorAction SilentlyContinue |
        Select-Object -ExpandProperty Line -Unique

    if ($ex) {
        Write-Host ''
        Write-Host '  Exception:' -ForegroundColor Red
        $ex | Select-Object -First 20 | ForEach-Object { Write-Host "    $_" -ForegroundColor Red }
        Write-Host ''
        return
    }

    # Nothing recognized. Show a tail with Unity's shutdown noise stripped, so the signal
    # is not buried under memory-leak dumps and thread-abort chatter.
    $noise = 'MemoryLeaks|abort_threads|Cleanup mono|usbmuxd|weakptr|StackAllocator|' +
             'Physics::Module|Licensing::|Input System|Killing ADB|debugger-agent|' +
             'Package Manager\] Server|AcceleratorClient|DomainUnload|^\s*$'

    Write-Host ''
    Write-Host '  Unrecognized failure. Filtered log tail:' -ForegroundColor Yellow
    Get-Content $Log -Tail 120 |
        Where-Object { $_ -notmatch $noise } |
        Select-Object -Last 25 |
        ForEach-Object { Write-Host "    $_" -ForegroundColor DarkGray }
    Write-Host ''
    Write-Host "  Full log: $Log" -ForegroundColor DarkGray
    Write-Host ''
}

function Show-TestResults {
    param([string]$ResultsXml)

    if (-not (Test-Path $ResultsXml)) {
        Write-Host '  No results XML produced.' -ForegroundColor Red
        return $false
    }

    [xml]$xml = Get-Content $ResultsXml
    $run = $xml.'test-run'

    $total = [int]$run.total
    $passed = [int]$run.passed
    $failed = [int]$run.failed
    $skipped = [int]$run.skipped

    $color = if ($failed -gt 0) { 'Red' } else { 'Green' }
    Write-Host "  $passed/$total passed, $failed failed, $skipped skipped" -ForegroundColor $color

    if ($failed -gt 0) {
        $xml.SelectNodes("//test-case[@result='Failed']") | ForEach-Object {
            Write-Host "    FAIL $($_.fullname)" -ForegroundColor Red
            $msg = $_.failure.message.'#cdata-section'
            if ($msg) { Write-Host "         $($msg.Trim() -split "`n" | Select-Object -First 3)" -ForegroundColor DarkRed }
        }
    }

    return ($failed -eq 0)
}

# --- Modes -------------------------------------------------------------------

$failures = 0

if ($Mode -in @('compile', 'all')) {
    Write-Host ''
    Write-Host 'COMPILE' -ForegroundColor Cyan
    $r = Invoke-Unity -ExtraArgs @('-quit') -Tag 'compile'
    if ($r.ExitCode -ne 0) { Show-CompileErrors -Log $r.Log; $failures++ }
}

if ($Mode -eq 'method') {
    if (-not $Method) { Write-Error 'Mode "method" requires -Method <FullyQualified.Static.Method>' }
    Write-Host ''
    Write-Host "EXECUTE $Method" -ForegroundColor Cyan
    $r = Invoke-Unity -ExtraArgs @('-quit', '-executeMethod', $Method) -Tag 'method'
    if ($r.ExitCode -ne 0) { Show-CompileErrors -Log $r.Log; $failures++ }
    else { Write-Host "  Log: $($r.Log)" -ForegroundColor DarkGray }
}

if ($Mode -in @('tests', 'all')) {
    $platforms = if ($Mode -eq 'all') { @('EditMode', 'PlayMode') } else { @($TestPlatform) }

    foreach ($p in $platforms) {
        Write-Host ''
        Write-Host "TESTS ($p)" -ForegroundColor Cyan

        $results = Join-Path $LogDir "results_$p`_$stamp.xml"

        # NOTE: -runTests must NOT be combined with -quit; Unity needs to stay alive
        # until the run completes and then exits on its own.
        $r = Invoke-Unity -ExtraArgs @('-runTests', '-testPlatform', $p, '-testResults', $results) -Tag "tests_$p"

        if (-not (Show-TestResults -ResultsXml $results)) { $failures++ }
        elseif ($r.ExitCode -ne 0) { Show-CompileErrors -Log $r.Log; $failures++ }
    }
}

# Release the serialization mutex before exiting. PowerShell's `exit` does not unwind
# through a closed try/catch, so this is explicit rather than a finally block. A run that
# dies without reaching here abandons the mutex, which the next waiter picks up via
# AbandonedMutexException — the queue does not deadlock.
function Release-VerifyLock {
    if ($script:mutexHeld -and $script:mutex) {
        try { $script:mutex.ReleaseMutex() } catch { }
        $script:mutex.Dispose()
        $script:mutexHeld = $false
    }
}

Write-Host ''
Release-VerifyLock

if ($failures -eq 0) {
    Write-Host 'PASS' -ForegroundColor Green
    exit 0
}
else {
    Write-Host "FAIL ($failures step(s))" -ForegroundColor Red
    exit 1
}
