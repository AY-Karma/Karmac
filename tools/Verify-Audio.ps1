param(
    [string]$ContainerId,
    [switch]$Connect,
    [switch]$Cycle,
    [switch]$VerifyExistingRoute,
    [switch]$ProbeBattery
)

# The default run enumerates saved devices and existing audio routes only.
# -Connect explicitly requests reconnecting the specified, already paired AirPods.
# -Cycle disconnects and restores the specified AirPods only when they are already
# active and default for every playback role. Restoration is attempted on failure.
# -VerifyExistingRoute only reapplies a route that is already active and default
# for all three Windows playback roles; it refuses to change the selected output.
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Push-Location -LiteralPath $projectRoot
try {
    if (($Connect -or $Cycle) -and [string]::IsNullOrWhiteSpace($ContainerId)) {
        throw 'Use -ContainerId with -Connect or -Cycle. Run without switches to list paired AirPods container IDs.'
    }
    if ($Connect -and $Cycle) { throw 'Choose either -Connect or -Cycle.' }
    if (-not [string]::IsNullOrWhiteSpace($ContainerId)) {
        $parsedContainer = [Guid]::Empty
        if (-not [Guid]::TryParse($ContainerId, [ref]$parsedContainer) -or $parsedContainer -eq [Guid]::Empty) {
            throw 'ContainerId must be a nonempty device container GUID from the read-only listing.'
        }
        $ContainerId = $parsedContainer.ToString()
    }

    $framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
    $metadata = Join-Path $env:WINDIR 'System32\WinMetadata'
    $compiler = Join-Path $framework 'csc.exe'
    if (-not (Test-Path -LiteralPath $compiler)) {
        throw 'Run on 64-bit Windows 10/11 with .NET Framework 4.8 enabled.'
    }
    $runtimeFacade = Get-ChildItem (Join-Path $env:WINDIR 'Microsoft.NET\assembly\GAC_MSIL\System.Runtime') -Filter System.Runtime.dll -Recurse |
        Select-Object -First 1 -ExpandProperty FullName
    $refs = @(
        'System.dll', 'System.Core.dll', 'System.Web.Extensions.dll',
        (Join-Path $framework 'System.Runtime.WindowsRuntime.dll'),
        (Join-Path $framework 'System.Runtime.InteropServices.WindowsRuntime.dll'),
        $runtimeFacade,
        (Join-Path $metadata 'Windows.Devices.winmd'),
        (Join-Path $metadata 'Windows.Foundation.winmd'),
        (Join-Path $metadata 'Windows.Storage.winmd')
    )
    New-Item -ItemType Directory -Force -Path artifacts | Out-Null
    $arguments = @('/nologo', '/codepage:65001', '/target:exe', '/platform:x64', '/optimize+',
        '/main:Karmac.Tests.HardwareCheck', '/out:artifacts\Karmac.HardwareCheck.exe')
    $arguments += $refs | ForEach-Object { '/r:' + $_ }
    $arguments += @('src\NativeAudio.cs', 'src\BluetoothTelemetry.cs', 'src\Infrastructure.cs', 'tests\HardwareCheck.cs')
    & $compiler @arguments
    if ($LASTEXITCODE -ne 0) { throw 'Hardware check compilation failed.' }

    $checkArguments = @()
    if ($ContainerId) { $checkArguments += @('--target', $ContainerId) }
    if ($Connect) { $checkArguments += '--connect' }
    if ($Cycle) { $checkArguments += '--cycle' }
    if ($VerifyExistingRoute) { $checkArguments += '--verify-existing-route' }
    if ($ProbeBattery) { $checkArguments += '--battery' }
    & .\artifacts\Karmac.HardwareCheck.exe @checkArguments
    if ($LASTEXITCODE -ne 0) { throw ('Hardware check failed (exit {0}).' -f $LASTEXITCODE) }
}
finally { Pop-Location }
