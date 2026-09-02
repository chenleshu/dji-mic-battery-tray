[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$sourceRoot = Join-Path $projectRoot 'src'
$assetsRoot = Join-Path $projectRoot 'assets'
$distRoot = Join-Path $projectRoot 'dist'
$iconPath = Join-Path $assetsRoot 'app.ico'
$exePath = Join-Path $distRoot '大疆麦克风电量.exe'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$wpfRoot = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\WPF'

if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) {
    throw "找不到 .NET Framework C# 编译器：$compiler"
}

New-Item -ItemType Directory -Path $assetsRoot -Force | Out-Null
New-Item -ItemType Directory -Path $distRoot -Force | Out-Null

Add-Type -AssemblyName System.Drawing
$bitmap = [Drawing.Bitmap]::new(32, 32)
$graphics = [Drawing.Graphics]::FromImage($bitmap)
$graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
$graphics.Clear([Drawing.Color]::Transparent)
$green = [Drawing.Color]::FromArgb(54, 201, 110)
$outline = [Drawing.Pen]::new([Drawing.Color]::FromArgb(30, 34, 40), 3)
$pen = [Drawing.Pen]::new($green, 2)
$brush = [Drawing.SolidBrush]::new($green)
try {
    $graphics.DrawRectangle($outline, 3, 7, 23, 18)
    $graphics.DrawRectangle($pen, 4, 8, 21, 16)
    $graphics.FillRectangle($brush, 6, 10, 17, 12)
    $graphics.FillRectangle($brush, 27, 12, 3, 8)
    $handle = $bitmap.GetHicon()
    $icon = [Drawing.Icon]::FromHandle($handle)
    $stream = [IO.File]::Create($iconPath)
    try { $icon.Save($stream) } finally { $stream.Dispose() }
} finally {
    $brush.Dispose()
    $pen.Dispose()
    $outline.Dispose()
    $graphics.Dispose()
    $bitmap.Dispose()
}

if (Test-Path -LiteralPath $exePath) {
    Remove-Item -LiteralPath $exePath -Force
}

$arguments = @(
    '/nologo',
    '/target:winexe',
    '/platform:x64',
    '/optimize+',
    "/win32icon:$iconPath",
    "/out:$exePath",
    '/reference:System.dll',
    '/reference:System.Core.dll',
    '/reference:System.Drawing.dll',
    '/reference:System.Xml.dll',
    '/reference:System.Windows.Forms.dll',
    '/reference:Microsoft.CSharp.dll',
    ('/reference:' + (Join-Path $wpfRoot 'UIAutomationClient.dll')),
    ('/reference:' + (Join-Path $wpfRoot 'UIAutomationTypes.dll')),
    ('/reference:' + (Join-Path $wpfRoot 'WindowsBase.dll')),
    (Join-Path $sourceRoot 'Program.cs'),
    (Join-Path $sourceRoot 'AutostartManager.cs'),
    (Join-Path $sourceRoot 'DjiButtonRemapper.cs'),
    (Join-Path $sourceRoot 'DjiButtonWinUsbSource.cs'),
    (Join-Path $sourceRoot 'TypelessAutoEnter.cs'),
    (Join-Path $sourceRoot 'DjiMicBluetooth.cs'),
    (Join-Path $sourceRoot 'DjiMicWinUsb.cs'),
    (Join-Path $sourceRoot 'DjiMicStatus.cs')
)

& $compiler @arguments
if ($LASTEXITCODE -ne 0) {
    throw "C# 编译失败，退出码：$LASTEXITCODE"
}

$file = Get-Item -LiteralPath $exePath
$hash = Get-FileHash -LiteralPath $exePath -Algorithm SHA256
[pscustomobject]@{
    Path = $file.FullName
    Bytes = $file.Length
    SHA256 = $hash.Hash
}
