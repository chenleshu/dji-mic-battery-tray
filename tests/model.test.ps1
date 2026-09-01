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

Assert-Equal $exe.VersionInfo.FileVersion '2.0.0.0' 'Assembly file version'

$keyGestureType = $assembly.GetType('DjiMicBattery.KeyGesture', $true)
$rightAlt = $keyGestureType.GetProperty('RightAlt', [Reflection.BindingFlags]'Public,Static').GetValue($null, $null)
$rightAltShift = $keyGestureType.GetProperty('RightAltShift', [Reflection.BindingFlags]'Public,Static').GetValue($null, $null)
$rightAltSpace = $keyGestureType.GetProperty('RightAltSpace', [Reflection.BindingFlags]'Public,Static').GetValue($null, $null)
Assert-Equal $rightAlt.DisplayName '右 Alt' 'Right Alt preset label'
Assert-Equal $rightAlt.Serialize() '165' 'Right Alt preset serialization'
Assert-Equal $rightAltShift.DisplayName '右 Alt + 右 Shift' 'Right Alt Shift preset label'
Assert-Equal $rightAltShift.Serialize() '165,161' 'Right Alt Shift uses right Shift for Typeless'
Assert-Equal $rightAltSpace.DisplayName '右 Alt + 空格' 'Right Alt Space preset label'
$parsedGesture = $keyGestureType.GetMethod('Parse', [Reflection.BindingFlags]'Public,Static').Invoke($null, @('165,32'))
Assert-Equal $parsedGesture.DisplayName '右 Alt + 空格' 'Custom gesture parse'
Assert-Equal $parsedGesture.SameAs($rightAltSpace) $true 'Custom gesture equality'

$remapperType = $assembly.GetType('DjiMicBattery.DjiButtonRemapper', $true)
$inputFor = $remapperType.GetMethod('InputFor', [Reflection.BindingFlags]'NonPublic,Static')
$rightAltInput = $inputFor.Invoke($null, @([int]0xA5, $false))
Assert-Equal ($rightAltInput.Union.Keyboard.ScanCode -gt 0) $true 'Right Alt injection includes hardware scan code'

$captureType = $assembly.GetType('DjiMicBattery.KeyGestureCaptureForm', $true)
$normalizeModifier = $captureType.GetMethod('NormalizeModifierKey', [Reflection.BindingFlags]'NonPublic,Static')
Assert-Equal $normalizeModifier.Invoke($null, @([int]0x10, [IntPtr]0x00360000)) 0xA1 'Custom capture distinguishes right Shift'
Assert-Equal $normalizeModifier.Invoke($null, @([int]0x12, [IntPtr]0x01380000)) 0xA5 'Custom capture distinguishes right Alt'

$timingType = $assembly.GetType('DjiMicBattery.AutoEnterTiming', $true)
Assert-Equal $timingType.GetField('DefaultTimeoutMilliseconds').GetRawConstantValue() 20000 'Auto Enter default timeout'
Assert-Equal $timingType.GetField('DefaultStableMilliseconds').GetRawConstantValue() 800 'Auto Enter default stable delay'
Assert-Equal $timingType.GetField('MinimumTimeoutMilliseconds').GetRawConstantValue() 2000 'Auto Enter timeout minimum constant'
Assert-Equal $timingType.GetField('MaximumTimeoutMilliseconds').GetRawConstantValue() 120000 'Auto Enter timeout maximum constant'
Assert-Equal $timingType.GetField('MinimumStableMilliseconds').GetRawConstantValue() 200 'Auto Enter stable minimum constant'
Assert-Equal $timingType.GetField('MaximumStableMilliseconds').GetRawConstantValue() 5000 'Auto Enter stable maximum constant'
Assert-Equal $timingType.GetMethod('NormalizeTimeout').Invoke($null, @([int]500)) 2000 'Auto Enter timeout lower bound'
Assert-Equal $timingType.GetMethod('NormalizeTimeout').Invoke($null, @([int]200000)) 120000 'Auto Enter timeout upper bound'
Assert-Equal $timingType.GetMethod('NormalizeTimeout').Invoke($null, @([int]3400)) 3400 'Auto Enter timeout preserves valid value'
Assert-Equal $timingType.GetMethod('NormalizeStable').Invoke($null, @([int]50)) 200 'Auto Enter stable lower bound'
Assert-Equal $timingType.GetMethod('NormalizeStable').Invoke($null, @([int]9000)) 5000 'Auto Enter stable upper bound'
Assert-Equal $timingType.GetMethod('NormalizeStable').Invoke($null, @([int]1100)) 1100 'Auto Enter stable delay preserves valid value'
Assert-Equal $timingType.GetMethod('FormatSeconds').Invoke($null, @([int]800)) '0.8 秒' 'Auto Enter timing label'

