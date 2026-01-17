@echo off
setlocal

REM Start NGSoftFM Web server (PowerShell supervisor) and open browser
set "ROOT=%~dp0.."
REM Bind to all interfaces so access works from other devices (LAN/Tailscale).
set "BIND_URL=http://0.0.0.0:5055"
REM Open UI locally via loopback.
set "OPEN_URL=http://127.0.0.1:5055/"
set "HLSJS=%ROOT%\web\NgSoftFmWeb\wwwroot\vendor\hls.js"
set "WEBPS1=%ROOT%\scripts\Start-Web.ps1"

where dotnet >nul 2>nul
if errorlevel 1 (
  echo ERROR: dotnet not found in PATH.
  echo Install .NET SDK and try again.
  exit /b 1
)

where powershell >nul 2>nul
if errorlevel 1 (
  echo ERROR: powershell not found.
  exit /b 1
)

if not exist "%WEBPS1%" (
  echo ERROR: not found: %WEBPS1%
  exit /b 1
)

REM Ensure hls.js is available (needed for HLS playback on Chrome/Edge).
if not exist "%HLSJS%" (
  echo hls.js not found: %HLSJS%
  echo Trying to download it...
  powershell -NoProfile -ExecutionPolicy Bypass -File "%ROOT%\scripts\Install-HlsJs.ps1" -RepoRoot "%ROOT%"
  if errorlevel 1 (
    echo ERROR: Failed to install hls.js. See messages above.
    echo You can also place hls.js manually at: %HLSJS%
    exit /b 1
  )
)

pushd "%ROOT%" >nul

REM Run the server in a separate window so this script can continue.
REM Start-Web.ps1 auto-restarts the server when the Web UI triggers /api/server/restart.
start "NgSoftFmWeb Server" /D "%ROOT%" powershell -NoProfile -ExecutionPolicy Bypass -File "%WEBPS1%" -Port 5055 -Urls "%BIND_URL%" -Configuration Release

REM Give the server a moment to bind the port.
timeout /t 2 /nobreak >nul

REM Open the UI.
start "" "%OPEN_URL%"

popd >nul
endlocal
