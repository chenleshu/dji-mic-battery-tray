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
    Wait-Process -Id $process.ProcessId -Timeout 5 -ErrorAction SilentlyContinue
}

New-Item -ItemType Directory -Path $InstallRoot -Force | Out-Null
Copy-Item -LiteralPath $sourceExe -Destination $installedExe -Force
New-Item -Path $runKey -Force | Out-Null
Set-ItemProperty -Path $runKey -Name $runValue -Value ('"{0}"' -f $installedExe)
Start-Process -FilePath $installedExe

[pscustomobject]@{
    Name = $runValue
    InstalledExe = $installedExe
    Autostart = $true
}
