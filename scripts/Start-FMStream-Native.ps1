param(
  [int]$Freq = 80000000,
  [int]$Port = 8000,
  [string]$Gain = 'auto',
  [int]$PcmRate = 48000,
  [int]$Srate = 1000000,
  [string]$Path = '/fm.mp3',
  [string]$BuildDir = '',
  [double]$PilotMin = 0,
  [double]$PilotMinOff = 0,
  [double]$PilotHyst = 0,
  [switch]$Mono,
  [switch]$Agc,
  [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

$repo = Join-Path $PSScriptRoot '..'

# Ensure MSYS2(UCRT64) runtime DLLs (librtlsdr.dll, libusb-1.0.dll, etc.) are discoverable.
$msysUcrtBin = 'C:\msys64\ucrt64\bin'
if (Test-Path $msysUcrtBin) {
  if (($env:Path -split ';') -notcontains $msysUcrtBin) {
    $env:Path = "$msysUcrtBin;$env:Path"
  }
}

$softfmCandidates = @()
if ($BuildDir -and $BuildDir.Trim() -ne '') {
  $softfmCandidates += (Join-Path (Join-Path $repo $BuildDir) 'softfm.exe')
}
$softfmCandidates += (Join-Path $repo 'build-ucrt64\softfm.exe')
$softfmCandidates += (Join-Path $repo 'build\softfm.exe')

$softfm = $softfmCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not (Test-Path $softfm)) {
  $searched = ($softfmCandidates -join "`n  - ")
  throw "softfm.exe not found. Searched:`n  - $searched`nBuild a native Windows binary first (e.g. with MSYS2/MinGW): cmake -B build-ucrt64 -S . -G `"MinGW Makefiles`" ; cmake --build build-ucrt64"
}

$ffmpeg = (Get-Command ffmpeg.exe -ErrorAction SilentlyContinue)
if (-not $ffmpeg) {
  throw "ffmpeg.exe not found in PATH. Install ffmpeg for Windows and add it to PATH."
}
$ffmpegPath = $ffmpeg.Source

if (-not $Path.StartsWith('/')) { $Path = "/$Path" }

$channels = 2
$monoFlag = ''
if ($Mono) { $channels = 1; $monoFlag = '-M' }

$cfg = "freq=$Freq,srate=$Srate,gain=$Gain"
if ($Agc) { $cfg = "$cfg,agc" }

$pilotFlags = @()
if ($PilotMin -gt 0) { $pilotFlags += "--pilot-min $PilotMin" }
if ($PilotMinOff -gt 0) { $pilotFlags += "--pilot-min-off $PilotMinOff" }
if ($PilotHyst -gt 0) { $pilotFlags += "--pilot-hyst $PilotHyst" }

$url = "http://0.0.0.0:$Port$Path"

Write-Host "Starting FM stream (Windows native)..." -ForegroundColor Cyan
Write-Host "Open: http://localhost:$Port$Path" -ForegroundColor Green
Write-Host "Stop: Ctrl+C in this window" -ForegroundColor Yellow

# Important: PowerShell pipeline is not byte-stream safe for raw PCM.
# Use cmd.exe pipeline to preserve raw bytes.
$softfmArgs = @(
  "-t rtlsdr",
  "-r $PcmRate",
  $monoFlag,
  ($pilotFlags -join ' '),
  ('-c "' + $cfg + '"'),
  "-R -"
) | Where-Object { $_ -and $_.Trim() -ne '' }

$ffmpegArgs = @(
  "-hide_banner -loglevel warning",
  "-f s16le -ar $PcmRate -ac $channels -i pipe:0",
  "-c:a libmp3lame -b:a 192k",
  "-content_type audio/mpeg",
  ('-listen 1 -f mp3 "' + $url + '"')
)

# Important: build a single cmd.exe command line (byte-stream safe piping).
$cmd = 'set "PATH=' + $msysUcrtBin + ';%PATH%" & ' +
  '"' + $softfm + '" ' + ($softfmArgs -join ' ') +
  ' | ' +
  '"' + $ffmpegPath + '" ' + ($ffmpegArgs -join ' ')

if ($DryRun) {
  Write-Host "cmd.exe /c $cmd" -ForegroundColor DarkGray
  return
}

cmd.exe /c $cmd
