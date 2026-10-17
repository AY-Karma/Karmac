param([switch]$Test)
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$metadata = Join-Path $env:WINDIR 'System32\WinMetadata'
$compiler = Join-Path $framework 'csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'Build on 64-bit Windows 10/11 with .NET Framework 4.8 enabled.' }
$runtimeFacade = Get-ChildItem (Join-Path $env:WINDIR 'Microsoft.NET\assembly\GAC_MSIL\System.Runtime') -Filter System.Runtime.dll -Recurse | Select-Object -First 1 -ExpandProperty FullName
$refs = @(
  'System.dll', 'System.Core.dll', 'System.Xaml.dll', 'System.Drawing.dll',
  'System.Windows.Forms.dll', 'System.Web.Extensions.dll',
  (Join-Path $framework 'WPF\PresentationCore.dll'),
  (Join-Path $framework 'WPF\PresentationFramework.dll'),
  (Join-Path $framework 'WPF\WindowsBase.dll'),
  (Join-Path $framework 'System.Runtime.WindowsRuntime.dll'),
  (Join-Path $framework 'System.Runtime.InteropServices.WindowsRuntime.dll'),
  $runtimeFacade,
  (Join-Path $metadata 'Windows.Devices.winmd'),
  (Join-Path $metadata 'Windows.Foundation.winmd'),
  (Join-Path $metadata 'Windows.Storage.winmd')
)
$arguments = @('/nologo', '/codepage:65001', '/target:winexe', '/platform:x64', '/optimize+', '/warn:4', '/win32manifest:app.manifest', '/out:artifacts\Karmac.exe', '/resource:src\MainWindow.xaml,MainWindow.xaml', '/resource:THIRD-PARTY-NOTICES.txt,Notices.txt')
if (Test-Path -LiteralPath 'assets\Karmac.ico') { $arguments += '/win32icon:assets\Karmac.ico'; $arguments += '/resource:assets\Karmac.ico,Karmac.ico' }
if (Test-Path -LiteralPath 'assets\airpods-pro-3-original.png') { $arguments += '/resource:assets\airpods-pro-3-original.png,AirPodsPro3.png' }
$headphoneResources = Get-ChildItem -LiteralPath assets\headphones -Filter '*.png' | ForEach-Object { '/resource:' + $_.FullName + ',Headphones.' + $_.Name }
$arguments += $headphoneResources
$arguments += $refs | ForEach-Object { '/r:' + $_ }
$arguments += Get-ChildItem -LiteralPath src -Filter '*.cs' | Select-Object -ExpandProperty FullName
New-Item -ItemType Directory -Force -Path artifacts | Out-Null
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw 'Compilation failed.' }
Copy-Item -LiteralPath 'Karmac.exe.config','THIRD-PARTY-NOTICES.txt','README.md','LICENSE' -Destination artifacts -Force
$readmeImages = @('app-overview.png','device-lineup.png','connection-metrics.svg','connection-flow.svg')
New-Item -ItemType Directory -Force -Path artifacts\docs\assets | Out-Null
foreach ($readmeImage in $readmeImages) {
  Copy-Item -LiteralPath (Join-Path 'docs\assets' $readmeImage) -Destination artifacts\docs\assets -Force
}
New-Item -ItemType Directory -Force -Path artifacts\assets | Out-Null
Copy-Item -LiteralPath assets\NOTICE.txt -Destination artifacts\assets\NOTICE.txt -Force
$exe = Get-Item -LiteralPath 'artifacts\Karmac.exe'
Write-Host ('Built {0} ({1:N0} KB)' -f $exe.FullName, ($exe.Length / 1KB))
if ($Test) {
  $testArguments = @('/nologo','/codepage:65001','/target:exe','/platform:x64','/out:artifacts\Karmac.Tests.exe','/main:Karmac.Tests.Program')
  $testArguments += $refs | ForEach-Object { '/r:' + $_ }
  $testArguments += @('src\NativeAudio.cs','src\BluetoothTelemetry.cs','src\Infrastructure.cs','src\ConnectionFlow.cs','tests\Program.cs')
  & $compiler @testArguments
  if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed.' }
  & .\artifacts\Karmac.Tests.exe
  if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
  $artworkArguments = @('/nologo','/codepage:65001','/target:exe','/platform:x64',
    '/main:Karmac.Tests.ArtworkSmoke','/out:artifacts\Karmac.ArtworkSmoke.exe')
  $artworkArguments += $refs | ForEach-Object { '/r:' + $_ }
  $artworkArguments += $headphoneResources
  $artworkArguments += Get-ChildItem -LiteralPath src -Filter '*.cs' | Select-Object -ExpandProperty FullName
  $artworkArguments += 'tests\ArtworkSmoke.cs'
  & $compiler @artworkArguments
  if ($LASTEXITCODE -ne 0) { throw 'Artwork checks failed to compile.' }
  & .\artifacts\Karmac.ArtworkSmoke.exe
  if ($LASTEXITCODE -ne 0) { throw 'Artwork checks failed.' }
}
