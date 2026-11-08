#Requires -Version 5.1
<#
.SYNOPSIS
  独立外部验证：PowerDock 主窗口底部 Dock 四项导航（真实鼠标 / 真实键盘 / UI Automation 语义）。

.DESCRIPTION
  本脚本是「实现无关」的外部验证方。它不引用 PowerDock 的任何源码、不调用其内部诊断入口，
  只用两种外部通道取证：

  1. UI Automation（UIAutomationClient）读取窗口与元素语义：
     - Dock 项：AutomationId = PowerDock.Dock.Overview|Devices|Widgets|Settings
     - Dock 项：Name = 概览 / 设备 / 组件 / 设置
     - Dock 项：ItemStatus = 已选中 / 未选中
     - 页面标题：AutomationId = PowerDock.PageTitle
     这些语义「可能不存在」——找不到就如实记录，不做存在性假设。

  2. 真实输入（user32 SendInput）：鼠标绝对移动 + 左键按下/抬起、Tab / Enter / Space。
     键盘结论只在「本进程确实把目标窗口置为前台」的前提下才成立，否则标未验证。

  输入通道必须先自证：
     - 先与产品完全无关地自建一个 Win32 探针窗口（QaProbe），用真实 SendInput 点它、按键给它，
       能收到 WM_LBUTTONDOWN / WM_KEYDOWN 才算通道可用；
     - 通道不可用（对照失败）时，所有依赖 SendInput 的行一律判「未验证」，
       并且绝不拿 UIA InvokePattern 的结果顶替真实鼠标键盘结论（Invoke 只作为旁证单独记录）。

.PARAMETER ExePath
  待验证的 PowerDock.exe。会被复制到工作区外 %LOCALAPPDATA%\Programs\PowerDock-qa\dock-nav\<commit7>\ 再运行。

