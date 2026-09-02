[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$InstallRoot = Join-Path $env:LOCALAPPDATA 'DjiMicBatteryTray'
$installedExe = Join-Path $InstallRoot 'DjiMicBatteryTray.exe'
$dataRoot = Join-Path $env:LOCALAPPDATA '大疆麦克风电量'
$legacyInstalledExe = Join-Path $dataRoot '大疆麦克风电量.exe'
$taskName = '大疆麦克风电量'
$legacyRunKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
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

function Remove-ExpectedDirectory([string]$Path, [string]$Expected) {
    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }
    $resolved = [IO.Path]::GetFullPath($Path)
    $expectedResolved = [IO.Path]::GetFullPath($Expected)
    if (-not [string]::Equals($resolved, $expectedResolved, [StringComparison]::OrdinalIgnoreCase)) {
        throw "拒绝删除非预期目录：$resolved"
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}

function Test-ManagedRunValue([string]$Value) {
    if ([string]::IsNullOrWhiteSpace($Value)) {
        return $false
    }
    $candidate = $Value.Trim().Trim('"')
    return Test-ManagedExecutablePath $candidate
}

$running = Get-CimInstance Win32_Process | Where-Object {
    $_.ExecutablePath -and (Test-ManagedExecutablePath $_.ExecutablePath)
}
foreach ($process in $running) {
    Stop-Process -Id $process.ProcessId -Force
    Wait-Process -Id $process.ProcessId -Timeout 5 -ErrorAction SilentlyContinue
}

$tasks = @(Get-ScheduledTask -TaskPath '\' -ErrorAction Stop | Where-Object {
    [string]::Equals($_.TaskName, $taskName, [StringComparison]::Ordinal)
})
foreach ($task in $tasks) {
    $actions = @($task.Actions)
    if ($actions.Count -ne 1 -or
        -not (Test-ManagedExecutablePath $actions[0].Execute) -or
        $actions[0].Arguments -ne '--autostart' -or
        -not [string]::Equals($actions[0].WorkingDirectory, (Split-Path -Parent $actions[0].Execute), [StringComparison]::OrdinalIgnoreCase)) {
        throw '拒绝删除定义与本程序不匹配的同名 Windows 登录任务。'
    }
    $task | Unregister-ScheduledTask -Confirm:$false -ErrorAction Stop
}
if (@(Get-ScheduledTask -TaskPath '\' -ErrorAction Stop | Where-Object {
    [string]::Equals($_.TaskName, $taskName, [StringComparison]::Ordinal)
}).Count -ne 0) {
    throw 'Windows 登录任务未能删除。'
}

if (Test-Path -LiteralPath $legacyRunKey) {
    $legacyRunValue = (Get-ItemProperty -LiteralPath $legacyRunKey -Name $taskName -ErrorAction SilentlyContinue).$taskName
    if (Test-ManagedRunValue $legacyRunValue) {
        Remove-ItemProperty -LiteralPath $legacyRunKey -Name $taskName -ErrorAction Stop
    }
}

if (Test-Path -LiteralPath $desktopShortcut -PathType Leaf) {
    $shell = New-Object -ComObject 'WScript.Shell'
    $shortcut = $null
    try {
        $shortcut = $shell.CreateShortcut($desktopShortcut)
        if (Test-ManagedExecutablePath $shortcut.TargetPath) {
            Remove-Item -LiteralPath $desktopShortcut -Force
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
}

Remove-ExpectedDirectory $InstallRoot (Join-Path $env:LOCALAPPDATA 'DjiMicBatteryTray')
Remove-ExpectedDirectory $dataRoot (Join-Path $env:LOCALAPPDATA '大疆麦克风电量')

'大疆麦克风电量已卸载。'
