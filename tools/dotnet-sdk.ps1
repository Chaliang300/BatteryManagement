# 解析可用的 .NET SDK 宿主（dotnet.exe），供 build.ps1 / pack.ps1 点引用。
#
# 约定：
# 1. 仓库不提交 global.json 锁死 SDK 补丁版本，避免团队升级后互相构建失败；
# 2. 不硬编码任何用户绝对路径，探测顺序为 PATH -> DOTNET_ROOT -> 常规安装目录 -> 常见就地部署目录；
# 3. 找不到时给出可读中文提示（不抛原始异常栈），由调用方 exit 1。

function Get-DotnetSdkCandidates {
    $list = New-Object System.Collections.Generic.List[string]

    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($cmd -and $cmd.Source) { $list.Add($cmd.Source) }

    if ($env:DOTNET_ROOT) { $list.Add((Join-Path $env:DOTNET_ROOT 'dotnet.exe')) }

    if ($env:ProgramFiles) { $list.Add((Join-Path $env:ProgramFiles 'dotnet\dotnet.exe')) }
    if (${env:ProgramFiles(x86)}) { $list.Add((Join-Path ${env:ProgramFiles(x86)} 'dotnet\dotnet.exe')) }

    if ($env:LOCALAPPDATA) { $list.Add((Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe')) }

    if ($env:USERPROFILE) {
        # 免安装就地部署（dotnet-install.ps1 -InstallDir "$env:USERPROFILE\.dotnet10"）的常见位置
        $list.Add((Join-Path $env:USERPROFILE '.dotnet10\dotnet.exe'))
        $list.Add((Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'))
    }

    return $list
}

function Get-DotnetSdkMajor {
    param([string[]]$SdkLines)

    $best = 0
    foreach ($line in $SdkLines) {
        if ($line -match '^\s*(\d+)\.(\d+)\.(\d+)') {
            $major = [int]$Matches[1]
            if ($major -gt $best) { $best = $major }
        }
    }
    return $best
}

function Resolve-DotnetSdk {
    param([int]$MinimumMajor = 10)

    $probeLog = New-Object System.Collections.Generic.List[string]
    $seen = @{}

    foreach ($candidate in (Get-DotnetSdkCandidates)) {
        if ([string]::IsNullOrWhiteSpace($candidate)) { continue }

        $full = $candidate
        try { $full = [IO.Path]::GetFullPath($candidate) } catch { continue }
        if ($seen.ContainsKey($full)) { continue }
        $seen[$full] = $true

        if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
            $probeLog.Add("  未安装      : $full")
            continue
        }

        try {
            $sdkLines = @(& $full --list-sdks 2>$null)
            $code = $LASTEXITCODE
        }
        catch {
            $probeLog.Add("  无法执行    : $full")
            continue
        }

        if ($code -ne 0) {
            $probeLog.Add("  执行失败($code) : $full")
            continue
        }

        $major = Get-DotnetSdkMajor -SdkLines $sdkLines
        if ($major -eq 0) {
            $probeLog.Add("  仅有运行时  : $full（--list-sdks 无输出）")
            continue
        }
        if ($major -lt $MinimumMajor) {
            $probeLog.Add("  SDK 过旧(主版本 $major) : $full")
            continue
        }

        return [pscustomobject]@{
            Exe      = $full
            Root     = (Split-Path -Parent $full)
            Sdks     = $sdkLines
            ProbeLog = $probeLog
        }
    }

    return [pscustomobject]@{
        Exe      = $null
        Root     = $null
        Sdks     = @()
        ProbeLog = $probeLog
    }
}

function Write-DotnetSdkMissingMessage {
    param(
        [Parameter(Mandatory = $true)] $Probe,
        [int]$MinimumMajor = 10
    )

    $probeLines = @($Probe.ProbeLog | Where-Object { $_ })
    if ($probeLines.Count -eq 0) { $probeLines = @('  （未发现任何候选路径）') }

    Write-Host ''
    Write-Host '==============================================================================' -ForegroundColor Red
    Write-Host (' 构建前置检查失败：未找到 .NET {0} SDK' -f $MinimumMajor) -ForegroundColor Red
    Write-Host '==============================================================================' -ForegroundColor Red
    Write-Host ''
    Write-Host ' PowerDock 的目标框架是 net10.0 / net10.0-windows（WPF），只安装运行时无法构建。'
    Write-Host ' 已探测的 dotnet 宿主与结果：'
    foreach ($line in $probeLines) { Write-Host $line }
    Write-Host ''
    Write-Host ' 安装方式（任选其一）：'
    Write-Host '   1) winget install Microsoft.DotNet.SDK.10'
    Write-Host '   2) 从 https://dotnet.microsoft.com/download/dotnet/10.0 下载 SDK 安装包'
    Write-Host '   3) 免安装就地部署后把 DOTNET_ROOT 指向安装目录，例如：'
    Write-Host '        $env:DOTNET_ROOT = "$env:USERPROFILE\.dotnet10"'
    Write-Host '        $env:PATH = "$env:DOTNET_ROOT;$env:PATH"' 
    Write-Host ''
    Write-Host ' 装好后用 `dotnet --list-sdks` 确认能看到 10.x。' -ForegroundColor Yellow
    Write-Host ''
}

function Initialize-DotnetSdk {
    param([int]$MinimumMajor = 10)

    $probe = Resolve-DotnetSdk -MinimumMajor $MinimumMajor
    if (-not $probe.Exe) {
        Write-DotnetSdkMissingMessage -Probe $probe -MinimumMajor $MinimumMajor
        return $null
    }

    $env:DOTNET_ROOT = $probe.Root
    $env:PATH = '{0};{1}' -f $probe.Root, $env:PATH
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_NOLOGO = '1'
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'

    return $probe.Exe
}

function Format-ByteSize {
    param([long]$Bytes)

    $units = @('B', 'KB', 'MB', 'GB')
    $value = [double]$Bytes
    $index = 0
    while ($value -ge 1024 -and $index -lt ($units.Count - 1)) {
        $value = $value / 1024
        $index++
    }
    return ('{0:N2} {1}' -f $value, $units[$index])
}
