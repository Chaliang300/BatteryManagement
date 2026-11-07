<#
.SYNOPSIS
    发布 PowerDock 自包含单文件包（D16：win-x64 未裁剪自包含单文件）。

.DESCRIPTION
    产物：
      dist\PowerDock-v<Version>-<Rid>\            解包后的发布目录（含单个 PowerDock.exe、LICENSE、README.md）
      dist\PowerDock-v<Version>-<Rid>.zip         分发包
      dist\pack-report-<Version>-<Rid>.json       本次打包的度量证据（字节数 / 耗时 / 哈希）

    发布参数锁定为：
      -c Release -r <Rid> --self-contained true
      -p:PublishTrimmed=false            WPF 不支持裁剪，禁止开启
      -p:PublishSingleFile=true          单文件
      -p:IncludeNativeLibrariesForSelfExtract=true
                                          把原生库（D3DCompiler / wpfgfx / vcruntime 等）也收进单个 exe

    注意（D16 验收事实）：单文件首次运行会把原生库解包到
    %TEMP%\.net\<AssemblyName>\<hash>\，因此首次启动比二次启动慢，
    且该目录需要在受限环境（无 TEMP 写权限）里单独验证。

.PARAMETER Version
    包版本号，形如 0.1.0。会写入程序集版本并参与目录名与 zip 名。

.PARAMETER RuntimeIdentifier
    目标运行时，默认 win-x64（D16 锁定）。

.PARAMETER NoZip
    只发布目录，不打 zip。

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\pack.ps1 -Version 0.1.0
#>
[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+([\.\-+][0-9A-Za-z\.\-+]*)?$')]
    [string]$Version = '0.1.0',

    [ValidateSet('win-x64', 'win-arm64')]
    [string]$RuntimeIdentifier = 'win-x64',

    [switch]$NoZip
)

$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$distDir = Join-Path $root 'dist'
$packageName = 'PowerDock-v{0}-{1}' -f $Version, $RuntimeIdentifier
$packageDir = Join-Path $distDir $packageName
$zipPath = Join-Path $distDir ('{0}.zip' -f $packageName)
$reportPath = Join-Path $distDir ('pack-report-{0}-{1}.json' -f $Version, $RuntimeIdentifier)

. (Join-Path $root 'tools\dotnet-sdk.ps1')

$dotnet = Initialize-DotnetSdk -MinimumMajor 10
if (-not $dotnet) { exit 1 }

$appProject = Join-Path $root 'src\PowerDock.App\PowerDock.App.csproj'
if (-not (Test-Path -LiteralPath $appProject -PathType Leaf)) {
    Write-Host ''
    Write-Host (' 发布失败：找不到工程文件 {0}' -f $appProject) -ForegroundColor Red
    Write-Host ''
    exit 1
}

# 只在 dist 目录内清理，避免误删
New-Item -ItemType Directory -Path $distDir -Force | Out-Null
$distFull = [IO.Path]::GetFullPath($distDir)
$packageFull = [IO.Path]::GetFullPath($packageDir)
if (-not $packageFull.StartsWith($distFull, [StringComparison]::OrdinalIgnoreCase)) {
    Write-Host (' 发布失败：目标目录不在 dist 下：{0}' -f $packageFull) -ForegroundColor Red
    exit 1
}
if (Test-Path -LiteralPath $packageFull) {
    Remove-Item -LiteralPath $packageFull -Recurse -Force
}
if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}

Write-Host ''
Write-Host '== PowerDock 打包（自包含单文件）==' -ForegroundColor Cyan
Write-Host (' SDK 宿主    : {0}' -f $dotnet)
$sdkVersion = (@(& $dotnet --version 2>$null) -join '').Trim()
Write-Host (' SDK 版本    : {0}' -f $sdkVersion)
Write-Host (' 版本        : {0}' -f $Version)
Write-Host (' 运行时      : {0}（自包含，未裁剪，单文件）' -f $RuntimeIdentifier)
Write-Host (' 目标目录    : {0}' -f $packageFull)
Write-Host ''

$publishArgs = @(
    'publish'
    $appProject
    '-c', 'Release'
    '-r', $RuntimeIdentifier
    '--self-contained', 'true'
    '-p:PublishTrimmed=false'
    '-p:PublishSingleFile=true'
    '-p:IncludeNativeLibrariesForSelfExtract=true'
    '-p:DebugType=None'
    '-p:DebugSymbols=false'
    ('-p:Version={0}' -f $Version)
    '-o', $packageFull
    '--nologo'
)