.PARAMETER Phase
  pre  = 修复前（期望复现「Dock 点击无响应」缺陷）
  post = 修复后（同一脚本、同一判据复跑）

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File tools\verify-dock-navigation.ps1 `
      -ExePath "E:\project\BatteryManagement-main559f499\dist\PowerDock-v0.1.0-win-x64\PowerDock.exe" -Phase pre

.NOTES
  编码 UTF-8(BOM) + LF。只写 tools/verify-dock-navigation.ps1 与证据目录，不触碰 src/。
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ExePath,
    [ValidateSet('pre', 'post')][string]$Phase = 'pre',
    [string]$Label = '',
    [string]$EvidenceRoot = 'E:\project\_evidence\dock-nav\external',
    [int]$StartupTimeoutSec = 150,
    [int]$SettleMs = 400,
    [int]$ProbeHoldSec = 3,
    [switch]$CopyWholeDirectory,
    [switch]$KeepProcess
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

# ---------------------------------------------------------------------------
# 全局状态
# ---------------------------------------------------------------------------
$script:Log = New-Object System.Collections.ArrayList
$script:Rows = New-Object System.Collections.ArrayList
$script:Steps = New-Object System.Collections.ArrayList
$script:Channel = [ordered]@{ mouse = $false; keyboard = $false; input = $false; reason = 'not-run' }
$script:Scale = 1.0
$script:StartTime = Get-Date

function Write-Ev {
    param([string]$Message, [string]$Color = 'Gray')
    $line = '[{0}] {1}' -f (Get-Date).ToString('HH:mm:ss.fff'), $Message
    [void]$script:Log.Add($line)
    Write-Host $line -ForegroundColor $Color
}

function Add-Row {
    param(
        [string]$Id,
        [string]$Title,
        [ValidateSet('通过', '失败', '未验证')][string]$Verdict,
        [string]$Evidence,
        $Data = $null
    )
    $row = [ordered]@{
        id       = $Id
        title    = $Title
        verdict  = $Verdict
        evidence = $Evidence
        data     = $Data
    }
    [void]$script:Rows.Add($row)
    $color = 'Gray'
    if ($Verdict -eq '通过') { $color = 'Green' }
    elseif ($Verdict -eq '失败') { $color = 'Red' }
    elseif ($Verdict -eq '未验证') { $color = 'Yellow' }
    Write-Host ('  [{0}] {1} :: {2}' -f $Verdict, $Id, $Evidence) -ForegroundColor $color
}

function Add-Step {
    param([string]$Id, $Data)
    $s = [ordered]@{ id = $Id; at = (Get-Date).ToString('o'); data = $Data }
    [void]$script:Steps.Add($s)
}

function Wait-Until {
    param([scriptblock]$Condition, [int]$TimeoutMs = 5000, [int]$IntervalMs = 120)
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $TimeoutMs) {
        $v = $null
        try { $v = & $Condition } catch { $v = $null }
        if ($v) { return $v }
        Start-Sleep -Milliseconds $IntervalMs
    }
    return $null
}

# ---------------------------------------------------------------------------
# 原生互操作 + 探针窗口（一次性编译）
# ---------------------------------------------------------------------------
$script:InteropSource = @'
using System;
using System.Text;
using System.Threading;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public static class QaNative
{
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern uint GetDpiForSystem();
    [DllImport("shcore.dll")] public static extern int GetDpiForMonitor(IntPtr hmon, int type, out uint dx, out uint dy);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool attach);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int height, bool repaint);

    // 屏幕尺寸必须实测：探针窗口与窗口定位一旦硬编码坐标，换到小屏就会落到可视区之外，
    // 于是"真实鼠标没到达探针"被误记成输入通道失效（2026-11-08 实测踩过）。
    public static int ScreenWidth() { return GetSystemMetrics(0); }
    public static int ScreenHeight() { return GetSystemMetrics(1); }

    private delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr l);

    public static IntPtr[] TopLevelWindows()
    {
        List<IntPtr> list = new List<IntPtr>();
        EnumProc cb = delegate(IntPtr h, IntPtr l) { list.Add(h); return true; };
        EnumWindows(cb, IntPtr.Zero);
        GC.KeepAlive(cb);
        return list.ToArray();
    }

    public static string Text(IntPtr h)
    {
        StringBuilder sb = new StringBuilder(1024);
        GetWindowText(h, sb, sb.Capacity);
        return sb.ToString();
    }

    public static string Cls(IntPtr h)
    {
        StringBuilder sb = new StringBuilder(512);
        GetClassName(h, sb, sb.Capacity);
        return sb.ToString();
    }

    public static uint PidOf(IntPtr h)
    {
        uint pid = 0;
        GetWindowThreadProcessId(h, out pid);
        return pid;
    }

    public static int[] RectOf(IntPtr h)
    {
        RECT r;
        if (!GetWindowRect(h, out r)) return new int[] { 0, 0, 0, 0 };
        return new int[] { r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top };
    }

    public static string WindowFromPointStr(int x, int y)
    {
        POINT p; p.X = x; p.Y = y;
        IntPtr h = WindowFromPoint(p);
        if (h == IntPtr.Zero) return "0";
        return h.ToInt64().ToString();
    }

    public static IntPtr WindowFromPointRaw(int x, int y)
    {
        POINT p; p.X = x; p.Y = y;
        return WindowFromPoint(p);
    }

    public static string CursorPosStr()
    {
        POINT p;
        if (!GetCursorPos(out p)) return "?";
        return p.X + "," + p.Y;
    }

    public static int DpiOfWindow(IntPtr h) { return (int)GetDpiForWindow(h); }

    public static int MonitorDpi(IntPtr h)
    {
        IntPtr mon = MonitorFromWindow(h, 2);
        uint dx = 0, dy = 0;
        int hr = GetDpiForMonitor(mon, 0, out dx, out dy);
        if (hr != 0) return -1;
        return (int)dx;
    }
}

public static class QaInput
{
    [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct HARDWAREINPUT { public uint uMsg; public ushort wParamL; public ushort wParamH; }
    [StructLayout(LayoutKind.Explicit)] private struct INPUTUNION { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; [FieldOffset(0)] public HARDWAREINPUT hi; }
    [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint type; public INPUTUNION u; }

    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int i);
    [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint code, uint type);

    private const uint INPUT_MOUSE = 0;
    private const uint INPUT_KEYBOARD = 1;
    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
    private const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    public static int LastError = 0;

    private static INPUT[] MoveEvents(int x, int y)
    {
        int vx = GetSystemMetrics(76);
        int vy = GetSystemMetrics(77);
        int vw = GetSystemMetrics(78);
        int vh = GetSystemMetrics(79);
        if (vw < 2) vw = 2;
        if (vh < 2) vh = 2;
        int ax = (int)Math.Round((x - vx) * 65535.0 / (vw - 1));
        int ay = (int)Math.Round((y - vy) * 65535.0 / (vh - 1));
        INPUT inp = new INPUT();
        inp.type = INPUT_MOUSE;
        inp.u.mi.dx = ax;
        inp.u.mi.dy = ay;
        inp.u.mi.dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK;
        return new INPUT[] { inp };
    }

    public static uint MouseMove(int x, int y)
    {
        INPUT[] a = MoveEvents(x, y);
        uint n = SendInput((uint)a.Length, a, Marshal.SizeOf(typeof(INPUT)));
        LastError = Marshal.GetLastWin32Error();
        return n;
    }

    public static uint LeftClick(int x, int y)
    {
        INPUT[] a = new INPUT[3];
        INPUT[] mv = MoveEvents(x, y);
        a[0] = mv[0];
        a[1] = new INPUT();
        a[1].type = INPUT_MOUSE;
        a[1].u.mi.dwFlags = MOUSEEVENTF_LEFTDOWN;
        a[2] = new INPUT();
        a[2].type = INPUT_MOUSE;
        a[2].u.mi.dwFlags = MOUSEEVENTF_LEFTUP;
        uint n = SendInput((uint)a.Length, a, Marshal.SizeOf(typeof(INPUT)));
        LastError = Marshal.GetLastWin32Error();
        return n;
    }

    public static uint Key(ushort vk)
    {
        INPUT[] a = new INPUT[2];
        a[0] = new INPUT();
        a[0].type = INPUT_KEYBOARD;
        a[0].u.ki.wVk = vk;
        a[0].u.ki.wScan = (ushort)MapVirtualKey(vk, 0);
        a[1] = new INPUT();
        a[1].type = INPUT_KEYBOARD;
        a[1].u.ki.wVk = vk;
        a[1].u.ki.wScan = (ushort)MapVirtualKey(vk, 0);
        a[1].u.ki.dwFlags = KEYEVENTF_KEYUP;
        uint n = SendInput((uint)a.Length, a, Marshal.SizeOf(typeof(INPUT)));
        LastError = Marshal.GetLastWin32Error();
        return n;
    }
}

// 与产品完全无关的对照窗口：证明 SendInput 通道本身可用。
public static class QaProbe
{
    private const uint WM_DESTROY = 0x0002;
    private const uint WM_CLOSE = 0x0010;
    private const uint WM_SETFOCUS = 0x0007;
    private const uint WM_KEYDOWN = 0x0100;
    private const uint WM_CHAR = 0x0102;
    private const uint WM_LBUTTONDOWN = 0x0201;
    private const uint WM_LBUTTONUP = 0x0202;

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize; public uint style; public IntPtr lpfnWndProc;
        public int cbClsExtra; public int cbWndExtra; public IntPtr hInstance;
        public IntPtr hIcon; public IntPtr hCursor; public IntPtr hbrBackground;
        public string lpszMenuName; public string lpszClassName; public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public int ptX; public int ptY; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WNDCLASSEX c);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateWindowEx(uint exStyle, string cls, string title, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG m, IntPtr h, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG m);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG m);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string name);
    [DllImport("user32.dll")] private static extern ushort UnregisterClass(string cls, IntPtr inst);
    [DllImport("user32.dll")] private static extern IntPtr LoadCursor(IntPtr inst, IntPtr name);

    private static IntPtr _hwnd = IntPtr.Zero;
    private static Thread _thread = null;
    private static ManualResetEvent _ready = null;
    private static WndProcDelegate _proc = null;
    private static int _mouseDown = 0, _mouseUp = 0, _keyDown = 0, _charCount = 0, _focusIn = 0;
    private static string _keys = "";
    private const string ClassName = "PowerDockQaProbeWnd";

    public static IntPtr Handle { get { return _hwnd; } }
    public static bool IsRunning { get { return _thread != null && _thread.IsAlive; } }
    public static int MouseDown { get { return _mouseDown; } }
    public static int MouseUp { get { return _mouseUp; } }
    public static int KeyDown { get { return _keyDown; } }
    public static int CharCount { get { return _charCount; } }
    public static int FocusIn { get { return _focusIn; } }
    public static string Keys { get { return _keys; } }

    public static void ResetStats()
    {
        _mouseDown = 0; _mouseUp = 0; _keyDown = 0; _charCount = 0; _focusIn = 0; _keys = "";
    }

    public static IntPtr Start(int x, int y, int w, int h)
    {
        if (_thread != null && _thread.IsAlive) return _hwnd;
        _ready = new ManualResetEvent(false);
        _thread = new Thread(delegate() { ThreadMain(x, y, w, h); });
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.IsBackground = true;
        _thread.Start();
        _ready.WaitOne(10000);
        return _hwnd;
    }

    public static void Stop()
    {
        try
        {
            if (_hwnd != IntPtr.Zero) PostMessage(_hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            if (_thread != null) _thread.Join(3000);
        }
        catch { }
        _hwnd = IntPtr.Zero;
        _thread = null;
    }

    private static void ThreadMain(int x, int y, int w, int h)
    {
        _proc = new WndProcDelegate(WndProc);
        WNDCLASSEX wc = new WNDCLASSEX();
        wc.cbSize = (uint)Marshal.SizeOf(typeof(WNDCLASSEX));
        wc.style = 0;
        wc.lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc);
        wc.hInstance = GetModuleHandle(null);
        wc.hCursor = LoadCursor(IntPtr.Zero, (IntPtr)32512);
        wc.hbrBackground = (IntPtr)6;
        wc.lpszClassName = ClassName;
        ushort atom = RegisterClassEx(ref wc);
        if (atom == 0) { _ready.Set(); return; }

        uint WS_OVERLAPPEDWINDOW = 0x00CF0000;
        uint WS_VISIBLE = 0x10000000;
        _hwnd = CreateWindowEx(0, ClassName, "PowerDockQA-Probe-Control", WS_OVERLAPPEDWINDOW | WS_VISIBLE,
            x, y, w, h, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        _ready.Set();
        if (_hwnd == IntPtr.Zero) return;

        MSG msg;
        while (GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
        UnregisterClass(ClassName, wc.hInstance);
    }

    private static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_LBUTTONDOWN: _mouseDown++; break;
            case WM_LBUTTONUP: _mouseUp++; break;
            case WM_KEYDOWN:
                _keyDown++;
                _keys = _keys + wParam.ToInt32().ToString() + ";";
                break;
            case WM_CHAR: _charCount++; break;
            case WM_SETFOCUS: _focusIn++; break;
            case WM_CLOSE: DestroyWindow(hWnd); return IntPtr.Zero;
            case WM_DESTROY: PostQuitMessage(0); return IntPtr.Zero;
        }
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }
}
'@

function Initialize-Interop {
    if (-not ('QaNative' -as [type])) {
        Add-Type -TypeDefinition $script:InteropSource -Language CSharp -ErrorAction Stop
    }
}

# ---------------------------------------------------------------------------
# 编码 / JSON / 证据落盘
# ---------------------------------------------------------------------------
$script:Utf8Bom = New-Object System.Text.UTF8Encoding($true)
$script:Utf8NoBom = New-Object System.Text.UTF8Encoding($false)

function Save-TextFile {
    param([string]$Path, [string]$Text)
    [System.IO.File]::WriteAllText($Path, $Text, $script:Utf8NoBom)
}

function Save-Json {
    param([string]$Path, $Object, [int]$Depth = 12)
    $json = $Object | ConvertTo-Json -Depth $Depth
    Save-TextFile -Path $Path -Text $json
}

# ---------------------------------------------------------------------------
# UIA 辅助
# ---------------------------------------------------------------------------
function Initialize-Uia {
    try {
        Add-Type -AssemblyName UIAutomationClient -ErrorAction Stop
        Add-Type -AssemblyName UIAutomationTypes -ErrorAction Stop
    }
    catch {
        $null = [System.Reflection.Assembly]::LoadWithPartialName('UIAutomationClient')
        $null = [System.Reflection.Assembly]::LoadWithPartialName('UIAutomationTypes')
    }
    if (-not ('System.Windows.Automation.AutomationElement' -as [type])) {
        throw 'UIAutomationClient 无法加载'
    }
}

function Get-ElPatterns($el) {
    $names = New-Object System.Collections.ArrayList
    $patterns = @(
        [System.Windows.Automation.InvokePattern]::Pattern,
        [System.Windows.Automation.SelectionItemPattern]::Pattern,
        [System.Windows.Automation.TogglePattern]::Pattern,
        [System.Windows.Automation.ScrollPattern]::Pattern,
        [System.Windows.Automation.ExpandCollapsePattern]::Pattern
    )
    foreach ($p in $patterns) {
        $obj = $null
        try {
            if ($el.TryGetCurrentPattern($p, [ref]$obj)) { [void]$names.Add($p.ProgrammaticName) }
        }
        catch { }
    }
    return @($names)
}

function Get-ElInfo($el) {
    $info = [ordered]@{
        name         = ''
        automationId = ''
        controlType  = ''
        className    = ''
        enabled      = $false
        offscreen    = $true
        focusable    = $false
        hasFocus     = $false
        itemStatus   = ''
        l            = 0.0
        t            = 0.0
        w            = 0.0
        h            = 0.0
        runtimeId    = ''
        patterns     = @()
    }
    try { $c = $el.Current } catch { return $info }
    try { $info.name = [string]$c.Name } catch { }
    try { $info.automationId = [string]$c.AutomationId } catch { }
    try { $info.controlType = [string]$c.ControlType.ProgrammaticName } catch { }
    try { $info.className = [string]$c.ClassName } catch { }
    try { $info.enabled = [bool]$c.IsEnabled } catch { }
    try { $info.offscreen = [bool]$c.IsOffscreen } catch { }
    try { $info.focusable = [bool]$c.IsKeyboardFocusable } catch { }
    try { $info.hasFocus = [bool]$c.HasKeyboardFocus } catch { }
    try { $info.itemStatus = [string]$el.GetCurrentPropertyValue([System.Windows.Automation.AutomationElement]::ItemStatusProperty) } catch { }
    try {
        $r = $c.BoundingRectangle
        $info.l = [double]$r.Left; $info.t = [double]$r.Top
        $info.w = [double]$r.Width; $info.h = [double]$r.Height
    }
    catch { }
    try { $info.runtimeId = (@($el.GetRuntimeId()) -join '-') } catch { }
    try { $info.patterns = @(Get-ElPatterns $el) } catch { }
    return $info
}

function Get-AllElementPairs {
    param($Root, [int]$Cap = 500)
    $out = New-Object System.Collections.ArrayList
    try {
        $found = $Root.FindAll([System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.Condition]::TrueCondition)
    }
    catch {
        return @()
    }
    for ($i = 0; $i -lt $found.Count -and $i -lt $Cap; $i++) {
        $el = $found.Item($i)
        [void]$out.Add(@{ el = $el; info = (Get-ElInfo $el) })
    }
    if ($found.Count -gt $Cap) { Write-Ev ("元素数 {0} 超出上限 {1}，仅取前 {1} 个" -f $found.Count, $Cap) 'Yellow' }
    return @($out)
}

function Get-AllElements {
    param($Root, [int]$Cap = 500)
    $out = New-Object System.Collections.ArrayList
    foreach ($p in @(Get-AllElementPairs -Root $Root -Cap $Cap)) { [void]$out.Add($p.info) }
    return @($out)
}

function Get-ElementByAutomationId {
    param($Root, [string]$AutomationId)
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $AutomationId)
    try {
        $found = $Root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
    }
    catch { return @() }
    $list = New-Object System.Collections.ArrayList
    for ($i = 0; $i -lt $found.Count; $i++) {
        $el = $found.Item($i)
        [void]$list.Add(@{ el = $el; info = (Get-ElInfo $el) })
    }
    return @($list)
}

function Pick-OnScreen {
    param($Candidates)
    # 注意：PowerShell 5.1 中 $null 进管道仍会执行一次 Where-Object 脚本块，
    # 因此这里必须显式过滤 null 与非字典项，不能直接 @($Candidates | Where-Object {...})。
    $list = New-Object System.Collections.ArrayList
    foreach ($c in @($Candidates)) {
        if ($null -eq $c) { continue }
        if (-not ($c -is [System.Collections.IDictionary])) { continue }
        if (-not $c.Contains('info')) { continue }
        [void]$list.Add($c)
    }
    if ($list.Count -eq 0) { return $null }
    $on = @($list | Where-Object { (-not $_.info.offscreen) -and $_.info.w -gt 1 -and $_.info.h -gt 1 })
    if ($on.Count -gt 0) { return $on[0] }
    return $list[0]
}

# ---------------------------------------------------------------------------
# 进程 / 窗口
# ---------------------------------------------------------------------------
function Stop-PowerDockAll {
    $killed = New-Object System.Collections.ArrayList
    $procs = @(Get-Process -Name PowerDock -ErrorAction SilentlyContinue)
    foreach ($p in $procs) {
        $path = ''
        try { $path = $p.Path } catch { }
        [void]$killed.Add(@{ pid = $p.Id; path = $path })
        try { Stop-Process -Id $p.Id -Force -ErrorAction Stop } catch { }
    }
    if ($killed.Count -gt 0) { Start-Sleep -Milliseconds 800 }
    return @($killed)
}

function Get-PowerDockProcesses {
    $out = New-Object System.Collections.ArrayList
    foreach ($p in @(Get-Process -Name PowerDock -ErrorAction SilentlyContinue)) {
        $path = ''
        try { $path = $p.Path } catch { }
        [void]$out.Add([ordered]@{
                pid     = $p.Id
                path    = $path
                started = ''
                mainWnd = $p.MainWindowHandle.ToInt64()
            })
    }
    return @($out)
}

function Get-TopLevelWindowInfos {
    param([int]$ProcessId)
    $out = New-Object System.Collections.ArrayList
    foreach ($h in [QaNative]::TopLevelWindows()) {
        if ([QaNative]::PidOf($h) -ne $ProcessId) { continue }
        $r = [QaNative]::RectOf($h)
        [void]$out.Add([ordered]@{
                hwnd    = $h.ToInt64()
                cls     = [QaNative]::Cls($h)
                title   = [QaNative]::Text($h)
                visible = [QaNative]::IsWindowVisible($h)
                iconic  = [QaNative]::IsIconic($h)
                l       = $r[0]; t = $r[1]; w = $r[2]; h = $r[3]
            })
    }
    return @($out)
}

function Find-MainWindowHandle {
    param([int]$ProcessId, [int]$TimeoutSec)
    $cond = {
        $best = [IntPtr]::Zero
        $bestArea = 0
        foreach ($h in [QaNative]::TopLevelWindows()) {
            if ([QaNative]::PidOf($h) -ne $ProcessId) { continue }
            if (-not [QaNative]::IsWindowVisible($h)) { continue }
            $r = [QaNative]::RectOf($h)
            $area = $r[2] * $r[3]
            if ($area -gt $bestArea) { $bestArea = $area; $best = $h }
        }
        if ($best -ne [IntPtr]::Zero) { return $best }
        return $null
    }
    return (Wait-Until -Condition $cond -TimeoutMs ($TimeoutSec * 1000) -IntervalMs 250)
}

# ---------------------------------------------------------------------------
# 窗口快照（页面标题 / 选中态 / 内容指纹）
# ---------------------------------------------------------------------------
$script:DockSpec = [ordered]@{
    Overview = @{ id = 'PowerDock.Dock.Overview'; label = '概览' }
    Devices  = @{ id = 'PowerDock.Dock.Devices'; label = '设备' }
    Widgets  = @{ id = 'PowerDock.Dock.Widgets'; label = '组件' }
    Settings = @{ id = 'PowerDock.Dock.Settings'; label = '设置' }
}
$script:DockOrder = @('Overview', 'Devices', 'Widgets', 'Settings')

function Get-WindowSnapshot {
    param($Hwnd, $WindowRect, [double]$Scale)

    # UIA 的 FromHandle 偶发 E_FAIL（"Unrecognized error"）；重试若干次再判定，避免一次瞬断作废整轮。
    $rootEl = $null
    $lastErr = $null
    for ($attempt = 1; $attempt -le 6; $attempt++) {
        try {
            if ([QaNative]::IsWindow($Hwnd)) {
                $rootEl = [System.Windows.Automation.AutomationElement]::FromHandle($Hwnd)
            }
        }
        catch { $lastErr = $_.Exception.Message; $rootEl = $null }
        if ($rootEl) { break }
        Start-Sleep -Milliseconds 300
    }
    if (-not $rootEl) {
        $alive = @(Get-Process -Name PowerDock -ErrorAction SilentlyContinue).Count
        throw ("UIA FromHandle 连续 6 次失败：hwnd={0} IsWindow={1} PowerDock进程数={2} 最后错误={3}" -f `
                $Hwnd.ToInt64(), [QaNative]::IsWindow($Hwnd), $alive, $lastErr)
    }
    $snap = [ordered]@{
        at            = (Get-Date).ToString('o')
        foreground    = ([QaNative]::GetForegroundWindow().ToInt64())
        foregroundIsTarget = (([QaNative]::GetForegroundWindow()) -eq $Hwnd)
        pageTitle     = $null
        pageTitleHits = 0
        pageTitleRuntimeId = $null
        fingerprint   = ''
        fingerprintIds = @()
        elementCount  = 0
        dock          = [ordered]@{}
        selected      = @()
        focus         = $null
        visibleTopLevel = @()
    }

    # 关键：Dock 语义（尤其 ItemStatus）必须每次「现读」，不能复用启动时抓的快照，
    # 否则选中态会永远停在初值，把「切换正常」误判成「ItemStatus 未更新」的假缺陷。
    $pairs = @(Get-AllElementPairs -Root $rootEl)
    $all = @($pairs | ForEach-Object { $_.info })
    $snap.elementCount = $all.Count
    $dockLive = Resolve-DockFromPairs -Pairs $pairs -WindowRect $WindowRect

    # --- 页面标题 ---
    $titleCands = @(Get-ElementByAutomationId -Root $rootEl -AutomationId 'PowerDock.PageTitle')
    $snap.pageTitleHits = $titleCands.Count
    # 缓存复用的四个页面实例可能都带同名 AutomationId：只认落在窗口矩形内、非 offscreen 的那个
    $titleInWin = @($titleCands | Where-Object {
            (-not $_.info.offscreen) -and $_.info.w -gt 1 -and $_.info.h -gt 1 -and
            ($_.info.l + ($_.info.w / 2)) -ge $WindowRect.Left -and
            ($_.info.l + ($_.info.w / 2)) -le ($WindowRect.Left + $WindowRect.Width) -and
            ($_.info.t + ($_.info.h / 2)) -ge $WindowRect.Top -and
            ($_.info.t + ($_.info.h / 2)) -le ($WindowRect.Top + $WindowRect.Height)
        })
    $snap.pageTitleCandidatesInWindow = $titleInWin.Count
    if ($titleInWin.Count -gt 0) { $picked = Pick-OnScreen -Candidates $titleInWin }
    else { $picked = Pick-OnScreen -Candidates $titleCands }
    if ($picked) {
        $snap.pageTitle = $picked.info.name
        $snap.pageTitleRuntimeId = $picked.info.runtimeId
    }

    # --- 内容指纹：只取「内容带」内可见且有名字的元素，排除 Dock 与标题栏 ---
    $contentTop = $WindowRect.Top + (60 * $Scale)
    $contentBottom = $WindowRect.Top + $WindowRect.Height - (90 * $Scale)
    $excluded = @{}
    foreach ($k in $script:DockOrder) {
        if ($dockLive.ContainsKey($k) -and $dockLive[$k]) {
            $excluded[$dockLive[$k]['info'].runtimeId] = $true
        }
    }
    $fp = New-Object System.Collections.ArrayList
    $fpIds = New-Object System.Collections.ArrayList
    foreach ($e in $all) {
        if ($e.offscreen) { continue }
        if ([string]::IsNullOrWhiteSpace($e.name)) { continue }
        if ($excluded.ContainsKey($e.runtimeId)) { continue }
        $cy = $e.t + ($e.h / 2.0)
        if ($cy -lt $contentTop -or $cy -gt $contentBottom) { continue }
        [void]$fp.Add(('{0}|{1}|{2}' -f $e.controlType, $e.name, $e.automationId))
        [void]$fpIds.Add($e.runtimeId)
    }
    $fpSorted = @($fp | Sort-Object)
    $snap.fingerprint = ($fpSorted -join "`n")
    $snap.fingerprintIds = @($fpIds | Sort-Object)
    $snap.fingerprintLines = $fpSorted

    # --- Dock 项（现读）---
    foreach ($k in $script:DockOrder) {
        $entry = [ordered]@{
            elementSource = 'missing'
            name = $null; automationId = $null; controlType = $null; itemStatus = $null
            focusable = $false; hasFocus = $false; patterns = @()
            l = 0.0; t = 0.0; w = 0.0; h = 0.0; runtimeId = $null; offscreen = $true
        }
        $sel = $null
        if ($dockLive.ContainsKey($k) -and $dockLive[$k]) { $sel = $dockLive[$k] }
        if ($sel) {
            $i = $sel['info']
            $entry.elementSource = $sel['source']
            $entry.name = $i.name; $entry.automationId = $i.automationId; $entry.controlType = $i.controlType
            $entry.itemStatus = $i.itemStatus; $entry.focusable = $i.focusable; $entry.hasFocus = $i.hasFocus
            $entry.patterns = $i.patterns; $entry.runtimeId = $i.runtimeId
            $entry.offscreen = $i.offscreen
            $entry.l = $i.l; $entry.t = $i.t; $entry.w = $i.w; $entry.h = $i.h
        }
        $snap.dock[$k] = $entry
        if ($entry.itemStatus -eq '已选中') { $snap.selected += $k }
    }

    # --- 键盘焦点 ---
    try {
        $fe = [System.Windows.Automation.AutomationElement]::FocusedElement
        if ($fe) {
            $fi = Get-ElInfo $fe
            $snap.focus = [ordered]@{
                name = $fi.name; automationId = $fi.automationId; controlType = $fi.controlType
                hasFocus = $fi.hasFocus; runtimeId = $fi.runtimeId; pid = 0
            }
            try { $snap.focus.pid = [int]$fe.Current.ProcessId } catch { }
        }
    }
    catch { }

    $snap.visibleTopLevel = @(Get-TopLevelWindowInfos -ProcessId ([QaNative]::PidOf($Hwnd)) |
        Where-Object { $_.visible })
    return $snap
}

