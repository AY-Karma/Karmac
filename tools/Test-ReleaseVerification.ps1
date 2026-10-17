# Exercise rejection paths on disposable copies, never the real release.
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $workspace 'artifacts'))
$sandbox = Join-Path $artifactRoot ('release-verification-' + [Guid]::NewGuid().ToString('N'))
$names = @('Karmac.exe','Karmac-Windows-x64.zip','SHA256SUMS.txt','release.json','release-notes.md')
$utf8 = New-Object Text.UTF8Encoding($false)
function Reset-Copies {
    foreach ($name in $names) { Copy-Item -LiteralPath (Join-Path $artifactRoot $name) -Destination $sandbox -Force }
}
function Assert-Rejected([string]$Expected) {
    $rejected = $false
    try { & (Join-Path $PSScriptRoot 'Verify-Release.ps1') -Version $version -ArtifactDirectory $sandbox }
    catch {
        if ($_.Exception.Message -notlike $Expected) { throw }
        $rejected = $true
    }
    if (-not $rejected) { throw ('Verifier incorrectly accepted invalid package: ' + $Expected) }
}
New-Item -ItemType Directory -Path $sandbox | Out-Null
try {
    Reset-Copies
    $version = (Get-Content -LiteralPath (Join-Path $sandbox 'release.json') -Raw -Encoding UTF8 | ConvertFrom-Json).version
    & (Join-Path $PSScriptRoot 'Verify-Release.ps1') -Version $version -ArtifactDirectory $sandbox
    [IO.File]::AppendAllText((Join-Path $sandbox 'Karmac.exe'), 'synthetic tampering', $utf8)
    Assert-Rejected 'Checksum mismatch: Karmac.exe'
    Write-Host 'PASS modified executable is rejected.'

    Reset-Copies
    $metadataPath = Join-Path $sandbox 'release.json'
    $metadata = Get-Content -LiteralPath $metadataPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $metadata.version = '0.0.0'
    [IO.File]::WriteAllText($metadataPath, ($metadata | ConvertTo-Json -Depth 5), $utf8)
    Assert-Rejected 'Release identity mismatch.'
    Write-Host 'PASS mismatched release version is rejected.'

    Reset-Copies
    $zipPath = Join-Path $sandbox 'Karmac-Windows-x64.zip'
    $archive = [IO.Compression.ZipFile]::Open($zipPath, [IO.Compression.ZipArchiveMode]::Update)
    try { [void]$archive.CreateEntry('diagnostics/synthetic-private-file.json') }
    finally { $archive.Dispose() }
    # Update both hash records so the ZIP allowlist must reject the extra entry.
    $metadata = Get-Content -LiteralPath $metadataPath -Raw -Encoding UTF8 | ConvertFrom-Json
    ($metadata.files | Where-Object { $_.name -eq 'Karmac-Windows-x64.zip' }).sha256 =
        (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText($metadataPath, ($metadata | ConvertTo-Json -Depth 5), $utf8)
    $sums = ($metadata.files | ForEach-Object { $_.sha256 + '  ' + $_.name }) -join "`n"
    [IO.File]::WriteAllText((Join-Path $sandbox 'SHA256SUMS.txt'), $sums + "`n", $utf8)
    Assert-Rejected 'Unexpected or duplicate ZIP entries.'
    Write-Host 'PASS extra ZIP entries are rejected even with matching checksums.'
}
finally {
    $resolved = [IO.Path]::GetFullPath($sandbox)
    if (-not $resolved.StartsWith($artifactRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
        -not [IO.Path]::GetFileName($resolved).StartsWith('release-verification-')) { throw 'Unsafe verification cleanup path.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
