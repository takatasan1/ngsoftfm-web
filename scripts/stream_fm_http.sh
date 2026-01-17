#!/usr/bin/env bash
set -euo pipefail

# Stream FM audio from softfm to an HTTP endpoint via ffmpeg.
# Intended for WSL/Linux: Windows can play via http://localhost:8000/fm.mp3
#
# Usage:
#   ./scripts/stream_fm_http.sh --freq 80000000
#   ./scripts/stream_fm_http.sh --freq 80000000 --mono
#   ./scripts/stream_fm_http.sh --freq 80000000 --gain 22.9 --port 8000
#   ./scripts/stream_fm_http.sh --freq 80000000 --no-sudo
#
# Notes:
# - Uses softfm raw output (-R -) to stdout.
# - ffmpeg runs as an HTTP server with -listen 1 (single-client is simplest).

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SOFTFM_BIN_DEFAULT="$ROOT_DIR/build/softfm"

freq="80000000"
srate="1000000"
pcmrate="48000"
gain="auto"
pilot_min=""
pilot_min_off=""
pilot_hyst=""
mono=0
agc=0
use_sudo=1
restart=1
port="8000"
path="/fm.mp3"
softfm_bin="$SOFTFM_BIN_DEFAULT"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --freq) freq="$2"; shift 2;;
    --srate) srate="$2"; shift 2;;
    --pcmrate) pcmrate="$2"; shift 2;;
    --gain) gain="$2"; shift 2;;
    --pilot-min) pilot_min="$2"; shift 2;;
    --pilot-min-off) pilot_min_off="$2"; shift 2;;
    --pilot-hyst) pilot_hyst="$2"; shift 2;;
    --mono) mono=1; shift;;
    --agc) agc=1; shift;;
    --sudo) use_sudo=1; shift;;
    --no-sudo) use_sudo=0; shift;;
    --restart) restart=1; shift;;
    --once) restart=0; shift;;
    --port) port="$2"; shift 2;;
    --path) path="$2"; shift 2;;
    --softfm) softfm_bin="$2"; shift 2;;
    -h|--help)
      sed -n '1,120p' "$0" | sed 's/^#\s\{0,1\}//'
      exit 0
      ;;
    *)
      echo "Unknown arg: $1" >&2
      exit 2
      ;;
  esac
done

if [[ ! -x "$softfm_bin" ]]; then
  echo "softfm not found or not executable: $softfm_bin" >&2
  echo "Build it first: mkdir -p build && cd build && cmake .. && make -j" >&2
  exit 1
fi

if ! command -v ffmpeg >/dev/null 2>&1; then
  echo "ffmpeg not found. Install in WSL: sudo apt-get install -y ffmpeg" >&2
  exit 1
fi

rtlsdr_device_count() {
  # Use softfm itself to query device list. This does not rely on rtl_test being installed.
  local out
  out="$({ "$softfm_bin" -t rtlsdr -d list -W /dev/null; } 2>&1 || true)"
  echo "$out" | sed -n 's/^Found \([0-9][0-9]*\) devices:.*/\1/p' | head -n 1
}

wait_for_rtlsdr() {
  local n
  n="$(rtlsdr_device_count)"
  if [[ -z "$n" ]]; then
    # Could not parse; assume something else will surface in run_once.
    return 0
  fi
  if [[ "$n" == "0" ]]; then
    echo "No RTL-SDR devices found in WSL." >&2
    echo "If you unplugged/replugged it, you usually must re-attach it to WSL2 (usbipd-win)." >&2
    echo "Windows (Admin PowerShell) example:" >&2
    echo "  usbipd list" >&2
    echo "  usbipd attach --wsl --busid <BUSID>" >&2
    echo "Then verify in WSL: lsusb" >&2
    return 1
  fi
  return 0
}

