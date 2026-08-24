#!/usr/bin/env python3
"""Acceptance harness for docs/design.md criteria 2, 4, 5, 6 (and the criterion-7
soak). Runs against a live sim: Unity in Play mode plus a mosquitto broker on
localhost:1883. Everything here is external to Unity on purpose - it exercises
exactly the surface the real cat-sentry detector uses.

Criteria 5 and 6 are only half covered. design.md asks for "chirp + ring in
sim, ack published, squatting cat flees"; from outside Unity only the ack is
visible. The chirp, the puff and the flee still need an in-editor check. The
CRITERIA roster below says so, and the banner and summary are printed from it.

    python3 tools/acceptance.py            # criteria 2, 4, 5, 6
    python3 tools/acceptance.py --soak 600 # + criterion 7 (seconds)

Exit codes: 0 every covered criterion passed, 1 a criterion failed, 3 the sim
could not be reached at all - infrastructure, not a product bug.

No third-party dependencies: MJPEG over http.client, MQTT over a hand-rolled
minimal 3.1.1 client (connect/subscribe/publish/ping only).
"""

import argparse
import hashlib
import http.client
import json
import re
import socket
import struct
import sys
import time

STREAM_HOST = "localhost"
STREAM_PORT = 8089
CONTROL_PORT = 8090
BROKER_HOST = "localhost"
BROKER_PORT = 1883

FIRE_TOPIC = "catsentry/deterrent/fire"
ACK_TOPIC = "catsentry/deterrent/ack"
TELEMETRY_TOPIC = "catsentry/telemetry"

# The catsentry-v1 frame spec. The gate and the string the harness prints both
# come from these, so a printed claim can never outrun the code again.
FPS_MIN = 10.0
FPS_MAX = 15.0
STREAM_SAMPLE_FRAMES = 30

# One row per criterion this harness can actually judge, with a label that
# claims no more than it proves. The banner, the summary and the exit code all
# come from this roster, so coverage cannot be overstated in one place and
# under-counted in another.
CRITERIA = (
    ("C2", "MJPEG stream and snapshot"),
    ("C4", "telemetry heartbeats"),
    ("C5", "sound deterrent (ack only - not the chirp, ring or flee)"),
    ("C6", "air deterrent (ack only - not the puff or flee)"),
)
SOAK_CRITERION = ("C7", "stream and MQTT survive a long run")

EXIT_OK = 0
EXIT_FAILED = 1
EXIT_INFRA = 3

JPEG_SOI = b"\xff\xd8"
JPEG_EOI = b"\xff\xd9"


class Result:
    """Verdicts per CRITERION, not per row. Several rows can belong to one
    criterion, and a criterion with no rows at all has not passed - against a
    dead sim a check can vanish rather than fail, and all([]) is True."""

    def __init__(self, roster):
        self.roster = list(roster)
        self.labels = dict(self.roster)
        self.rows = []
        self.skipped = {}

    def add(self, criterion, name, ok, detail):
        self.rows.append((criterion, name, ok, detail))
        mark = "PASS" if ok else "FAIL"
        print(f"[{mark}] {criterion}  {name}: {detail}", flush=True)

    def skip(self, criterion, why):
        self.skipped[criterion] = why
        print(f"[SKIP] {criterion}  {self.labels.get(criterion, '')}: {why}", flush=True)

    def rows_for(self, criterion):
        return [r for r in self.rows if r[0] == criterion]

    def verdict(self, criterion):
        if criterion in self.skipped:
            return "SKIP"
        rows = self.rows_for(criterion)
        if not rows:
            return "FAIL"
        return "PASS" if all(r[2] for r in rows) else "FAIL"

    def note(self, criterion):
        if criterion in self.skipped:
            return self.skipped[criterion]
        rows = self.rows_for(criterion)
        if not rows:
            return "no check completed - the run never got this far"
        failed = [r for r in rows if not r[2]]
        if failed:
            return "; ".join(f"{r[1]}: {r[3]}" for r in failed)
        return f"{len(rows)}/{len(rows)} checks"


def coverage_line(roster=CRITERIA):
    return ", ".join(f"{code} {label}" for code, label in roster)


