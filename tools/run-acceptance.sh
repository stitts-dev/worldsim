#!/usr/bin/env bash
#
# Bring the whole sim up unattended and run the acceptance harness against it.
#
# Unity cannot run the sim headless — Play mode needs a live editor — so this
# launches a real editor with -autoplay (see Assets/Editor/AutoPlay.cs), waits
# for the MJPEG stream to answer, runs tools/acceptance.py over exactly the
# surface the real cat-sentry detector uses, and tears the editor down again.
#
# Exit code is the harness's own: 0 all checks passed, 1 something failed.
# Any argument is forwarded to acceptance.py (e.g. --soak 1800).
#
#   ./tools/run-acceptance.sh
#   ./tools/run-acceptance.sh --soak 1800
#
set -uo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
UNITY="/Applications/Unity/Hub/Editor/6000.0.82f1/Unity.app/Contents/MacOS/Unity"
MOSQUITTO="/opt/homebrew/opt/mosquitto/sbin/mosquitto"
STREAM_URL="http://localhost:8089/snapshot"
RUN_LOG="$REPO/autoplay.log"
SCENARIO="$REPO/tools/scenarios/cat-basement-smoke.json"
SCENE="$(python3 "$REPO/tools/pi_unity.py" "$SCENARIO" --show scene)" || exit 2

# How long to wait for the editor to compile, open the scene and start serving.
# A cold library import is slow; a warm one is well under a minute.
BOOT_TIMEOUT_SEC=300

UNITY_PID=""
MOSQUITTO_STARTED=0

log() { printf '\n=== %s\n' "$*"; }

cleanup() {
  local code=$?
  if [[ -n "$UNITY_PID" ]] && kill -0 "$UNITY_PID" 2>/dev/null; then
    log "stopping Unity (pid $UNITY_PID)"
    # TERM lets the editor exit Play mode and close its sockets; a bare KILL
    # leaves port 8089 in TIME_WAIT and the next run fails to bind.
    kill -TERM "$UNITY_PID" 2>/dev/null
    for _ in $(seq 1 20); do
      kill -0 "$UNITY_PID" 2>/dev/null || break
      sleep 1
    done
    kill -0 "$UNITY_PID" 2>/dev/null && kill -KILL "$UNITY_PID" 2>/dev/null
  fi
  if [[ "$MOSQUITTO_STARTED" == "1" ]]; then
    log "stopping mosquitto (this run started it)"
    pkill -x mosquitto 2>/dev/null
  fi
  exit "$code"
}
trap cleanup EXIT INT TERM

[[ -x "$UNITY" ]] || { echo "no Unity editor at $UNITY" >&2; exit 2; }

# ---- broker ----
if pgrep -x mosquitto >/dev/null; then
  log "mosquitto already running, leaving it alone"
else
  [[ -x "$MOSQUITTO" ]] || { echo "no mosquitto at $MOSQUITTO (brew install mosquitto)" >&2; exit 2; }
  log "starting mosquitto on 1883"
  "$MOSQUITTO" -p 1883 >"$REPO/mosquitto.log" 2>&1 &
  MOSQUITTO_STARTED=1
  sleep 1
fi

# ---- editor ----
# A stale editor holds the project lock and port 8089; refuse rather than fight it.
if pgrep -f "Unity.app/Contents/MacOS/Unity.*$REPO" >/dev/null; then
  echo "a Unity editor already has this project open - close it first" >&2
  exit 2
fi

log "launching Unity with -autoplay (log: $RUN_LOG)"
: >"$RUN_LOG" || { echo "cannot write Unity log at $RUN_LOG" >&2; exit 2; }
"$UNITY" -projectPath "$REPO" -autoplay -scene "$SCENE" -logFile "$RUN_LOG" &
UNITY_PID=$!

log "waiting for the stream on $STREAM_URL (up to ${BOOT_TIMEOUT_SEC}s)"
booted=0
for _ in $(seq 1 "$BOOT_TIMEOUT_SEC"); do
  if ! kill -0 "$UNITY_PID" 2>/dev/null; then
    echo "Unity exited before it ever served a frame - see $RUN_LOG" >&2
    grep -E "error CS|Fatal|Aborted" "$RUN_LOG" | head -20 >&2
    wait "$UNITY_PID"
    RESULT=$?
    UNITY_PID=""
    [[ "$RESULT" == "0" ]] && RESULT=1
    exit "$RESULT"
  fi
  if curl -fsS --max-time 2 -o /dev/null "$STREAM_URL" 2>/dev/null; then
    booted=1
    break
  fi
  sleep 1
done

if [[ "$booted" != "1" ]]; then
  echo "stream never came up within ${BOOT_TIMEOUT_SEC}s - see $RUN_LOG" >&2
  grep -E "error CS|Exception|FrameStreamer" "$RUN_LOG" | head -20 >&2
  exit 1
fi

log "stream is live, running the Pi Unity scenario gate"
python3 "$REPO/tools/test_pi_unity.py"
PI_UNITY_RESULT=$?
if [[ "$PI_UNITY_RESULT" != "0" ]]; then
  exit "$PI_UNITY_RESULT"
fi

# acceptance.py needs a RUNNING sim, and until now it only got one because a
# test that happens to sort last happens to end by calling /play. Rename that
# test and the whole suite would run against a frozen sim without failing:
# FrameStreamer ticks on unscaledDeltaTime, so a paused sim keeps serving
# frames and the criteria still pass on what is really a still image. Ask for
# play here instead of inheriting it from alphabetical test order.
log "resuming the sim before the acceptance criteria"
if ! curl -fsS --max-time 5 -o /dev/null "http://localhost:8090/play"; then
  echo "the control surface on 8090 stopped answering during the scenario gate -" >&2
  echo "the sim is gone, so the acceptance criteria cannot mean anything. See $RUN_LOG" >&2
  exit 1
fi

# Liveness was proved before the gate ran, and the gate reloads the scene
# several times. FrameStreamer has no DontDestroyOnLoad, so every reload stops
# and restarts its listener - and a failed rebind kills the stream for good.
# Without this re-probe acceptance.py reports a dead port as criterion
# failures, which sends the reader hunting for a bug in the sim. A harness that
# blames the wrong thing costs more than one that says nothing.
if ! curl -fsS --max-time 5 -o /dev/null "$STREAM_URL"; then
  echo "the MJPEG stream on $STREAM_URL died during the scenario gate -" >&2
  echo "this is an infrastructure failure, not a criterion failure. See $RUN_LOG" >&2
  exit 1
fi

python3 "$REPO/tools/acceptance.py" "$@"
RESULT=$?

# Console errors are a criterion in their own right (design.md #1) and the
# harness cannot see them from outside the process.
log "Unity console errors during the run"
# Exclude only AutoPlay's benign progress line. The blanket "^AutoPlay" filter
# also swallowed "AutoPlay: could not enter Play mode - <exception>", which is
# the one AutoPlay line this criterion exists to surface.
ERRORS="$(grep -E "error CS|NullReferenceException|Exception:" "$RUN_LOG" | grep -v "^AutoPlay: entering Play mode" | sed -n '1,20p')"
if [[ -n "$ERRORS" ]]; then
  printf '%s\n' "$ERRORS"
  echo "(errors above - criterion 1 is not met even if the harness passed)"
  RESULT=1
else
  echo "none"
fi

exit "$RESULT"
