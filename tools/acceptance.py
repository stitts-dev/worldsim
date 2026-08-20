#!/usr/bin/env python3
"""Acceptance harness for docs/design.md criteria 2, 4, 5, 6 (and the criterion-7
soak). Runs against a live sim: Unity in Play mode plus a mosquitto broker on
localhost:1883. Everything here is external to Unity on purpose — it exercises
exactly the surface the real cat-sentry detector uses.

    python3 tools/acceptance.py            # criteria 2, 4, 5, 6
    python3 tools/acceptance.py --soak 600 # + criterion 7 (seconds)

No third-party dependencies: MJPEG over http.client, MQTT over a hand-rolled
minimal 3.1.1 client (connect/subscribe/publish/ping only).
"""

import argparse
import http.client
import json
import re
import socket
import struct
import sys
import time

STREAM_HOST = "localhost"
STREAM_PORT = 8089
BROKER_HOST = "localhost"
BROKER_PORT = 1883

FIRE_TOPIC = "catsentry/deterrent/fire"
ACK_TOPIC = "catsentry/deterrent/ack"
TELEMETRY_TOPIC = "catsentry/telemetry"

JPEG_SOI = b"\xff\xd8"
JPEG_EOI = b"\xff\xd9"


class Result:
    def __init__(self):
        self.rows = []

    def add(self, criterion, name, ok, detail):
        self.rows.append((criterion, name, ok, detail))
        mark = "PASS" if ok else "FAIL"
        print(f"[{mark}] {criterion}  {name}: {detail}", flush=True)

    @property
    def ok(self):
        return all(r[2] for r in self.rows)


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


def check_stream(result, want_frames=5):
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

        frames, sizes, start = 0, [], time.time()
        buf = b""
        while frames < want_frames and time.time() - start < 15:
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
                sizes.append(eoi + 2 - soi)
                buf = buf[eoi + 2:]
                frames += 1
            if len(buf) > 4_000_000:
                buf = buf[-1_000_000:]
        elapsed = time.time() - start
        fps = (frames - 1) / elapsed if frames > 1 and elapsed > 0 else 0.0
        ok = frames >= want_frames and 8.0 <= fps <= 18.0
        result.add("C2", "/stream frames", ok,
                   f"{frames} JPEGs in {elapsed:.1f}s (~{fps:.1f} fps, contract 10-15), "
                   f"median {sorted(sizes)[len(sizes) // 2] if sizes else 0} bytes")
        conn.close()
    except Exception as e:
        result.add("C2", "/stream frames", False, f"{type(e).__name__}: {e}")


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

        if len(stamps) < 2:
            result.add("C4", "telemetry heartbeat", False,
                       f"only {len(stamps)} heartbeat(s) in {window}s (expected >=2)")
            return
        gaps = [b - a for a, b in zip(stamps, stamps[1:])]
        avg = sum(gaps) / len(gaps)
        try:
            msg = json.loads(payloads[-1])
            shape_ok = msg.get("source") == "sim" and msg.get("status") == "ok" and \
                re.match(r"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$", msg.get("ts", "")) is not None
        except json.JSONDecodeError:
            shape_ok = False
        ok = 8.0 <= avg <= 12.5 and shape_ok
        result.add("C4", "telemetry heartbeat", ok,
                   f"{len(stamps)} beats, avg gap {avg:.1f}s (contract 10s), payload {payloads[-1]}")
    except Exception as e:
        result.add("C4", "telemetry heartbeat", False, f"{type(e).__name__}: {e}")


def check_fire(result, level, criterion):
    """Criteria 5/6: publishing a fire command produces a matching ack."""
    try:
        client = MiniMqtt(client_id=f"acceptance-fire-{level}")
        client.subscribe(ACK_TOPIC)
        time.sleep(0.5)  # let SUBACK land before firing

        fire = {
            "ts": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
            "level": level,
            "target": [0.5, 0.5],
            "duration_ms": 1000,
            "reason": "acceptance test",
        }
        client.publish(FIRE_TOPIC, json.dumps(fire), qos=1)

        ack = None
        for topic, payload in client.messages(6):
            if topic == ACK_TOPIC:
                ack = payload
                break
        client.close()

        if ack is None:
            result.add(criterion, f"fire level={level}", False, "no ack within 6s")
            return
        msg = json.loads(ack)
        ok = msg.get("level") == level and msg.get("ok") is True and bool(msg.get("detail"))
        result.add(criterion, f"fire level={level}", ok, f"ack {ack}")
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

    result = Result()
    print("=== worldsim acceptance (docs/design.md criteria 2, 4, 5, 6) ===\n", flush=True)
    check_stream(result)
    if not args.skip_telemetry:
        check_telemetry(result)
    check_fire(result, "sound", "C5")
    check_fire(result, "air", "C6")
    if args.soak:
        soak(result, args.soak)

    passed = sum(1 for r in result.rows if r[2])
    print(f"\n=== {passed}/{len(result.rows)} checks passed ===", flush=True)
    return 0 if result.ok else 1


if __name__ == "__main__":
    sys.exit(main())