$publishWatch = [Diagnostics.Stopwatch]::StartNew()
$lines = New-Object System.Collections.Generic.List[string]
& $dotnet @publishArgs 2>&1 | ForEach-Object {
    $text = $_.ToString()
    $lines.Add($text)
    Write-Host $text
}
$publishExit = $LASTEXITCODE
$publishWatch.Stop()
$publishSeconds = [Math]::Round($publishWatch.Elapsed.TotalSeconds, 2)

if ($publishExit -ne 0) {
    Write-Host ''
    Write-Host (' 发布失败：退出码 {0}，耗时 {1}s' -f $publishExit, $publishSeconds) -ForegroundColor Red
    Write-Host ''
    exit $publishExit
}

$exePath = Join-Path $packageFull 'PowerDock.exe'
if (-not (Test-Path -LiteralPath $exePath -PathType Leaf)) {
    Write-Host ''
    Write-Host (' 发布失败：未生成单文件 {0}' -f $exePath) -ForegroundColor Red
    Write-Host ''
    exit 1
}

# 附带许可与说明
Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination $packageFull -Force
Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination $packageFull -Force

# 度量：单文件 exe、解包后总字节、条目数
$exeInfo = Get-Item -LiteralPath $exePath
$packageFiles = @(Get-ChildItem -LiteralPath $packageFull -Recurse -File)
$totalBytes = [long]0
foreach ($file in $packageFiles) { $totalBytes += $file.Length }
$exeSha256 = (Get-FileHash -LiteralPath $exePath -Algorithm SHA256).Hash

$zipBytes = [long]0
$zipSha256 = ''
if (-not $NoZip) {
    $zipWatch = [Diagnostics.Stopwatch]::StartNew()
    Compress-Archive -Path (Join-Path $packageFull '*') -DestinationPath $zipPath -CompressionLevel Optimal -Force
    $zipWatch.Stop()
    $zipZipSeconds = [Math]::Round($zipWatch.Elapsed.TotalSeconds, 2)
    $zipInfo = Get-Item -LiteralPath $zipPath
    $zipBytes = $zipInfo.Length
    $zipSha256 = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
}

Write-Host ''
Write-Host '== 包体度量 ==' -ForegroundColor Cyan
Write-Host (' 发布耗时        : {0}s' -f $publishSeconds)
if (-not $NoZip) { Write-Host (' 压缩耗时        : {0}s' -f $zipZipSeconds) }
Write-Host (' 单文件 exe      : {0} 字节（{1}）' -f $exeInfo.Length, (Format-ByteSize $exeInfo.Length))
Write-Host (' 解包后总字节    : {0} 字节（{1}），{2} 个文件' -f $totalBytes, (Format-ByteSize $totalBytes), $packageFiles.Count)
if (-not $NoZip) {
    Write-Host (' zip 字节数      : {0} 字节（{1}）' -f $zipBytes, (Format-ByteSize $zipBytes))
    Write-Host (' 压缩比          : {0:P1}' -f ($zipBytes / $totalBytes))
}
Write-Host (' exe SHA256      : {0}' -f $exeSha256)
if ($zipSha256) { Write-Host (' zip SHA256      : {0}' -f $zipSha256) }
Write-Host ''
Write-Host (' 单文件首次运行会把原生库解包到 %TEMP%\.net\PowerDock\<hash>\，首次启动因此慢于二次启动。') -ForegroundColor Yellow
Write-Host ''

# 机器可读证据
$report = [ordered]@{
    product              = 'PowerDock'
    version              = $Version
    runtimeIdentifier    = $RuntimeIdentifier
    configuration        = 'Release'
    selfContained        = $true
    publishTrimmed       = $false
    publishSingleFile    = $true
    includeNativeLibsForSelfExtract = $true
    sdkVersion           = $sdkVersion
    publishSeconds       = $publishSeconds
    exeBytes             = [long]$exeInfo.Length
    exeSha256            = $exeSha256
    packageTotalBytes    = $totalBytes
    packageFileCount     = $packageFiles.Count
    packageDirectory     = $packageFull
    zipBytes             = $zipBytes
    zipSha256            = $zipSha256
    zipPath              = $(if ($NoZip) { '' } else { $zipPath })
    generatedAtUtc       = (Get-Date).ToUniversalTime().ToString('o')
}
$report | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $reportPath -Encoding UTF8
Write-Host (' 度量报告        : {0}' -f $reportPath)
Write-Host ''
exit 0
