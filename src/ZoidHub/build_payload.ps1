# Zips Assets\WebMap, Assets\Renderer, and ..\..\LuaMod into Assets\Payload.zip, which gets
# embedded into the exe as a single resource (see PayloadExtractor.cs) - this is what makes
# ZoidHub a genuinely portable single exe instead of needing a companion folder kept alongside
# it. Skips the zip step if nothing under those folders changed since it was last built.
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$zipPath = Join-Path $root "Assets\Payload.zip"
$sources = @(
    (Join-Path $root "Assets\WebMap"),
    (Join-Path $root "Assets\Renderer"),
    (Join-Path $root "..\..\LuaMod")
)

$newestSource = $sources |
    ForEach-Object { Get-ChildItem $_ -Recurse -File -ErrorAction SilentlyContinue } |
    Where-Object { $_.FullName -notmatch '__pycache__' -and $_.Name -ne 'python-embed.zip' } |
    Measure-Object -Property LastWriteTimeUtc -Maximum |
    Select-Object -ExpandProperty Maximum

if ((Test-Path $zipPath) -and (Get-Item $zipPath).LastWriteTimeUtc -gt $newestSource) {
    Write-Host "Payload.zip is up to date, skipping."
    exit 0
}

Write-Host "Building Assets\Payload.zip..."
$stagingDir = Join-Path $env:TEMP "zoidhub_payload_staging"
if (Test-Path $stagingDir) { Remove-Item $stagingDir -Recurse -Force }
New-Item -ItemType Directory -Path $stagingDir | Out-Null

Copy-Item (Join-Path $root "Assets\WebMap") (Join-Path $stagingDir "WebMap") -Recurse
Copy-Item (Join-Path $root "Assets\Renderer") (Join-Path $stagingDir "Renderer") -Recurse
Copy-Item (Join-Path $root "..\..\LuaMod") (Join-Path $stagingDir "LuaMod") -Recurse

Get-ChildItem $stagingDir -Recurse -Directory -Filter "__pycache__" | Remove-Item -Recurse -Force
$embedZip = Join-Path $stagingDir "Renderer\python-embed.zip"
if (Test-Path $embedZip) { Remove-Item $embedZip -Force }

if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path (Join-Path $stagingDir "*") -DestinationPath $zipPath -CompressionLevel Optimal

Remove-Item $stagingDir -Recurse -Force
Write-Host "Wrote $zipPath ($([math]::Round((Get-Item $zipPath).Length / 1MB, 1)) MB)"
