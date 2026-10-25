# Verify that the release carries the designed icon in Windows and tray resources.
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
Add-Type -AssemblyName System.Drawing
$path = Join-Path $workspace 'artifacts\Karmac.exe'
$assembly = [Reflection.Assembly]::LoadFile($path)
$stream = $assembly.GetManifestResourceStream('Karmac.ico')
if ($null -eq $stream) { throw 'The executable is missing its tray icon resource.' }
$reader = New-Object IO.BinaryReader($stream)
try {
    if ($reader.ReadUInt16() -ne 0 -or $reader.ReadUInt16() -ne 1) { throw 'Invalid ICO header.' }
    $count = $reader.ReadUInt16()
    $sizes = @()
    for ($i = 0; $i -lt $count; $i++) {
        $width = [int]$reader.ReadByte(); $height = [int]$reader.ReadByte()
        if ($width -eq 0) { $width = 256 }; if ($height -eq 0) { $height = 256 }
        if ($width -ne $height) { throw 'Icon frame is not square.' }
        [void]$reader.ReadBytes(6)
        $length = $reader.ReadUInt32(); $offset = $reader.ReadUInt32()
        if ($length -eq 0 -or $offset + $length -gt $stream.Length) { throw 'Invalid ICO frame bounds.' }
        $sizes += $width
    }
    if (($sizes -join ',') -ne '16,20,24,32,40,48,64,128,256') { throw 'Missing Windows icon sizes.' }
    $stream.Position = 0
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $embedded = ([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-', '') }
    finally { $sha.Dispose() }
    if ($embedded -cne (Get-FileHash -LiteralPath (Join-Path $workspace 'assets\Karmac.ico')).Hash) {
        throw 'Embedded tray icon does not match the current artwork.'
    }
}
finally { $reader.Dispose() }
$nativeIcon = [Drawing.Icon]::ExtractAssociatedIcon($path)
if ($null -eq $nativeIcon) { throw 'Windows cannot load the executable icon.' }
try {
    $bitmap = $nativeIcon.ToBitmap()
    try {
        if ($bitmap.Width -lt 16 -or $bitmap.Height -lt 16) { throw 'Native icon is too small.' }
    }
    finally { $bitmap.Dispose() }
}
finally { $nativeIcon.Dispose() }
Write-Host 'Verified Windows EXE icon and matching embedded tray artwork at all nine sizes.'