def sim_reachable(timeout=3):
    """Liveness oracle for the FAIL-vs-INFRA call. /doctor on the control
    surface is independent of both the stream path and the MQTT path, so it
    answers "is the sim there at all" without reusing the thing that failed."""
    try:
        conn = http.client.HTTPConnection(STREAM_HOST, CONTROL_PORT, timeout=timeout)
        conn.request("GET", "/doctor")
        resp = conn.getresponse()
        resp.read()
        conn.close()
        return resp.status == 200
    except Exception:
        return False


def summarize(result, probe=None):
    """Print the per-criterion summary and return the process exit code."""
    probe = probe or sim_reachable
    marks = {code: result.verdict(code) for code, _ in result.roster}
    failed = [code for code, mark in marks.items() if mark == "FAIL"]
    infra = failed and not probe()

    print("\n=== summary ===", flush=True)
    for code, label in result.roster:
        mark = "INFRA" if infra and marks[code] == "FAIL" else marks[code]
        print(f"  [{mark:5}] {code}  {label}: {result.note(code)}", flush=True)

    passed = sum(1 for m in marks.values() if m == "PASS")
    skipped = sum(1 for m in marks.values() if m == "SKIP")
    covered = len(marks) - skipped
    line = f"{passed}/{covered} criteria passed"
    if skipped:
        line += f", {skipped} skipped (not run, not passed)"
    print(f"=== {line} ===", flush=True)

    if infra:
        print(f"the control surface at {STREAM_HOST}:{CONTROL_PORT}/doctor did not answer - "
              "the sim is not running, or its control surface is not enabled. "
              "Treat the failures above as infrastructure, not product.", flush=True)
        return EXIT_INFRA
    return EXIT_FAILED if failed else EXIT_OK


# ---------------- MQTT (minimal 3.1.1 client) ----------------


class MiniMqtt:
    """Just enough MQTT for the acceptance run: CONNECT, SUBSCRIBE, PUBLISH,
    PINGREQ, and reading PUBLISH packets. QoS 0 and 1 publish (fire-and-forget
    on the PUBACK — the sim's own client handles delivery)."""

    def __init__(self, host=BROKER_HOST, port=BROKER_PORT, client_id="acceptance"):
        self.sock = socket.create_connection((host, port), timeout=5)
        self.sock.settimeout(0.2)
        self.client_id = client_id
        self.buf = b""
        self.packet_id = 0
        self._connect()

    def _connect(self):
        cid = self.client_id.encode()
        payload = struct.pack("!H", len(cid)) + cid
        variable = struct.pack("!H", 4) + b"MQTT" + bytes([4, 0x02]) + struct.pack("!H", 30)
        self._send(0x10, variable + payload)
        pkt = self._read_packet(timeout=5)
        if pkt is None or pkt[0] >> 4 != 2:
            raise RuntimeError("MQTT CONNACK not received")
        if pkt[1][1] != 0:
            raise RuntimeError(f"MQTT connection refused, code {pkt[1][1]}")

    def _next_packet_id(self):
        self.packet_id = (self.packet_id % 65535) + 1
        return self.packet_id

    def _send(self, packet_type_flags, body):
        self.sock.sendall(bytes([packet_type_flags]) + self._remaining_length(len(body)) + body)

    @staticmethod
    def _remaining_length(n):
        out = b""
        while True:
            digit = n % 128
            n //= 128
            if n > 0:
                digit |= 0x80
            out += bytes([digit])
            if n == 0:
                return out

    def subscribe(self, topic, qos=1):
        body = struct.pack("!H", self._next_packet_id())
        body += struct.pack("!H", len(topic)) + topic.encode() + bytes([qos])
        self._send(0x82, body)

    def publish(self, topic, payload, qos=1):
        body = struct.pack("!H", len(topic)) + topic.encode()
        if qos > 0:
            body += struct.pack("!H", self._next_packet_id())
        body += payload.encode()
        self._send(0x30 | (qos << 1), body)

    def ping(self):
        self._send(0xC0, b"")

    def _fill(self, timeout):
        deadline = time.time() + timeout
        while time.time() < deadline:
            try:
                chunk = self.sock.recv(4096)
            except socket.timeout:
                continue
            except OSError:
                return False
            if not chunk:
                return False
            self.buf += chunk
            return True
        return False

    def _read_packet(self, timeout=1.0):
        """Returns (header_byte, body) or None on timeout."""
        deadline = time.time() + timeout
        while True:
            if len(self.buf) >= 2:
                multiplier, value, idx = 1, 0, 1
                while idx < len(self.buf):
                    digit = self.buf[idx]
                    value += (digit & 127) * multiplier
                    multiplier *= 128
                    idx += 1
                    if not digit & 0x80:
                        break
                else:
                    if not self._fill(max(0.0, deadline - time.time())):
                        return None
                    continue
                if len(self.buf) >= idx + value:
                    header = self.buf[0]
                    body = self.buf[idx:idx + value]
                    self.buf = self.buf[idx + value:]
                    return header, body
            if time.time() >= deadline:
                return None
            if not self._fill(max(0.0, deadline - time.time())):
                if time.time() >= deadline:
                    return None

    def messages(self, timeout):
        """Yield (topic, payload) PUBLISH packets until timeout elapses."""
        deadline = time.time() + timeout
        while time.time() < deadline:
            pkt = self._read_packet(timeout=min(0.5, max(0.05, deadline - time.time())))
            if pkt is None:
                continue
            header, body = pkt
            if header >> 4 != 3:
                continue
            topic_len = struct.unpack("!H", body[:2])[0]
            topic = body[2:2 + topic_len].decode()
            rest = body[2 + topic_len:]
            if (header >> 1) & 0x03 > 0:  # QoS > 0 carries a packet id
                rest = rest[2:]
            yield topic, rest.decode(errors="replace")

    def close(self):
        try:
            self._send(0xE0, b"")  # DISCONNECT
        except OSError:
            pass
        self.sock.close()


