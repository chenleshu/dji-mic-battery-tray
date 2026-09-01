[CmdletBinding()]
param(
    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA '大疆麦克风电量')
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$sourceExe = Join-Path $projectRoot 'dist\大疆麦克风电量.exe'
$installedExe = Join-Path $InstallRoot '大疆麦克风电量.exe'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$runValue = '大疆麦克风电量'

if (-not (Test-Path -LiteralPath $sourceExe -PathType Leaf)) {
    throw '请先运行 scripts\build.ps1。'
}

$running = Get-CimInstance Win32_Process | Where-Object {
    $_.ExecutablePath -and [string]::Equals($_.ExecutablePath, $installedExe, [StringComparison]::OrdinalIgnoreCase)
}
foreach ($process in $running) {
    Stop-Process -Id $process.ProcessId -Force
}

$stopDeadline = (Get-Date).AddSeconds(12)
do {
    $remaining = @(Get-CimInstance Win32_Process | Where-Object {
        $_.ExecutablePath -and [string]::Equals($_.ExecutablePath, $installedExe, [StringComparison]::OrdinalIgnoreCase)
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
New-Item -Path $runKey -Force | Out-Null
Set-ItemProperty -Path $runKey -Name $runValue -Value ('"{0}"' -f $installedExe)
Start-Process -FilePath $installedExe

[pscustomobject]@{
    Name = $runValue
    InstalledExe = $installedExe
    Autostart = $true
}
