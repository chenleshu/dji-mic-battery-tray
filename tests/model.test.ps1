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

[pscustomobject]@{ Status = 'passed'; Assertions = $passed }