$settingsFormType = $assembly.GetType('DjiMicBattery.AutoEnterSettingsForm', $true)
$settingsForm = [Activator]::CreateInstance($settingsFormType, @($true, [int]3400, [int]1100))
try {
    Assert-Equal $settingsForm.AutoEnterEnabled $true 'Auto Enter settings enabled value'
    Assert-Equal $settingsForm.TimeoutMilliseconds 3400 'Auto Enter settings timeout conversion'
    Assert-Equal $settingsForm.StableMilliseconds 1100 'Auto Enter settings stable conversion'
    $timeoutControl = $settingsFormType.GetField('timeoutSeconds', [Reflection.BindingFlags]'NonPublic,Instance').GetValue($settingsForm)
    $stableControl = $settingsFormType.GetField('stableSeconds', [Reflection.BindingFlags]'NonPublic,Instance').GetValue($settingsForm)
    Assert-Equal $timeoutControl.Minimum ([decimal]2.0) 'Auto Enter timeout control minimum'
    Assert-Equal $timeoutControl.Maximum ([decimal]120.0) 'Auto Enter timeout control maximum'
    Assert-Equal $stableControl.Minimum ([decimal]0.2) 'Auto Enter stable control minimum'
    Assert-Equal $stableControl.Maximum ([decimal]5.0) 'Auto Enter stable control maximum'
    Assert-Equal $timeoutControl.Increment ([decimal]0.1) 'Auto Enter timeout control increment'
    Assert-Equal $stableControl.Increment ([decimal]0.1) 'Auto Enter stable control increment'
} finally {
    $settingsForm.Dispose()
}

$clampedSettingsForm = [Activator]::CreateInstance($settingsFormType, @($false, [int]500, [int]9000))
try {
    Assert-Equal $clampedSettingsForm.AutoEnterEnabled $false 'Auto Enter settings disabled value'
    Assert-Equal $clampedSettingsForm.TimeoutMilliseconds 2000 'Auto Enter settings clamps timeout'
    Assert-Equal $clampedSettingsForm.StableMilliseconds 5000 'Auto Enter settings clamps stable delay'
} finally {
    $clampedSettingsForm.Dispose()
}

$instanceFlags = [Reflection.BindingFlags]'NonPublic,Instance'
$configPathField = $remapperType.GetField('configPath', $instanceFlags)
$enabledField = $remapperType.GetField('enabled', $instanceFlags)
$autoEnterEnabledField = $remapperType.GetField('autoEnterEnabled', $instanceFlags)
$timeoutField = $remapperType.GetField('autoEnterTimeoutMilliseconds', $instanceFlags)
$stableField = $remapperType.GetField('autoEnterStableMilliseconds', $instanceFlags)
$gestureField = $remapperType.GetField('gesture', $instanceFlags)
$loadConfig = $remapperType.GetMethod('Load', $instanceFlags)
$saveConfig = $remapperType.GetMethod('Save', $instanceFlags)

function New-UninitializedRemapper([string]$ConfigPath) {
    $instance = [Runtime.Serialization.FormatterServices]::GetUninitializedObject($remapperType)
    $configPathField.SetValue($instance, $ConfigPath)
    return $instance
}

