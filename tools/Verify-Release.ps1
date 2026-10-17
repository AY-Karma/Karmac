param(
    [string]$Version = '1.0.0-preview.1',
    [string]$ArtifactDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts')
)
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'Portable-Files.ps1')
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$metadata = Get-Content -LiteralPath (Join-Path $ArtifactDirectory 'release.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ($metadata.product -ne 'Karmac' -or $metadata.version -ne $Version) { throw 'Release identity mismatch.' }
$manifest = @(Get-Content -LiteralPath (Join-Path $ArtifactDirectory 'SHA256SUMS.txt') -Encoding UTF8)
if ($manifest.Count -ne 2 -or @($metadata.files).Count -ne 2) { throw 'Expected exactly two checksums.' }
foreach ($name in @('Karmac.exe','Karmac-Windows-x64.zip')) {
    $record = @($metadata.files | Where-Object { $_.name -ceq $name })
    if ($record.Count -ne 1 -or $record[0].sha256 -cnotmatch '^[a-f0-9]{64}$') { throw 'Invalid file record.' }
    $actual = (Get-FileHash -LiteralPath (Join-Path $ArtifactDirectory $name) -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -cne $record[0].sha256 -or $manifest -cnotcontains ($actual + '  ' + $name)) { throw ('Checksum mismatch: ' + $name) }
}
$exePath = Join-Path $ArtifactDirectory 'Karmac.exe'
$binary = New-Object IO.BinaryReader([IO.File]::OpenRead($exePath))
try {
    if ($binary.ReadUInt16() -ne 0x5A4D) { throw 'Executable is not a Windows PE file.' }
    $binary.BaseStream.Position = 0x3C
    $peOffset = $binary.ReadInt32()
    if ($peOffset -lt 0 -or $peOffset -gt $binary.BaseStream.Length - 26) { throw 'Invalid PE header offset.' }
    $binary.BaseStream.Position = $peOffset
    if ($binary.ReadUInt32() -ne 0x4550 -or $binary.ReadUInt16() -ne 0x8664) { throw 'Release executable is not x64.' }
    $binary.BaseStream.Position = $peOffset + 24
    if ($binary.ReadUInt16() -ne 0x20B) { throw 'Release executable is not PE32+.' }
}
finally { $binary.Dispose() }
$info = [Diagnostics.FileVersionInfo]::GetVersionInfo($exePath)
if ($info.ProductName -ne 'Karmac' -or $info.ProductVersion -ne $Version -or $info.FileVersion -ne $metadata.assemblyVersion) { throw 'Executable version mismatch.' }
if ([string](Get-AuthenticodeSignature -LiteralPath $exePath).Status -ne $metadata.signatureStatus) { throw 'Signature status mismatch.' }
if ($metadata.signatureStatus -notin @('NotSigned','Valid')) { throw 'Invalid or untrusted executable signature.' }
$archive = [IO.Compression.ZipFile]::OpenRead((Join-Path $ArtifactDirectory 'Karmac-Windows-x64.zip'))
try {
    $names = @($archive.Entries | ForEach-Object { $_.FullName })
    if ($names.Count -ne $PortableFiles.Count -or @($names | Select-Object -Unique).Count -ne $names.Count) { throw 'Unexpected or duplicate ZIP entries.' }
    foreach ($name in $names) { if ($PortableFiles -cnotcontains $name) { throw ('Unexpected ZIP entry: ' + $name) } }
    foreach ($name in $PortableFiles) {
        $entry = $archive.GetEntry($name)
        if ($null -eq $entry) { throw ('Missing ZIP entry: ' + $name) }
        $source = if ($name -eq 'Karmac.exe') { $exePath } else { Join-Path $workspace $name }
        $stream = $entry.Open()
        $sha = [Security.Cryptography.SHA256]::Create()
        try { $entryHash = ([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-','') }
        finally { $sha.Dispose(); $stream.Dispose() }
        if ($entryHash -cne (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash) { throw ('Stale or modified ZIP content: ' + $name) }
    }
}
finally { $archive.Dispose() }
if ((Get-FileHash -LiteralPath (Join-Path $ArtifactDirectory 'release-notes.md')).Hash -ne (Get-FileHash -LiteralPath (Join-Path $workspace ('docs/releases/' + $Version + '.md'))).Hash) { throw 'Release notes mismatch.' }
Write-Host ('Verified version, signature status, checksums, notes, and all ' + $PortableFiles.Count + ' portable ZIP entries.')
