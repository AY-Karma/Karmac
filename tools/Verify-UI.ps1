param([string]$CaptureDirectory, [switch]$Background)
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $workspace
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$metadata = Join-Path $env:WINDIR 'System32\WinMetadata'
$compiler = Join-Path $framework 'csc.exe'
$runtimeFacade = Get-ChildItem (Join-Path $env:WINDIR 'Microsoft.NET\assembly\GAC_MSIL\System.Runtime') -Filter System.Runtime.dll -Recurse | Select-Object -First 1 -ExpandProperty FullName
$refs = @('System.dll','System.Core.dll','System.Xaml.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Web.Extensions.dll',
    (Join-Path $framework 'WPF\PresentationCore.dll'), (Join-Path $framework 'WPF\PresentationFramework.dll'),
    (Join-Path $framework 'WPF\WindowsBase.dll'), (Join-Path $framework 'System.Runtime.WindowsRuntime.dll'),
    (Join-Path $framework 'System.Runtime.InteropServices.WindowsRuntime.dll'), $runtimeFacade,
    (Join-Path $metadata 'Windows.Devices.winmd'), (Join-Path $metadata 'Windows.Foundation.winmd'),
    (Join-Path $metadata 'Windows.Storage.winmd'))
$arguments = @('/nologo','/codepage:65001','/target:exe','/platform:x64','/warn:4',
    '/main:Karmac.Tests.UiSmoke','/out:artifacts\Karmac.UiSmoke.exe',
    '/resource:src\MainWindow.xaml,MainWindow.xaml','/resource:THIRD-PARTY-NOTICES.txt,Notices.txt',
    '/resource:assets\airpods-pro-3-original.png,AirPodsPro3.png')
if (Test-Path -LiteralPath 'assets\Karmac.ico') { $arguments += '/resource:assets\Karmac.ico,Karmac.ico' }
$arguments += Get-ChildItem -LiteralPath assets\headphones -Filter '*.png' | ForEach-Object { '/resource:' + $_.FullName + ',Headphones.' + $_.Name }
$arguments += $refs | ForEach-Object { '/r:' + $_ }
$arguments += Get-ChildItem -LiteralPath src -Filter '*.cs' | Select-Object -ExpandProperty FullName
$arguments += 'tests\UiSmoke.cs'
New-Item -ItemType Directory -Force -Path artifacts | Out-Null
$settingsPath = Join-Path $env:LOCALAPPDATA 'Karmac\settings.json'
$settingsExisted = Test-Path -LiteralPath $settingsPath
$savedSettings = if ($settingsExisted) { [IO.File]::ReadAllBytes($settingsPath) } else { $null }
try {
    & $compiler @arguments
    if ($LASTEXITCODE -ne 0) { throw 'UI smoke compilation failed.' }
    Copy-Item -LiteralPath 'Karmac.exe.config' -Destination 'artifacts\Karmac.UiSmoke.exe.config' -Force
    $smokeArguments = @()
    if ($CaptureDirectory) { $smokeArguments += [IO.Path]::GetFullPath($CaptureDirectory) }
    if ($Background) { $smokeArguments += '--background' }
    & .\artifacts\Karmac.UiSmoke.exe @smokeArguments
    if ($LASTEXITCODE -ne 0) { throw 'UI smoke failed.' }
}
finally {
    if ($settingsExisted) { [IO.File]::WriteAllBytes($settingsPath, [byte[]]$savedSettings) }
    elseif (Test-Path -LiteralPath $settingsPath) { Remove-Item -LiteralPath $settingsPath -Force }
    Write-Host 'Original settings.json restored.'
}
