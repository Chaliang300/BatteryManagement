#requires -Version 5.1
<#
.SYNOPSIS
    Acceptance gate for PowerDock single-instance behaviour (PR-S0-03 main deliverable).

.DESCRIPTION
    Runs real multi-process checks (never in-process simulation) against a PowerDock.exe that
    MUST live outside the repository working tree (AGENTS.md section 9: executables inside the
    workspace are sandbox-downgraded; Shell/tray/focus results there are invalid).

    For every case the script records:
      - process snapshot (Get-CimInstance Win32_Process / Get-Process)
      - exit codes (not just "exited 0")
      - window handle / visibility / foreground window
      - app log slice with millisecond UTC timestamps and ordering assertions
      - raw probe output and (for the foreground case) taskbar flash frames

    Output: <EvidenceDir>\verify-single-instance.json plus per-case artifacts.
    Exit code: 0 only when every case passes; 1 when any assertion fails; 2 on harness error
    or when a precondition fails (interactive desktop / foreground capability unavailable).
    A locked or disconnected session can still create windows and register tray icons but cannot
    set the foreground window; in that state the harness reports "environment invalid" and exits 2
    instead of blaming the product.

    EXIT CODE CONTRACT (PR-S0-03 must implement and document in --help):
        0  = success: first instance is running; a secondary instance activated it
        20 = no primary instance: mutex held but no live pipe server / connect timeout
        21 = primary not ready: pipe not listening within the retry budget
        22 = foreground activation denied: window flashed, user-visible feedback required
        23 = pipe client identity rejected: client SID != current user SID
        24 = protocol/response timeout: bounded response not received
    The zero-instance case asserts "secondary MUST NOT exit 0 silently": expected 20 (or 21/24).
    The foreground-denied case expects 22 and requires both the deny log line and observed
    taskbar flash frames (mean pixel delta over threshold).

    SID-CHECK DESIGN (PR-S0-03 implementation - do not relax to name matching):
        Server side, after EndWaitForConnection:
          GetNamedPipeClientProcessId(server.SafePipeHandle, out clientPid)
          hProc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, clientPid)
          hTok  = OpenProcessToken(hProc, TOKEN_QUERY)
          GetTokenInformation(hTok, TokenUser, ...) -> TOKEN_USER.User.Sid
          ConvertSidToStringSidW(Sid) -> clientSid
          currentSid = WindowsIdentity.GetCurrent().User.Value
          if (clientSid != currentSid) { log reason; reply DENIED; return; }
        Every step failure = reject + log the concrete reason. Never allow on exception
        (the current :299-301 catch-all is the defect this replaces).
        Keep the pipe DACL (PipeSecurity, current user ReadWrite) as defence in depth,
        and log when PipeSecurity creation falls back to the default descriptor.

    LOG MARKERS REQUIRED BY THIS SCRIPT (PR-S0-03 keeps or script is updated with it):
        "Acquired single-instance mutex as primary instance: <mutexName>"
        "Mutex is already held by primary instance. Notifying primary instance via pipe: <pipeName>"
        "Received activate request from client PID: <pid>"
        "Primary instance response: <result>"
        "Window brought to foreground successfully."
        "SetForegroundWindow denied by system" (deny path)
        "Foreground authorization: AllowSetForegroundWindow(pid) returned <True|False>, win32=<code>"
        "Foreground activation: SetForegroundWindow returned <True|False>"
    The two foreground lines MUST be emitted separately so that "authorization failed"
    (ASFW returned False / granted nothing) is distinguishable from "target did not respond"
    (ASFW returned True but the primary's SetForegroundWindow returned False).
        plus a readable failure reason for the zero-instance path.

.PARAMETER ExePath
    Full path to the PowerDock.exe under test. Must not be under the repository root.

.PARAMETER ExpectedInstances
    Expected number of surviving PowerDock processes for normal cases (default 1).

.PARAMETER Concurrent
    Number of concurrent secondary launches (default 10).

.PARAMETER EvidenceDir
    Artifact directory. Defaults to %TEMP%\pd-verify-<timestamp>.

.PARAMETER TimeoutMs
    Exit/connection budget in ms (default 1500). Zero-instance wait uses max(8000, 4x).

.PARAMETER SkipForegroundDenied
    Skip case 5 on non-interactive desktops (the case needs an interactive desktop).

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File tools\verify-single-instance.ps1 `
        -ExePath "$env:LOCALAPPDATA\Programs\PowerDock-qa\<commit>\PowerDock.exe"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ExePath,
    [int]$ExpectedInstances = 1,
    [int]$Concurrent = 10,
    [string]$EvidenceDir = (Join-Path $env:TEMP ("pd-verify-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))),
    [int]$TimeoutMs = 1500,
    [switch]$SkipForegroundDenied,
    [switch]$KeepRunningOnSuccess
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

# Exit-code contract (mirrored in the header block above).
$script:ExitCodes = @{
    Success            = 0
    NoPrimary          = 20
    PrimaryNotReady    = 21
    ForegroundDenied   = 22
    IdentityRejected   = 23
    ProtocolTimeout    = 24
}

$script:RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path.TrimEnd('\')
$script:Csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$script:Failures = 0
$script:Cases = New-Object System.Collections.ArrayList
$script:ProbeExe = $null
$script:ForegroundCapable = $false
$script:LogPath = $null
$script:PowerDockProcessName = 'PowerDock'

function Write-Section([string]$text) {
    Write-Host ''
    Write-Host ('===== ' + $text + ' =====') -ForegroundColor Cyan
}

function Get-CommitHash {
    try {
        $out = & git -C $script:RepoRoot rev-parse --short HEAD 2>$null
        if ($null -ne $out) { return ([string]$out).Trim() }
    }
    catch { }
    return "unknown"
}

function Get-UtcStamp {
    return (Get-Date).ToUniversalTime().ToString('o')
}

function Get-PipePowerDockNames {
    $result = @()
    try {
        $all = [System.IO.Directory]::GetFiles('\\.\pipe\')
        foreach ($p in $all) {
            $leaf = [System.IO.Path]::GetFileName($p)
            if ($leaf -like '*PowerDock*') { $result += $leaf }
        }
    }
    catch {
        # Pipe enumeration can fail on restricted tokens; treat as "unknown" and let the
        # dedicated pipe assertions report the reason.
        return @('__ENUM_FAILED__')
    }
    return $result
}

function Get-PowerDockProcesses {
    return @(Get-Process -Name $script:PowerDockProcessName -ErrorAction SilentlyContinue)
}

function Get-ProcessSnapshot([string]$name) {
    $snap = New-Object System.Collections.ArrayList
    try {
        $rows = @(Get-CimInstance Win32_Process -Filter ("Name='" + $name + ".exe'") -ErrorAction SilentlyContinue)
        foreach ($r in $rows) {
            [void]$snap.Add([ordered]@{
                pid         = [int]$r.ProcessId
                executable  = [string]$r.ExecutablePath
                commandLine = [string]$r.CommandLine
                createdUtc  = ([datetime]$r.CreationDate).ToUniversalTime().ToString('o')
            })
        }
    }
    catch {
        foreach ($p in @(Get-Process -Name $name -ErrorAction SilentlyContinue)) {
            [void]$snap.Add([ordered]@{ pid = $p.Id; executable = $null; commandLine = $null; createdUtc = $null })
        }
    }
    return @($snap)
}

function Wait-Until([scriptblock]$condition, [int]$timeoutMs, [int]$intervalMs = 40) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $timeoutMs) {
        if (& $condition) { return $true }
        Start-Sleep -Milliseconds $intervalMs
    }
    return [bool](& $condition)
}

function Wait-WindowHandle([int]$targetPid, [int]$timeoutMs) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $timeoutMs) {
        $p = Get-Process -Id $targetPid -ErrorAction SilentlyContinue
        if ($null -ne $p -and $p.MainWindowHandle -ne [IntPtr]::Zero) {
            return [int]$sw.ElapsedMilliseconds
        }
        Start-Sleep -Milliseconds 25
    }
    return -1
}

function Clear-PowerDockState {
    # Protocol from the Team Lead: clean before and after every process-level verification.
    $deadline = (Get-Date).AddSeconds(5)
    while (@(Get-PowerDockProcesses).Count -gt 0 -and (Get-Date) -lt $deadline) {
        Get-Process -Name $script:PowerDockProcessName -ErrorAction SilentlyContinue |
            Stop-Process -Force -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 150
    }
    Get-Process -Name 'PdVerifyProbe' -ErrorAction SilentlyContinue |
        Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 200
}

function Invoke-Probe {
    param([string[]]$ProbeArgs, [string]$OutputFile, [switch]$Background)
    $argList = @()
    foreach ($a in $ProbeArgs) {
        if ($a -match '\s') { $argList += ('"' + $a + '"') } else { $argList += $a }
    }
    if ($OutputFile) {
        $p = Start-Process -FilePath $script:ProbeExe -ArgumentList $argList -NoNewWindow -PassThru `
            -RedirectStandardOutput $OutputFile -ErrorAction Stop
    }
    else {
        $p = Start-Process -FilePath $script:ProbeExe -ArgumentList $argList -NoNewWindow -PassThru `
            -RedirectStandardOutput ($script:EvidenceDir + '\probe-last.txt') -ErrorAction Stop
        $OutputFile = $script:EvidenceDir + '\probe-last.txt'
    }
    if ($Background) {
        return @{ Process = $p; OutputFile = $OutputFile }
    }
    [void]$p.WaitForExit(20000)
    $p.Refresh()
    return @{ Process = $p; OutputFile = $OutputFile; ExitCode = $p.ExitCode }
}

function Read-ProbeValues([string]$path) {
    $map = @{}
    if (Test-Path -LiteralPath $path) {
        foreach ($line in (Get-Content -LiteralPath $path -Encoding UTF8 -ErrorAction SilentlyContinue)) {
            $m = [regex]::Match($line, '^([A-Za-z0-9_]+)=(.*)$')
            if ($m.Success) { $map[$m.Groups[1].Value] = $m.Groups[2].Value }
        }
    }
    return $map
}

function Get-AppLogLines {
    if ($null -eq $script:LogPath -or -not (Test-Path -LiteralPath $script:LogPath)) { return @() }
    return @(Get-Content -LiteralPath $script:LogPath -Encoding UTF8 -ErrorAction SilentlyContinue)
}

function Get-LogSlice([int]$startIndex) {
    $lines = Get-AppLogLines
    if ($startIndex -ge $lines.Count) { return @() }
    return @($lines[$startIndex..($lines.Count - 1)])
}

function Find-LogIndex([string[]]$lines, [string]$pattern) {
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match $pattern) { return $i }
    }
    return -1
}

function Count-LogMatches([string[]]$lines, [string]$pattern) {
    $n = 0
    foreach ($l in $lines) { if ($l -match $pattern) { $n++ } }
    return $n
}

function New-Case([string]$id, [string]$name) {
    $case = [ordered]@{
        id          = $id
        name        = $name
        startedUtc  = (Get-UtcStamp)
        status      = 'fail'
        assertions  = New-Object System.Collections.ArrayList
        evidence    = [ordered]@{}
        error       = $null
    }
    return $case
}

function Add-Check {
    param($Case, [string]$Name, [bool]$Pass, [string]$Detail)
    [void]$Case.assertions.Add([ordered]@{ name = $Name; pass = [bool]$Pass; detail = $Detail })
    if (-not $Pass) {
        Write-Host ('  [FAIL] ' + $Name + ' :: ' + $Detail) -ForegroundColor Red
        $script:Failures++
    }
    else {
        Write-Host ('  [ ok ] ' + $Name) -ForegroundColor Green
    }
}

function Complete-Case($Case, [string]$CaseDir) {
    $failed = @($Case.assertions | Where-Object { -not $_.pass })
    $Case.status = if ($failed.Count -eq 0) { 'pass' } else { 'fail' }
    $Case.finishedUtc = (Get-UtcStamp)
    [void]$script:Cases.Add($Case)
    $json = $Case | ConvertTo-Json -Depth 12
    Set-Content -LiteralPath (Join-Path $CaseDir 'case.json') -Value $json -Encoding UTF8
    Write-Host ('  -> ' + $Case.id + ': ' + $Case.status.ToUpperInvariant())
}

function Save-Artifact([string]$CaseDir, [string]$name, [object]$content) {
    $path = Join-Path $CaseDir $name
    $text = if ($content -is [string]) { $content } else { $content | ConvertTo-Json -Depth 12 }
    Set-Content -LiteralPath $path -Value $text -Encoding UTF8
    return $path
}

function Assert-ExeOutsideWorkspace([string]$fullPath) {
    $sep = [System.IO.Path]::DirectorySeparatorChar
    $prefix = $script:RepoRoot + $sep
    if ($fullPath.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw ("ExePath is inside the repository workspace (" + $fullPath + "). " +
               "AGENTS.md section 9: executables inside the workspace are sandbox-downgraded; " +
               "copy/build the exe to %LOCALAPPDATA%\Programs\PowerDock-qa\<commit>\ or %TEMP% first.")
    }
}

function Compile-Probe {
    $src = Join-Path $script:EvidenceDir 'PdVerifyProbe.cs'
    $exe = Join-Path $script:EvidenceDir 'PdVerifyProbe.exe'
    $probeSource = @'
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

// Verification-only helper. Never shipped with the product.
// Modes: state | hide | show | foreground | hold-mutex <name> <seconds> |
//        lock-foreground <seconds> | flash-watch <outDir> <seconds> <intervalMs>
public static class PdVerifyProbe
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr FindWindow(string cls, string title);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint a, uint b, bool attach);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool LockSetForegroundWindow(uint lockCode);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();

    static void P(string key, object value) { Console.WriteLine(key + "=" + value); }

    [STAThread]
    public static int Main(string[] args)
    {
        try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { }
        if (args.Length == 0) { Console.WriteLine("usage: probe <mode>"); return 2; }
        string mode = args[0].ToLowerInvariant();
        if (mode == "state") return State();
        if (mode == "hide" || mode == "show") return ShowHide(mode == "show");
        if (mode == "foreground") return Foreground();
        if (mode == "hold-mutex") return HoldMutex(args);
        if (mode == "lock-foreground") return LockForeground(args);
        if (mode == "flash-watch") return FlashWatch(args);
        if (mode == "fg-capable") return ForegroundCapable();
        Console.WriteLine("unknown mode");
        return 2;
    }

    static IntPtr FindPowerDock()
    {
        IntPtr h = FindWindow(null, "PowerDock");
        return h;
    }

    static int State()
    {
        IntPtr h = FindPowerDock();
        P("FOUND", h != IntPtr.Zero);
        if (h == IntPtr.Zero)
        {
            P("PROCESS_COUNT", Process.GetProcessesByName("PowerDock").Length);
            return 3;
        }
        uint pid; GetWindowThreadProcessId(h, out pid);
        RECT r; GetWindowRect(h, out r);
        uint dpi = GetDpiForWindow(h);
        P("HWND", "0x" + h.ToInt64().ToString("X"));
        P("PID", pid);
        P("VISIBLE", IsWindowVisible(h));
        P("DPI", dpi);
        P("WINDOW_PX", (r.Right - r.Left) + "x" + (r.Bottom - r.Top));
        P("WINDOW_DIP", ((r.Right - r.Left) * 96.0 / dpi).ToString("F1") + "x" + ((r.Bottom - r.Top) * 96.0 / dpi).ToString("F1"));
        return 0;
    }

    static int ShowHide(bool show)
    {
        IntPtr h = FindPowerDock();
        P("FOUND", h != IntPtr.Zero);
        if (h == IntPtr.Zero) return 3;
        ShowWindow(h, show ? 5 : 0);
        Thread.Sleep(400);
        P("VISIBLE", IsWindowVisible(h));
        return 0;
    }

    static int Foreground()
    {
        IntPtr h = GetForegroundWindow();
        uint pid; GetWindowThreadProcessId(h, out pid);
        var sb = new StringBuilder(512); GetWindowText(h, sb, 512);
        P("FOREGROUND_HWND", "0x" + h.ToInt64().ToString("X"));
        P("FOREGROUND_PID", pid);
        P("FOREGROUND_TITLE", sb.ToString());
        try { P("FOREGROUND_PROCESS", Process.GetProcessById((int)pid).ProcessName); } catch { P("FOREGROUND_PROCESS", "unknown"); }
        return 0;
    }

    static int HoldMutex(string[] args)
    {
        if (args.Length < 3) { Console.WriteLine("usage: hold-mutex <name> <seconds>"); return 2; }
        string name = args[1];
        int seconds = int.Parse(args[2]);
        bool created;
        Mutex m = new Mutex(true, name, out created);
        P("HELD_MUTEX_NAME", name);
        P("CREATED_NEW", created);
        Console.Out.Flush();
        Thread.Sleep(seconds * 1000);
        try { m.ReleaseMutex(); } catch { }
        m.Dispose();
        return 0;
    }

    static int LockForeground(string[] args)
    {
        int seconds = args.Length > 1 ? int.Parse(args[1]) : 20;
        Form form = new Form();
        form.Text = "PD-FG-LOCK";
        form.FormBorderStyle = FormBorderStyle.FixedToolWindow;
        form.StartPosition = FormStartPosition.Manual;
        form.Bounds = new Rectangle(40, 40, 320, 160);
        form.TopMost = true;
        form.Show();
        Application.DoEvents();
        IntPtr fg = GetForegroundWindow();
        uint fgPid; uint fgThread = GetWindowThreadProcessId(fg, out fgPid);
        uint myThread = GetCurrentThreadId();
        AttachThreadInput(myThread, fgThread, true);
        bool setOk = SetForegroundWindow(form.Handle);
        AttachThreadInput(myThread, fgThread, false);
        bool lockOk = LockSetForegroundWindow(1);
        P("LOCK_WINDOW_HWND", "0x" + form.Handle.ToInt64().ToString("X"));
        P("SET_FOREGROUND_SELF_OK", setOk);
        P("LOCK_SET_FOREGROUND_WINDOW_OK", lockOk);
        P("FOREGROUND_IS_LOCK_WINDOW", GetForegroundWindow() == form.Handle);
        Console.Out.Flush();
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < seconds) { Application.DoEvents(); Thread.Sleep(50); }
        form.Close();
        return 0;
    }

    static int ForegroundCapable()
    {
        Form form = new Form();
        form.Text = "PD-FG-CAPABLE";
        form.FormBorderStyle = FormBorderStyle.FixedToolWindow;
        form.StartPosition = FormStartPosition.Manual;
        form.Bounds = new Rectangle(600, 300, 240, 120);
        form.TopMost = true;
        form.Show();
        Application.DoEvents();
        IntPtr fg = GetForegroundWindow();
        uint fgPid; uint fgThread = GetWindowThreadProcessId(fg, out fgPid);
        uint myThread = GetCurrentThreadId();
        AttachThreadInput(myThread, fgThread, true);
        bool setOk = SetForegroundWindow(form.Handle);
        AttachThreadInput(myThread, fgThread, false);
        Application.DoEvents();
        System.Threading.Thread.Sleep(400);
        P("FG_SET_OK", setOk);
        P("FG_CAPABLE", GetForegroundWindow() == form.Handle);
        form.Close();
        return 0;
    }

    static int FlashWatch(string[] args)
    {
        string outDir = args.Length > 1 ? args[1] : Path.GetTempPath();
        double seconds = args.Length > 2 ? double.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) : 4.0;
        int interval = args.Length > 3 ? int.Parse(args[3]) : 100;
        Directory.CreateDirectory(outDir);
        Rectangle vs = SystemInformation.VirtualScreen;
        int stripH = 90;
        Rectangle strip = new Rectangle(vs.Left, vs.Bottom - stripH, vs.Width, stripH);
        Bitmap prev = Capture(strip);
        int frames = 0, changed = 0;
        double maxMean = 0;
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            Thread.Sleep(interval);
            using (Bitmap cur = Capture(strip))
            {
                long diff = 0; long n = 0; int maxd = 0;
                for (int y = 0; y < strip.Height; y += 4)
                {
                    for (int x = 0; x < strip.Width; x += 4)
                    {
                        Color a = cur.GetPixel(x, y); Color b = prev.GetPixel(x, y);
                        int d = Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);
                        diff += d; n++;
                        if (d > maxd) maxd = d;
                    }
                }
                double mean = diff / (double)n;
                if (mean > 1.0 || maxd > 60)
                {
                    changed++;
                    cur.Save(Path.Combine(outDir, "frame-" + frames.ToString("D3") + ".png"), ImageFormat.Png);
                }
                if (mean > maxMean) maxMean = mean;
                Console.WriteLine("FLASH_FRAME=" + frames + " t=" + sw.ElapsedMilliseconds + " mean=" + mean.ToString("F2") + " max=" + maxd);
                prev.Dispose();
                prev = (Bitmap)cur.Clone();
            }
            frames++;
        }
        prev.Dispose();
        P("FLASH_FRAMES", frames);
        P("FLASH_CHANGED_FRAMES", changed);
        P("FLASH_MAX_MEAN_DIFF", maxMean.ToString("F2"));
        return 0;
    }

    static Bitmap Capture(Rectangle rect)
    {
        Bitmap bmp = new Bitmap(rect.Width, rect.Height, PixelFormat.Format24bppRgb);
        using (Graphics g = Graphics.FromImage(bmp)) g.CopyFromScreen(rect.X, rect.Y, 0, 0, rect.Size);
        return bmp;
    }
}
'@
    Set-Content -LiteralPath $src -Value $probeSource -Encoding UTF8
    if (-not (Test-Path -LiteralPath $script:Csc)) {
        throw ("C# compiler not found: " + $script:Csc + ". Cannot build the verification probe.")
    }
    $refs = @(
        '/nologo', '/target:exe', '/platform:x64', '/optimize+', '/utf8output', '/codepage:65001',
        ('/out:' + $exe),
        '/reference:System.dll',
        '/reference:System.Core.dll',
        '/reference:System.Drawing.dll',
        '/reference:System.Windows.Forms.dll',
        $src
    )
    $proc = Start-Process -FilePath $script:Csc -ArgumentList $refs -NoNewWindow -Wait -PassThru
    if ($proc.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $exe)) {
        throw ("Verification probe compilation failed with exit code " + $proc.ExitCode)
    }
    $script:ProbeExe = $exe
}

function New-CaseDir([string]$caseId) {
    $dir = Join-Path $script:EvidenceDir $caseId
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    return $dir
}

function Start-Primary([string[]]$ExtraArgs) {
    $startArgs = @{ FilePath = $script:ExeFullPath; PassThru = $true }
    if ($null -ne $ExtraArgs -and $ExtraArgs.Count -gt 0) {
        $startArgs['ArgumentList'] = $ExtraArgs
    }
    $p = Start-Process @startArgs
    $ms = Wait-WindowHandle -targetPid $p.Id -timeoutMs 10000
    return @{ Process = $p; WindowMs = $ms }
}

function Stop-App {
    Get-Process -Name $script:PowerDockProcessName -ErrorAction SilentlyContinue |
        Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 250
}

function Test-CleanState {
    $procCount = @(Get-PowerDockProcesses).Count
    $pipes = @(Get-PipePowerDockNames)
    $pipeEnumerable = -not ($pipes.Count -eq 1 -and $pipes[0] -eq '__ENUM_FAILED__')
    $clean = ($procCount -eq 0) -and ((-not $pipeEnumerable) -or ($pipes.Count -eq 0))
    return @{ Clean = $clean; ProcessCount = $procCount; Pipes = $pipes; PipeEnumerable = $pipeEnumerable }
}

function Assert-CleanState($Case, [string]$Context) {
    $count = @(Get-PowerDockProcesses).Count
    Add-Check -Case $Case -Name ($Context + ': no PowerDock process before case') -Pass ($count -eq 0) -Detail ('processes=' + $count)
    $pipes = @(Get-PipePowerDockNames)
    $enumerable = -not ($pipes.Count -eq 1 -and $pipes[0] -eq '__ENUM_FAILED__')
    Add-Check -Case $Case -Name ($Context + ': no PowerDock pipe before case') -Pass ((-not $enumerable) -or ($pipes.Count -eq 0)) -Detail (($pipes -join ','))
}

# --------------------------- main ---------------------------
$script:StartedUtc = Get-UtcStamp
try {
    New-Item -ItemType Directory -Path $EvidenceDir -Force | Out-Null
    $EvidenceDir = (Resolve-Path -LiteralPath $EvidenceDir).Path
    $script:EvidenceDir = $EvidenceDir
    Write-Section ('PowerDock single-instance acceptance: ' + $EvidenceDir)

    # ---- preflight ----
    $script:ExeFullPath = (Resolve-Path -LiteralPath $ExePath -ErrorAction Stop).Path
    Assert-ExeOutsideWorkspace -fullPath $script:ExeFullPath
    $script:LogPath = Join-Path (Split-Path -Parent $script:ExeFullPath) 'powerdock.log'
    $sha = (Get-FileHash -LiteralPath $script:ExeFullPath -Algorithm SHA256).Hash
    $sessionId = (Get-Process -Id $PID).SessionId
    Clear-PowerDockState
    if (@(Get-PowerDockProcesses).Count -ne 0) {
        throw 'Preflight failed: could not terminate pre-existing PowerDock processes.'
    }
    Compile-Probe
    $envInfo = [ordered]@{
        startedUtc     = $script:StartedUtc
        scriptVersion  = '1.0'
        commit         = (Get-CommitHash)
        exe            = $script:ExeFullPath
        exeSha256      = $sha
        sessionId      = $sessionId
        repoRoot       = $script:RepoRoot
        powershell     = $PSVersionTable.PSVersion.ToString()
        os             = (Get-CimInstance Win32_OperatingSystem | Select-Object Caption, BuildNumber, Version)
        user           = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
        currentUserSid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
        expectedExitCodes = $script:ExitCodes
        mutexNameHint  = ('Local\PowerDock_Mutex_' + $sessionId)
        pipeNameHint   = ('PowerDock_Pipe_' + $sessionId)
    }
    Save-Artifact -CaseDir $EvidenceDir -name 'environment.json' -content $envInfo | Out-Null
    Add-Content -LiteralPath (Join-Path $EvidenceDir 'environment.txt') -Value (
        "EXE=$($script:ExeFullPath)`r`nSHA256=$sha`r`nSESSION=$sessionId`r`nCOMMIT=$($envInfo.commit)`r`nUSER=$($envInfo.user)`r`nSID=$($envInfo.currentUserSid)"
    )
    Write-Host ('Exe: ' + $script:ExeFullPath)
    Write-Host ('SHA256: ' + $sha)
    Write-Host ('DPR session: ' + $sessionId) -ErrorAction SilentlyContinue

    # Discover instance names from a real primary log (keeps the harness working after
    # PR-S0-03 renames the per-user mutex/pipe: names are read, not hard-coded).
    $mutexName = 'Local\PowerDock_Mutex_' + $sessionId
    $pipeName = 'PowerDock_Pipe_' + $sessionId
    $probe = Invoke-Probe -ProbeArgs @('state') -OutputFile (Join-Path $EvidenceDir 'preflight-state.txt')
    Start-Sleep -Milliseconds 1500
    $preClean = Test-CleanState
    $p1 = Start-Primary -ExtraArgs @()
    $discoveryCase = New-Case 'C0' 'name discovery / preflight'
    $discoveryDir = New-CaseDir 'C0'
    Add-Check -Case $discoveryCase -Name 'preflight: no foreign PowerDock instance appears during 1.5s stability window' -Pass $preClean.Clean -Detail ('processes=' + $preClean.ProcessCount + ' pipes=' + ($preClean.Pipes -join ','))
    Add-Check -Case $discoveryCase -Name 'primary window appears' -Pass ($p1.WindowMs -ge 0) -Detail ('windowMs=' + $p1.WindowMs)
    Start-Sleep -Milliseconds 500
    $allLog = Get-AppLogLines
    foreach ($line in $allLog) {
        $m = [regex]::Match($line, 'Acquired single-instance mutex as primary instance:\s*(.+)$')
        if ($m.Success) { $mutexName = $m.Groups[1].Value.Trim() }
        $m2 = [regex]::Match($line, 'Notifying primary instance via pipe:\s*(.+)$')
        if ($m2.Success) { $pipeName = $m2.Groups[1].Value.Trim() }
    }
    $discoveryCase.evidence['mutexName'] = $mutexName
    $discoveryCase.evidence['pipeName'] = $pipeName
    Add-Check -Case $discoveryCase -Name 'mutex name discovered from log' -Pass ($mutexName -ne '') -Detail $mutexName
    Add-Check -Case $discoveryCase -Name 'pipe name hint' -Pass ($pipeName -ne '') -Detail $pipeName
    Complete-Case -Case $discoveryCase -CaseDir $discoveryDir
    Stop-App

    # Precondition: this gate needs an interactive desktop (foreground capability). A locked or
    # disconnected session can create windows but cannot set the foreground window; in that state
    # report "environment invalid" (exit 2) instead of blaming the product.
    $fgCapFile = Join-Path $EvidenceDir 'preflight-fg-capable.txt'
    [void](Invoke-Probe -ProbeArgs @('fg-capable') -OutputFile $fgCapFile)
    $fgCapVals = Read-ProbeValues -path $fgCapFile
    $script:ForegroundCapable = ($fgCapVals['FG_CAPABLE'] -eq 'True')
    Write-Host ('foreground capability: ' + $script:ForegroundCapable + ' (FG_SET_OK=' + $fgCapVals['FG_SET_OK'] + ')')
    if (-not $script:ForegroundCapable) {
        $envInvalid = [ordered]@{
            timestampUtc = (Get-UtcStamp)
            status = 'environment-invalid'
            reason = 'interactive foreground capability unavailable (locked/disconnected session or non-interactive desktop)'
            foregroundCapable = $false
            exe = $script:ExeFullPath
            exeSha256 = $sha
            sessionId = $sessionId
            hint = 'Run this gate in an interactive Session-1 desktop; do not disable the lock screen.'
        }
        Set-Content -LiteralPath (Join-Path $EvidenceDir 'verify-single-instance.json') -Value ($envInvalid | ConvertTo-Json -Depth 6) -Encoding UTF8
        Write-Host 'ENVIRONMENT INVALID: cannot acquire the foreground window; no product verdict is produced.' -ForegroundColor Yellow
        Write-Host ('evidence: ' + (Join-Path $EvidenceDir 'verify-single-instance.json'))
        exit 2
    }

    # ---- C1 regular second start ----
    Write-Section 'C1 regular second start'
    $case = New-Case 'C1' 'regular second start (visible primary)'
    $caseDir = New-CaseDir 'C1'
    Clear-PowerDockState
    Assert-CleanState -Case $case -Context 'C1'
    $logStart = (Get-AppLogLines).Count
    $primary = Start-Primary -ExtraArgs @()
    Add-Check -Case $case -Name 'primary window appears' -Pass ($primary.WindowMs -ge 0) -Detail ('windowMs=' + $primary.WindowMs)
    Start-Sleep -Milliseconds 400
    $state = Read-ProbeValues -path (Invoke-Probe -ProbeArgs @('state') -OutputFile (Join-Path $caseDir 'state-primary.txt')).OutputFile
    $hwnd = $state['HWND']
    $case.evidence['primaryPid'] = $primary.Process.Id
    $case.evidence['hwnd'] = $hwnd
    Save-Artifact -CaseDir $caseDir -name 'processes-before.json' -content (Get-ProcessSnapshot 'PowerDock') | Out-Null
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $sec = Start-Process -FilePath $script:ExeFullPath -ArgumentList @("--no-feedback") -PassThru
    $exited = $sec.WaitForExit([Math]::Max(2000, $TimeoutMs * 2))
    $sw.Stop()
    if ($exited) { $sec.Refresh() }
    $actualExit = if ($exited) { $sec.ExitCode } else { -1 }
    $case.evidence['secondaryPid'] = $sec.Id
    $case.evidence['secondaryExit'] = $actualExit
    $case.evidence['secondaryElapsedMs'] = [int]$sw.ElapsedMilliseconds
    Add-Check -Case $case -Name 'secondary exits within budget' -Pass ($exited -and $sw.ElapsedMilliseconds -le [Math]::Max(2000, $TimeoutMs * 2)) -Detail ('exited=' + $exited + ' ms=' + [int]$sw.ElapsedMilliseconds)
    Add-Check -Case $case -Name 'secondary exit code is 0 (success)' -Pass ($actualExit -eq $script:ExitCodes.Success) -Detail ('exit=' + $actualExit)
    Start-Sleep -Milliseconds 500
    $slice = Get-LogSlice -startIndex $logStart
    Save-Artifact -CaseDir $caseDir -name 'log-slice.txt' -content ($slice -join "`r`n") | Out-Null
    $iMutex = Find-LogIndex -lines $slice -pattern 'Mutex is already held by primary instance'
    $iRecv = Find-LogIndex -lines $slice -pattern 'Received activate request from client PID'
    $iOk = Find-LogIndex -lines $slice -pattern 'Primary instance response'
    $iFg = Find-LogIndex -lines $slice -pattern 'Window brought to foreground successfully'
    Add-Check -Case $case -Name 'log: mutex-held line' -Pass ($iMutex -ge 0) -Detail ('index=' + $iMutex)
    Add-Check -Case $case -Name 'log: activate-request line' -Pass ($iRecv -ge 0) -Detail ('index=' + $iRecv)
    Add-Check -Case $case -Name 'log: response line' -Pass ($iOk -ge 0) -Detail ('index=' + $iOk)
    Add-Check -Case $case -Name 'log: foreground line' -Pass ($iFg -ge 0) -Detail ('index=' + $iFg)
    # PR-S0-03 protocol: the primary logs the activation outcome (foreground) inside the callback,
    # then writes and logs the RESULT line, so the observable order is mutex < activate < foreground
    # and activate < response (response may follow foreground).
    Add-Check -Case $case -Name 'log order mutex < activate, activate < foreground, activate < response' -Pass (($iMutex -ge 0) -and ($iRecv -ge 0) -and ($iMutex -lt $iRecv) -and ($iRecv -lt $iFg) -and ($iRecv -lt $iOk)) -Detail ("mutex=$iMutex activate=$iRecv response=$iOk foreground=$iFg")
    Add-Check -Case $case -Name 'log: no connect failure' -Pass ((Count-LogMatches -lines $slice -pattern 'Failed to connect') -eq 0) -Detail ''
    $fg = Read-ProbeValues -path (Invoke-Probe -ProbeArgs @('foreground') -OutputFile (Join-Path $caseDir 'foreground.txt')).OutputFile
    Add-Check -Case $case -Name 'foreground is primary window' -Pass ($fg['FOREGROUND_HWND'] -eq $hwnd) -Detail ('fg=' + $fg['FOREGROUND_HWND'] + ' hwnd=' + $hwnd)
    $survivors = @(Get-PowerDockProcesses).Count
    Add-Check -Case $case -Name ('surviving instances = ' + $ExpectedInstances) -Pass ($survivors -eq $ExpectedInstances) -Detail ('count=' + $survivors)
    Save-Artifact -CaseDir $caseDir -name 'processes-after.json' -content (Get-ProcessSnapshot 'PowerDock') | Out-Null
    Complete-Case -Case $case -CaseDir $caseDir
    Stop-App

    # ---- C2 hidden --autostart primary + user double-click ----
    Write-Section 'C2 hidden autostart primary + secondary activation'
    $case = New-Case 'C2' 'hidden --autostart primary, secondary restores window'
    $caseDir = New-CaseDir 'C2'
    Clear-PowerDockState
    Assert-CleanState -Case $case -Context 'C2'
    $logStart = (Get-AppLogLines).Count
    $p = Start-Process -FilePath $script:ExeFullPath -ArgumentList '--autostart' -PassThru
    Start-Sleep -Milliseconds 2000
    $alive = -not $p.HasExited
    Add-Check -Case $case -Name 'autostart instance alive' -Pass $alive -Detail ('pid=' + $p.Id + ' exited=' + $p.HasExited)
    $st = Read-ProbeValues -path (Invoke-Probe -ProbeArgs @('state') -OutputFile (Join-Path $caseDir 'state-hidden.txt')).OutputFile
    Add-Check -Case $case -Name 'autostart instance has no visible window' -Pass ($st['FOUND'] -ne 'True' -or $st['VISIBLE'] -eq 'False') -Detail ('FOUND=' + $st['FOUND'] + ' VISIBLE=' + $st['VISIBLE'])
    $sec = Start-Process -FilePath $script:ExeFullPath -ArgumentList @("--no-feedback") -PassThru
    $exited = $sec.WaitForExit([Math]::Max(2000, $TimeoutMs * 2))
    if ($exited) { $sec.Refresh() }
    $actualExit = if ($exited) { $sec.ExitCode } else { -1 }
    $case.evidence['secondaryExit'] = $actualExit
    Add-Check -Case $case -Name 'secondary exit code is 0 (success)' -Pass ($actualExit -eq $script:ExitCodes.Success) -Detail ('exit=' + $actualExit)
    Start-Sleep -Milliseconds 600
    $st2 = Read-ProbeValues -path (Invoke-Probe -ProbeArgs @('state') -OutputFile (Join-Path $caseDir 'state-shown.txt')).OutputFile
    Add-Check -Case $case -Name 'window visible after activation' -Pass ($st2['VISIBLE'] -eq 'True') -Detail ('VISIBLE=' + $st2['VISIBLE'])
    $fg2 = Read-ProbeValues -path (Invoke-Probe -ProbeArgs @('foreground') -OutputFile (Join-Path $caseDir 'foreground.txt')).OutputFile
    Add-Check -Case $case -Name 'foreground is primary window' -Pass ($fg2['FOREGROUND_HWND'] -eq $st2['HWND']) -Detail ('fg=' + $fg2['FOREGROUND_HWND'] + ' hwnd=' + $st2['HWND'])
    Add-Check -Case $case -Name ('surviving instances = ' + $ExpectedInstances) -Pass ((@(Get-PowerDockProcesses).Count) -eq $ExpectedInstances) -Detail ('count=' + @(Get-PowerDockProcesses).Count)
    Complete-Case -Case $case -CaseDir $caseDir
    Stop-App

    # ---- C3 concurrent secondaries ----
    Write-Section ('C3 concurrent secondaries x' + $Concurrent)
    $case = New-Case 'C3' ('concurrent second starts x' + $Concurrent)
    $caseDir = New-CaseDir 'C3'
    Clear-PowerDockState
    Assert-CleanState -Case $case -Context 'C3'
    $primary = Start-Primary -ExtraArgs @()
    Add-Check -Case $case -Name 'primary window appears' -Pass ($primary.WindowMs -ge 0) -Detail ('windowMs=' + $primary.WindowMs)
    Start-Sleep -Milliseconds 400
    $logStart = (Get-AppLogLines).Count
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $secondaries = @()
    for ($i = 0; $i -lt $Concurrent; $i++) {
        $secondaries += Start-Process -FilePath $script:ExeFullPath -ArgumentList @("--no-feedback") -PassThru
    }
    $rows = @()
    foreach ($s in $secondaries) {
        $ok = $s.WaitForExit([Math]::Max(4000, $TimeoutMs * 3))
        if ($ok) { $s.Refresh() }
        $rows += [ordered]@{ pid = $s.Id; exited = $ok; exitCode = $(if ($ok) { $s.ExitCode } else { -1 }) }
    }
    $sw.Stop()
    $case.evidence['secondaries'] = $rows
    $case.evidence['totalMs'] = [int]$sw.ElapsedMilliseconds
    $okCount = @($rows | Where-Object { $_.exited -and $_.exitCode -eq $script:ExitCodes.Success }).Count
    Add-Check -Case $case -Name ($okCount.ToString() + '/' + $Concurrent + ' secondaries exit 0') -Pass ($okCount -eq $Concurrent) -Detail (($rows | ConvertTo-Json -Compress))
    Start-Sleep -Milliseconds 700
    $slice = Get-LogSlice -startIndex $logStart
    Save-Artifact -CaseDir $caseDir -name 'log-slice.txt' -content ($slice -join "`r`n") | Out-Null
    $recv = Count-LogMatches -lines $slice -pattern 'Received activate request from client PID'
    Add-Check -Case $case -Name 'all activate requests logged' -Pass ($recv -ge $Concurrent) -Detail ('received=' + $recv)
    Add-Check -Case $case -Name 'no connect failure in happy path' -Pass ((Count-LogMatches -lines $slice -pattern 'Failed to connect') -eq 0) -Detail ''
    $snapshotAfter = @(Get-ProcessSnapshot 'PowerDock')
    Save-Artifact -CaseDir $caseDir -name 'processes-after.json' -content $snapshotAfter | Out-Null
    $primaryAlive = (@(Get-Process -Id $primary.Process.Id -ErrorAction SilentlyContinue).Count -eq 1)
    Add-Check -Case $case -Name 'primary process still alive after burst' -Pass $primaryAlive -Detail ('pid=' + $primary.Process.Id + ' (0 means external interference: another agent killed it, see Team protocol)')
    $survivors = @(Get-PowerDockProcesses).Count
    Add-Check -Case $case -Name ('surviving instances = ' + $ExpectedInstances) -Pass ($survivors -eq $ExpectedInstances) -Detail ('count=' + $survivors)
    $fg3 = Read-ProbeValues -path (Invoke-Probe -ProbeArgs @('foreground') -OutputFile (Join-Path $caseDir 'foreground.txt')).OutputFile
    $st3 = Read-ProbeValues -path (Invoke-Probe -ProbeArgs @('state') -OutputFile (Join-Path $caseDir 'state.txt')).OutputFile
    Add-Check -Case $case -Name 'foreground is primary window' -Pass ($fg3['FOREGROUND_HWND'] -eq $st3['HWND']) -Detail ('fg=' + $fg3['FOREGROUND_HWND'] + ' hwnd=' + $st3['HWND'])
    Complete-Case -Case $case -CaseDir $caseDir
    Stop-App

    # ---- C4 zero-instance: mutex held, no pipe server ----
    Write-Section 'C4 zero-instance (mutex held by probe, no pipe server)'
    $case = New-Case 'C4' 'zero-instance: secondary must not exit 0 silently'
    $caseDir = New-CaseDir 'C4'
    Clear-PowerDockState
    $dirty = Test-CleanState
    if (-not $dirty.Clean) {
        Start-Sleep -Milliseconds 1200
        Clear-PowerDockState
        $dirty = Test-CleanState
    }
    Assert-CleanState -Case $case -Context 'C4'
    if (-not $dirty.Clean) {
        Add-Check -Case $case -Name 'C4 aborted: environment not clean (another agent instance is running)' -Pass $false -Detail ('processes=' + $dirty.ProcessCount + ' pipes=' + ($dirty.Pipes -join ','))
        Complete-Case -Case $case -CaseDir $caseDir
        Stop-App
        $case = $null
    }
    if ($null -ne $case) {
    $logStart = (Get-AppLogLines).Count
    $holderOut = Join-Path $caseDir 'holder.txt'
    $holder = Invoke-Probe -ProbeArgs @('hold-mutex', $mutexName, '30') -OutputFile $holderOut -Background
    Start-Sleep -Milliseconds 1200
    $holderVals = Read-ProbeValues -path $holderOut
    Add-Check -Case $case -Name 'probe holds the mutex' -Pass ($holderVals['CREATED_NEW'] -eq 'True' -or $holderVals['HELD_MUTEX_NAME'] -eq $mutexName) -Detail ($holderVals['HELD_MUTEX_NAME'])
    $case.evidence['mutexName'] = $mutexName
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $app = Start-Process -FilePath $script:ExeFullPath -ArgumentList @("--no-feedback") -PassThru
    $exited = $app.WaitForExit([Math]::Max(8000, $TimeoutMs * 4))
    $sw.Stop()
    if ($exited) { $app.Refresh() }
    $actualExit = if ($exited) { $app.ExitCode } else { -1 }
    $case.evidence['appPid'] = $app.Id
    $case.evidence['appExit'] = $actualExit
    $case.evidence['appElapsedMs'] = [int]$sw.ElapsedMilliseconds
    Add-Check -Case $case -Name 'app exits within zero-instance budget' -Pass ($exited) -Detail ('exited=' + $exited + ' ms=' + [int]$sw.ElapsedMilliseconds)
    Add-Check -Case $case -Name 'NOT 0: silent success forbidden' -Pass ($actualExit -ne $script:ExitCodes.Success) -Detail ('exit=' + $actualExit)
    Add-Check -Case $case -Name 'exit code = NoPrimary(20) [or PrimaryNotReady(21)/ProtocolTimeout(24)]' -Pass (@($script:ExitCodes.NoPrimary, $script:ExitCodes.PrimaryNotReady, $script:ExitCodes.ProtocolTimeout) -contains $actualExit) -Detail ('exit=' + $actualExit)
    $st4 = Read-ProbeValues -path (Invoke-Probe -ProbeArgs @('state') -OutputFile (Join-Path $caseDir 'state.txt')).OutputFile
    Add-Check -Case $case -Name 'no main window while mutex is held by non-primary' -Pass ($st4['FOUND'] -ne 'True') -Detail ('FOUND=' + $st4['FOUND'])
    $slice = Get-LogSlice -startIndex $logStart
    Save-Artifact -CaseDir $caseDir -name 'log-slice.txt' -content ($slice -join "`r`n") | Out-Null
    $reason = ($slice | Where-Object { $_ -match '\[(WARN|ERROR)\]' } | Select-Object -First 1)
    Add-Check -Case $case -Name 'readable failure reason in log' -Pass ([bool]$reason) -Detail ([string]$reason)
    $case.evidence['failureReasonLine'] = [string]$reason
    try { $holder.Process | Stop-Process -Force -ErrorAction SilentlyContinue } catch { }
    Complete-Case -Case $case -CaseDir $caseDir
    Stop-App
    }

    # ---- C5 foreground denied ----
    if (-not $SkipForegroundDenied) {
        Write-Section 'C5 foreground activation denied (locked foreground)'
        $case = New-Case 'C5' 'foreground denied: flash observed, secondary must report it'
        $caseDir = New-CaseDir 'C5'
        Clear-PowerDockState
        $dirty5 = Test-CleanState
        Assert-CleanState -Case $case -Context 'C5'
        if (-not $dirty5.Clean) {
            Add-Check -Case $case -Name 'C5 aborted: environment not clean (another agent instance is running)' -Pass $false -Detail ('processes=' + $dirty5.ProcessCount)
            Complete-Case -Case $case -CaseDir $caseDir
        }
        else {
            $primary = Start-Primary -ExtraArgs @()
        Add-Check -Case $case -Name 'primary window appears' -Pass ($primary.WindowMs -ge 0) -Detail ('windowMs=' + $primary.WindowMs)
        Start-Sleep -Milliseconds 400
        $lockOut = Join-Path $caseDir 'lock.txt'
        $lock = Invoke-Probe -ProbeArgs @('lock-foreground', '25') -OutputFile $lockOut -Background
        $lockReady = Wait-Until -condition {
            $vals = Read-ProbeValues -path $lockOut
            return ($vals['FOREGROUND_IS_LOCK_WINDOW'] -eq 'True')
        } -timeoutMs 5000
        $lockVals = Read-ProbeValues -path $lockOut
        Add-Check -Case $case -Name 'foreground lock acquired' -Pass $lockReady -Detail ('LOCK_OK=' + $lockVals['LOCK_SET_FOREGROUND_WINDOW_OK'] + ' FG_SELF=' + $lockVals['FOREGROUND_IS_LOCK_WINDOW'])
        [void](Invoke-Probe -ProbeArgs @('hide') -OutputFile (Join-Path $caseDir 'hide.txt'))
        $logStart = (Get-AppLogLines).Count
        $flashDir = Join-Path $caseDir 'flash'
        New-Item -ItemType Directory -Path $flashDir -Force | Out-Null
        $flash = Invoke-Probe -ProbeArgs @('flash-watch', $flashDir, '4', '100') -OutputFile (Join-Path $caseDir 'flashwatch.txt') -Background
        Start-Sleep -Milliseconds 700
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        $sec = Start-Process -FilePath $script:ExeFullPath -ArgumentList @("--no-feedback") -PassThru
        $exited = $sec.WaitForExit([Math]::Max(3000, $TimeoutMs * 2))
        $sw.Stop()
        if ($exited) { $sec.Refresh() }
        $actualExit = if ($exited) { $sec.ExitCode } else { -1 }
        $case.evidence['secondaryExit'] = $actualExit
        Add-Check -Case $case -Name 'secondary exits within budget' -Pass ($exited) -Detail ('exited=' + $exited + ' ms=' + [int]$sw.ElapsedMilliseconds)
        [void]$flash.Process.WaitForExit(12000)
        $flashVals = Read-ProbeValues -path (Join-Path $caseDir 'flashwatch.txt')
        $case.evidence['flashChangedFrames'] = $flashVals['FLASH_CHANGED_FRAMES']
        $case.evidence['flashMaxMeanDiff'] = $flashVals['FLASH_MAX_MEAN_DIFF']
        Start-Sleep -Milliseconds 300
        $slice = Get-LogSlice -startIndex $logStart
        Save-Artifact -CaseDir $caseDir -name 'log-slice.txt' -content ($slice -join "`r`n") | Out-Null
        $denyLine = @($slice | Where-Object { $_ -match 'SetForegroundWindow denied' })
        Add-Check -Case $case -Name 'log: SetForegroundWindow denied' -Pass ($denyLine.Count -gt 0) -Detail ([string]($denyLine | Select-Object -First 1))
        $authLine = @($slice | Where-Object { $_ -match 'AllowSetForegroundWindow' })
        $actLine = @($slice | Where-Object { $_ -match 'Foreground activation|SetForegroundWindow returned' })
        $authExplicit = ($authLine.Count -gt 0) -and ($authLine[0] -match '(True|False)')
        $actExplicit = ($actLine.Count -gt 0) -and ($actLine[0] -match '(True|False)')
        Add-Check -Case $case -Name 'log: foreground authorization outcome explicit (AllowSetForegroundWindow returned True/False)' -Pass $authExplicit -Detail ([string]($authLine | Select-Object -First 1))
        Add-Check -Case $case -Name 'log: foreground activation outcome explicit (SetForegroundWindow returned True/False)' -Pass $actExplicit -Detail ([string]($actLine | Select-Object -First 1))
        Add-Check -Case $case -Name 'log: authorization failure and target-no-response are distinguishable' -Pass ($authExplicit -and $actExplicit) -Detail ('authLines=' + $authLine.Count + ' actLines=' + $actLine.Count)
        $changedFrames = 0
        if ($flashVals['FLASH_CHANGED_FRAMES']) { $changedFrames = [int]$flashVals['FLASH_CHANGED_FRAMES'] }
        $maxMean = 0.0
        if ($flashVals['FLASH_MAX_MEAN_DIFF']) { $maxMean = [double]::Parse($flashVals['FLASH_MAX_MEAN_DIFF'], [System.Globalization.CultureInfo]::InvariantCulture) }
        Add-Check -Case $case -Name 'taskbar flash observed (changedFrames >= 3)' -Pass ($changedFrames -ge 3) -Detail ('changedFrames=' + $changedFrames)
        Add-Check -Case $case -Name 'taskbar flash mean diff > 5' -Pass ($maxMean -gt 5.0) -Detail ('maxMean=' + $maxMean)
        $fg5 = Read-ProbeValues -path (Invoke-Probe -ProbeArgs @('foreground') -OutputFile (Join-Path $caseDir 'foreground.txt')).OutputFile
        $lockHwnd = $lockVals['LOCK_WINDOW_HWND']
        Add-Check -Case $case -Name 'foreground remains the lock window' -Pass ($fg5['FOREGROUND_HWND'] -eq $lockHwnd) -Detail ('fg=' + $fg5['FOREGROUND_HWND'] + ' lock=' + $lockHwnd)
        $st5 = Read-ProbeValues -path (Invoke-Probe -ProbeArgs @('state') -OutputFile (Join-Path $caseDir 'state.txt')).OutputFile
        Add-Check -Case $case -Name 'primary window restored (visible)' -Pass ($st5['VISIBLE'] -eq 'True') -Detail ('VISIBLE=' + $st5['VISIBLE'])
        Add-Check -Case $case -Name 'exit code = ForegroundDenied(22)' -Pass ($actualExit -eq $script:ExitCodes.ForegroundDenied) -Detail ('exit=' + $actualExit)
            try { $lock.Process | Stop-Process -Force -ErrorAction SilentlyContinue } catch { }
            Complete-Case -Case $case -CaseDir $caseDir
            Stop-App
        }
    }
    else {
        Write-Host 'C5 skipped (-SkipForegroundDenied).' -ForegroundColor Yellow
    }

    # ---- C6 exit cleanup ----
    Write-Section 'C6 exit cleanup (pipe residue <= 500ms, mutex reusable)'
    $case = New-Case 'C6' 'exit cleanup and re-acquire'
    $caseDir = New-CaseDir 'C6'
    Clear-PowerDockState
    $deadline = [DateTime]::UtcNow.AddMilliseconds(500)
    $pipes = @(Get-PipePowerDockNames)
    while ($pipes.Count -gt 0 -and [DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 50
        $pipes = @(Get-PipePowerDockNames)
    }
    $pipeEnumerable = -not ($pipes.Count -eq 1 -and $pipes[0] -eq '__ENUM_FAILED__')
    Add-Check -Case $case -Name 'pipe enumeration worked' -Pass $pipeEnumerable -Detail (($pipes -join ','))
    Add-Check -Case $case -Name 'no PowerDock pipe residue within 500ms' -Pass ((-not $pipeEnumerable) -or ($pipes.Count -eq 0)) -Detail (($pipes -join ','))
    Add-Check -Case $case -Name 'no PowerDock process left' -Pass (@(Get-PowerDockProcesses).Count -eq 0) -Detail ('count=' + @(Get-PowerDockProcesses).Count)
    $reacquire = Start-Primary -ExtraArgs @()
    Add-Check -Case $case -Name 'new instance can acquire the mutex (window appears)' -Pass ($reacquire.WindowMs -ge 0) -Detail ('windowMs=' + $reacquire.WindowMs)
    $st6 = Read-ProbeValues -path (Invoke-Probe -ProbeArgs @('state') -OutputFile (Join-Path $caseDir 'state.txt')).OutputFile
    Add-Check -Case $case -Name 're-acquired instance owns a visible window' -Pass ($st6['VISIBLE'] -eq 'True') -Detail ('VISIBLE=' + $st6['VISIBLE'])
    Stop-App
    Complete-Case -Case $case -CaseDir $caseDir

    # ---- summary ----
    $failed = @($script:Cases | Where-Object { $_.status -ne 'pass' })
    $summary = [ordered]@{
        finishedUtc  = (Get-UtcStamp)
        totalCases   = $script:Cases.Count
        passedCases  = $script:Cases.Count - $failed.Count
        failedCases  = $failed.Count
        failedIds    = @($failed | ForEach-Object { $_.id })
        exitCodes    = $script:ExitCodes
        exe          = $script:ExeFullPath
        exeSha256    = $sha
        evidenceDir  = $EvidenceDir
    }
    $result = [ordered]@{
        environment = $envInfo
        summary     = $summary
        cases       = @($script:Cases)
    }
    $jsonPath = Join-Path $EvidenceDir 'verify-single-instance.json'
    Set-Content -LiteralPath $jsonPath -Value ($result | ConvertTo-Json -Depth 14) -Encoding UTF8
    Write-Section 'RESULT'
    Write-Host ('cases: ' + $summary.totalCases + ', passed: ' + $summary.passedCases + ', failed: ' + $summary.failedCases)
    if ($failed.Count -gt 0) {
        Write-Host ('failed case ids: ' + ($summary.failedIds -join ', ')) -ForegroundColor Red
        Write-Host ('JSON: ' + $jsonPath)
        exit 1
    }
    Write-Host ('JSON: ' + $jsonPath)
    Write-Host 'ALL CASES PASSED' -ForegroundColor Green
    exit 0
}
catch {
    Write-Host ('HARNESS ERROR: ' + $_.Exception.Message) -ForegroundColor Red
    if ($_.ScriptStackTrace) { Write-Host $_.ScriptStackTrace -ForegroundColor DarkGray }
    exit 2
}
finally {
    if (-not $KeepRunningOnSuccess) {
        try {
            Get-Process -Name $script:PowerDockProcessName -ErrorAction SilentlyContinue |
                Stop-Process -Force -ErrorAction SilentlyContinue
            Get-Process -Name 'PdVerifyProbe' -ErrorAction SilentlyContinue |
                Stop-Process -Force -ErrorAction SilentlyContinue
        }
        catch { }
    }
}









