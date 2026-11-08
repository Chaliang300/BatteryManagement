<#
.SYNOPSIS
    NON-GATING stress probe: deterministically force the single-instance "primary busy" path (exit 21).

.DESCRIPTION
    gating = FALSE. A red result from this script is NOT a product failure and must never block a PR.
    It is a stress/diagnostic probe; the authoritative gate remains tools/verify-single-instance.ps1.

    WHY THIS EXISTS
      The secondary already reports exit=21 (PrimaryNotReady) when the readiness event exists but the
      pipe refuses a connection for ConnectWaitMs (3500 ms = ProtocolTimeoutMs + 1000). In normal
      operation that branch is a pure fallback and is effectively unreachable:
        - the server drops any single stuck client after ProtocolTimeoutMs (2500 ms) < 3500 ms,
        - so however many silent clients you queue, the secondary always connects once the next
          listening instance appears and then recovers to 22 (denied) / 0 (activated).
      Therefore the only deterministic way to starve the secondary's connect window is to freeze the
      primary process so it never returns to BeginWaitForConnection.

    CONSTRUCTION (order matters)
      1. start the primary and wait for the log line "Primary instance ready (pipe listening)";
      2. connect ONE silent client and keep it open. This consumes the server's single listening
         pipe instance, so the kernel can no longer complete a new client connection;
      3. IMMEDIATELY suspend the primary (NtSuspendProcess) BEFORE it drops the silent client
         (the server drops it after 2500 ms, so there is no safe sleep here);
      4. start the secondary -> no listening instance exists and the listener thread is frozen ->
         connect cannot complete for ConnectWaitMs -> exit=21 + log tag [case=PRIMARY_BUSY].
      NOTE: if you suspend the primary WITHOUT consuming the listening instance first, the kernel
      completes the secondary's connect immediately, the secondary then blocks on the protocol
      response for 2500 ms and exits 24 (ProtocolTimeout). That is a different path and not the
      behaviour this probe targets.

    DANGER / SAFETY
      NtSuspendProcess freezes EVERY thread of the target process. While frozen, the primary keeps
      holding the single-instance mutex and its readiness event, so every later second start will
      report 20/21 until it is resumed or killed. This script therefore:
        - resumes in a `finally` block (normal, error and Ctrl+C paths) and re-checks liveness;
        - refuses to run if the target PID is 0 / not a PowerDock process;
        - if NtSuspendProcess itself fails, reports inconclusive (SKIP) instead of failing;
        - always kills the probe-owned instances at the end.
      Permissions: PROCESS_SUSPEND_RESUME (0x0800) on our own, non-protected process - no elevation.
      Never point this at a process you did not start, and never leave a target suspended.

.PARAMETER ExePath
    Path to PowerDock.exe (framework-dependent build output is fine). Must be outside the repo
    workspace for reliable tray/shell behaviour.

.PARAMETER EvidenceDir
    Directory for the JSON report, log slices and probe stdout.

.PARAMETER ConnectWindowMs
    Expected secondary connect window (3500 ms). Used only to bound waits / sanity-check timings.

