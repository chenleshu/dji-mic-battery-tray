[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$exePath = Join-Path $projectRoot 'dist\大疆麦克风电量.exe'
if (-not (Test-Path -LiteralPath $exePath -PathType Leaf)) {
    throw '请先运行 scripts\build.ps1。'
}

$assembly = [Reflection.Assembly]::LoadFile($exePath)
$product = $assembly.GetCustomAttributes([Reflection.AssemblyProductAttribute], $false)[0].Product
if ($product -ne '大疆麦克风电量') { throw "产品名称错误：$product" }

$type = $assembly.GetType('DjiMicBattery.GaugeInfo', $true)
$method = $type.GetMethod('FromGauge', [Reflection.BindingFlags]'Public,Static')
$cases = @(
    @{ Gauge = 1; Property = 'EstimatedPercent'; Expected = 100 },
    @{ Gauge = 5; Property = 'EstimatedPercent'; Expected = 20 },
    @{ Gauge = 5; Property = 'Tone'; Expected = 'good' },
    @{ Gauge = 6; Property = 'EstimatedPercent'; Expected = 9 },
    @{ Gauge = 6; Property = 'Tone'; Expected = 'caution' },
    @{ Gauge = 7; Property = 'EstimatedPercent'; Expected = 5 },
    @{ Gauge = 7; Property = 'Tone'; Expected = 'critical' }
)
$passed = 1
foreach ($case in $cases) {
    $result = $method.Invoke($null, @([Nullable[int]]$case.Gauge))
    $actual = $type.GetProperty($case.Property).GetValue($result, $null)
    if ($actual -ne $case.Expected) {
        throw "档位 $($case.Gauge) 的 $($case.Property) 应为 $($case.Expected)，实际为 $actual"
    }
    $passed++
}

$readerResultType = $assembly.GetType('DjiMicBattery.ReaderResult', $true)
$transmitterType = $assembly.GetType('DjiMicBattery.TransmitterState', $true)
$trayViewType = $assembly.GetType('DjiMicBattery.TrayView', $true)
$readerResult = [Activator]::CreateInstance($readerResultType)
$readerResultType.GetProperty('Status').SetValue($readerResult, 'ok', $null)
$transmitter = [Activator]::CreateInstance($transmitterType)
$transmitterType.GetProperty('Slot').SetValue($transmitter, 1, $null)
$transmitterType.GetProperty('Connected').SetValue($transmitter, $true, $null)
$transmitterType.GetProperty('BatteryGauge').SetValue($transmitter, [Nullable[int]]6, $null)
$readerResultType.GetProperty('Transmitters').GetValue($readerResult, $null).Add($transmitter)
$view = $trayViewType.GetMethod('FromResult', [Reflection.BindingFlags]'Public,Static').Invoke($null, @($readerResult))
$tooltip = $trayViewType.GetProperty('Tooltip').GetValue($view, $null)
$tone = $trayViewType.GetProperty('Tone').GetValue($view, $null)
if ($tooltip -ne '大疆麦克风电量 | TX1 约 9%') { throw "悬停文字错误：$tooltip" }
$passed++
if ($tone -ne 'caution') { throw "9% 图标颜色等级错误：$tone" }
$passed++

$batteryVisualType = $assembly.GetType('DjiMicBattery.BatteryVisual', $true)
$fromPercent = $batteryVisualType.GetMethod('FromPercent', [Reflection.BindingFlags]'Public,Static')
$percentCases = @(
    @{ Percent = 50; Tone = 'good'; Fill = 0.5 },
    @{ Percent = 9; Tone = 'caution'; Fill = 0.09 },
    @{ Percent = 6; Tone = 'caution'; Fill = 0.06 },
    @{ Percent = 5; Tone = 'critical'; Fill = 0.05 },
    @{ Percent = 0; Tone = 'critical'; Fill = 0.0 }
)
foreach ($case in $percentCases) {
    $visual = $fromPercent.Invoke($null, @($case.Percent))
    $actualTone = $batteryVisualType.GetProperty('Tone').GetValue($visual, $null)
    $actualFill = $batteryVisualType.GetProperty('Fill').GetValue($visual, $null)
    if ($actualTone -ne $case.Tone) { throw "$($case.Percent)% 的颜色应为 $($case.Tone)，实际为 $actualTone" }
    if ([Math]::Abs($actualFill - $case.Fill) -gt 0.0001) { throw "$($case.Percent)% 的填充量错误：$actualFill" }
    $passed += 2
}

$bluetoothResultType = $assembly.GetType('DjiMicBattery.BluetoothBatteryResult', $true)
$bluetoothResult = [Activator]::CreateInstance($bluetoothResultType)
$bluetoothResultType.GetProperty('Status').SetValue($bluetoothResult, 'ok', $null)
$bluetoothResultType.GetProperty('DeviceName').SetValue($bluetoothResult, 'DJI Mic Mini-TEST Hands-Free AG', $null)
$bluetoothResultType.GetProperty('BatteryPercent').SetValue($bluetoothResult, [Nullable[int]]50, $null)
$bluetoothView = $trayViewType.GetMethod('FromBluetooth', [Reflection.BindingFlags]'Public,Static').Invoke($null, @($bluetoothResult))
$bluetoothTooltip = $trayViewType.GetProperty('Tooltip').GetValue($bluetoothView, $null)
$bluetoothSummary = $trayViewType.GetProperty('Summary').GetValue($bluetoothView, $null)
if ($bluetoothTooltip -ne '大疆麦克风电量 | DJI Mic Mini-TEST Hands-Free AG | 蓝牙 50%') { throw "蓝牙悬停文字错误：$bluetoothTooltip" }
$passed++
if ($bluetoothSummary -ne '蓝牙 50%') { throw "蓝牙菜单摘要错误：$bluetoothSummary" }
$passed++

[pscustomobject]@{ Status = 'passed'; Assertions = $passed }
