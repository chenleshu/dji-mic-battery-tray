[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$InstallRoot = Join-Path $env:LOCALAPPDATA 'DjiMicBatteryTray'
$projectRoot = Split-Path -Parent $PSScriptRoot
$sourceExe = Join-Path $projectRoot 'dist\大疆麦克风电量.exe'
$installedExe = Join-Path $InstallRoot 'DjiMicBatteryTray.exe'
$dataRoot = Join-Path $env:LOCALAPPDATA '大疆麦克风电量'
$legacyInstalledExe = Join-Path $dataRoot '大疆麦克风电量.exe'
$taskName = '大疆麦克风电量'
$statusPath = Join-Path $dataRoot 'status.txt'
$desktopDirectory = [Environment]::GetFolderPath([Environment+SpecialFolder]::DesktopDirectory)
$desktopShortcut = Join-Path $desktopDirectory '大疆麦克风电量.lnk'
$managedExecutables = @($installedExe, $legacyInstalledExe)

function Test-ManagedExecutablePath([string]$Path) {
    foreach ($candidate in $managedExecutables) {
        if ([string]::Equals($Path, $candidate, [StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
    }
    return $false
}

function Invoke-AppControl([string]$Argument) {
    $control = Start-Process -FilePath $installedExe -ArgumentList $Argument -WindowStyle Hidden -Wait -PassThru
    if ($control.ExitCode -ne 0) {
        throw "程序控制命令失败：$Argument，退出码 $($control.ExitCode)"
    }
}

function Read-Status([string]$Path) {
    $values = @{}
    foreach ($line in Get-Content -LiteralPath $Path -Encoding UTF8) {
        $separator = $line.IndexOf('=')
        if ($separator -gt 0) {
            $values[$line.Substring(0, $separator)] = $line.Substring($separator + 1)
        }
    }
    return $values
}

if (-not (Test-Path -LiteralPath $sourceExe -PathType Leaf)) {
    throw '请先运行 scripts\build.ps1。'
}

$running = Get-CimInstance Win32_Process | Where-Object {
    $_.ExecutablePath -and (Test-ManagedExecutablePath $_.ExecutablePath)
}
foreach ($process in $running) {
    Stop-Process -Id $process.ProcessId -Force
}

$stopDeadline = (Get-Date).AddSeconds(12)
do {
    $remaining = @(Get-CimInstance Win32_Process | Where-Object {
        $_.ExecutablePath -and (Test-ManagedExecutablePath $_.ExecutablePath)
    })
    if ($remaining.Count -eq 0) {
        break
    }
    Start-Sleep -Milliseconds 300
} while ((Get-Date) -lt $stopDeadline)

if ($remaining.Count -gt 0) {
    throw "旧版本未能退出，无法安全覆盖：$($remaining.ProcessId -join ', ')"
}

New-Item -ItemType Directory -Path $InstallRoot -Force | Out-Null
$copyDeadline = (Get-Date).AddSeconds(8)
do {
    try {
        Copy-Item -LiteralPath $sourceExe -Destination $installedExe -Force
        $copied = $true
    }
    catch [IO.IOException] {
        if ((Get-Date) -ge $copyDeadline) {
            throw
        }
        Start-Sleep -Milliseconds 300
    }
} while (-not $copied)

if (Test-Path -LiteralPath $legacyInstalledExe -PathType Leaf) {
    Remove-Item -LiteralPath $legacyInstalledExe -Force
}

$shell = New-Object -ComObject 'WScript.Shell'
$shortcut = $null
try {
    $shortcut = $shell.CreateShortcut($desktopShortcut)
    $shortcut.TargetPath = $installedExe
    $shortcut.WorkingDirectory = $InstallRoot
    $shortcut.Description = '大疆麦克风电量'
    $shortcut.IconLocation = "$installedExe,0"
    $shortcut.Save()
    if (-not [string]::Equals($shortcut.TargetPath, $installedExe, [StringComparison]::OrdinalIgnoreCase)) {
        throw '桌面快捷方式目标校验失败。'
    }
}
finally {
    if ($null -ne $shortcut -and [Runtime.InteropServices.Marshal]::IsComObject($shortcut)) {
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcut)
    }
    if ([Runtime.InteropServices.Marshal]::IsComObject($shell)) {
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell)
    }
}

$expectedVersion = (Get-Item -LiteralPath $installedExe).VersionInfo.FileVersion
Invoke-AppControl '--enable-autostart'
Invoke-AppControl '--check-autostart'

$task = Get-ScheduledTask -TaskPath '\' -TaskName $taskName -ErrorAction Stop
if ($task.State -eq 'Disabled') {
    throw 'Windows 登录任务已创建，但当前状态为已禁用。'
}

$launchStartedUtc = [DateTime]::UtcNow
Invoke-AppControl '--run-autostart'

$verifiedProcess = $null
$verifiedStatus = $null
$verifyDeadline = (Get-Date).AddSeconds(30)
do {
    Start-Sleep -Milliseconds 400
    $processes = @(Get-CimInstance Win32_Process | Where-Object {
        $_.ExecutablePath -and [string]::Equals($_.ExecutablePath, $installedExe, [StringComparison]::OrdinalIgnoreCase)
    })
    if ($processes.Count -eq 1 -and (Test-Path -LiteralPath $statusPath -PathType Leaf)) {
        $statusFile = Get-Item -LiteralPath $statusPath
        if ($statusFile.LastWriteTimeUtc -ge $launchStartedUtc.AddSeconds(-1)) {
            $status = Read-Status $statusPath
            $candidate = $processes[0]
            if ($status['版本'] -eq $expectedVersion -and
                $status['启动来源'] -eq 'autostart' -and
                $status['自动启动'] -eq 'enabled' -and
                $status['进程ID'] -eq [string]$candidate.ProcessId -and
                [string]::Equals($status['进程路径'], $installedExe, [StringComparison]::OrdinalIgnoreCase)) {
                $verifiedProcess = $candidate
                $verifiedStatus = $status
                break
            }
        }
    }
} while ((Get-Date) -lt $verifyDeadline)

if ($null -eq $verifiedProcess) {
    throw '登录任务已注册，但未能在 30 秒内验证由该任务启动的托盘进程。'
}

[pscustomobject]@{
    Name = $taskName
    InstalledExe = $installedExe
    Version = $expectedVersion
    ProcessId = $verifiedProcess.ProcessId
    StartupSource = $verifiedStatus['启动来源']
    Autostart = $verifiedStatus['自动启动']
    AutostartVerified = $true
    DesktopShortcut = $desktopShortcut
}