$configTestDirectory = Join-Path ([IO.Path]::GetTempPath()) ('dji-mic-auto-enter-test-' + [Guid]::NewGuid().ToString('N'))
$configTestPath = Join-Path $configTestDirectory 'button-mapping.conf'
[IO.Directory]::CreateDirectory($configTestDirectory) | Out-Null
try {
    $savingRemapper = New-UninitializedRemapper $configTestPath
    $enabledField.SetValue($savingRemapper, $true)
    $autoEnterEnabledField.SetValue($savingRemapper, $false)
    $timeoutField.SetValue($savingRemapper, [int]3400)
    $stableField.SetValue($savingRemapper, [int]1100)
    $gestureField.SetValue($savingRemapper, $rightAltSpace)
    $saveConfig.Invoke($savingRemapper, $null)

    $savedConfig = [IO.File]::ReadAllLines($configTestPath, [Text.Encoding]::UTF8)
    Assert-Equal ($savedConfig -contains 'enabled=1') $true 'Config saves mapping enabled state'
    Assert-Equal ($savedConfig -contains 'keys=165,32') $true 'Config saves mapped gesture'
    Assert-Equal ($savedConfig -contains 'auto_enter_enabled=0') $true 'Config saves Auto Enter enabled state'
    Assert-Equal ($savedConfig -contains 'auto_enter_timeout_ms=3400') $true 'Config saves Auto Enter timeout'
    Assert-Equal ($savedConfig -contains 'auto_enter_stable_ms=1100') $true 'Config saves Auto Enter stable delay'

    $loadedRemapper = New-UninitializedRemapper $configTestPath
    $enabledField.SetValue($loadedRemapper, $false)
    $autoEnterEnabledField.SetValue($loadedRemapper, $true)
    $timeoutField.SetValue($loadedRemapper, [int]20000)
    $stableField.SetValue($loadedRemapper, [int]800)
    $gestureField.SetValue($loadedRemapper, $rightAlt)
    $loadConfig.Invoke($loadedRemapper, $null)
    Assert-Equal $enabledField.GetValue($loadedRemapper) $true 'Config roundtrip loads mapping enabled state'
    Assert-Equal $autoEnterEnabledField.GetValue($loadedRemapper) $false 'Config roundtrip loads Auto Enter enabled state'
    Assert-Equal $timeoutField.GetValue($loadedRemapper) 3400 'Config roundtrip loads Auto Enter timeout'
    Assert-Equal $stableField.GetValue($loadedRemapper) 1100 'Config roundtrip loads Auto Enter stable delay'
    Assert-Equal $gestureField.GetValue($loadedRemapper).SameAs($rightAltSpace) $true 'Config roundtrip loads mapped gesture'

    [IO.File]::WriteAllLines(
        $configTestPath,
        [string[]]@(
            'enabled=1',
            'keys=165',
            'auto_enter_enabled=1',
            'auto_enter_timeout_ms=500',
            'auto_enter_stable_ms=9000'
        ),
        [Text.UTF8Encoding]::new($false)
    )
    $boundedRemapper = New-UninitializedRemapper $configTestPath
    $enabledField.SetValue($boundedRemapper, $true)
    $autoEnterEnabledField.SetValue($boundedRemapper, $true)
    $timeoutField.SetValue($boundedRemapper, [int]20000)
    $stableField.SetValue($boundedRemapper, [int]800)
    $gestureField.SetValue($boundedRemapper, $rightAlt)
    $loadConfig.Invoke($boundedRemapper, $null)
    Assert-Equal $timeoutField.GetValue($boundedRemapper) 2000 'Config load clamps Auto Enter timeout'
    Assert-Equal $stableField.GetValue($boundedRemapper) 5000 'Config load clamps Auto Enter stable delay'
} finally {
    if ([IO.File]::Exists($configTestPath)) { [IO.File]::Delete($configTestPath) }
    if ([IO.Directory]::Exists($configTestDirectory)) { [IO.Directory]::Delete($configTestDirectory) }
}