.PARAMETER SuspendLeadMs
    How long to wait after the silent client connects before suspending. Keep small (<=300 ms);
    a long sleep lets the server drop the silent client and the probe degenerates to exit 24.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File tools\verify-single-instance-stress.ps1 `
        -ExePath "$env:LOCALAPPDATA\Programs\PowerDock-qa\9693c45\PowerDock.exe"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ExePath,
    [string]$EvidenceDir = (Join-Path $env:LOCALAPPDATA 'Programs\PowerDock-qa\stress'),
    [int]$ConnectWindowMs = 3500,
    [int]$SuspendLeadMs = 250
)

$ErrorActionPreference = 'Stop'
$script:Gating = $false
$script:Checks = New-Object System.Collections.ArrayList
$script:ExpectedNoPrimary = 20
$script:ExpectedPrimaryNotReady = 21

function Write-Section([string]$text) {
    Write-Host ''
    Write-Host ('== ' + $text) -ForegroundColor Cyan
}

function Add-Check([string]$name, [bool]$pass, [string]$detail = '', [bool]$inconclusive = $false) {
    $status = 'fail'
    if ($inconclusive) { $status = 'inconclusive' } elseif ($pass) { $status = 'pass' }
    [void]$script:Checks.Add([ordered]@{ name = $name; status = $status; detail = $detail })
    $color = 'Red'
    if ($inconclusive) { $color = 'Yellow' } elseif ($pass) { $color = 'Green' }
    $suffix = ''
    if ($detail) { $suffix = ' :: ' + $detail }
    Write-Host ('  [' + $status + '] ' + $name + $suffix) -ForegroundColor $color
}

# ---------------------------------------------------------------------------------------------
# Native helpers: suspend/resume only. Kept minimal on purpose.
# ---------------------------------------------------------------------------------------------
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class QaSuspend
{
    public const uint PROCESS_SUSPEND_RESUME = 0x0800;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr hObject);

    [DllImport("ntdll.dll")]
    public static extern int NtSuspendProcess(IntPtr processHandle);

    [DllImport("ntdll.dll")]
    public static extern int NtResumeProcess(IntPtr processHandle);

    /// <summary>Returns 0 on success, otherwise the NTSTATUS code.</summary>
    public static int Suspend(int pid, out int win32Error)
    {
        win32Error = 0;
        IntPtr h = OpenProcess(PROCESS_SUSPEND_RESUME, false, pid);
        if (h == IntPtr.Zero) { win32Error = Marshal.GetLastWin32Error(); return -1; }
        try { return NtSuspendProcess(h); }
        finally { CloseHandle(h); }
    }

    /// <summary>Returns 0 on success, otherwise the NTSTATUS code.</summary>
    public static int Resume(int pid, out int win32Error)
    {
        win32Error = 0;
        IntPtr h = OpenProcess(PROCESS_SUSPEND_RESUME, false, pid);
        if (h == IntPtr.Zero) { win32Error = Marshal.GetLastWin32Error(); return -1; }
        try { return NtResumeProcess(h); }
        finally { CloseHandle(h); }
    }
}
'@ -ErrorAction Stop

# ---------------------------------------------------------------------------------------------
# Environment helpers
# ---------------------------------------------------------------------------------------------
$script:ExeFullPath = (Resolve-Path -LiteralPath $ExePath -ErrorAction Stop).Path
$script:ExeDir = Split-Path -Parent $script:ExeFullPath
$script:LogPath = Join-Path $script:ExeDir 'powerdock.log'
$script:SessionId = (Get-Process -Id $PID).SessionId
$script:UserSid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$script:MutexName = "Local\PowerDock_Mutex_$($script:SessionId)_$($script:UserSid)"
$script:PipeName = "PowerDock_Pipe_$($script:SessionId)_$($script:UserSid)"

function Get-PowerDockProcesses {
    @(Get-Process -Name 'PowerDock' -ErrorAction SilentlyContinue)
}

function Get-PipePowerDockNames {
    try {
        @([System.IO.Directory]::GetFiles('\\.\pipe\') | Where-Object { $_ -match 'PowerDock' });
    }
    catch {
        @('__ENUM_FAILED__');
    }
}

function Clear-State {
    Get-PowerDockProcesses | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500
}

function Get-LogText {
    if (Test-Path -LiteralPath $script:LogPath) { return (Get-Content -LiteralPath $script:LogPath -Raw -ErrorAction SilentlyContinue) }
    return ''
}

function Wait-LogMatch([string]$pattern, [int]$timeoutMs) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $timeoutMs) {
        if ((Get-LogText) -match $pattern) { return $true }
        Start-Sleep -Milliseconds 100
    }
    return $false
}

# ---------------------------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------------------------
$script:StartedUtc = (Get-Date).ToUniversalTime().ToString('o')
New-Item -ItemType Directory -Path $EvidenceDir -Force | Out-Null
$script:EvidenceDir = (Resolve-Path -LiteralPath $EvidenceDir).Path
$primary = $null
$silent = $null
$suspended = $false

Write-Section ('single-instance stress (NON-GATING, gating=false) : ' + $script:EvidenceDir)
Write-Host ('  exe        : ' + $script:ExeFullPath)
Write-Host ('  mutex      : ' + $script:MutexName)
Write-Host ('  pipe       : ' + $script:PipeName)
Write-Host ('  connectWin : ' + $ConnectWindowMs + ' ms (expected)')

try {
    # ---- 0) clean state ----
    Clear-State
    Remove-Item -LiteralPath $script:LogPath -Force -ErrorAction SilentlyContinue
    $dirty = (Get-PowerDockProcesses).Count
    Add-Check 'environment clean before probe (no PowerDock process)' ($dirty -eq 0) ('processes=' + $dirty)

    # ---- 1) primary up + ready ----
    $primary = Start-Process -FilePath $script:ExeFullPath -PassThru
    $ready = Wait-LogMatch 'Primary instance ready \(pipe listening\)' 20000
    Add-Check 'primary reached pipe-listening state' $ready ('pid=' + $primary.Id)
    if (-not $ready) {
        Add-Check 'probe aborted: primary never reported ready' $false 'cannot construct the busy window' $true
        throw ([System.OperationCanceledException]::new('primary not ready'))
    }

    # ---- 2) consume the single listening instance with a silent client ----
    $silent = New-Object System.IO.Pipes.NamedPipeClientStream('.', $script:PipeName, [System.IO.Pipes.PipeDirection]::InOut)
    $silent.Connect(2000)
    Add-Check 'silent client consumed the listening pipe instance' $true 'connected, sends nothing'

    # ---- 3) freeze the primary BEFORE it drops the silent client (<= ~2500ms) ----
    if ($SuspendLeadMs -gt 0) { Start-Sleep -Milliseconds $SuspendLeadMs }
    $win32 = 0
    $nt = [QaSuspend]::Suspend($primary.Id, [ref]$win32)
    if ($nt -ne 0) {
        Add-Check ('NtSuspendProcess failed (nt=0x{0:X}, win32={1}) - cannot construct the busy window' -f $nt, $win32) $false '' $true
        throw ([System.InvalidOperationException]::new('suspend failed'))
    }
    $suspended = $true
    Add-Check 'primary suspended (listener thread frozen)' $true ('pid=' + $primary.Id)

    # ---- 4) secondary must exhaust the connect window and report 21 (never 20) ----
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $secondary = Start-Process -FilePath $script:ExeFullPath -ArgumentList @('--no-feedback') -PassThru
    $exited = $secondary.WaitForExit(30000)
    $sw.Stop()
    $exitCode = if ($exited) { $secondary.ExitCode } else { -1 }
    Add-Check 'secondary exited (no hang)' $exited ('ms=' + [int]$sw.ElapsedMilliseconds)
    Add-Check ('exit = PrimaryNotReady(21)') ($exitCode -eq $script:ExpectedPrimaryNotReady) ('exit=' + $exitCode)
    Add-Check ('NOT NoPrimary(20): the primary exists, it is only frozen') ($exitCode -ne $script:ExpectedNoPrimary) ('exit=' + $exitCode)
    Add-Check 'connect wait respected the widened window' ($sw.ElapsedMilliseconds -ge [Math]::Max(1000, $ConnectWindowMs - 1500)) ('ms=' + [int]$sw.ElapsedMilliseconds)

    $logText = Get-LogText
    Add-Check 'log carries [case=PRIMARY_BUSY]' ($logText -match 'case=PRIMARY_BUSY') ''
    Add-Check 'log does NOT carry [case=NO_PRIMARY]' (-not ($logText -match 'case=NO_PRIMARY')) ''

    if (-not $exited) { try { $secondary | Stop-Process -Force -ErrorAction SilentlyContinue } catch { } }
}
catch {
    if ($_.Exception -isnot [System.OperationCanceledException] -and $_.Exception -isnot [System.InvalidOperationException]) {
        Add-Check 'unexpected probe error' $false ($_.Exception.Message) $true
    }
}
finally {
    # ---- ALWAYS resume, even on error / Ctrl+C. A suspended primary keeps the mutex and would
    #      make every later second start report 20/21 until resumed or killed. ----
    if ($suspended -and $null -ne $primary) {
        try {
            $win32 = 0
            $nt = [QaSuspend]::Resume($primary.Id, [ref]$win32)
            if ($nt -eq 0) {
                Add-Check 'primary resumed in finally' $true ('pid=' + $primary.Id)
            }
            else {
                Add-Check ('RESUME FAILED (nt=0x{0:X}, win32={1}) - killing the primary instead' -f $nt, $win32) $false '' $true
            }
        }
        catch {
            Add-Check ('RESUME threw: ' + $_.Exception.Message + ' - killing the primary instead') $false '' $true
        }
        Start-Sleep -Milliseconds 300
        # Safety net: never leave a suspended process behind. Killing a suspended process is safe.
        try {
            $stillThere = Get-Process -Id $primary.Id -ErrorAction SilentlyContinue
            if ($stillThere) {
                $primary.Refresh()
                Add-Check 'primary alive after resume' (-not $primary.HasExited) ('hasExited=' + $primary.HasExited)
            }
        }
        catch { }
    }
    if ($null -ne $silent) { try { $silent.Dispose() } catch { } }
    Clear-State
    $leftProcs = (Get-PowerDockProcesses).Count
    $leftPipes = Get-PipePowerDockNames
    Add-Check 'no PowerDock process left' ($leftProcs -eq 0) ('processes=' + $leftProcs)
    Add-Check 'no PowerDock pipe residue' (@($leftPipes | Where-Object { $_ -ne '__ENUM_FAILED__' }).Count -eq 0) (($leftPipes -join ','))

    # ---- report (gating=false) ----
    $failed = @($script:Checks | Where-Object { $_.status -eq 'fail' })
    $inconclusive = @($script:Checks | Where-Object { $_.status -eq 'inconclusive' })
    $report = [ordered]@{
        gating           = $false
        note             = 'NON-GATING stress probe. A red result is NOT a product failure; the authoritative gate is tools/verify-single-instance.ps1.'
        startedUtc       = $script:StartedUtc
        finishedUtc      = (Get-Date).ToUniversalTime().ToString('o')
        exe              = $script:ExeFullPath
        exeSha256        = (Get-FileHash -LiteralPath $script:ExeFullPath -Algorithm SHA256).Hash
        evidenceDir      = $script:EvidenceDir
        exitCodes        = [ordered]@{ NoPrimary = 20; PrimaryNotReady = 21 }
        connectWindowMs  = $ConnectWindowMs
        summary          = [ordered]@{ total = $script:Checks.Count; failed = $failed.Count; inconclusive = $inconclusive.Count }
        checks           = @($script:Checks)
    }
    $jsonPath = Join-Path $script:EvidenceDir 'verify-single-instance-stress.json'
    Set-Content -LiteralPath $jsonPath -Value ($report | ConvertTo-Json -Depth 8) -Encoding UTF8
    Write-Section 'RESULT (gating=false)'
    Write-Host ('  checks=' + $script:Checks.Count + ' failed=' + $failed.Count + ' inconclusive=' + $inconclusive.Count)
    Write-Host ('  json=' + $jsonPath)
    if ($failed.Count -gt 0) { exit 1 } else { exit 0 }
}
