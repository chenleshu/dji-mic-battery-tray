[CmdletBinding()]
param(
    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA '大疆麦克风电量')
)

$ErrorActionPreference = 'Stop'
$installedExe = Join-Path $InstallRoot '大疆麦克风电量.exe'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'

$running = Get-CimInstance Win32_Process | Where-Object {
    $_.ExecutablePath -and [string]::Equals($_.ExecutablePath, $installedExe, [StringComparison]::OrdinalIgnoreCase)
}
foreach ($process in $running) {
    Stop-Process -Id $process.ProcessId -Force
    Wait-Process -Id $process.ProcessId -Timeout 5 -ErrorAction SilentlyContinue
}

Remove-ItemProperty -Path $runKey -Name '大疆麦克风电量' -ErrorAction SilentlyContinue
if (Test-Path -LiteralPath $InstallRoot) {
    $resolved = [IO.Path]::GetFullPath($InstallRoot)
    $expected = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA '大疆麦克风电量'))
    if (-not [string]::Equals($resolved, $expected, [StringComparison]::OrdinalIgnoreCase)) {
        throw "拒绝删除非默认安装目录：$resolved"
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}

'大疆麦克风电量已卸载。'
