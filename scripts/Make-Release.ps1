param(
  [ValidateSet('Release','Debug')]
  [string]$Configuration = 'Release',

  [ValidateSet('win-x64')]
  [string]$Runtime = 'win-x64',

  [switch]$SelfContained = $true
)

$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$webProj  = Join-Path $repoRoot 'web\NgSoftFmWeb\NgSoftFmWeb.csproj'
$nativeExe = Join-Path $repoRoot 'build-ucrt64\softfm.exe'

$distRoot = Join-Path $repoRoot 'dist'
$outRoot  = Join-Path $distRoot 'NgSoftFM-win-x64'
$webOut   = Join-Path $outRoot 'NgSoftFmWeb'
$nativeOut= Join-Path $outRoot 'native'

Write-Host "Repo: $repoRoot"
Write-Host "Out : $outRoot"

if (Test-Path $outRoot) {
  Remove-Item -Recurse -Force $outRoot
}
New-Item -ItemType Directory -Force -Path $webOut | Out-Null
New-Item -ItemType Directory -Force -Path $nativeOut | Out-Null

if (!(Test-Path $webProj)) {
  throw "Web project not found: $webProj"
}

# Publish web
$publishArgs = @(
  'publish', $webProj,
  '-c', $Configuration,
  '-r', $Runtime,
  '-o', $webOut
)
if ($SelfContained) {
  $publishArgs += @('--self-contained', 'true')
} else {
  $publishArgs += @('--self-contained', 'false')
}

Write-Host "Running: dotnet $($publishArgs -join ' ')"
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

# Copy native softfm.exe
if (!(Test-Path $nativeExe)) {
  throw "softfm.exe not found: $nativeExe`nBuild it first (build-ucrt64/softfm.exe)."
}
Copy-Item -Force $nativeExe (Join-Path $nativeOut 'softfm.exe')

# Add starter BAT (Release-friendly)
$starterBat = Join-Path $outRoot 'Start-NgSoftFmWeb.bat'
@'
@echo off
setlocal

REM Requirements:
REM - ffmpeg.exe must be available in PATH

where ffmpeg.exe >NUL 2>NUL
if errorlevel 1 (
  echo [ERROR] ffmpeg.exe was not found in PATH.
  echo         Example: winget install Gyan.FFmpeg
  pause
  exit /b 1
)

set "ROOT=%~dp0"
set "WEB=%ROOT%NgSoftFmWeb"
set "EXE=%WEB%\NgSoftFmWeb.exe"
set "DLL=%WEB%\NgSoftFmWeb.dll"

if exist "%EXE%" (
  "%EXE%"
  exit /b %ERRORLEVEL%
)

where dotnet >NUL 2>NUL
if errorlevel 1 (
  echo [ERROR] dotnet was not found. This package might not be self-contained.
  echo         Rebuild as self-contained or install .NET runtime.
  pause
  exit /b 1
)

if exist "%DLL%" (
  dotnet "%DLL%"
  exit /b %ERRORLEVEL%
)

echo [ERROR] NgSoftFmWeb.exe / NgSoftFmWeb.dll not found.
pause
exit /b 1
'@ | Set-Content -Encoding ASCII $starterBat

# Copy notices
Copy-Item -Force (Join-Path $repoRoot 'LICENSE') (Join-Path $outRoot 'LICENSE')
Copy-Item -Force (Join-Path $repoRoot 'THIRD_PARTY_NOTICES.md') (Join-Path $outRoot 'THIRD_PARTY_NOTICES.md')

# README (Japanese) - generated from UTF-8 base64 to avoid script file encoding issues
$readme = Join-Path $outRoot 'README.txt'
$readmeB64 = @'
TkdTb2Z0Rk0gKHdpbi14NjQpCgrotbfli5U6CiAgMSkgZmZtcGVnLmV4ZSDjgpIgUEFUSCDjgavpgJrjgZkKICAgICDkvos6IHdpbmdldCBpbnN0YWxsIEd5YW4uRkZtcGVnCiAgMikgU3RhcnQtTmdTb2Z0Rm1XZWIuYmF0IOOCkuWun+ihjAoK5rOo5oSPOgotIHNvZnRmbS5leGUg44GvIG5hdGl2ZS9zb2Z0Zm0uZXhlIOOBq+WQjOaisea4iOOBv+OBp+OBmeOAggotIOODl+ODquOCu+ODg+ODiOOBryAlTE9DQUxBUFBEQVRBJVxOZ1NvZnRGbVdlYlxwcmVzZXRzLmpzb24g44Gr5L+d5a2Y44GV44KM44G+44GZ44CC
'@
$readmeBytes = [Convert]::FromBase64String(($readmeB64 -replace '\s',''))
$readmeText = [Text.Encoding]::UTF8.GetString($readmeBytes)
[System.IO.File]::WriteAllText($readme, $readmeText, (New-Object System.Text.UTF8Encoding($true)))

# Zip
$zipPath = Join-Path $distRoot 'NgSoftFM-win-x64.zip'
if (Test-Path $zipPath) { Remove-Item -Force $zipPath }
Write-Host "Creating ZIP: $zipPath"
Compress-Archive -Path (Join-Path $outRoot '*') -DestinationPath $zipPath

Write-Host "Done: $zipPath"