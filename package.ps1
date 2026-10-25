param(
    [ValidatePattern('^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$')][string]$Version = '1.0.0-preview.2',
    # Use only to package an already tested binary, including after trusted signing.
    [switch]$UseExistingBuild
)
$ErrorActionPreference = 'Stop'
Push-Location -LiteralPath $PSScriptRoot
try {
    if (-not $UseExistingBuild) { & .\build.ps1 -Test }
    . .\tools\Portable-Files.ps1
    $exePath = Join-Path $PSScriptRoot 'artifacts\Karmac.exe'
    $info = [Diagnostics.FileVersionInfo]::GetVersionInfo($exePath)
    if ($info.ProductName -ne 'Karmac' -or $info.ProductVersion -ne $Version) {
        throw 'The requested release version/product does not match the compiled executable.'
    }
    $notesPath = Join-Path $PSScriptRoot ('docs\releases\' + $Version + '.md')
    if (-not (Test-Path -LiteralPath $notesPath)) { throw 'Versioned release notes are missing.' }
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zipPath = Join-Path $PSScriptRoot 'artifacts\Karmac-Windows-x64.zip'
    $zipStream = [IO.File]::Open($zipPath, [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::None)
    $archive = New-Object IO.Compression.ZipArchive($zipStream, [IO.Compression.ZipArchiveMode]::Create, $false)
    try {
        foreach ($entryName in $PortableFiles) {
            $source = if ($entryName -eq 'Karmac.exe') { $exePath } else { Join-Path $PSScriptRoot $entryName }
            $entry = $archive.CreateEntry($entryName, [IO.Compression.CompressionLevel]::Optimal)
            $inputStream = [IO.File]::Open($source, [IO.FileMode]::Open, [IO.FileAccess]::Read,
                ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
            try {
                $entryStream = $entry.Open()
                try { $inputStream.CopyTo($entryStream) } finally { $entryStream.Dispose() }
            }
            finally { $inputStream.Dispose() }
        }
    }
    finally { $archive.Dispose(); $zipStream.Dispose() }

    $utf8 = New-Object Text.UTF8Encoding($false)
    $hashes = @($exePath, $zipPath) | ForEach-Object {
        $hash = Get-FileHash -LiteralPath $_ -Algorithm SHA256
        [ordered]@{ name = [IO.Path]::GetFileName($_); sha256 = $hash.Hash.ToLowerInvariant() }
    }
    $sums = ($hashes | ForEach-Object { $_.sha256 + '  ' + $_.name }) -join "`n"
    [IO.File]::WriteAllText((Join-Path $PSScriptRoot 'artifacts\SHA256SUMS.txt'), $sums + "`n", $utf8)
    Copy-Item -LiteralPath $notesPath -Destination artifacts\release-notes.md -Force
    $signature = Get-AuthenticodeSignature -LiteralPath $exePath
    if ([string]$signature.Status -notin @('NotSigned','Valid')) { throw 'Refusing to package a binary with an invalid or untrusted signature.' }
    $commit = $null
    $dirty = $null
    if (Test-Path -LiteralPath .git) {
        $commit = (& git rev-parse HEAD 2>$null)
        if ($LASTEXITCODE -ne 0) { throw 'Could not record the Git commit.' }
        $dirty = @(& git status --porcelain).Count -gt 0
        if ($LASTEXITCODE -ne 0) { throw 'Could not inspect source status.' }
    }
    $metadata = [ordered]@{ product = 'Karmac'; version = $Version; assemblyVersion = $info.FileVersion;
        generatedUtc = [DateTime]::UtcNow.ToString('o'); gitCommit = $commit; gitDirty = $dirty;
        signatureStatus = [string]$signature.Status; files = @($hashes) }
    [IO.File]::WriteAllText((Join-Path $PSScriptRoot 'artifacts\release.json'),
        ($metadata | ConvertTo-Json -Depth 5) + "`n", $utf8)
    & .\tools\Verify-Release.ps1 -Version $Version

    $releaseFolder = Join-Path $PSScriptRoot ('artifacts\releases\v' + $Version)
    New-Item -ItemType Directory -Force -Path $releaseFolder | Out-Null
    foreach ($name in @('Karmac-Windows-x64.zip','SHA256SUMS.txt','release-notes.md','release.json')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot ('artifacts\' + $name)) -Destination $releaseFolder -Force
    }
    New-Item -ItemType Directory -Force -Path downloads | Out-Null
    Copy-Item -LiteralPath $zipPath -Destination downloads\Karmac-Windows-x64.zip -Force
    Write-Host ('Release ready locally: ' + $releaseFolder)
    Write-Host ('Authenticode: ' + $signature.Status)
    Write-Host $sums
}
finally { Pop-Location }
