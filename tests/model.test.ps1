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
    @{ Gauge = 1; Property = 'Label'; Expected = '满电' },
    @{ Gauge = 5; Property = 'Tone'; Expected = 'caution' },
    @{ Gauge = 6; Property = 'Tone'; Expected = 'warning' },
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

[pscustomobject]@{ Status = 'passed'; Assertions = $passed }