function Resolve-DockFromPairs {
    param($Pairs, $WindowRect)
    $bandTop = $WindowRect.Top + ($WindowRect.Height * 0.60)
    $map = @{}
    foreach ($k in $script:DockOrder) {
        $spec = $script:DockSpec[$k]
        $byId = $null
        foreach ($p in $Pairs) {
            if ($p.info.automationId -eq $spec.id) { $byId = $p; break }
        }
        if ($byId) {
            $map[$k] = @{ source = 'AutomationId'; el = $byId.el; info = $byId.info }
            continue
        }
        # 兜底：底部带内按 Name 文本匹配（修复前的 TextBlock 只能这样定位）
        $cand = $null
        foreach ($p in $Pairs) {
            $e = $p.info
            if ([string]::IsNullOrWhiteSpace($e.name)) { continue }
            if ($e.name -notlike ('*' + $spec.label + '*')) { continue }
            $cy = $e.t + ($e.h / 2.0)
            if ($cy -lt $bandTop) { continue }
            if ($e.w -lt 1 -or $e.h -lt 1) { continue }
            if ($null -eq $cand -or (($e.w * $e.h) -lt ($cand.info.w * $cand.info.h))) { $cand = $p }
        }
        if ($cand) { $map[$k] = @{ source = 'NameFallback'; el = $cand.el; info = $cand.info } }
        else { $map[$k] = $null }
    }
    return $map
}

function Get-DockElementMap {
    param($RootEl, $WindowRect, [double]$Scale)
    return Resolve-DockFromPairs -Pairs @(Get-AllElementPairs -Root $RootEl) -WindowRect $WindowRect
}

# ---------------------------------------------------------------------------
# 输入动作
# ---------------------------------------------------------------------------
function Set-TargetForeground {
    param($Hwnd, $WindowRect, [double]$Scale)
    $fg = [QaNative]::GetForegroundWindow()
    if ($fg -eq $Hwnd) { return @{ ok = $true; method = 'already-foreground' } }
    [void][QaNative]::SetForegroundWindow($Hwnd)
    Start-Sleep -Milliseconds 200
    if (([QaNative]::GetForegroundWindow()) -eq $Hwnd) { return @{ ok = $true; method = 'SetForegroundWindow' } }
    # 兜底：在窗口内容区顶部点一下（避开标题栏拖动区与 Dock），点击本身会激活窗口
    $x = [int]($WindowRect.Left + ($WindowRect.Width / 2))
    $y = [int]($WindowRect.Top + (75 * $Scale))
    [void][QaInput]::LeftClick($x, $y)
    Start-Sleep -Milliseconds 300
    if (([QaNative]::GetForegroundWindow()) -eq $Hwnd) { return @{ ok = $true; method = 'neutral-click' } }
    return @{ ok = $false; method = 'failed' }
}

function Invoke-RealClick {
    param([int]$X, [int]$Y, $TargetHwnd, [string]$Note)
    $before = [QaNative]::CursorPosStr()
    $wfp = [QaNative]::WindowFromPointRaw($X, $Y)
    $n = [QaInput]::LeftClick($X, $Y)
    Start-Sleep -Milliseconds 60
    $after = [QaNative]::CursorPosStr()
    $rec = [ordered]@{
        note = $Note; x = $X; y = $Y
        sendInputReturned = $n
        sendInputLastError = [QaInput]::LastError
        cursorBefore = $before; cursorAfter = $after
        windowFromPoint = $wfp.ToInt64()
        windowFromPointMatchesTarget = ($wfp -eq $TargetHwnd)
        foregroundBefore = 0; foregroundAfter = 0
    }
    return $rec
}

function Invoke-RealKey {
    param([string]$Name, [int]$Vk)
    $n = [QaInput]::Key([uint16]$Vk)
    Start-Sleep -Milliseconds 60
    return [ordered]@{
        key = $Name; vk = $Vk
        sendInputReturned = $n
        sendInputLastError = [QaInput]::LastError
    }
}

function Get-ClickPoints {
    param($Info, [double]$Scale, [int]$WindowLeft, [int]$WindowTop, [int]$WindowW, [int]$WindowH)
    $l = [int][Math]::Round($Info.l); $t = [int][Math]::Round($Info.t)
    $w = [int][Math]::Round($Info.w); $h = [int][Math]::Round($Info.h)
    $cy = $t + [int]($h / 2)
    $pad = [int][Math]::Round(8 * $Scale)
    $left = $l + $pad; if ($left -gt ($l + $w - 3)) { $left = $l + [int]($w / 2) }
    if ($left -lt ($l + 2)) { $left = $l + 2 }
    $right = $l + $w - $pad; if ($right -lt ($l + 3)) { $right = $l + [int]($w / 2) }
    if ($right -gt ($l + $w - 2)) { $right = $l + $w - 2 }
    $center = $l + [int]($w / 2)
    $pts = [ordered]@{
        center      = @{ x = $center; y = $cy }
        leftInner   = @{ x = $left; y = $cy }
        rightInner  = @{ x = $right; y = $cy }
    }
    # 全部夹到窗口矩形内，越界点直接标记
    foreach ($k in @($pts.Keys)) {
        $p = $pts[$k]
        $inside = ($p.x -ge $WindowLeft -and $p.x -le ($WindowLeft + $WindowW) -and
                   $p.y -ge $WindowTop -and $p.y -le ($WindowTop + $WindowH))
        $p.inWindow = $inside
        $p.inElement = ($p.x -ge $l -and $p.x -le ($l + $w) -and $p.y -ge $t -and $p.y -le ($t + $h))
    }
    return $pts
}

function Test-TitleMatches {
    param([string]$Title, [string]$DockKey)
    if ([string]::IsNullOrEmpty($Title)) { return $false }
    $label = $script:DockSpec[$DockKey].label
    return ($Title -like ('*' + $label + '*'))
}

function Get-ShortHash {
    param([string]$Text)
    if ([string]::IsNullOrEmpty($Text)) { return 'empty' }
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $h = $sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($Text))
        return (([System.BitConverter]::ToString($h)) -replace '-', '').Substring(0, 8)
    }
    finally { $sha.Dispose() }
}

