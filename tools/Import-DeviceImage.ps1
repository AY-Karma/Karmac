param(
    [Parameter(Mandatory=$true)][string]$ModelKey,
    [Parameter(Mandatory=$true)][string]$ImagePath
)
$ErrorActionPreference = 'Stop'
$catalogPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'assets\model-images\catalog.json'
$catalog = Get-Content -LiteralPath $catalogPath -Raw | ConvertFrom-Json
if ($ModelKey -notmatch '^[a-z0-9-]{1,80}$' -or $ModelKey -notin $catalog.models.key) {
    throw ('Unknown model key. Choose: ' + ($catalog.models.key -join ', '))
}
$source = Get-Item -LiteralPath $ImagePath
if ($source.PSIsContainer -or $source.Extension.ToLowerInvariant() -notin @('.png','.jpg','.jpeg')) {
    throw 'Choose a PNG or JPEG image that you have the right to use.'
}
if ($source.Length -eq 0 -or $source.Length -gt 4MB) { throw 'Images must be between 1 byte and 4 MB.' }
Add-Type -AssemblyName System.Drawing
$stream = [IO.File]::OpenRead($source.FullName)
try {
    $image = [Drawing.Image]::FromStream($stream, $false, $true)
    try {
        if ($image.Width -lt 1 -or $image.Height -lt 1 -or $image.Width -gt 8192 -or $image.Height -gt 8192) {
            throw 'Choose an image no larger than 8192 pixels on either side.'
        }
    } finally { $image.Dispose() }
} finally { $stream.Dispose() }
$folder = Join-Path $env:LOCALAPPDATA 'Karmac\Artwork'
New-Item -ItemType Directory -Path $folder -Force | Out-Null
$destination = Join-Path $folder ($ModelKey + $source.Extension.ToLowerInvariant())
if ([IO.Path]::GetFullPath($destination) -ne $source.FullName) {
    Copy-Item -LiteralPath $source.FullName -Destination $destination -Force
}
# Remove only the two alternative formats of this exact model, after the new copy succeeds.
foreach ($extension in @('.png','.jpg','.jpeg')) {
    $alternative = Join-Path $folder ($ModelKey + $extension)
    if ($alternative -ne $destination -and (Test-Path -LiteralPath $alternative)) {
        Remove-Item -LiteralPath $alternative
    }
}
Write-Host ('Installed image for {0}: {1}' -f $ModelKey,$destination)
Write-Host 'Use Refresh in Karmac or reopen the app. The source image is copied unchanged and is not added to release packages.'
