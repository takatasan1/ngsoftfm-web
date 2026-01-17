param(
  [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot ".."))
)

$destDir = Join-Path $RepoRoot "web\NgSoftFmWeb\wwwroot\vendor"
$destFile = Join-Path $destDir "hls.js"

New-Item -ItemType Directory -Force -Path $destDir | Out-Null

if (Test-Path $destFile) {
  Write-Host "OK: already exists: $destFile"
  exit 0
}

$urls = @(
  "https://cdn.jsdelivr.net/npm/hls.js@1/dist/hls.min.js",
  "https://unpkg.com/hls.js@1/dist/hls.min.js",
  "https://cdnjs.cloudflare.com/ajax/libs/hls.js/1.5.17/hls.min.js"
)

$progressPreference = 'SilentlyContinue'
$lastError = $null

foreach ($url in $urls) {
  try {
    Write-Host "Downloading: $url"
    Invoke-WebRequest -Uri $url -UseBasicParsing -OutFile $destFile

    if ((Get-Item $destFile).Length -lt 10000) {
      throw "Downloaded file too small, likely blocked or error page."
    }

    Write-Host "OK: saved to $destFile"
    exit 0
  } catch {
    $lastError = $_
    try { Remove-Item -Force $destFile -ErrorAction SilentlyContinue } catch {}
    Write-Host "Failed: $url"
  }
}

Write-Error "Failed to download hls.js from all sources. Last error: $lastError"
exit 1