$fingerprintType = $assembly.GetType('DjiMicBattery.TextFingerprint', $true)
$fromText = $fingerprintType.GetMethod('FromText', [Reflection.BindingFlags]'Public,Static')
$baselineFingerprint = $fromText.Invoke($null, @('原文字'))
$changedFingerprint = $fromText.Invoke($null, @('原文字已补全'))
$secondChangedFingerprint = $fromText.Invoke($null, @('原文字已补全。'))
Assert-Equal $baselineFingerprint.Equals($fromText.Invoke($null, @('原文字'))) $true 'Text fingerprint is deterministic'
Assert-Equal $baselineFingerprint.Equals($changedFingerprint) $false 'Text fingerprint detects inserted text'

$stabilityType = $assembly.GetType('DjiMicBattery.TextStabilityTracker', $true)
$tracker = [Activator]::CreateInstance($stabilityType, @($baselineFingerprint, [int]800))
$observe = $stabilityType.GetMethod('Observe', [Reflection.BindingFlags]'Public,Instance')
Assert-Equal $observe.Invoke($tracker, @($baselineFingerprint, [int]100)) $false 'Auto Enter ignores unchanged baseline'
Assert-Equal $observe.Invoke($tracker, @($changedFingerprint, [int]200)) $false 'Auto Enter starts stability timer after text change'
Assert-Equal $observe.Invoke($tracker, @($changedFingerprint, [int]999)) $false 'Auto Enter waits for full stable delay'
Assert-Equal $observe.Invoke($tracker, @($changedFingerprint, [int]1000)) $true 'Auto Enter fires after stable delay'

$resetTracker = [Activator]::CreateInstance($stabilityType, @($baselineFingerprint, [int]800))
Assert-Equal $observe.Invoke($resetTracker, @($changedFingerprint, [int]100)) $false 'Auto Enter observes first result'
Assert-Equal $observe.Invoke($resetTracker, @($secondChangedFingerprint, [int]700)) $false 'Auto Enter resets delay on later completion'
Assert-Equal $observe.Invoke($resetTracker, @($secondChangedFingerprint, [int]1499)) $false 'Auto Enter waits after reset'
Assert-Equal $observe.Invoke($resetTracker, @($secondChangedFingerprint, [int]1500)) $true 'Auto Enter fires after reset delay'

$autoEnterPhaseType = $assembly.GetType('DjiMicBattery.TypelessAutoEnterPhase', $true)
Assert-Equal ([Enum]::GetNames($autoEnterPhaseType) -join ',') 'Idle,Recording,AwaitingCompletion' 'Auto Enter exposes explicit recording phases'
$autoEnterControllerType = $assembly.GetType('DjiMicBattery.TypelessAutoEnterController', $true)
Assert-Equal $autoEnterControllerType.GetProperty('Phase').PropertyType $autoEnterPhaseType 'Auto Enter controller exposes typed phase state'
Assert-Equal $autoEnterControllerType.GetProperty('IsRecording').PropertyType ([bool]) 'Auto Enter controller exposes recording state'
Assert-Equal $autoEnterControllerType.GetMethod('PrepareFinishRecording').ReturnType ([bool]) 'Auto Enter explicitly prepares the stop baseline before sending Right Alt'
Assert-Equal $autoEnterControllerType.GetMethod('FinishRecording').ReturnType ([void]) 'Auto Enter finish transition is explicit'