resolve_supported_gain() {
  local requested="$1"

  if [[ "$requested" == "auto" || "$requested" == "list" ]]; then
    echo "$requested"
    return 0
  fi

  if [[ ! "$requested" =~ ^-?[0-9]+(\.[0-9]+)?$ ]]; then
    echo "$requested"
    return 0
  fi

  # Query supported gains from softfm. This requires opening the device.
  local query_cmd=("$softfm_bin" -t rtlsdr -c gain=list -W /dev/null)
  if [[ $use_sudo -eq 1 ]]; then
    query_cmd=(sudo "${query_cmd[@]}")
  fi

  local out
  out="$({ "${query_cmd[@]}"; } 2>&1 || true)"

  local gains_line
  gains_line="$(echo "$out" | sed -n 's/.*Available gains (dB): //p' | head -n 1)"
  if [[ -z "$gains_line" ]]; then
    echo "$requested"
    return 0
  fi

  local best
  best="$(echo "$gains_line" | awk -v r="$requested" '
    BEGIN { best=""; bestd=1e9 }
    {
      for (i=1; i<=NF; i++) {
        g=$i
        d=g-r
        if (d<0) d=-d
        if (d<bestd) { bestd=d; best=g }
      }
    }
    END { if (best!="") print best }
  ')"

  if [[ -n "$best" && "$best" != "$requested" ]]; then
    echo "Requested gain ${requested} dB -> using supported gain ${best} dB" >&2
    echo "$best"
    return 0
  fi

  echo "$requested"
}

gain="$(resolve_supported_gain "$gain")"

cfg="freq=${freq},srate=${srate},gain=${gain}"
if [[ $agc -eq 1 ]]; then
  cfg+=",agc"
fi

mono_flag=()
channels=2
if [[ $mono -eq 1 ]]; then
  mono_flag=(-M)
  channels=1
fi

if [[ "$path" != /* ]]; then
  path="/$path"
fi

url="http://0.0.0.0:${port}${path}"
echo "Starting softfm -> ffmpeg HTTP stream"
echo "  softfm:  $softfm_bin"
echo "  freq:    $freq Hz"
echo "  IF rate:  $srate Hz"
echo "  audio:   $pcmrate Hz, channels=$channels"
echo "  gain:    $gain"
if [[ -n "$pilot_min" || -n "$pilot_min_off" || -n "$pilot_hyst" ]]; then
  echo "  pilot:   min=$pilot_min off=$pilot_min_off hyst=$pilot_hyst"
fi
echo "  sudo:    $use_sudo"
echo "  url:     $url"
echo

echo "Tip: open on Windows: http://localhost:${port}${path}"
echo "Note: If the player disconnects, 'Broken pipe' is normal."
echo

pilot_flags=()
if [[ -n "$pilot_min" ]]; then
  pilot_flags+=(--pilot-min "$pilot_min")
fi
if [[ -n "$pilot_min_off" ]]; then
  pilot_flags+=(--pilot-min-off "$pilot_min_off")
fi
if [[ -n "$pilot_hyst" ]]; then
  pilot_flags+=(--pilot-hyst "$pilot_hyst")
fi

softfm_cmd=("$softfm_bin" -t rtlsdr -r "$pcmrate" "${pilot_flags[@]}" "${mono_flag[@]}" -c "$cfg" -R -)
if [[ $use_sudo -eq 1 ]]; then
  sudo -v
  softfm_cmd=(sudo "${softfm_cmd[@]}")
fi

run_once() {
  "${softfm_cmd[@]}" \
    | ffmpeg -hide_banner -loglevel warning \
        -f s16le -ar "$pcmrate" -ac "$channels" -i pipe:0 \
        -c:a libmp3lame -b:a 192k \
        -content_type audio/mpeg \
        -listen 1 -f mp3 "$url"
}

while true; do
  if ! wait_for_rtlsdr; then
    if [[ $restart -eq 1 ]]; then
      sleep 2
      continue
    fi
    exit 1
  fi

  set +e
  run_once
  rc=$?
  set -e

  if [[ $restart -eq 1 ]]; then
    echo "Stream ended (rc=$rc). Waiting for next client..." >&2
    sleep 1
    continue
  fi

  exit $rc
done
