param(
  [int]$Port = 8000,
  [string]$Urls = '',
  [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$repo = Join-Path $PSScriptRoot '..'
$proj = Join-Path $repo 'web\NgSoftFmWeb\NgSoftFmWeb.csproj'

if (-not (Test-Path $proj)) {
  throw "Project not found: $proj"
}

if (-not $Urls -or $Urls.Trim() -eq '') {
  $Urls = "http://0.0.0.0:$Port"
}

# Optional: set NGSOFTFM_ADMIN_TOKEN before starting if you want to protect restart endpoint.
# Example:
#   $env:NGSOFTFM_ADMIN_TOKEN = 'change-me'

$env:ASPNETCORE_URLS = $Urls

$restartExitCode = 42

Write-Host "Starting NgSoftFmWeb with auto-restart..." -ForegroundColor Cyan
Write-Host "URLs: $Urls" -ForegroundColor Green
Write-Host "Restart exit code: $restartExitCode" -ForegroundColor DarkGray
Write-Host "Stop permanently: Ctrl+C" -ForegroundColor Yellow

while ($true) {
  Write-Host "\n[web] dotnet run ($Configuration)" -ForegroundColor Cyan

  dotnet run --project $proj -c $Configuration --no-launch-profile --urls $Urls
  $code = $LASTEXITCODE

  if ($code -eq $restartExitCode) {
    Write-Host "[web] Restart requested. Restarting in 1s..." -ForegroundColor Yellow
    Start-Sleep -Seconds 1
    continue
  }

  Write-Host "[web] Exited with code $code. Not restarting." -ForegroundColor Red
  exit $code
}
