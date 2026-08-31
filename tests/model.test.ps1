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

function New-Mic(
    [string]$Source,
    [string]$Label,
    [Nullable[int]]$Battery,
    [bool]$Approximate,
    [string]$DeviceName = '',
    [string]$ProductType = '',
    [string]$SerialNumber = '',
    [string]$ReceiverSerial = '',
    [string]$ReceiverProductType = '',
    [string]$DeviceId = ''
) {
    $mic = [Activator]::CreateInstance($micType)
    $mic.Source = $Source
    $mic.Label = $Label
    $mic.BatteryPercent = $Battery
    $mic.Approximate = $Approximate
    $mic.DeviceName = $DeviceName
    $mic.ProductType = $ProductType
    $mic.SerialNumber = $SerialNumber
    $mic.ReceiverSerial = $ReceiverSerial
    $mic.ReceiverProductType = $ReceiverProductType
    $mic.DeviceId = $DeviceId
    return $mic
}

function New-View([object[]]$Microphones) {
    $snapshot = [Activator]::CreateInstance($snapshotType)
    foreach ($mic in $Microphones) { $snapshot.Microphones.Add($mic) }
    return $fromSnapshot.Invoke($null, @($snapshot))
}

$dualView = New-View @(
    (New-Mic 'Bluetooth' 'BT' 40 $false 'DJI Mic Mini-BT Hands-Free AG' 'DJI Mic Mini' '62D525'),
    (New-Mic 'USB' 'USB1/TX1' 9 $true '' 'DJI Mic Mini 2' 'B3MTP5P015VAZY' 'B3NTP5D005U1T2' 'DJI Mic Mini 2' 'usb-one'),
    (New-Mic 'USB' 'USB1/TX2' 60 $true '' 'DJI Mic Mini 2S' 'BXTTP59013PLX1' 'B3NTP5D005U1T2' 'DJI Mic Mini 2' 'usb-one')
)
Assert-Equal $dualView.Tone 'caution' 'Dual connection minimum tone'
if ([Math]::Abs($dualView.Fill - 0.09) -gt 0.0001) { throw "Dual connection fill: $($dualView.Fill)" }
$passed++
if ($dualView.Summary -notmatch '9%' -or $dualView.Summary -notmatch '3') { throw "Dual summary: $($dualView.Summary)" }
$passed++
if ($dualView.Summary -match '约|~') { throw "Summary must not show an approximation marker: $($dualView.Summary)" }
$passed++
Assert-Equal $dualView.DetailGroups.Count 2 'Bluetooth and USB detail groups'
Assert-Equal $dualView.DetailGroups[0].Kind 'Bluetooth' 'Bluetooth detail group kind'
Assert-Equal $dualView.DetailGroups[1].Kind 'USB' 'USB detail group kind'
Assert-Equal $dualView.DetailGroups[0].Rows.Count 1 'One Bluetooth microphone row'
Assert-Equal $dualView.DetailGroups[1].Rows.Count 2 'Two USB microphone rows'
$details = (($dualView.DetailGroups | ForEach-Object { $_.Rows.Text }) -join '|')
if ($details -notmatch 'DJI Mic Mini 2S' -or $details -notmatch 'BXTTP59013PLX1' -or $details -notmatch '62D525') { throw "Identity detail rows: $details" }
$passed++
if ($details -match '约值|~') { throw "Detail rows must not show an approximation marker: $details" }
$passed++
$tooltip = $dualView.Tooltip
$bluetoothSymbol = [char]::ConvertFromUtf32(0x1F4F6)
$usbSymbol = [char]::ConvertFromUtf32(0x1F50C)
$batterySymbol = [char]::ConvertFromUtf32(0x1F50B)
if ($tooltip -notmatch ([regex]::Escape($bluetoothSymbol + '蓝牙 Mini' + $batterySymbol + '40%')) -or
    $tooltip -notmatch ([regex]::Escape($usbSymbol + 'USB1/T1 Mini 2' + $batterySymbol + '9%')) -or
    $tooltip -notmatch ([regex]::Escape($usbSymbol + 'USB1/T2 Mini 2S' + $batterySymbol + '60%'))) {
    throw "Compact tooltip rows: $tooltip"
}
$passed++
if ($tooltip -match '约|~') { throw "Tooltip must not show an approximation marker: $tooltip" }
$passed++
if ($tooltip -match '62D525|B3MTP5P015VAZY|BXTTP59013PLX1|B3NTP5D005U1T2') {
    throw "Tooltip must not contain serial numbers: $tooltip"
}
$passed++
if ($tooltip -match '大疆麦克风电量') { throw "Tooltip should contain device rows only: $tooltip" }
$passed++
if ($tooltip -notmatch "`r?`n") { throw "Tooltip should use one line per microphone: $tooltip" }
$passed++
if ($tooltip.Length -gt 63) { throw "Tooltip exceeds the Windows NotifyIcon limit: $($tooltip.Length)" }
$passed++

$multiBluetoothView = New-View @(
    (New-Mic 'Bluetooth' 'BT1' 70 $false 'DJI Mic Mini-A Hands-Free AG'),
    (New-Mic 'Bluetooth' 'BT2' 5 $false 'DJI Mic Mini-B Hands-Free AG')
)
Assert-Equal $multiBluetoothView.Tone 'critical' 'Multiple Bluetooth minimum tone'
if ($multiBluetoothView.Summary -notmatch '5%' -or $multiBluetoothView.Summary -notmatch '2') { throw "Multiple Bluetooth summary: $($multiBluetoothView.Summary)" }
$passed++

