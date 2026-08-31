[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$exe = Get-ChildItem -LiteralPath (Join-Path $projectRoot 'dist') -Filter '*.exe' | Select-Object -First 1
if ($null -eq $exe) { throw 'Run scripts\build.ps1 first.' }

$assembly = [Reflection.Assembly]::LoadFile($exe.FullName)
$product = $assembly.GetCustomAttributes([Reflection.AssemblyProductAttribute], $false)[0].Product
if ([string]::IsNullOrWhiteSpace($product)) { throw 'Assembly product name is missing.' }
$passed = 1

function Assert-Equal($Actual, $Expected, [string]$Message) {
    if ($Actual -ne $Expected) { throw "$Message; expected $Expected, actual $Actual" }
    $script:passed++
}

$gaugeType = $assembly.GetType('DjiMicBattery.GaugeInfo', $true)
$fromGauge = $gaugeType.GetMethod('FromGauge', [Reflection.BindingFlags]'Public,Static')
$gaugeCases = @(
    @{ Gauge = 1; Percent = 100; Tone = 'good' },
    @{ Gauge = 5; Percent = 20; Tone = 'good' },
    @{ Gauge = 6; Percent = 9; Tone = 'caution' },
    @{ Gauge = 7; Percent = 5; Tone = 'critical' }
)
foreach ($case in $gaugeCases) {
    $result = $fromGauge.Invoke($null, @([Nullable[int]]$case.Gauge))
    Assert-Equal $result.EstimatedPercent $case.Percent "Gauge $($case.Gauge) percent"
    Assert-Equal $result.Tone $case.Tone "Gauge $($case.Gauge) tone"
}

$batteryVisualType = $assembly.GetType('DjiMicBattery.BatteryVisual', $true)
$fromPercent = $batteryVisualType.GetMethod('FromPercent', [Reflection.BindingFlags]'Public,Static')
$percentCases = @(
    @{ Percent = 50; Tone = 'good'; Fill = 0.5 },
    @{ Percent = 9; Tone = 'caution'; Fill = 0.09 },
    @{ Percent = 5; Tone = 'critical'; Fill = 0.05 }
)
foreach ($case in $percentCases) {
    $visual = $fromPercent.Invoke($null, @($case.Percent))
    Assert-Equal $visual.Tone $case.Tone "$($case.Percent)% tone"
    if ([Math]::Abs($visual.Fill - $case.Fill) -gt 0.0001) { throw "$($case.Percent)% fill: $($visual.Fill)" }
    $passed++
}

$micType = $assembly.GetType('DjiMicBattery.MicrophoneStatus', $true)
$snapshotType = $assembly.GetType('DjiMicBattery.MicStatusSnapshot', $true)
$trayViewType = $assembly.GetType('DjiMicBattery.TrayView', $true)
$fromSnapshot = $trayViewType.GetMethod('FromSnapshot', [Reflection.BindingFlags]'Public,Static')

function New-Mic([string]$Source, [string]$Label, [Nullable[int]]$Battery, [bool]$Approximate, [string]$DeviceName = '') {
    $mic = [Activator]::CreateInstance($micType)
    $mic.Source = $Source
    $mic.Label = $Label
    $mic.BatteryPercent = $Battery
    $mic.Approximate = $Approximate
    $mic.DeviceName = $DeviceName
    return $mic
}

function New-View([object[]]$Microphones) {
    $snapshot = [Activator]::CreateInstance($snapshotType)
    foreach ($mic in $Microphones) { $snapshot.Microphones.Add($mic) }
    return $fromSnapshot.Invoke($null, @($snapshot))
}

$dualView = New-View @(
    (New-Mic 'BT' 'BT' 40 $false 'DJI Mic Mini-BT Hands-Free AG'),
    (New-Mic 'USB' 'USB1/TX1' 9 $true),
    (New-Mic 'USB' 'USB1/TX2' 60 $true)
)
Assert-Equal $dualView.Tone 'caution' 'Dual connection minimum tone'
if ([Math]::Abs($dualView.Fill - 0.09) -gt 0.0001) { throw "Dual connection fill: $($dualView.Fill)" }
$passed++
if ($dualView.Summary -notmatch '9%' -or $dualView.Summary -notmatch '3') { throw "Dual summary: $($dualView.Summary)" }
$passed++
Assert-Equal $dualView.DetailLines.Count 3 'All microphones in details'
$details = $dualView.DetailLines -join '|'
if ($details -notmatch '40%' -or $details -notmatch '9%' -or $details -notmatch '60%') { throw "Detail batteries: $details" }
$passed++

$multiBluetoothView = New-View @(
    (New-Mic 'BT' 'BT1' 70 $false 'DJI Mic Mini-A Hands-Free AG'),
    (New-Mic 'BT' 'BT2' 5 $false 'DJI Mic Mini-B Hands-Free AG')
)
Assert-Equal $multiBluetoothView.Tone 'critical' 'Multiple Bluetooth minimum tone'
if ($multiBluetoothView.Summary -notmatch '5%' -or $multiBluetoothView.Summary -notmatch '2') { throw "Multiple Bluetooth summary: $($multiBluetoothView.Summary)" }
$passed++

$multiUsbView = New-View @(
    (New-Mic 'USB' 'USB1/TX1' 80 $true),
    (New-Mic 'USB' 'USB2/TX1' 20 $true)
)
Assert-Equal $multiUsbView.Tone 'good' 'Multiple USB minimum tone'
if ($multiUsbView.Summary -notmatch '20%' -or $multiUsbView.Summary -notmatch '2') { throw "Multiple USB summary: $($multiUsbView.Summary)" }
$passed++

$tieView = New-View @(
    (New-Mic 'BT' 'BT' 5 $false 'DJI Mic Mini-TIE Hands-Free AG'),
    (New-Mic 'USB' 'USB1/TX1' 5 $true)
)
Assert-Equal $tieView.Tone 'critical' 'Exact/approximate tie tone'
if ($tieView.Summary -notmatch '5%' -or $tieView.Summary -notmatch '2') { throw "Tie summary: $($tieView.Summary)" }
$passed++

[pscustomobject]@{ Status = 'passed'; Assertions = $passed }