# ---------------- criteria ----------------


class StreamSample:
    """Frames pulled off an MJPEG response, with the clock reading at which each
    one was extracted. Kept as data so the criterion-2 verdicts can be reached
    without a live sim."""

    def __init__(self, started):
        self.started = started
        self.finished = started
        self.sizes = []
        self.digests = []
        self.stamps = []

    def add(self, frame, when):
        self.sizes.append(len(frame))
        self.digests.append(hashlib.sha1(frame).hexdigest())
        self.stamps.append(when)

    @property
    def frames(self):
        return len(self.sizes)


def collect_stream_sample(resp, want_frames, timeout, clock=time.time):
    """Extract JPEGs from an MJPEG body. `resp` needs only .read(n); `clock` is
    injectable so a synthetic stream with known timings can be replayed."""
    sample = StreamSample(started=clock())
    buf = b""
    while sample.frames < want_frames and clock() - sample.started < timeout:
        chunk = resp.read(16384)
        if not chunk:
            break
        buf += chunk
        while True:
            soi = buf.find(JPEG_SOI)
            if soi < 0:
                break
            eoi = buf.find(JPEG_EOI, soi + 2)
            if eoi < 0:
                break
            sample.add(buf[soi:eoi + 2], clock())
            buf = buf[eoi + 2:]
        if len(buf) > 4_000_000:
            buf = buf[-1_000_000:]
    sample.finished = clock()
    return sample


def measured_window(sample):
    """Seconds between the first and last extracted frame. Not the time since
    the request: joining an MJPEG stream costs on average half a frame interval
    waiting for the first complete JPEG, and that wait is not the sim's rate."""
    if sample.frames < 2:
        return 0.0
    return sample.stamps[-1] - sample.stamps[0]


def estimate_fps(sample):
    window = measured_window(sample)
    if sample.frames > 1 and window > 0:
        return (sample.frames - 1) / window
    return 0.0