# ===========================================================================
# 主流程
# ===========================================================================
Write-Host ''
Write-Host '============================================================' -ForegroundColor Cyan
Write-Host (' PowerDock Dock 导航 外部验证（UIA + 真实 SendInput）  阶段={0}' -f $Phase) -ForegroundColor Cyan
Write-Host '============================================================' -ForegroundColor Cyan
Write-Host ''

Initialize-Interop
Initialize-Uia
[void][QaNative]::SetProcessDpiAwarenessContext([IntPtr](-4))

$exitCode = 0
$proc = $null
$evidenceDir = $null
$finalReport = $null
$killed = @()
$killedEnd = @()
$env0 = [ordered]@{}
$hwnd = $null
$windowRect = $null
$dockMap = @{}
$script:TargetPid = 0
$script:TargetHwnd = [IntPtr]::Zero
$script:Interference = $false
$script:InterferenceReason = ''
$script:ProductDllBefore = ''
$script:ProductDllAfter = ''
$script:QaDllBefore = ''
$script:QaDllAfter = ''

try {
    # ------------------------------------------------------------------
    # 0. 环境取证
    # ------------------------------------------------------------------
    Write-Ev '--- 步骤 0：环境取证 ---' 'Cyan'

    if (-not (Test-Path -LiteralPath $ExePath)) { throw "找不到 ExePath: $ExePath" }
    $exeItem = Get-Item -LiteralPath $ExePath

    $gitInfo = @{ commit = 'nogit'; root = $null }
    try {
        $dir = Split-Path -Parent $exeItem.FullName
        while ($dir) {
            if (Test-Path -LiteralPath (Join-Path $dir '.git')) {
                $c = & git -C $dir rev-parse --short=7 HEAD 2>$null
                if ($LASTEXITCODE -eq 0 -and $c) { $gitInfo = @{ commit = $c.Trim(); root = $dir } }
                break
            }
            $parent = Split-Path -Parent $dir
            if (-not $parent -or $parent -eq $dir) { break }
            $dir = $parent
        }
    }
    catch { }
    $commit7 = $gitInfo.commit

    $stamp = (Get-Date).ToString('yyyyMMdd-HHmmss')
    $phaseTag = $Phase
    if ($Label) { $phaseTag = $Phase + '-' + $Label }
    $evidenceDir = Join-Path $EvidenceRoot ('{0}-{1}' -f $phaseTag, $stamp)
    New-Item -ItemType Directory -Force -Path $evidenceDir | Out-Null
    Write-Ev ("证据目录: {0}" -f $evidenceDir)

    $scriptHash = (Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash
    $exeHash = (Get-FileHash -LiteralPath $exeItem.FullName -Algorithm SHA256).Hash
    $exeVersion = ''
    try { $exeVersion = $exeItem.VersionInfo.FileVersion } catch { }

    # 产物身份（Lead 口径）：apphost 的 exe 哈希在多次构建间会变，必须记录同目录 DLL 的哈希。
    # 同时记录全部文件的 mtime，用于事后判断「运行期间产物是否被重建」。
    $exeDirPath = Split-Path -Parent $exeItem.FullName
    $exeBase = [System.IO.Path]::GetFileNameWithoutExtension($exeItem.Name)
    $productFiles = New-Object System.Collections.ArrayList
    $hashBudget = 60
    foreach ($f in @(Get-ChildItem -LiteralPath $exeDirPath -File -ErrorAction SilentlyContinue |
            Sort-Object Name)) {
        $entry = [ordered]@{
            name     = $f.Name
            length   = $f.Length
            mtimeUtc = $f.LastWriteTimeUtc.ToString('o')
            sha256   = $null
        }
        if ($f.Extension -in @('.exe', '.dll', '.json') -and $hashBudget -gt 0) {
            try { $entry.sha256 = (Get-FileHash -LiteralPath $f.FullName -Algorithm SHA256).Hash; $hashBudget-- } catch { }
        }
        [void]$productFiles.Add($entry)
    }
    $dllEntry = $productFiles | Where-Object { $_.name -eq ($exeBase + '.dll') } | Select-Object -First 1
    $script:ProductDllBefore = if ($dllEntry) { [string]$dllEntry.sha256 } else { '' }
    Write-Ev ("产物身份：{0}.dll sha256={1}（{2} 个文件已清点）" -f $exeBase,
        $(if ($script:ProductDllBefore) { $script:ProductDllBefore.Substring(0, 16) + '…' } else { '不存在' }), $productFiles.Count)

    $os = $null
    try { $os = Get-CimInstance Win32_OperatingSystem } catch { }
    $env0 = [ordered]@{
        phase            = $Phase
        label            = $Label
        stamp            = $stamp
        startedAt        = $script:StartTime.ToString('o')
        scriptPath       = $PSCommandPath
        scriptSha256     = $scriptHash
        exePath          = $exeItem.FullName
        exeSha256        = $exeHash
        exeSizeBytes     = $exeItem.Length
        exeFileVersion   = $exeVersion
        sourceCommit7    = $commit7
        sourceRepoRoot   = $gitInfo.root
        psVersion        = $PSVersionTable.PSVersion.ToString()
        psEdition        = $PSVersionTable.PSEdition
        clrVersion       = $PSVersionTable.CLRVersion.ToString()
        userName         = $env:USERNAME
        sessionName      = $env:SESSIONNAME
        userInteractive  = [Environment]::UserInteractive
        osCaption        = if ($os) { $os.Caption } else { '' }
        osBuild          = if ($os) { $os.BuildNumber } else { '' }
        dpiForSystem     = [QaNative]::GetDpiForSystem()
        screenMetrics    = @{ cx = [QaNative]::GetSystemMetrics(0); cy = [QaNative]::GetSystemMetrics(1) }
        virtualScreen    = @{
            x = [QaNative]::GetSystemMetrics(76); y = [QaNative]::GetSystemMetrics(77)
            w = [QaNative]::GetSystemMetrics(78); h = [QaNative]::GetSystemMetrics(79)
        }
    }
    Save-Json -Path (Join-Path $evidenceDir 'env.json') -Object $env0

    $envLines = @()
    foreach ($k in $env0.Keys) { $envLines += ('{0} = {1}' -f $k, ($env0[$k] | ConvertTo-Json -Compress -Depth 4)) }
    Save-TextFile -Path (Join-Path $evidenceDir 'env.txt') -Text ($envLines -join "`n")

    $cmdText = @()
    $cmdText += 'script  : ' + $PSCommandPath
    $cmdText += 'sha256  : ' + $scriptHash
    $cmdText += 'exe     : ' + $exeItem.FullName
    $cmdText += 'exe256  : ' + $exeHash
    $cmdText += 'invocation:'
    $cmdText += ('  powershell -NoProfile -ExecutionPolicy Bypass -File "{0}" -ExePath "{1}" -Phase {2}' -f $PSCommandPath, $exeItem.FullName, $Phase)
    $cmdText += ('  PowerShell {0} ({1}), CLR {2}' -f $PSVersionTable.PSVersion, $PSVersionTable.PSEdition, $PSVersionTable.CLRVersion)
    Save-TextFile -Path (Join-Path $evidenceDir 'command.txt') -Text ($cmdText -join "`n")

    Write-Ev ("源提交={0}  exe={1} ({2} bytes)  PS={3}" -f $commit7, $exeItem.Name, $exeItem.Length, $PSVersionTable.PSVersion)

    # ------------------------------------------------------------------
    # 1. 清场 + 部署到工作区外
    # ------------------------------------------------------------------
    Write-Ev '--- 步骤 1：清场与部署（AGENTS.md §9） ---' 'Cyan'

    $killed = @(Stop-PowerDockAll)
    Write-Ev ("启动前清场：杀掉 {0} 个既有 PowerDock 进程" -f $killed.Count)
    foreach ($k in $killed) { Write-Ev ("  killed pid={0} path={1}" -f $k.pid, $k.path) 'DarkYellow' }

    # 工作区外路径：%LOCALAPPDATA%\Programs\PowerDock-qa\dock-nav\<commit7>\
    $exeDir = Split-Path -Parent $exeItem.FullName
    $exeBase = [System.IO.Path]::GetFileNameWithoutExtension($exeItem.Name)
    # 框架依赖构建（build.ps1 产物）必须连同 .runtimeconfig.json / .deps.json / 依赖 DLL 一起搬，
    # 只搬 exe 会让 hostfxr 找不到运行时；自包含单文件包（pack.ps1 产物）则只需那一个 exe。
    $isFdd = Test-Path -LiteralPath (Join-Path $exeDir ($exeBase + '.runtimeconfig.json'))
    $copyWhole = ($CopyWholeDirectory.IsPresent -or $isFdd)

    $qaSuffix = $commit7
    if ($isFdd) { $qaSuffix = $commit7 + '-fdd' }
    $qaRoot = Join-Path $env:LOCALAPPDATA ('Programs\PowerDock-qa\dock-nav\' + $qaSuffix)
    New-Item -ItemType Directory -Force -Path $qaRoot | Out-Null
    $qaExe = Join-Path $qaRoot $exeItem.Name
    if ($copyWhole) {
        Copy-Item -Path (Join-Path $exeDir '*') -Destination $qaRoot -Recurse -Force
        Write-Ev ("框架依赖构建：整目录复制 {0} → {1}" -f $exeDir, $qaRoot)
    }
    else {
        Copy-Item -LiteralPath $exeItem.FullName -Destination $qaExe -Force
    }
    $qaHash = (Get-FileHash -LiteralPath $qaExe -Algorithm SHA256).Hash

    $workspaceRoots = @('E:\project\BatteryManagement', 'E:\project\BatteryManagement-dock-nav')
    $exeOutsideWorkspace = $true
    foreach ($wr in $workspaceRoots) {
        if ($qaExe.StartsWith($wr, [StringComparison]::OrdinalIgnoreCase)) { $exeOutsideWorkspace = $false }
    }
    $env0['qaExePath'] = $qaExe
    $env0['qaExeSha256'] = $qaHash
    $env0['exeOutsideWorkspace'] = $exeOutsideWorkspace
    $env0['frameworkDependent'] = $isFdd
    $env0['copyWholeDirectory'] = $copyWhole
    Save-Json -Path (Join-Path $evidenceDir 'env.json') -Object $env0

    $envRowEvidence = ("清场杀 {0} 个；运行副本 {1}（sha256 {2}）；工作区外={3}；框架依赖={4} 整目录复制={5}" -f `
            $killed.Count, $qaExe, $qaHash.Substring(0, 12), $exeOutsideWorkspace, $isFdd, $copyWhole)
    if ($killed.Count -ge 0 -and $exeOutsideWorkspace -and $qaHash -eq $exeHash -and (Test-Path -LiteralPath $qaExe)) {
        Add-Row -Id 'ENV-01' -Title '环境纪律：清场 + 工作区外运行 + 副本一致' -Verdict '通过' `
            -Evidence $envRowEvidence -Data @{ killed = $killed; qaExe = $qaExe; exeOutsideWorkspace = $exeOutsideWorkspace; frameworkDependent = $isFdd }
    }
    else {
        Add-Row -Id 'ENV-01' -Title '环境纪律：清场 + 工作区外运行 + 副本一致' -Verdict '失败' `
            -Evidence $envRowEvidence -Data @{ killed = $killed; qaExe = $qaExe; exeOutsideWorkspace = $exeOutsideWorkspace; copyHashMatches = ($qaHash -eq $exeHash) }
    }

    # ------------------------------------------------------------------
    # 2. 启动产品
    # ------------------------------------------------------------------
    Write-Ev '--- 步骤 2：启动产品并等待主窗口 ---' 'Cyan'
    $proc = Start-Process -FilePath $qaExe -WorkingDirectory $qaRoot -PassThru
    Write-Ev ("已启动 pid={0}（Start-Process，未等待）" -f $proc.Id)

    # 轮询窗口，同时做「进程已退出」快速失败，避免无窗口时白等满超时
    $hwnd = $null
    $exitCodeObserved = $null
    $deadline = (Get-Date).AddSeconds($StartupTimeoutSec)
    while ((Get-Date) -lt $deadline) {
        $live = Get-Process -Id $proc.Id -ErrorAction SilentlyContinue
        if (-not $live) {
            try { $exitCodeObserved = $proc.ExitCode } catch { }
            Write-Ev ("进程 pid={0} 已退出（未出现窗口），ExitCode={1}" -f $proc.Id, $exitCodeObserved) 'Red'
            break
        }
        $hwnd = Find-MainWindowHandle -ProcessId $proc.Id -TimeoutSec 1
        if ($null -ne $hwnd -and $hwnd -ne [IntPtr]::Zero) { break }
        Start-Sleep -Milliseconds 250
    }
    $env0['startupExitCode'] = $exitCodeObserved
    Save-Json -Path (Join-Path $evidenceDir 'env.json') -Object $env0

    if ($null -eq $hwnd -or $hwnd -eq [IntPtr]::Zero) {
        # 单文件自解包可能有父子进程，按名字兜底
        $alt = @(Get-Process -Name PowerDock -ErrorAction SilentlyContinue | Sort-Object StartTime -Descending)
        foreach ($p in $alt) {
            $hwnd = Find-MainWindowHandle -ProcessId $p.Id -TimeoutSec 20
            if ($null -ne $hwnd -and $hwnd -ne [IntPtr]::Zero) { $proc = $p; break }
        }
    }
    if ($null -eq $hwnd -or $hwnd -eq [IntPtr]::Zero) {
        $why = if ($null -ne $exitCodeObserved) { ("进程已退出 ExitCode={0}（启动即失败，先确认包是否完整/是否需要整目录复制）" -f $exitCodeObserved) } else { "等待超时" }
        throw ("未能取得主窗口：{0}；pid={1}；qaExe={2}" -f $why, $proc.Id, $qaExe)
    }

    Start-Sleep -Milliseconds 700

    # 句柄有效性 + 稳定性复检：单文件自解包/首帧阶段可能出现瞬时窗口，选到后随即销毁。
    $hwndStable = $false
    for ($i = 0; $i -lt 8; $i++) {
        if ($null -ne $hwnd -and $hwnd -ne [IntPtr]::Zero -and [QaNative]::IsWindow($hwnd)) {
            $pv = [int][QaNative]::PidOf($hwnd)
            $rv = [QaNative]::RectOf($hwnd)
            if ($pv -ne 0 -and $rv[2] -gt 100 -and $rv[3] -gt 100) { $hwndStable = $true; break }
            Write-Ev ("窗口句柄 {0} 无效（pid={1} rect={2}x{3}），重新定位" -f $hwnd.ToInt64(), $pv, $rv[2], $rv[3]) 'Yellow'
        }
        Start-Sleep -Milliseconds 350
        $hwnd = Find-MainWindowHandle -ProcessId $proc.Id -TimeoutSec 12
        if ($null -eq $hwnd -or $hwnd -eq [IntPtr]::Zero) { break }
    }
    if (-not $hwndStable) {
        throw ("未能取得稳定的有效主窗口：hwnd={0} IsWindow={1} 目标pid={2}" -f `
                $(if ($null -eq $hwnd) { 'null' } else { $hwnd.ToInt64() }), `
                $(if ($null -eq $hwnd) { 'n/a' } else { [QaNative]::IsWindow($hwnd) }), $proc.Id)
    }

    $targetPid = [int][QaNative]::PidOf($hwnd)
    $winDpi = [QaNative]::DpiOfWindow($hwnd)
    $monDpi = [QaNative]::MonitorDpi($hwnd)
    $script:Scale = [double]$winDpi / 96.0
    $env0['dpiForWindow'] = $winDpi
    $env0['monitorDpi'] = $monDpi
    $env0['scale'] = $script:Scale
    Save-Json -Path (Join-Path $evidenceDir 'env.json') -Object $env0

    $r0 = [QaNative]::RectOf($hwnd)
    $windowRect = [ordered]@{ Left = $r0[0]; Top = $r0[1]; Width = $r0[2]; Height = $r0[3] }
    $allProcs = @(Get-PowerDockProcesses)

    Write-Ev ("主窗口 hwnd={0} pid={1} rect={2},{3} {4}x{5}" -f $hwnd.ToInt64(), $targetPid,
        $windowRect.Left, $windowRect.Top, $windowRect.Width, $windowRect.Height)
    Write-Ev ("GetDpiForWindow={0}（{1}%）  GetDpiForMonitor(effective)={2}  → 判定缩放={3}%" -f `
            $winDpi, ($winDpi / 96.0 * 100), $monDpi, ($script:Scale * 100)) 'Cyan'

    Add-Step -Id 'startup' -Data ([ordered]@{
            pid = $targetPid; hwnd = $hwnd.ToInt64(); windowRect = $windowRect
            dpiForWindow = $winDpi; monitorDpi = $monDpi; scale = $script:Scale
            processes = $allProcs
        })

    $dpiOk = ($winDpi -ge 96) -and ($windowRect.Width -gt 100) -and ($windowRect.Height -gt 100)
    Add-Row -Id 'ENV-02' -Title '缩放与环境：GetDpiForWindow 记录并说明 100%/150%' `
        -Verdict $(if ($dpiOk) { '通过' } else { '失败' }) `
        -Evidence ("GetDpiForWindow={0} → {1}%；显示器 effective DPI={2}；窗口 {3}x{4}px" -f `
            $winDpi, ($script:Scale * 100), $monDpi, $windowRect.Width, $windowRect.Height) `
        -Data @{ dpiForWindow = $winDpi; monitorDpi = $monDpi; scale = $script:Scale }

    # ------------------------------------------------------------------
    # 3. 输入通道自证（与产品无关的自建对照窗口）
    # ------------------------------------------------------------------
    Write-Ev '--- 步骤 3：输入通道自证（自建 Win32 探针窗口） ---' 'Cyan'
    # 探针坐标必须按实测屏幕算，不能硬编码：换到小屏后硬编码坐标会落到屏外，
    # 于是「真实鼠标点击没到达探针」被误记成输入通道失效（2026-11-08 实测踩过）。
    $screenW = [int][QaNative]::ScreenWidth()
    $screenH = [int][QaNative]::ScreenHeight()
    $probeW = [Math]::Min(420, [Math]::Max(200, $screenW - 40))
    $probeH = [Math]::Min(260, [Math]::Max(160, $screenH - 40))
    $probeX = [Math]::Max(0, $screenW - $probeW - 20)
    $probeY = 20
    $probeHwnd = [QaProbe]::Start($probeX, $probeY, $probeW, $probeH)
    Start-Sleep -Milliseconds 500
    $probeRec = [ordered]@{
        started = ($probeHwnd -ne [IntPtr]::Zero)
        hwnd = $probeHwnd.ToInt64()
        visible = [QaNative]::IsWindowVisible($probeHwnd)
        rect = [QaNative]::RectOf($probeHwnd)
        foregroundBefore = [QaNative]::GetForegroundWindow().ToInt64()
    }
    Write-Ev ("探针窗口 hwnd={0} visible={1} rect={2}" -f $probeRec.hwnd, $probeRec.visible, ($probeRec.rect -join ','))

    if ($probeHwnd -eq [IntPtr]::Zero) {
        $script:Channel.reason = 'probe-window-create-failed'
        Add-Row -Id 'CH-01' -Title '输入通道自证：真实鼠标点击到达已知可用窗口' -Verdict '失败' `
            -Evidence '探针窗口创建失败，无法建立对照' -Data $probeRec
        Add-Row -Id 'CH-02' -Title '输入通道自证：真实键盘按键到达已知可用窗口' -Verdict '失败' `
            -Evidence '探针窗口创建失败，无法建立对照' -Data $probeRec
    }
    else {
        [void][QaNative]::SetForegroundWindow($probeHwnd)
        Start-Sleep -Milliseconds 300

        $pc = [ordered]@{ x = $probeX + [int]($probeW / 2); y = $probeY + [int]($probeH / 2) }
        $wfpProbe = [QaNative]::WindowFromPointRaw($pc.x, $pc.y)
        $probeRec.windowFromPointAtCenter = $wfpProbe.ToInt64()
        $probeRec.windowFromPointIsProbe = ($wfpProbe -eq $probeHwnd)
        $probeRec.cursorBefore = [QaNative]::CursorPosStr()
        $n1 = [QaInput]::LeftClick($pc.x, $pc.y)
        Start-Sleep -Milliseconds 250
        $probeRec.cursorAfter = [QaNative]::CursorPosStr()
        $probeRec.sendInputReturned = $n1
        $probeRec.mouseDown = [QaProbe]::MouseDown
        $probeRec.mouseUp = [QaProbe]::MouseUp
        $probeRec.foregroundAfterClick = [QaNative]::GetForegroundWindow().ToInt64()
        $probeRec.foregroundIsProbe = (([QaNative]::GetForegroundWindow()) -eq $probeHwnd)

        Add-Step -Id 'channel-mouse' -Data $probeRec

        $mouseOk = ($probeRec.mouseDown -ge 1) -and ($probeRec.mouseUp -ge 1) -and $probeRec.windowFromPointIsProbe
        if ($mouseOk) {
            $script:Channel.mouse = $true
            Add-Row -Id 'CH-01' -Title '输入通道自证：真实鼠标点击到达已知可用窗口' -Verdict '通过' `
                -Evidence ("探针收到 WM_LBUTTONDOWN={0} / WM_LBUTTONUP={1}；WindowFromPoint({2},{3})=探针；光标 {4}→{5}" -f `
                    $probeRec.mouseDown, $probeRec.mouseUp, $pc.x, $pc.y, $probeRec.cursorBefore, $probeRec.cursorAfter) `
                -Data $probeRec
        }
        else {
            Add-Row -Id 'CH-01' -Title '输入通道自证：真实鼠标点击到达已知可用窗口' -Verdict '失败' `
                -Evidence ("探针窗口未收到鼠标消息（down={0} up={1} WindowFromPointIsProbe={2}）" -f `
                    $probeRec.mouseDown, $probeRec.mouseUp, $probeRec.windowFromPointIsProbe) -Data $probeRec
        }

        # 键盘对照：Tab / Enter / Space
        [QaProbe]::ResetStats()
        [void][QaNative]::SetForegroundWindow($probeHwnd)
        Start-Sleep -Milliseconds 250
        $kRec = [ordered]@{ foreground = [QaNative]::GetForegroundWindow().ToInt64(); isProbe = (([QaNative]::GetForegroundWindow()) -eq $probeHwnd) }
        $kRec.tab = (Invoke-RealKey -Name 'Tab' -Vk 0x09)
        Start-Sleep -Milliseconds 120
        $kRec.enter = (Invoke-RealKey -Name 'Enter' -Vk 0x0D)
        Start-Sleep -Milliseconds 120
        $kRec.space = (Invoke-RealKey -Name 'Space' -Vk 0x20)
        Start-Sleep -Milliseconds 250
        $kRec.keyDown = [QaProbe]::KeyDown
        $kRec.charCount = [QaProbe]::CharCount
        $kRec.focusIn = [QaProbe]::FocusIn
        $kRec.keys = [QaProbe]::Keys

        Add-Step -Id 'channel-keyboard' -Data $kRec

        $kbOk = ($kRec.keyDown -ge 3) -and $kRec.isProbe
        if ($kbOk) {
            $script:Channel.keyboard = $true
            Add-Row -Id 'CH-02' -Title '输入通道自证：真实键盘按键到达已知可用窗口' -Verdict '通过' `
                -Evidence ("探针收到 WM_KEYDOWN={0}（Tab/Enter/Space），探测器前台={1}，字符消息={2}" -f `
                    $kRec.keyDown, $kRec.isProbe, $kRec.charCount) -Data $kRec
        }
        else {
            Add-Row -Id 'CH-02' -Title '输入通道自证：真实键盘按键到达已知可用窗口' -Verdict '失败' `
                -Evidence ("探针键盘消息不足：WM_KEYDOWN={0}，前台={1}（需窗口可被置前）" -f $kRec.keyDown, $kRec.isProbe) -Data $kRec
        }
    }

    [QaProbe]::Stop()
    Start-Sleep -Milliseconds 300

    $script:Channel.input = ($script:Channel.mouse -or $script:Channel.keyboard)
    if (-not $script:Channel.input) {
        $script:Channel.reason = 'positive-control-failed'
        Write-Ev '输入通道对照失败：所有依赖真实 SendInput 的行将判「未验证」，不用 UIA Invoke 顶替。' 'Yellow'
    }
    else {
        $script:Channel.reason = 'positive-control-passed'
    }

    # ------------------------------------------------------------------
    # 4. UIA 清点：Dock 项语义
    # ------------------------------------------------------------------
    Write-Ev '--- 步骤 4：UIA 清点 Dock 项语义 ---' 'Cyan'
    $rootEl = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
    $allEls = @(Get-AllElements $rootEl)
    Save-Json -Path (Join-Path $evidenceDir 'uia-inventory.json') -Object $allEls
    Write-Ev ("UIA 子树元素数={0}，清单已落盘 uia-inventory.json" -f $allEls.Count)

    $dockMap = Get-DockElementMap -RootEl $rootEl -WindowRect $windowRect -Scale $script:Scale
    $dockFoundById = 0; $dockFoundByName = 0; $dockMissing = @()
    $dockSummary = [ordered]@{}
    foreach ($k in $script:DockOrder) {
        $e = $dockMap[$k]
        if ($e -and $e.source -eq 'AutomationId') { $dockFoundById++ }
        elseif ($e -and $e.source -eq 'NameFallback') { $dockFoundByName++ }
        else { $dockMissing += $k }
        $dockSummary[$k] = if ($e) {
            [ordered]@{
                source = $e.source; name = $e.info.name; automationId = $e.info.automationId
                controlType = $e.info.controlType; itemStatus = $e.info.itemStatus
                focusable = $e.info.focusable; h = $e.info.h; w = $e.info.w
                l = $e.info.l; t = $e.info.t; runtimeId = $e.info.runtimeId
                patterns = $e.info.patterns
            }
        }
        else { $null }
    }
    Save-Json -Path (Join-Path $evidenceDir 'dock-elements.json') -Object $dockSummary
    Write-Ev ("Dock 元素：AutomationId 命中 {0}/4，Name 兜底命中 {1}/4，缺失 {2}" -f `
            $dockFoundById, $dockFoundByName, ($(if ($dockMissing.Count) { $dockMissing -join ',' } else { '无' })))

    if ($dockFoundById -eq 4) {
        Add-Row -Id 'UIA-01' -Title 'UIA 语义：四项以 PowerDock.Dock.* AutomationId 暴露' -Verdict '通过' `
            -Evidence ("四项 AutomationId 全部命中：{0}" -f (($script:DockOrder | ForEach-Object { $script:DockSpec[$_].id }) -join ', ')) `
            -Data $dockSummary
    }
    else {
        $desc = @()
        foreach ($k in $script:DockOrder) {
            $e = $dockMap[$k]
            if ($e) { $desc += ('{0}→{1}[{2}|{3}]' -f $k, $e.source, $e.info.controlType, $e.info.name) }
            else { $desc += ('{0}→缺失' -f $k) }
        }
        Add-Row -Id 'UIA-01' -Title 'UIA 语义：四项以 PowerDock.Dock.* AutomationId 暴露' -Verdict '失败' `
            -Evidence ("AutomationId 命中 {0}/4；实际定位结果：{1}" -f $dockFoundById, ($desc -join '; ')) -Data $dockSummary
    }

    $nameOk = $true; $statusOk = $true
    foreach ($k in $script:DockOrder) {
        $e = $dockMap[$k]
        if (-not $e) { $nameOk = $false; $statusOk = $false; continue }
        if ($e.info.name -ne $script:DockSpec[$k].label) { $nameOk = $false }
        if (@('已选中', '未选中') -notcontains $e.info.itemStatus) { $statusOk = $false }
    }
    $initSelected = @($script:DockOrder | Where-Object { $dockMap[$_] -and $dockMap[$_].info.itemStatus -eq '已选中' })
    $uiaNameEv = @()
    foreach ($k in $script:DockOrder) {
        $e = $dockMap[$k]
        if ($e) { $uiaNameEv += ('{0}:Name="{1}" ItemStatus="{2}"' -f $k, $e.info.name, $e.info.itemStatus) }
    }
    $uiaNameEv += ('初始已选中=[' + ($initSelected -join ',') + ']（期望恰好 Overview）')
    if ($nameOk -and $statusOk -and $initSelected.Count -eq 1 -and $initSelected[0] -eq 'Overview') {
        Add-Row -Id 'UIA-02' -Title 'UIA 语义：Name=概览/设备/组件/设置，ItemStatus=已选中/未选中，初始唯一选中 Overview' `
            -Verdict '通过' -Evidence ($uiaNameEv -join '; ') -Data $dockSummary
    }
    else {
        Add-Row -Id 'UIA-02' -Title 'UIA 语义：Name=概览/设备/组件/设置，ItemStatus=已选中/未选中，初始唯一选中 Overview' `
            -Verdict '失败' -Evidence ($uiaNameEv -join '; ') -Data $dockSummary
    }

    # UIA InvokePattern 旁证（不作为真实输入的替代）
    $invokeAvail = @($script:DockOrder | Where-Object {
            $dockMap[$_] -and (@($dockMap[$_].info.patterns) -contains 'InvokePatternIdentifiers.Pattern')
        })
    Add-Row -Id 'UIA-03' -Title 'UIA 旁证：Dock 项是否暴露 InvokePattern（仅记录，不顶替真实输入）' `
        -Verdict $(if ($invokeAvail.Count -eq 4) { '通过' } else { '失败' }) `
        -Evidence ("暴露 InvokePattern 的项：{0}/4" -f $invokeAvail.Count) -Data @{ invokeAvailable = $invokeAvail }

    # ------------------------------------------------------------------
    # 5. 鼠标点击（三种点位）
    # ------------------------------------------------------------------
    Write-Ev '--- 步骤 5：真实鼠标点击 ---' 'Cyan'

    $mouseSeqResults = New-Object System.Collections.ArrayList
    $pointKindRows = [ordered]@{ center = $null; leftInner = $null; rightInner = $null }

    # 先让产品窗口前台（点击本身也会激活）
    $fgInfo = Set-TargetForeground -Hwnd $hwnd -WindowRect $windowRect -Scale $script:Scale
    Write-Ev ("置前结果：{0}（method={1}）" -f $fgInfo.ok, $fgInfo.method)
    $r0 = [QaNative]::RectOf($hwnd)
    $windowRect = [ordered]@{ Left = $r0[0]; Top = $r0[1]; Width = $r0[2]; Height = $r0[3] }

    # 重新取一次 Dock 元素（窗口可能因置前点击而未移动，但矩形以最新为准）
    $dockMap = Get-DockElementMap -RootEl $rootEl -WindowRect $windowRect -Scale $script:Scale

    $pointKinds = @('center', 'leftInner', 'rightInner')
    $pointKindLabels = @{ center = '中心点（文字点）'; leftInner = '靠左内边距点'; rightInner = '靠右内边距点' }

    $initialSnap = Get-WindowSnapshot -Hwnd $hwnd -WindowRect $windowRect -Scale $script:Scale
    Add-Step -Id 'initial-snapshot' -Data $initialSnap

    $m01Steps = New-Object System.Collections.ArrayList
    $m02Steps = New-Object System.Collections.ArrayList

    foreach ($kind in $pointKinds) {
        $kindSteps = New-Object System.Collections.ArrayList
        $clickSeq = @('Overview', 'Devices', 'Widgets', 'Settings', 'Overview')
        foreach ($key in $clickSeq) {
            $e = $dockMap[$key]
            if (-not $e) {
                [void]$kindSteps.Add([ordered]@{ dock = $key; kind = $kind; skipped = 'element-missing' })
                continue
            }
            $pts = Get-ClickPoints -Info $e.info -Scale $script:Scale `
                -WindowLeft $windowRect.Left -WindowTop $windowRect.Top -WindowW $windowRect.Width -WindowH $windowRect.Height
            $p = $pts[$kind]
            $before = Get-WindowSnapshot -Hwnd $hwnd -WindowRect $windowRect -Scale $script:Scale
            $click = Invoke-RealClick -X $p.x -Y $p.y -TargetHwnd $hwnd -Note ("{0}/{1}" -f $kind, $key)
            $click.foregroundBefore = $before.foreground
            $click.inWindow = $p.inWindow
            $click.inElement = $p.inElement
            Start-Sleep -Milliseconds $SettleMs
            $after = Get-WindowSnapshot -Hwnd $hwnd -WindowRect $windowRect -Scale $script:Scale
            $click.foregroundAfter = $after.foreground

            $rec = [ordered]@{
                kind = $kind; dock = $key
                clickPoint = $p
                click = $click
                titleBefore = $before.pageTitle; titleAfter = $after.pageTitle
                titleExpectedMatch = (Test-TitleMatches -Title $after.pageTitle -DockKey $key)
                selectedBefore = $before.selected; selectedAfter = $after.selected
                selectionUniqueAfter = ($after.selected.Count -eq 1)
                selectionIsTargetAfter = ($after.selected.Count -eq 1 -and $after.selected[0] -eq $key)
                fingerprintBefore = $before.fingerprint
                fingerprintAfter = $after.fingerprint
                fingerprintChanged = ($before.fingerprint -ne $after.fingerprint)
                pageTitleRuntimeIdBefore = $before.pageTitleRuntimeId
                pageTitleRuntimeIdAfter = $after.pageTitleRuntimeId
                pageTitleSameInstance = ($before.pageTitleRuntimeId -and $before.pageTitleRuntimeId -eq $after.pageTitleRuntimeId)
            }
            [void]$kindSteps.Add($rec)
            [void]$mouseSeqResults.Add($rec)
            if ($kind -eq 'center') { [void]$m01Steps.Add($rec); [void]$m02Steps.Add($rec) }
        }
        Add-Step -Id ('mouse-' + $kind) -Data $kindSteps
        $pointKindRows[$kind] = $kindSteps
    }

    Save-Json -Path (Join-Path $evidenceDir 'mouse-steps.json') -Object $mouseSeqResults

    # --- M-01 页面标题 ---
    $titleOkSteps = @($m01Steps | Where-Object { $_.titleExpectedMatch })
    $m01Ev = @()
    foreach ($s in $m01Steps) {
        $m01Ev += ('{0}: 标题 "{1}"（期望含"{2}"）{3}' -f $s.dock, $s.titleAfter, $script:DockSpec[$s.dock].label,
            $(if ($s.titleExpectedMatch) { 'OK' } else { 'NG' }))
    }
    $m01Verdict = '未验证'
    if ($mouseSeqResults.Count -eq 0) {
        Add-Row -Id 'M-01' -Title '鼠标依次点击四项再回概览：每次页面标题正确' -Verdict '未验证' `
            -Evidence '无法定位任何 Dock 元素，未产生点击' -Data @{ dockSummary = $dockSummary }
    }
    else {
        $m01Verdict = '失败'
        if (-not $script:Channel.mouse) { $m01Verdict = '未验证' }
        elseif ($titleOkSteps.Count -eq $m01Steps.Count -and $m01Steps.Count -gt 0) { $m01Verdict = '通过' }
        Add-Row -Id 'M-01' -Title '鼠标依次点击四项再回概览：每次页面标题正确' `
            -Verdict $m01Verdict -Evidence (($m01Ev -join ' | ') + $(if (-not $script:Channel.mouse) { ' [鼠标通道对照未通过→未验证]' } else { '' })) `
            -Data $m01Steps
    }

    # --- M-02 唯一选中 ---
    $m02Ev = @()
    foreach ($s in $m02Steps) {
        $m02Ev += ('点{0}→选中[{1}]' -f $s.dock, ($s.selectedAfter -join ','))
    }
    $m02Ok = ($m02Steps.Count -gt 0) -and (@($m02Steps | Where-Object { $_.selectionIsTargetAfter }).Count -eq $m02Steps.Count)
    if (-not $script:Channel.mouse -and $m02Steps.Count -gt 0) {
        Add-Row -Id 'M-02' -Title '鼠标点击后：仅当前项 ItemStatus=已选中' -Verdict '未验证' `
            -Evidence (($m02Ev -join ' | ') + ' [鼠标通道对照未通过→未验证]') -Data $m02Steps
    }
    else {
        Add-Row -Id 'M-02' -Title '鼠标点击后：仅当前项 ItemStatus=已选中' `
            -Verdict $(if ($m02Ok) { '通过' } else { '失败' }) -Evidence ($m02Ev -join ' | ') -Data $m02Steps
    }

    # --- M-03/04/05 三种点位 ---
    $kindRowIds = @{ center = 'M-03'; leftInner = 'M-04'; rightInner = 'M-05' }
    $kindTitles = @{
        center     = '鼠标：按钮文字中心点点击可切页'
        leftInner  = '鼠标：按钮内部靠左留白点点击可切页'
        rightInner = '鼠标：按钮内部靠右留白点点击可切页'
    }
    foreach ($kind in $pointKinds) {
        $steps = $pointKindRows[$kind]
        $okSteps = @($steps | Where-Object { -not $_.Contains('skipped') -and $_.selectionIsTargetAfter -and $_.titleExpectedMatch })
        $skipped = @($steps | Where-Object { $_.Contains('skipped') })
        $ev = @()
        foreach ($s in $steps) {
            if ($s.Contains('skipped')) { $ev += ('{0}: 跳过({1})' -f $s.dock, $s['skipped']); continue }
            $ev += ('{0}: 选中=[{1}] 标题="{2}" 点({3},{4}) inElement={5}' -f `
                    $s.dock, ($s.selectedAfter -join ','), $s.titleAfter, $s.clickPoint.x, $s.clickPoint.y, $s.click.inElement)
        }
        $verdict = '失败'
        if (-not $script:Channel.mouse) { $verdict = '未验证' }
        elseif ($skipped.Count -eq $steps.Count) { $verdict = '未验证' }
        elseif ($okSteps.Count -eq ($steps.Count - $skipped.Count) -and ($steps.Count - $skipped.Count) -gt 0) { $verdict = '通过' }
        Add-Row -Id $kindRowIds[$kind] -Title $kindTitles[$kind] -Verdict $verdict -Evidence ($ev -join ' | ') -Data $steps
    }

    # --- M-06 内容指纹兜底（不依赖实现是否暴露 PageTitle：页面是否真的换了）---
    $fpSeq = @($m01Steps | ForEach-Object { $_.fingerprintAfter })
    $fpInit = if ($m01Steps.Count -gt 0) { @($m01Steps)[0].fingerprintBefore } else { '' }
    $fpDistinct = @($fpSeq | Sort-Object -Unique)
    $fpFinal = if ($m01Steps.Count -gt 0) { @($m01Steps)[-1].fingerprintAfter } else { '' }
    $fpFinalEqualsInit = ($fpFinal -eq $fpInit)
    $m06Ok = ($m01Steps.Count -eq 5) -and ($fpDistinct.Count -ge 3) -and $fpFinalEqualsInit
    $fpEv = @()
    if ($m01Steps.Count -gt 0) {
        $fpEv += ('概览(初始)={0}' -f (Get-ShortHash $fpInit))
        foreach ($s in $m01Steps) { $fpEv += ('点{0}→{1}' -f $s.dock, (Get-ShortHash $s.fingerprintAfter)) }
    }
    $m06Verdict = '失败'
    if (-not $script:Channel.mouse) { $m06Verdict = '未验证' }
    elseif ($m01Steps.Count -eq 0) { $m06Verdict = '未验证' }
    elseif ($m06Ok) { $m06Verdict = '通过' }
    Add-Row -Id 'M-06' -Title '内容指纹兜底：概览→设备→组件→设置→概览 指纹序列确有变化且回到初值' `
        -Verdict $m06Verdict `
        -Evidence (($fpEv -join ' | ') + (' ; 去重后指纹数={0}（需≥3） 末值等于初值={1}' -f $fpDistinct.Count, $fpFinalEqualsInit)) `
        -Data @{ fingerprints = $fpEv; distinct = $fpDistinct.Count; finalEqualsInit = $fpFinalEqualsInit }

    # ------------------------------------------------------------------
    # 6. 键盘：Tab / Enter / Space
    # ------------------------------------------------------------------
    Write-Ev '--- 步骤 6：真实键盘 ---' 'Cyan'
    $kbSteps = New-Object System.Collections.ArrayList
    $tabOrder = New-Object System.Collections.ArrayList
    $tabHitAll = $false

    if (-not $script:Channel.keyboard) {
        Add-Row -Id 'K-01' -Title 'Tab 能聚焦 Dock 项且 UIA HasKeyboardFocus=true' -Verdict '未验证' `
            -Evidence '键盘通道对照（自建探针窗口）未通过，真实键盘结论不成立' -Data $null
        Add-Row -Id 'K-02' -Title 'Enter 激活当前焦点 Dock 项' -Verdict '未验证' `
            -Evidence '键盘通道对照未通过，真实键盘结论不成立' -Data $null
        Add-Row -Id 'K-03' -Title 'Space 激活当前焦点 Dock 项' -Verdict '未验证' `
            -Evidence '键盘通道对照未通过，真实键盘结论不成立' -Data $null
    }
    else {
        $fgInfo = Set-TargetForeground -Hwnd $hwnd -WindowRect $windowRect -Scale $script:Scale
        $fgIsTarget = (([QaNative]::GetForegroundWindow()) -eq $hwnd)
        Write-Ev ("键盘前置：置前={0} method={1}；当前前台={2}" -f $fgIsTarget, $fgInfo.method, ([QaNative]::GetForegroundWindow().ToInt64()))

        if (-not $fgIsTarget) {
            Add-Row -Id 'K-01' -Title 'Tab 能聚焦 Dock 项且 UIA HasKeyboardFocus=true' -Verdict '未验证' `
                -Evidence '无法把产品窗口置为前台，键盘输入结论不成立' -Data @{ foreground = [QaNative]::GetForegroundWindow().ToInt64() }
            Add-Row -Id 'K-02' -Title 'Enter 激活当前焦点 Dock 项' -Verdict '未验证' -Evidence '产品窗口未能置前' -Data $null
            Add-Row -Id 'K-03' -Title 'Space 激活当前焦点 Dock 项' -Verdict '未验证' -Evidence '产品窗口未能置前' -Data $null
        }
        else {
            $seen = @{}
            for ($i = 1; $i -le 14; $i++) {
                $k = Invoke-RealKey -Name 'Tab' -Vk 0x09
                Start-Sleep -Milliseconds 200
                $snap = Get-WindowSnapshot -Hwnd $hwnd -WindowRect $windowRect -Scale $script:Scale
                $focusId = if ($snap.focus) { $snap.focus.automationId } else { '' }
                $focusName = if ($snap.focus) { $snap.focus.name } else { '' }
                $hitKey = $null
                foreach ($dk in $script:DockOrder) {
                    if ($focusId -eq $script:DockSpec[$dk].id) { $hitKey = $dk }
                }
                if (-not $hitKey) {
                    foreach ($dk in $script:DockOrder) {
                        if ($focusName -eq $script:DockSpec[$dk].label) { $hitKey = $dk }
                    }
                }
                # UIA 焦点元素自身的 HasKeyboardFocus：既是判据，也是「该属性在本进程确实可读」的对照
                $focusHasFocus = if ($snap.focus) { [bool]$snap.focus.hasFocus } else { $false }
                $focusedDockHasFocus = $false
                if ($hitKey) {
                    $focusedDockHasFocus = $focusHasFocus
                    $seen[$hitKey] = $true
                }
                [void]$tabOrder.Add([ordered]@{
                        step = $i; focusName = $focusName; focusAutomationId = $focusId
                        focusControlType = if ($snap.focus) { $snap.focus.controlType } else { '' }
                        focusPid = if ($snap.focus) { $snap.focus.pid } else { 0 }
                        focusHasFocus = $focusHasFocus
                        matchedDock = $hitKey; dockHasKeyboardFocus = $focusedDockHasFocus
                        keyRec = $k
                        selectedAfter = $snap.selected
                    })
                if ($seen.Keys.Count -eq 4) { $tabHitAll = $true; break }
            }
            Add-Step -Id 'tab-order' -Data $tabOrder
            Save-Json -Path (Join-Path $evidenceDir 'tab-order.json') -Object $tabOrder

            $tabEv = @()
            foreach ($t in $tabOrder) {
                $tabEv += ('Tab#{0}→"{1}"({2}) dock={3} HasKeyboardFocus={4}' -f $t.step, $t.focusName, $t.focusAutomationId, $t.matchedDock, $t.focusHasFocus)
            }
            $tabFocusOk = (@($tabOrder | Where-Object { $_.matchedDock -and $_.dockHasKeyboardFocus }).Count -gt 0)
            $k01Ok = $tabHitAll -and $tabFocusOk
            Add-Row -Id 'K-01' -Title 'Tab 能聚焦 Dock 项且 UIA HasKeyboardFocus=true' `
                -Verdict $(if ($k01Ok) { '通过' } else { '失败' }) `
                -Evidence (($tabEv -join ' | ') + (' ; 四项全部可达={0}，至少一项 HasKeyboardFocus=true={1}' -f $tabHitAll, $tabFocusOk)) `
                -Data $tabOrder

            # Enter / Space：Tab 把焦点移到指定项后，才按真实按键（未到达则绝不按键，避免误触关闭按钮）
            function Invoke-FocusAndKey {
                param([string]$DockKey, [string]$KeyName, [int]$Vk)
                $targetId = $script:DockSpec[$DockKey].id
                $targetLabel = $script:DockSpec[$DockKey].label
                $reached = $false
                $tabTrail = New-Object System.Collections.ArrayList
                for ($i = 1; $i -le 14; $i++) {
                    $snap = Get-WindowSnapshot -Hwnd $hwnd -WindowRect $windowRect -Scale $script:Scale
                    $fid = if ($snap.focus) { $snap.focus.automationId } else { '' }
                    $fname = if ($snap.focus) { $snap.focus.name } else { '' }
                    [void]$tabTrail.Add($fname)
                    if ($fid -eq $targetId -or $fname -eq $targetLabel) { $reached = $true; break }
                    $null = Invoke-RealKey -Name 'Tab' -Vk 0x09
                    Start-Sleep -Milliseconds 180
                }
                $before = Get-WindowSnapshot -Hwnd $hwnd -WindowRect $windowRect -Scale $script:Scale
                $focusName = if ($before.focus) { $before.focus.name } else { '' }
                $focusId = if ($before.focus) { $before.focus.automationId } else { '' }
                $keyRec = $null
                $pressed = $false
                if ($reached) {
                    $keyRec = Invoke-RealKey -Name $KeyName -Vk $Vk
                    $pressed = $true
                    Start-Sleep -Milliseconds $SettleMs
                }
                $after = Get-WindowSnapshot -Hwnd $hwnd -WindowRect $windowRect -Scale $script:Scale
                return [ordered]@{
                    dock              = $DockKey
                    keyName           = $KeyName
                    reached           = $reached
                    pressed           = $pressed
                    tabTrail          = @($tabTrail)
                    focusNameBefore   = $focusName
                    focusIdBefore     = $focusId
                    keyRec            = $keyRec
                    selectedBefore    = $before.selected
                    selectedAfter     = $after.selected
                    titleBefore       = $before.pageTitle
                    titleAfter        = $after.pageTitle
                    selectionIsTargetAfter = ($after.selected.Count -eq 1 -and $after.selected[0] -eq $DockKey)
                    titleExpectedMatch     = (Test-TitleMatches -Title $after.pageTitle -DockKey $DockKey)
                }
            }

            # Space 先在「设备」上制造可观测变化；Enter 再把焦点移到「概览」并按 Enter 切回
            $spaceRec = Invoke-FocusAndKey -DockKey 'Devices' -KeyName 'Space' -Vk 0x20
            Start-Sleep -Milliseconds 300
            $enterRec = Invoke-FocusAndKey -DockKey 'Overview' -KeyName 'Enter' -Vk 0x0D
            Add-Step -Id 'keyboard-enter-space' -Data @{ enter = $enterRec; space = $spaceRec }
            Save-Json -Path (Join-Path $evidenceDir 'keyboard-enter-space.json') -Object @{ enter = $enterRec; space = $spaceRec }

            $enterOk = $enterRec.reached -and $enterRec.pressed -and $enterRec.selectionIsTargetAfter -and $enterRec.titleExpectedMatch
            $spaceOk = $spaceRec.reached -and $spaceRec.pressed -and $spaceRec.selectionIsTargetAfter -and $spaceRec.titleExpectedMatch
            Add-Row -Id 'K-02' -Title 'Enter 激活当前焦点 Dock 项' -Verdict $(if ($enterOk) { '通过' } else { '失败' }) `
                -Evidence ("Tab 轨迹=[{5}]；到达={2} 已按键={6}；按 Enter 后选中=[{3}] 标题=""{4}""" -f `
                    $enterRec.focusNameBefore, $enterRec.focusIdBefore, $enterRec.reached, ($enterRec.selectedAfter -join ','), $enterRec.titleAfter, ($enterRec.tabTrail -join '>'), $enterRec.pressed) `
                -Data $enterRec
            Add-Row -Id 'K-03' -Title 'Space 激活当前焦点 Dock 项' -Verdict $(if ($spaceOk) { '通过' } else { '失败' }) `
                -Evidence ("Tab 轨迹=[{5}]；到达={2} 已按键={6}；按 Space 后选中=[{3}] 标题=""{4}""" -f `
                    $spaceRec.focusNameBefore, $spaceRec.focusIdBefore, $spaceRec.reached, ($spaceRec.selectedAfter -join ','), $spaceRec.titleAfter, ($spaceRec.tabTrail -join '>'), $spaceRec.pressed) `
                -Data $spaceRec
        }
    }

    # ------------------------------------------------------------------
    # 7. 重复点击当前项不重置
    # ------------------------------------------------------------------
    Write-Ev '--- 步骤 7：重复点击当前项 ---' 'Cyan'
    if (-not $script:Channel.mouse) {
        Add-Row -Id 'R-01' -Title '重复点击当前项不重置页面（标题/元素身份/选中态不变）' -Verdict '未验证' `
            -Evidence '鼠标通道对照未通过，真实鼠标结论不成立' -Data $null
    }
    elseif (-not $dockMap['Devices']) {
        Add-Row -Id 'R-01' -Title '重复点击当前项不重置页面（标题/元素身份/选中态不变）' -Verdict '未验证' `
            -Evidence '未定位到 Devices 项，无法执行重复点击' -Data $null
    }
    else {
        # 先切到 Devices
        $e = $dockMap['Devices']
        $pts = Get-ClickPoints -Info $e.info -Scale $script:Scale `
            -WindowLeft $windowRect.Left -WindowTop $windowRect.Top -WindowW $windowRect.Width -WindowH $windowRect.Height
        $p = $pts['center']
        $null = Invoke-RealClick -X $p.x -Y $p.y -TargetHwnd $hwnd -Note 'repeat:set-Devices'
        Start-Sleep -Milliseconds $SettleMs
        $base = Get-WindowSnapshot -Hwnd $hwnd -WindowRect $windowRect -Scale $script:Scale

        $repeats = New-Object System.Collections.ArrayList
        for ($i = 1; $i -le 3; $i++) {
            $null = Invoke-RealClick -X $p.x -Y $p.y -TargetHwnd $hwnd -Note ('repeat:{0}' -f $i)
            Start-Sleep -Milliseconds $SettleMs
            $s = Get-WindowSnapshot -Hwnd $hwnd -WindowRect $windowRect -Scale $script:Scale
            [void]$repeats.Add([ordered]@{
                    iteration = $i
                    title = $s.pageTitle
                    titleSame = ($s.pageTitle -eq $base.pageTitle)
                    pageTitleRuntimeId = $s.pageTitleRuntimeId
                    pageTitleSameInstance = ($s.pageTitleRuntimeId -eq $base.pageTitleRuntimeId)
                    selected = $s.selected
                    selectionSame = (($s.selected -join ',') -eq ($base.selected -join ','))
                    fingerprintSame = ($s.fingerprint -eq $base.fingerprint)
                    fingerprintIdsSame = (($s.fingerprintIds -join ',') -eq ($base.fingerprintIds -join ','))
                })
        }
        Add-Step -Id 'repeat-click' -Data @{ baseline = $base; repeats = $repeats }
        Save-Json -Path (Join-Path $evidenceDir 'repeat-click.json') -Object @{ baseline = $base; repeats = $repeats }

        $rOk = (@($repeats | Where-Object {
                    $_.titleSame -and $_.pageTitleSameInstance -and $_.selectionSame -and $_.fingerprintSame -and $_.fingerprintIdsSame
                }).Count -eq $repeats.Count)
        $rEv = @()
        foreach ($x in $repeats) {
            $rEv += ('#{0}: 标题 "{1}" same={2} 同一实例={3} 选中same={4} 指纹same={5} 元素身份same={6}' -f `
                    $x.iteration, $x.title, $x.titleSame, $x.pageTitleSameInstance, $x.selectionSame, $x.fingerprintSame, $x.fingerprintIdsSame)
        }
        $rEvidence = ("基线：标题=""{0}"" 选中=[{1}]；" -f $base.pageTitle, ($base.selected -join ',')) + ($rEv -join ' | ')
        # 防「空洞通过」：若基线根本不切页（M-01 未通过），"重复点击页面不变" 是恒真命题，不能算通过
        $rVerdict = '失败'
        if ($m01Verdict -ne '通过') {
            $rVerdict = '未验证'
            $rEvidence += ' [防空：M-01 未通过（页面本就不切换），"不重置"为恒真，改判未验证]'
        }
        elseif ($rOk) { $rVerdict = '通过' }
        Add-Row -Id 'R-01' -Title '重复点击当前项不重置页面（标题/元素身份/选中态不变）' `
            -Verdict $rVerdict -Evidence $rEvidence -Data @{ baseline = $base; repeats = $repeats }
    }

    # ------------------------------------------------------------------
    # 8. 来回切换后仅 1 个主窗口
    # ------------------------------------------------------------------
    Write-Ev '--- 步骤 8：进程与顶层窗口计数 ---' 'Cyan'
    $procsEnd = @(Get-PowerDockProcesses)
    $winsEnd = @(Get-TopLevelWindowInfos -ProcessId $targetPid | Where-Object { $_.visible })
    Add-Step -Id 'window-count' -Data @{ processes = $procsEnd; visibleTopLevel = $winsEnd }
    Save-Json -Path (Join-Path $evidenceDir 'window-count.json') -Object @{ processes = $procsEnd; visibleTopLevel = $winsEnd }

    $wEv = @()
    $wEv += ('PowerDock 进程数={0}' -f $procsEnd.Count)
    foreach ($p in $procsEnd) { $wEv += ('  pid={0} path={1}' -f $p.pid, $p.path) }
    $wEv += ('目标 pid={0} 的可见顶层窗口数={1}' -f $targetPid, $winsEnd.Count)
    foreach ($w in $winsEnd) { $wEv += ('  hwnd={0} cls={1} title="{2}" {3},{4} {5}x{6}' -f $w.hwnd, $w.cls, $w.title, $w.l, $w.t, $w.w, $w.h) }
    $wOk = ($procsEnd.Count -eq 1) -and ($winsEnd.Count -eq 1)
    Add-Row -Id 'W-01' -Title '来回切换后仅 1 个 PowerDock 进程与 1 个可见主窗口' `
        -Verdict $(if ($wOk) { '通过' } else { '失败' }) -Evidence ($wEv -join ' | ') `
        -Data @{ processes = $procsEnd; visibleTopLevel = $winsEnd }
}
catch {
    $err = $_
    Write-Host ''
    Write-Host ('!! 运行异常：{0}' -f $err.Exception.Message) -ForegroundColor Red
    Write-Host ($err.ScriptStackTrace) -ForegroundColor DarkRed
    [void]$script:Log.Add('[ERROR] ' + $err.Exception.Message)
    [void]$script:Log.Add($err.ScriptStackTrace)
    Add-Row -Id 'RUN-ERROR' -Title '脚本运行异常' -Verdict '失败' -Evidence $err.Exception.Message -Data @{ stack = $err.ScriptStackTrace }
    $exitCode = 2
}
finally {
    # 结束必须杀进程
    $killedEnd = @()
    if (-not $KeepProcess) {
        $killedEnd = @(Stop-PowerDockAll)
    }
    try { [QaProbe]::Stop() } catch { }

    $verdictCounts = [ordered]@{ '通过' = 0; '失败' = 0; '未验证' = 0 }
    foreach ($r in $script:Rows) { $verdictCounts[$r.verdict] = $verdictCounts[$r.verdict] + 1 }

    $defectReproduced = $null
    $m01 = $script:Rows | Where-Object { $_.id -eq 'M-01' } | Select-Object -First 1
    if ($m01) {
        if ($m01.verdict -eq '失败') { $defectReproduced = $true }
        elseif ($m01.verdict -eq '通过') { $defectReproduced = $false }
    }

    $finalReport = [ordered]@{
        phase             = $Phase
        label             = $Label
        startedAt         = $script:StartTime.ToString('o')
        finishedAt        = (Get-Date).ToString('o')
        durationSec       = [Math]::Round(((Get-Date) - $script:StartTime).TotalSeconds, 1)
        scriptPath        = $PSCommandPath
        scriptSha256      = (Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash
        exePath           = $ExePath
        exeSha256         = (Get-FileHash -LiteralPath $ExePath -Algorithm SHA256).Hash
        evidenceDir       = $evidenceDir
        scale             = $script:Scale
        inputChannel      = $script:Channel
        verdictCounts     = $verdictCounts
        defectReproduced  = $defectReproduced
        killedBeforeStart = $killed
        killedAtEnd       = $killedEnd
        rows              = @($script:Rows)
    }

    if ($evidenceDir) {
        Save-Json -Path (Join-Path $evidenceDir 'report.json') -Object $finalReport
        Save-Json -Path (Join-Path $evidenceDir 'steps.json') -Object @($script:Steps)

        $md = New-Object System.Collections.ArrayList
        [void]$md.Add('# PowerDock Dock 导航 外部验证报告')
        [void]$md.Add('')
        [void]$md.Add(('- 阶段: {0}{1}' -f $Phase, $(if ($Label) { ' / ' + $Label } else { '' })))
        [void]$md.Add(('- 开始: {0}  结束: {1}  用时: {2}s' -f $finalReport.startedAt, $finalReport.finishedAt, $finalReport.durationSec))
        [void]$md.Add(('- 脚本: `{0}`  sha256={1}' -f $PSCommandPath, $finalReport.scriptSha256))
        [void]$md.Add(('- 被测 exe: `{0}`  sha256={1}' -f $ExePath, $finalReport.exeSha256))
        [void]$md.Add(('- 运行副本: `{0}`' -f $env0['qaExePath']))
        [void]$md.Add(('- 缩放: GetDpiForWindow={0} → {1}%' -f $env0['dpiForWindow'], ($script:Scale * 100)))
        [void]$md.Add(('- 输入通道: mouse={0} keyboard={1} ({2})' -f $script:Channel.mouse, $script:Channel.keyboard, $script:Channel.reason))
        [void]$md.Add(('- 结论计数: 通过={0} 失败={1} 未验证={2}' -f $verdictCounts['通过'], $verdictCounts['失败'], $verdictCounts['未验证']))
        [void]$md.Add(('- 缺陷复现（M-01 鼠标点击切页失败）: {0}' -f $defectReproduced))
        [void]$md.Add('')
        [void]$md.Add('| ID | 判据 | 结论 | 原始观测 |')
        [void]$md.Add('| --- | --- | --- | --- |')
        foreach ($r in $script:Rows) {
            $ev = ($r.evidence -replace '\|', '\|') -replace "`r?`n", ' '
            [void]$md.Add(('| {0} | {1} | {2} | {3} |' -f $r.id, $r.title, $r.verdict, $ev))
        }
        [void]$md.Add('')
        [void]$md.Add('## 运行日志')
        [void]$md.Add('```')
        foreach ($l in $script:Log) { [void]$md.Add($l) }
        [void]$md.Add('```')
        Save-TextFile -Path (Join-Path $evidenceDir 'report.md') -Text ($md -join "`n")
        Save-TextFile -Path (Join-Path $evidenceDir 'run.log') -Text (($script:Log -join "`n") + "`n")

        Write-Host ''
        Write-Host ('证据落盘: {0}' -f $evidenceDir) -ForegroundColor Cyan
    }

    Write-Host ''
    Write-Host '================ 结论汇总 ================' -ForegroundColor Cyan
    foreach ($r in $script:Rows) {
        $c = 'Gray'
        if ($r.verdict -eq '通过') { $c = 'Green' } elseif ($r.verdict -eq '失败') { $c = 'Red' } elseif ($r.verdict -eq '未验证') { $c = 'Yellow' }
        Write-Host ('  {0,-7} {1,-8} {2}' -f $r.id, $r.verdict, $r.title) -ForegroundColor $c
    }
    Write-Host ('  通过={0} 失败={1} 未验证={2}' -f $verdictCounts['通过'], $verdictCounts['失败'], $verdictCounts['未验证']) -ForegroundColor Cyan
    Write-Host ('  输入通道: mouse={0} keyboard={1}' -f $script:Channel.mouse, $script:Channel.keyboard) -ForegroundColor Cyan
    if ($defectReproduced -ne $null) {
        Write-Host ('  缺陷复现: {0}' -f $defectReproduced) -ForegroundColor Cyan
    }
    Write-Host '==========================================' -ForegroundColor Cyan

    if ($exitCode -eq 0) {
        $failCount = @($script:Rows | Where-Object { $_.verdict -eq '失败' }).Count
        if ($failCount -gt 0) { $exitCode = 1 }
    }
}

exit $exitCode