$recognitionSessionType = $autoEnterControllerType.GetNestedType('RecognitionSession', [Reflection.BindingFlags]'NonPublic')
$invalidSessionFactory = $recognitionSessionType.GetMethod('Invalid', [Reflection.BindingFlags]'Public,Static')
$stopSession = $invalidSessionFactory.Invoke($null, @([IntPtr]::Zero, [int]20000, [int]800, 'test session'))
try {
    $stopPreparedProperty = $recognitionSessionType.GetProperty('StopPrepared', [Reflection.BindingFlags]'Public,Instance')
    $sessionBaselineField = $recognitionSessionType.GetField('Baseline', [Reflection.BindingFlags]'Public,Instance')
    $setStopBaseline = $recognitionSessionType.GetMethod('SetStopBaseline', [Reflection.BindingFlags]'Public,Instance')
    Assert-Equal $stopPreparedProperty.GetValue($stopSession, $null) $false 'Auto Enter session is not prepared before the stop snapshot'
    $setStopBaseline.Invoke($stopSession, @($changedFingerprint))
    Assert-Equal $stopPreparedProperty.GetValue($stopSession, $null) $true 'Auto Enter session records that the stop baseline was captured'
    $stopBaselineFingerprint = $sessionBaselineField.GetValue($stopSession)
    Assert-Equal $stopBaselineFingerprint.Equals($changedFingerprint) $true 'Auto Enter replaces the start snapshot with the stop baseline'

    $postFinishTracker = [Activator]::CreateInstance($stabilityType, @($stopBaselineFingerprint, [int]800))
    Assert-Equal $observe.Invoke($postFinishTracker, @($stopBaselineFingerprint, [int]0)) $false 'Auto Enter does not fire when recognition finishes before text changes'
    Assert-Equal $observe.Invoke($postFinishTracker, @($stopBaselineFingerprint, [int]5000)) $false 'Auto Enter never fires from elapsed time alone after recognition finishes'
    Assert-Equal $observe.Invoke($postFinishTracker, @($secondChangedFingerprint, [int]5001)) $false 'Auto Enter starts observing completion only after post-finish text change'
    Assert-Equal $observe.Invoke($postFinishTracker, @($secondChangedFingerprint, [int]5800)) $false 'Auto Enter waits the complete stable delay after post-finish change'
    Assert-Equal $observe.Invoke($postFinishTracker, @($secondChangedFingerprint, [int]5801)) $true 'Auto Enter fires only after post-finish text is stable'
} finally {
    $recognitionSessionType.GetMethod('DisposeCancellationSignal', [Reflection.BindingFlags]'Public,Instance').Invoke($stopSession, $null)
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
    [string]$DeviceId = '',
    [bool]$Charging = $false
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
    $mic.Charging = $Charging
    return $mic
}

function New-View([object[]]$Microphones) {
    $snapshot = [Activator]::CreateInstance($snapshotType)
    foreach ($mic in $Microphones) { $snapshot.Microphones.Add($mic) }
    return $fromSnapshot.Invoke($null, @($snapshot))
}