def evaluate_stream(sample, want_frames):
    fps = estimate_fps(sample)
    window = measured_window(sample)
    distinct = len(set(sample.digests))
    moving = distinct >= 2
    ok = sample.frames >= want_frames and FPS_MIN <= fps <= FPS_MAX and moving
    median = sorted(sample.sizes)[len(sample.sizes) // 2] if sample.sizes else 0
    detail = (f"{sample.frames} JPEGs in {window:.1f}s "
              f"(~{fps:.1f} fps, contract {FPS_MIN:g}-{FPS_MAX:g}), "
              f"median {median} bytes, {distinct} distinct")
    if not moving:
        detail += " - every frame identical, the stream is frozen"
    return ok, detail


def check_stream(result, want_frames=STREAM_SAMPLE_FRAMES):
    """Criterion 2: /stream serves MJPEG at contract fps, /snapshot serves a JPEG."""
    try:
        conn = http.client.HTTPConnection(STREAM_HOST, STREAM_PORT, timeout=10)
        conn.request("GET", "/snapshot")
        resp = conn.getresponse()
        body = resp.read()
        ok = resp.status == 200 and body.startswith(JPEG_SOI) and body.endswith(JPEG_EOI)
        result.add("C2", "/snapshot", ok,
                   f"HTTP {resp.status}, {len(body)} bytes, JPEG markers {'ok' if ok else 'missing'}")
        conn.close()
    except Exception as e:
        result.add("C2", "/snapshot", False, f"{type(e).__name__}: {e}")

    try:
        conn = http.client.HTTPConnection(STREAM_HOST, STREAM_PORT, timeout=15)
        conn.request("GET", "/stream")
        resp = conn.getresponse()
        ctype = resp.getheader("Content-Type") or ""
        ctype_ok = "multipart/x-mixed-replace" in ctype and "boundary=frame" in ctype
        result.add("C2", "/stream content-type", ctype_ok, ctype or "(none)")

        sample = collect_stream_sample(resp, want_frames, timeout=15)
        ok, detail = evaluate_stream(sample, want_frames)
        result.add("C2", "/stream frames", ok, detail)
        conn.close()
    except Exception as e:
        result.add("C2", "/stream frames", False, f"{type(e).__name__}: {e}")


def parse_heartbeat(payload):
    """True if one telemetry payload matches the catsentry-v1 shape. Anything
    unreadable is a shape failure, not an exception: a payload that is valid
    JSON but not an object used to escape .get as an AttributeError and get
    reported as a crash."""
    try:
        msg = json.loads(payload)
    except ValueError:
        return False
    if not isinstance(msg, dict):
        return False
    ts = msg.get("ts")
    return (msg.get("source") == "sim" and msg.get("status") == "ok"
            and isinstance(ts, str)
            and re.match(r"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$", ts) is not None)


def evaluate_telemetry(stamps, payloads, window):
    """Criterion 4 verdict from a collected heartbeat window. Every payload has
    to hold up - checking only the last one passed four malformed beats so long
    as the fifth was well formed."""
    if len(stamps) < 2:
        return False, f"only {len(stamps)} heartbeat(s) in {window}s (expected >=2)"
    gaps = [b - a for a, b in zip(stamps, stamps[1:])]
    avg = sum(gaps) / len(gaps)
    good = [parse_heartbeat(p) for p in payloads]
    shape_ok = all(good)
    ok = 8.0 <= avg <= 12.5 and shape_ok
    detail = (f"{len(stamps)} beats, avg gap {avg:.1f}s (contract 10s), "
              f"{sum(good)}/{len(good)} payloads valid")
    if shape_ok:
        return ok, f"{detail}, payload {payloads[-1]}"
    first_bad = payloads[good.index(False)]
    return ok, f"{detail}, first bad payload {first_bad!r}"


def check_telemetry(result, window=25):
    """Criterion 4: catsentry/telemetry heartbeats every ~10 s."""
    try:
        client = MiniMqtt(client_id="acceptance-telemetry")
        client.subscribe("catsentry/#")
        stamps, payloads = [], []
        for topic, payload in client.messages(window):
            if topic == TELEMETRY_TOPIC:
                stamps.append(time.time())
                payloads.append(payload)
        client.close()
        ok, detail = evaluate_telemetry(stamps, payloads, window)
        result.add("C4", "telemetry heartbeat", ok, detail)
    except Exception as e:
        result.add("C4", "telemetry heartbeat", False, f"{type(e).__name__}: {e}")


def collect_acks(client, timeout):
    """Every payload seen on the ack topic for the full window. `client` needs
    only .messages(). Reading the whole window rather than stopping at the
    first ack is what makes a duplicate ack visible."""
    return [payload for topic, payload in client.messages(timeout) if topic == ACK_TOPIC]


def evaluate_ack(acks, level, window):
    """Criteria 5/6 verdict: one fire must produce EXACTLY ONE ack.

    catsentry-v1 carries no correlation id, so an ack cannot be matched to the
    fire that caused it. Counting acks in a drained window is the strongest
    check the frozen contract allows - and it is the one that catches a turret
    acking the same command twice."""
    if not acks:
        return False, f"no ack within {window:g}s"
    if len(acks) != 1:
        return False, f"{len(acks)} acks, expected 1: {acks}"
    try:
        msg = json.loads(acks[0])
    except ValueError:
        return False, f"unreadable ack (not JSON): {acks[0]!r}"
    if not isinstance(msg, dict):
        return False, f"unreadable ack (not a JSON object): {acks[0]!r}"
    ok = msg.get("level") == level and msg.get("ok") is True and bool(msg.get("detail"))
    return ok, f"ack {acks[0]}"


def check_fire(result, level, criterion, window=6, drain=1.0, connect=MiniMqtt):
    """Criteria 5/6: publishing a fire command produces exactly one ack."""
    try:
        client = connect(client_id=f"acceptance-fire-{level}")
        client.subscribe(ACK_TOPIC)
        # Drain first: this consumes the SUBACK and discards any ack left over
        # from an earlier run or fired by something else, so the count after
        # the publish below belongs to this fire alone.
        for _ in client.messages(drain):
            pass

        fire = {
            "ts": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
            "level": level,
            "target": [0.5, 0.5],
            "duration_ms": 1000,
            "reason": "acceptance test",
        }
        client.publish(FIRE_TOPIC, json.dumps(fire), qos=1)
        acks = collect_acks(client, window)
        client.close()

        ok, detail = evaluate_ack(acks, level, window)
        result.add(criterion, f"fire level={level}", ok, detail)
    except Exception as e:
        result.add(criterion, f"fire level={level}", False, f"{type(e).__name__}: {e}")


def soak(result, seconds):
    """Criterion 7: stream + MQTT survive a long run without dropping out."""
    print(f"\n--- soak: {seconds}s ---", flush=True)
    start = time.time()
    stream_checks, stream_ok = 0, 0
    beats = 0
    client = MiniMqtt(client_id="acceptance-soak")
    client.subscribe(TELEMETRY_TOPIC)
    last_ping = time.time()

    while time.time() - start < seconds:
        for topic, _ in client.messages(15):
            if topic == TELEMETRY_TOPIC:
                beats += 1
        if time.time() - last_ping > 20:
            client.ping()
            last_ping = time.time()
        try:
            conn = http.client.HTTPConnection(STREAM_HOST, STREAM_PORT, timeout=10)
            conn.request("GET", "/snapshot")
            resp = conn.getresponse()
            body = resp.read()
            conn.close()
            stream_checks += 1
            if resp.status == 200 and body.startswith(JPEG_SOI):
                stream_ok += 1
        except Exception:
            stream_checks += 1
        elapsed = int(time.time() - start)
        print(f"  t+{elapsed}s  snapshots {stream_ok}/{stream_checks}  heartbeats {beats}", flush=True)
    client.close()

    expected_beats = int((time.time() - start) / 10) - 1
    ok = stream_ok == stream_checks and beats >= max(1, expected_beats)
    result.add("C7", f"soak {seconds}s", ok,
               f"snapshots {stream_ok}/{stream_checks}, heartbeats {beats} (expected >={max(1, expected_beats)})")


def main():
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--soak", type=int, default=0,
                        help="also run the criterion-7 soak for this many seconds")
    parser.add_argument("--skip-telemetry", action="store_true",
                        help="skip the 25 s telemetry window (faster smoke run)")
    args = parser.parse_args()

    roster = list(CRITERIA)
    if args.soak:
        roster.append(SOAK_CRITERION)
    result = Result(roster)
    print("=== worldsim acceptance ===", flush=True)
    print(f"covers: {coverage_line(roster)}\n", flush=True)

    check_stream(result)
    if args.skip_telemetry:
        result.skip("C4", "--skip-telemetry was passed")
    else:
        check_telemetry(result)
    check_fire(result, "sound", "C5")
    check_fire(result, "air", "C6")
    if args.soak:
        soak(result, args.soak)

    return summarize(result)


if __name__ == "__main__":
    sys.exit(main())
