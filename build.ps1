<#
.SYNOPSIS
    使用 .NET 10 SDK 构建 PowerDock（Core / App / Core.Tests）。

.DESCRIPTION
    S0 工程基线：不再使用 csc.exe（.NET Framework 编译器）。本脚本负责：
      1. 探测可用的 .NET 10 SDK（PATH -> DOTNET_ROOT -> 常规与就地安装目录）；
      2. 用 dotnet build 构建 PowerDock.sln，返回真实退出码与错误/警告计数；
      3. 打印明确产物路径。

    构建产物为「框架依赖」输出，需目标机有 .NET 10 运行时。
    需要可分发的自包含单文件包请用 pack.ps1。

.PARAMETER Configuration
    Debug（默认）或 Release。

.PARAMETER OutputDirectory
    可选。构建成功后把 App 的输出目录（exe / dll / deps.json / runtimeconfig.json）复制到该目录。

.PARAMETER NoRestore
    可选。跳过 NuGet 还原（使用上一次还原结果，离线或已还原时更快）。

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -Configuration Release -OutputDirectory .\artifacts\build
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',

    [string]$OutputDirectory,

    [switch]$NoRestore
)

$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$solution = Join-Path $root 'PowerDock.sln'
$appProject = Join-Path $root 'src\PowerDock.App\PowerDock.App.csproj'

. (Join-Path $root 'tools\dotnet-sdk.ps1')

$dotnet = Initialize-DotnetSdk -MinimumMajor 10
if (-not $dotnet) { exit 1 }

if (-not (Test-Path -LiteralPath $solution -PathType Leaf)) {
    Write-Host ''
    Write-Host (' 构建失败：找不到解决方案文件 {0}' -f $solution) -ForegroundColor Red
    Write-Host ''
    exit 1
}

Write-Host ''
Write-Host '== PowerDock 构建 ==' -ForegroundColor Cyan
Write-Host (' SDK 宿主  : {0}' -f $dotnet)
$sdkVersion = (@(& $dotnet --version 2>$null) -join '').Trim()
Write-Host (' SDK 版本  : {0}' -f $sdkVersion)
Write-Host (' 配置      : {0}' -f $Configuration)
Write-Host ''

$buildArgs = @('build', $solution, '-c', $Configuration, '--nologo', '-v', 'minimal')
if ($NoRestore) { $buildArgs += '--no-restore' }

$stopwatch = [Diagnostics.Stopwatch]::StartNew()
$lines = New-Object System.Collections.Generic.List[string]
& $dotnet @buildArgs 2>&1 | ForEach-Object {
    $text = $_.ToString()
    $lines.Add($text)
    Write-Host $text
}
$exitCode = $LASTEXITCODE
$stopwatch.Stop()

$output = $lines -join [Environment]::NewLine

function Get-CountFromOutput {
    param([string]$Text, [string[]]$Patterns)

    foreach ($pattern in $Patterns) {
        $match = [regex]::Match($Text, $pattern)
        if ($match.Success) { return [int]$match.Groups[1].Value }
    }
    return -1
}

$errorCount = Get-CountFromOutput -Text $output -Patterns @('(?m)^\s*(\d+)\s*个错误\s*$', '(?m)^\s*(\d+)\s+Error\(s\)\s*$')
$warnCount = Get-CountFromOutput -Text $output -Patterns @('(?m)^\s*(\d+)\s*个警告\s*$', '(?m)^\s*(\d+)\s+Warning\(s\)\s*$')

Write-Host ''
if ($exitCode -ne 0) {
    Write-Host (' 构建失败：退出码 {0}，耗时 {1:N1}s' -f $exitCode, $stopwatch.Elapsed.TotalSeconds) -ForegroundColor Red
    if ($errorCount -ge 0) { Write-Host (' 错误 {0} 个 / 警告 {1} 个' -f $errorCount, $warnCount) -ForegroundColor Red }
    Write-Host ''
    exit $exitCode
}

$artifactDir = Join-Path $root ('src\PowerDock.App\bin\{0}\net10.0-windows' -f $Configuration)
$artifactExe = Join-Path $artifactDir 'PowerDock.exe'

Write-Host (' 构建成功：{0} 个错误 / {1} 个警告，耗时 {2:N1}s' -f $errorCount, $warnCount, $stopwatch.Elapsed.TotalSeconds) -ForegroundColor Green
Write-Host (' 产物目录：{0}' -f $artifactDir)
if (Test-Path -LiteralPath $artifactExe -PathType Leaf) {
    $exeInfo = Get-Item -LiteralPath $artifactExe
    Write-Host (' 主程序  ：{0}（{1} 字节）' -f $exeInfo.FullName, $exeInfo.Length)
}
else {
    Write-Host (' 警告：未在预期位置找到 {0}' -f $artifactExe) -ForegroundColor Yellow
}

if ($OutputDirectory) {
    $target = [IO.Path]::GetFullPath($OutputDirectory)
    Write-Host (' 复制输出到：{0}' -f $target)
    New-Item -ItemType Directory -Path $target -Force | Out-Null
    Get-ChildItem -LiteralPath $artifactDir -File | Copy-Item -Destination $target -Force
    Write-Host ' 复制完成。'
}

Write-Host ''
exit 0