$multiUsbView = New-View @(
    (New-Mic 'USB' 'USB1/TX1' 80 $true '' '' '' '' '' 'usb-one'),
    (New-Mic 'USB' 'USB2/TX1' 20 $true '' '' '' '' '' 'usb-two')
)
Assert-Equal $multiUsbView.Tone 'good' 'Multiple USB minimum tone'
if ($multiUsbView.Summary -notmatch '20%' -or $multiUsbView.Summary -notmatch '2') { throw "Multiple USB summary: $($multiUsbView.Summary)" }
$passed++

$tieView = New-View @(
    (New-Mic 'Bluetooth' 'BT' 5 $false 'DJI Mic Mini-TIE Hands-Free AG'),
    (New-Mic 'USB' 'USB1/TX1' 5 $true)
)
Assert-Equal $tieView.Tone 'critical' 'Exact/approximate tie tone'
if ($tieView.Summary -notmatch '5%' -or $tieView.Summary -notmatch '2') { throw "Tie summary: $($tieView.Summary)" }
$passed++

$badgeType = $assembly.GetType('DjiMicBattery.BatteryBadgeFactory', $true)
$badgeArgs = New-Object object[] 1
$badgeArgs[0] = [Nullable[int]]90
$badge = $badgeType.GetMethod('Create', [Reflection.BindingFlags]'Public,Static').Invoke($null, $badgeArgs)
Assert-Equal $badge.Width 68 'Battery badge width'
Assert-Equal $badge.Height 26 'Battery badge height'
$badge.Dispose()

$connectionIconType = $assembly.GetType('DjiMicBattery.ConnectionIconFactory', $true)
foreach ($kind in @('Bluetooth', 'USB')) {
    $connectionIcon = $connectionIconType.GetMethod('Create', [Reflection.BindingFlags]'Public,Static').Invoke($null, @($kind))
    Assert-Equal $connectionIcon.Width 22 "$kind icon width"
    Assert-Equal $connectionIcon.Height 22 "$kind icon height"
    if ($connectionIcon.GetPixel(11, 11).A -eq 0) { throw "$kind icon center is transparent" }
    $passed++
    $connectionIcon.Dispose()
}

$readerType = $assembly.GetType('DjiMicBattery.Reader', $true)
$statusFrame = New-Object byte[] 118
$statusFrame[0] = 0x55
$statusFrame[1] = 118
$statusFrame[2] = 0x04
$statusFrame[8] = 0x00
$statusFrame[9] = 0x5b
$statusFrame[10] = 0x03
$statusFrame[11] = 0x03
$statusFrame[44] = 0x03
$statusFrame[53] = 0x01
$statusFrame[59] = 0x04
$statusFrame[85] = 0x02
$statusFrame[91] = 0x04

$records = [Collections.Generic.List[byte]]::new()
function Add-IdentityRecord([byte]$Tag, [byte]$Unit, [byte[]]$Data) {
    $records.Add($Tag)
    $records.Add($Unit)
    $records.Add(0)
    $records.Add(0)
    $records.Add(0)
    $records.Add([byte]$Data.Length)
    $records.AddRange($Data)
}
function Identity-Data([string]$Serial) {
    $data = [Collections.Generic.List[byte]]::new()
    $data.AddRange([byte[]](0, 17, 3, 2))
    $data.AddRange([Text.Encoding]::ASCII.GetBytes($Serial))
    return $data.ToArray()
}
Add-IdentityRecord 0x01 0 (Identity-Data 'RX000000000001')
Add-IdentityRecord 0x06 0 ([Text.Encoding]::ASCII.GetBytes('DJI Mic Mini 2'))
Add-IdentityRecord 0x01 1 (Identity-Data 'TX100000000001')
Add-IdentityRecord 0x06 1 ([Text.Encoding]::ASCII.GetBytes('DJI Mic Mini 2'))
Add-IdentityRecord 0x01 2 (Identity-Data 'TX200000000002')
Add-IdentityRecord 0x06 2 ([Text.Encoding]::ASCII.GetBytes('DJI Mic Mini 2S'))
$identityFrame = New-Object byte[] (14 + $records.Count + 2)
$identityFrame[0] = 0x55
$identityFrame[1] = [byte]$identityFrame.Length
$identityFrame[2] = 0x04
$identityFrame[8] = 0x00
$identityFrame[9] = 0x5b
$identityFrame[10] = 0x03
$identityFrame[11] = 0x03
[Array]::Copy($records.ToArray(), 0, $identityFrame, 14, $records.Count)
$decodeArgs = New-Object object[] 1
$decodeArgs[0] = [byte[][]]@($identityFrame, $statusFrame)
$decoded = $readerType.GetMethod('DecodeFramesForTest', [Reflection.BindingFlags]'Public,Static').Invoke($null, $decodeArgs)
Assert-Equal $decoded.ReceiverSerial 'RX000000000001' 'Receiver serial decode'
Assert-Equal $decoded.Transmitters[0].ProductName 'DJI Mic Mini 2' 'TX1 product decode'
Assert-Equal $decoded.Transmitters[0].SerialNumber 'TX100000000001' 'TX1 serial decode'
Assert-Equal $decoded.Transmitters[1].ProductName 'DJI Mic Mini 2S' 'TX2 product decode'
Assert-Equal $decoded.Transmitters[1].SerialNumber 'TX200000000002' 'TX2 serial decode'

[pscustomobject]@{ Status = 'passed'; Assertions = $passed }
