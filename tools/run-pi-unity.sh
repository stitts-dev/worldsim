#!/usr/bin/env bash
# Launch the Unity scene named by a saved Pi Unity scenario, run it, and tear down.
set -uo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
UNITY="${UNITY:-/Applications/Unity/Hub/Editor/6000.0.82f1/Unity.app/Contents/MacOS/Unity}"
BOOT_TIMEOUT_SEC="${PI_UNITY_BOOT_TIMEOUT_SEC:-300}"
RUN_LOG="${PI_UNITY_LOG:-$REPO/pi-unity.log}"

if [[ "$#" -lt 1 || "$#" -gt 2 ]]; then
  echo "usage: $0 SCENARIO.json [OUTPUT_DIR]" >&2
  exit 2
fi

SCENARIO="$1"
[[ "$SCENARIO" = /* ]] || SCENARIO="$PWD/$SCENARIO"
OUTPUT="${2:-$REPO/artifacts/$(basename "$SCENARIO" .json)}"
[[ "$OUTPUT" = /* ]] || OUTPUT="$PWD/$OUTPUT"

SCENE="$(python3 "$REPO/tools/pi_unity.py" "$SCENARIO" --show scene)" || exit 2
CONTROL_URL="$(python3 "$REPO/tools/pi_unity.py" "$SCENARIO" --show url)" || exit 2
UNITY_PID=""

cleanup() {
  local code=$?
  if [[ -n "$UNITY_PID" ]] && kill -0 "$UNITY_PID" 2>/dev/null; then
    kill -TERM "$UNITY_PID" 2>/dev/null
    for _ in $(seq 1 20); do
      kill -0 "$UNITY_PID" 2>/dev/null || break
      sleep 1
    done
    kill -0 "$UNITY_PID" 2>/dev/null && kill -KILL "$UNITY_PID" 2>/dev/null
  fi
  exit "$code"
}
trap cleanup EXIT INT TERM

[[ -x "$UNITY" ]] || { echo "no Unity editor at $UNITY" >&2; exit 2; }
if pgrep -f "Unity.app/Contents/MacOS/Unity.*$REPO" >/dev/null; then
  echo "a Unity editor already has this project open - close it first" >&2
  exit 2
fi
: >"$RUN_LOG" || { echo "cannot write Unity log at $RUN_LOG" >&2; exit 2; }

"$UNITY" -projectPath "$REPO" -autoplay -scene "$SCENE" -logFile "$RUN_LOG" &
UNITY_PID=$!

booted=0
for _ in $(seq 1 "$BOOT_TIMEOUT_SEC"); do
  if ! kill -0 "$UNITY_PID" 2>/dev/null; then
    echo "Unity exited before the control surface started - see $RUN_LOG" >&2
    grep -E "error CS|Fatal|Aborted|AutoPlay" "$RUN_LOG" | tail -20 >&2
    wait "$UNITY_PID"
    RESULT=$?
    UNITY_PID=""
    [[ "$RESULT" == "0" ]] && RESULT=1
    exit "$RESULT"
  fi
  if curl -fsS --max-time 2 -o /dev/null "${CONTROL_URL%/}/doctor" 2>/dev/null; then
    booted=1
    break
  fi
  sleep 1
done

if [[ "$booted" != "1" ]]; then
  echo "control surface never started within ${BOOT_TIMEOUT_SEC}s - see $RUN_LOG" >&2
  grep -E "error CS|Exception|AutoPlay|SimControl" "$RUN_LOG" | tail -20 >&2
  exit 1
fi

python3 "$REPO/tools/pi_unity.py" "$SCENARIO" --output "$OUTPUT"
RESULT=$?

if grep -Em 20 "error CS|NullReferenceException|Exception:" "$RUN_LOG"; then
  RESULT=1
fi
exit "$RESULT"
