param(
  [int]$Freq = 80000000,
  [int]$Port = 8000,
  [string]$Gain = 'auto',
  [int]$PcmRate = 48000,
  [int]$Srate = 1000000,
  [string]$Path = '/fm.mp3',
  [double]$PilotMin = 0,
  [double]$PilotMinOff = 0,
  [double]$PilotHyst = 0,
  [switch]$Mono,
  [switch]$Agc,
  [switch]$NoSudo,
  [switch]$DryRun
)

function Convert-ToWslPath {
  param([Parameter(Mandatory=$true)][string]$WindowsPath)
  $full = (Resolve-Path $WindowsPath).Path
  $drive = $full.Substring(0,1).ToLowerInvariant()
  $rest = $full.Substring(2).Replace('\','/')
  return "/mnt/$drive$rest"
}

$repoWin = Join-Path $PSScriptRoot '..'
$repoWsl = Convert-ToWslPath $repoWin

$argsList = @(
  "--freq $Freq",
  "--port $Port",
  "--gain $Gain",
  "--pcmrate $PcmRate",
  "--srate $Srate",
  "--path $Path"
)

if ($PilotMin -gt 0)    { $argsList += "--pilot-min $PilotMin" }
if ($PilotMinOff -gt 0) { $argsList += "--pilot-min-off $PilotMinOff" }
if ($PilotHyst -gt 0)   { $argsList += "--pilot-hyst $PilotHyst" }

if ($Mono)  { $argsList += '--mono' }
if ($Agc)   { $argsList += '--agc' }
if ($NoSudo){ $argsList += '--no-sudo' }

$wslCmd = "cd $repoWsl && ./scripts/stream_fm_http.sh $($argsList -join ' ')"

Write-Host "Starting FM stream (WSL backend)..." -ForegroundColor Cyan
Write-Host "Open: http://localhost:$Port$Path" -ForegroundColor Green
Write-Host "Stop: Ctrl+C in this window" -ForegroundColor Yellow

if ($DryRun) {
  Write-Host "WSL command:" -ForegroundColor DarkGray
  Write-Host $wslCmd -ForegroundColor DarkGray
  return
}

wsl.exe -e bash -lc $wslCmd