$dualView = New-View @(
    (New-Mic 'Bluetooth' 'BT' 40 $false 'DJI Mic Mini-BT Hands-Free AG' 'DJI Mic Mini' 'BT1234'),
    (New-Mic 'USB' 'USB1/TX1' 9 $true '' 'DJI Mic Mini 2' 'TX100000000001' 'RX000000000001' 'DJI Mic Mini 2' 'usb-one' $true),
    (New-Mic 'USB' 'USB1/TX2' 60 $true '' 'DJI Mic Mini 2S' 'TX200000000002' 'RX000000000001' 'DJI Mic Mini 2' 'usb-one')
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
if ($details -notmatch 'DJI Mic Mini 2S' -or $details -notmatch 'TX200000000002' -or $details -notmatch 'BT1234') { throw "Identity detail rows: $details" }
$passed++
if ($details -match '约值|~') { throw "Detail rows must not show an approximation marker: $details" }
$passed++
if ($details -notmatch '充电中') { throw "Charging detail marker: $details" }
$passed++
$chargedRow = $dualView.DetailGroups[1].Rows[0]
Assert-Equal $chargedRow.Charging $true 'Charging detail row state'
$passed++
$tooltip = $dualView.Tooltip
$bluetoothSymbol = [char]::ConvertFromUtf32(0x1F4F6)
$usbSymbol = [char]::ConvertFromUtf32(0x1F50C)
$batterySymbol = [char]::ConvertFromUtf32(0x1F50B)
$chargingSymbol = [char]0x26A1
if ($tooltip -notmatch ([regex]::Escape($bluetoothSymbol + '蓝牙 Mini' + $batterySymbol + '40%')) -or
    $tooltip -notmatch ([regex]::Escape($usbSymbol + 'USB1/T1 Mini 2' + $batterySymbol + '9%' + $chargingSymbol)) -or
    $tooltip -notmatch ([regex]::Escape($usbSymbol + 'USB1/T2 Mini 2S' + $batterySymbol + '60%'))) {
    throw "Compact tooltip rows: $tooltip"
}
$passed++
Assert-Equal $dualView.Charging $true 'Tray charging state'
if ($dualView.Summary -notmatch '充电中 1 支') { throw "Charging summary: $($dualView.Summary)" }
$passed++
if ($tooltip -match '约|~') { throw "Tooltip must not show an approximation marker: $tooltip" }
$passed++
if ($tooltip -match 'BT1234|TX100000000001|TX200000000002|RX000000000001') {
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
$badgeArgs = New-Object object[] 2
$badgeArgs[0] = [Nullable[int]]90
$badgeArgs[1] = $true
$badge = $badgeType.GetMethod('Create', [Reflection.BindingFlags]'Public,Static').Invoke($null, $badgeArgs)
Assert-Equal $badge.Width 68 'Battery badge width'
Assert-Equal $badge.Height 26 'Battery badge height'
$chargingPixels = 0
for ($x = 0; $x -lt $badge.Width; $x++) {
    for ($y = 0; $y -lt $badge.Height; $y++) {
        $pixel = $badge.GetPixel($x, $y)
        if ($pixel.R -gt 240 -and $pixel.G -gt 180 -and $pixel.B -lt 80) { $chargingPixels++ }
    }
}
if ($chargingPixels -lt 4) { throw "Charging badge glyph pixels: $chargingPixels" }
$passed++
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
$statusFrame[59] = 0x06
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
Assert-Equal $decoded.Transmitters[0].Charging $true 'TX1 charging flag decode'
Assert-Equal $decoded.Transmitters[1].Charging $false 'TX2 charging flag decode'

$buttonProtocolType = $assembly.GetType('DjiMicBattery.DjiButtonProtocol', $true)
$parseButtonReport = $buttonProtocolType.GetMethod('ParseReport', [Reflection.BindingFlags]'Public,Static')
function Parse-DjiButtonReport([byte[]]$Report) {
    $arguments = New-Object object[] 1
    $arguments[0] = $Report
    return $parseButtonReport.Invoke($null, $arguments)
}
Assert-Equal (Parse-DjiButtonReport ([byte[]](0x06, 0x01, 0x00))).ToString() 'Press' 'DJI WinUSB press report'
Assert-Equal (Parse-DjiButtonReport ([byte[]](0x06, 0x00, 0x00))).ToString() 'Release' 'DJI WinUSB release report'
Assert-Equal (Parse-DjiButtonReport ([byte[]](0x06, 0x02, 0x00))).ToString() 'Ignore' 'DJI WinUSB ignores other consumer reports'
Assert-Equal (Parse-DjiButtonReport ([byte[]](0x05, 0x01, 0x00))).ToString() 'Ignore' 'DJI WinUSB requires report ID 06'
Assert-Equal (Parse-DjiButtonReport ([byte[]](0x06, 0x01))).ToString() 'Ignore' 'DJI WinUSB rejects truncated report'
Assert-Equal (Parse-DjiButtonReport ([byte[]](0x06, 0x01, 0x00, 0x00))).ToString() 'Ignore' 'DJI WinUSB rejects extended reports'
$formatButtonReport = $buttonProtocolType.GetMethod('FormatReport', [Reflection.BindingFlags]'Public,Static')
$formatArguments = New-Object object[] 1
$formatArguments[0] = [byte[]](0x06, 0x01, 0x00)
Assert-Equal $formatButtonReport.Invoke($null, $formatArguments) '06-01-00' 'DJI WinUSB report diagnostics'

[pscustomobject]@{ Status = 'passed'; Assertions = $passed }
