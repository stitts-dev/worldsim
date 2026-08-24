#!/usr/bin/env python3
"""Offline tests for the acceptance harness's verdicts.

These need no Unity and no broker. They feed the collection and evaluation
seams fabricated inputs that describe a sim broken in a specific way, and
assert the harness WITHHOLDS the pass. Every case here was written red against
the behaviour it replaces.

    cd tools && python3 -m unittest test_acceptance -v
"""

import json
import unittest

import acceptance
from acceptance import ACK_TOPIC, TELEMETRY_TOPIC


def ack(level="sound", ok=True, detail="played chirp 1000ms"):
    return json.dumps({"ts": "2026-08-24T00:00:00Z", "level": level, "ok": ok, "detail": detail})


class FakeMqtt:
    """Stands in for MiniMqtt. Each call to messages() drains one queued batch,
    so a test can say what arrives during the drain window and what arrives
    after the fire."""

    def __init__(self, *batches):
        self.batches = list(batches)
        self.log = []

    def subscribe(self, topic, qos=1):
        self.log.append(("subscribe", topic))

    def publish(self, topic, payload, qos=1):
        self.log.append(("publish", topic))

    def messages(self, timeout):
        self.log.append(("messages", timeout))
        batch = self.batches.pop(0) if self.batches else []
        for item in batch:
            yield item

    def close(self):
        self.log.append(("close", None))


class AckCountTest(unittest.TestCase):
    def test_collect_acks_reads_the_whole_window(self):
        client = FakeMqtt([(ACK_TOPIC, ack()), (ACK_TOPIC, ack())])
        self.assertEqual(len(acceptance.collect_acks(client, 6)), 2)

    def test_collect_acks_ignores_other_topics(self):
        client = FakeMqtt([(TELEMETRY_TOPIC, "{}"), (ACK_TOPIC, ack())])
        self.assertEqual(acceptance.collect_acks(client, 6), [ack()])

    def test_one_good_ack_passes(self):
        ok, detail = acceptance.evaluate_ack([ack()], "sound", 6)
        self.assertTrue(ok, detail)

    def test_two_acks_for_one_fire_fail_and_report_the_count(self):
        ok, detail = acceptance.evaluate_ack([ack(), ack()], "sound", 6)
        self.assertFalse(ok)
        self.assertIn("2 acks, expected 1", detail)

    def test_no_ack_fails(self):
        ok, detail = acceptance.evaluate_ack([], "sound", 6)
        self.assertFalse(ok)
        self.assertIn("no ack", detail)

    def test_wrong_level_fails(self):
        ok, _ = acceptance.evaluate_ack([ack(level="air")], "sound", 6)
        self.assertFalse(ok)

    def test_malformed_ack_is_a_shape_failure_not_a_crash(self):
        for payload in ("not json", "[]", "3"):
            with self.subTest(payload=payload):
                ok, detail = acceptance.evaluate_ack([payload], "sound", 6)
                self.assertFalse(ok)
                self.assertIn("unreadable", detail)


def jpeg(seed):
    """A byte-distinct fake JPEG. Pixel-identical renders encode to identical
    bytes, so distinct bytes is the same signal a real frame gives."""
    return acceptance.JPEG_SOI + f"frame-{seed}".encode() + acceptance.JPEG_EOI


def sample_of(frames, interval=0.1, lead_in=0.0):
    """A StreamSample as the collector would have built it: the clock starts at
    lead_in seconds before the first frame is extracted."""
    sample = acceptance.StreamSample(started=0.0)
    for i, frame in enumerate(frames):
        sample.add(frame, lead_in + i * interval)
    sample.finished = lead_in + (len(frames) - 1) * interval
    return sample


class FrozenStreamTest(unittest.TestCase):
    def test_moving_frames_pass(self):
        ok, detail = acceptance.evaluate_stream(sample_of([jpeg(i) for i in range(5)]), 5)
        self.assertTrue(ok, detail)

    def test_identical_frames_fail_even_at_a_healthy_rate(self):
        # A paused sim keeps encoding: the frame server runs on unscaled time,
        # so delivery rate alone cannot tell a live sim from a frozen one.
        ok, detail = acceptance.evaluate_stream(sample_of([jpeg(0)] * 5), 5)
        self.assertFalse(ok)
        self.assertIn("identical", detail)

    def test_one_changed_frame_is_enough(self):
        frames = [jpeg(0), jpeg(0), jpeg(0), jpeg(0), jpeg(1)]
        ok, detail = acceptance.evaluate_stream(sample_of(frames), 5)
        self.assertTrue(ok, detail)


class Clock:
    def __init__(self):
        self.now = 0.0

    def __call__(self):
        return self.now


class FakeStream:
    """An MJPEG body that hands out one frame per read, `interval` seconds of
    wall clock apart. The first read returns only part of the first frame:
    joining a live stream mid-frame is the normal case, and the wait for the
    first complete JPEG must not land inside the measured window."""

    def __init__(self, clock, count, interval=0.1):
        first = jpeg(0)
        self.chunks = [first[:3], first[3:]]
        self.chunks += [jpeg(i) for i in range(1, count)]
        self.clock = clock
        self.interval = interval

    def read(self, n):
        self.clock.now += self.interval
        return self.chunks.pop(0) if self.chunks else b""


class FpsEstimatorTest(unittest.TestCase):
    def collect(self, count, interval=0.1):
        clock = Clock()
        stream = FakeStream(clock, count, interval)
        return acceptance.collect_stream_sample(stream, count, timeout=60, clock=clock)

    def test_the_wait_for_the_first_frame_is_not_measured(self):
        # 30 frames 0.1s apart is exactly 10 fps. Timing from before the first
        # read charges the harness for the partial frame it joined mid-way and
        # biases the estimate low.
        sample = self.collect(30)
        self.assertEqual(sample.frames, 30)
        self.assertAlmostEqual(acceptance.estimate_fps(sample), 10.0, places=9)

    def test_the_estimate_is_unbiased_at_any_sample_length(self):
        for count in (5, 10, 30):
            with self.subTest(count=count):
                self.assertAlmostEqual(acceptance.estimate_fps(self.collect(count)), 10.0, places=9)

    def test_a_genuinely_slow_stream_still_reads_slow(self):
        self.assertAlmostEqual(acceptance.estimate_fps(self.collect(30, interval=0.2)), 5.0, places=9)


def sample_at_fps(fps):
    """Frames spanning exactly one second, so the band edges are testable
    without float slop."""
    count = int(fps) + 1
    sample = acceptance.StreamSample(started=0.0)
    for i in range(count):
        sample.add(jpeg(i), i * 1.0 / (count - 1))
    sample.finished = 1.0
    return sample


class FpsGateTest(unittest.TestCase):
    def evaluate(self, fps):
        sample = sample_at_fps(fps)
        return acceptance.evaluate_stream(sample, sample.frames)

    def test_the_contract_band_is_the_gate(self):
        for fps in (acceptance.FPS_MIN, 12.0, acceptance.FPS_MAX):
            with self.subTest(fps=fps):
                ok, detail = self.evaluate(fps)
                self.assertTrue(ok, detail)
        for fps in (9.0, 16.0):
            with self.subTest(fps=fps):
                ok, _ = self.evaluate(fps)
                self.assertFalse(ok)

    def test_the_printed_band_comes_from_the_gate_constants(self):
        _, detail = self.evaluate(12.0)
        self.assertIn(f"contract {acceptance.FPS_MIN:g}-{acceptance.FPS_MAX:g}", detail)
        self.assertEqual((acceptance.FPS_MIN, acceptance.FPS_MAX), (10.0, 15.0))



def beat(ts="2026-08-24T00:00:00Z", source="sim", status="ok"):
    return json.dumps({"ts": ts, "source": source, "status": status})


class TelemetryShapeTest(unittest.TestCase):
    def evaluate(self, payloads):
        stamps = [i * 10.0 for i in range(len(payloads))]
        return acceptance.evaluate_telemetry(stamps, payloads, 25)

    def test_all_good_beats_pass(self):
        ok, detail = self.evaluate([beat()] * 3)
        self.assertTrue(ok, detail)

    def test_one_good_beat_does_not_excuse_four_bad_ones(self):
        ok, detail = self.evaluate([beat(status="degraded")] * 4 + [beat()])
        self.assertFalse(ok)
        self.assertIn("1/5 payloads valid", detail)

    def test_a_bad_beat_anywhere_fails(self):
        ok, detail = self.evaluate([beat(), beat(source="sensor"), beat()])
        self.assertFalse(ok)
        self.assertIn("2/3 payloads valid", detail)

    def test_json_that_is_not_an_object_is_a_shape_failure_not_a_crash(self):
        for payload in ("not json", "[]", "3", '{"ts": 7, "source": "sim", "status": "ok"}'):
            with self.subTest(payload=payload):
                ok, detail = self.evaluate([beat(), payload])
                self.assertFalse(ok)
                self.assertIn("1/2 payloads valid", detail)

    def test_a_bad_gap_fails_even_with_good_payloads(self):
        ok, _ = acceptance.evaluate_telemetry([0.0, 30.0], [beat(), beat()], 25)
        self.assertFalse(ok)


class FireDrainTest(unittest.TestCase):
    def test_a_stale_ack_is_drained_before_the_fire(self):
        # An ack already on the topic when the harness subscribes must not be
        # counted against the fire this run publishes.
        client = FakeMqtt([(ACK_TOPIC, ack())], [(ACK_TOPIC, ack())])
        result = acceptance.Result(acceptance.CRITERIA)
        acceptance.check_fire(result, "sound", "C5", connect=lambda **kw: client)
        self.assertTrue(result.rows[0][2], result.rows[0][3])
        order = [entry[0] for entry in client.log]
        self.assertLess(order.index("messages"), order.index("publish"))

    def test_a_duplicate_ack_after_the_fire_fails(self):
        client = FakeMqtt([], [(ACK_TOPIC, ack()), (ACK_TOPIC, ack())])
        result = acceptance.Result(acceptance.CRITERIA)
        acceptance.check_fire(result, "sound", "C5", connect=lambda **kw: client)
        self.assertFalse(result.rows[0][2])
        self.assertIn("2 acks, expected 1", result.rows[0][3])



class RosterTest(unittest.TestCase):
    def result(self, rows=(), skips=()):
        r = acceptance.Result(acceptance.CRITERIA)
        for code, ok in rows:
            r.add(code, "check", ok, "detail")
        for code, why in skips:
            r.skip(code, why)
        return r

    def test_a_criterion_whose_check_never_ran_is_not_a_pass(self):
        # Against a dead sim the connection throws before some rows are ever
        # added. The old summary moved its own denominator and all([]) is True.
        r = self.result([("C2", True)])
        self.assertEqual(r.verdict("C2"), "PASS")
        for code in ("C4", "C5", "C6"):
            with self.subTest(code=code):
                self.assertEqual(r.verdict(code), "FAIL")

    def test_a_run_where_nothing_ran_at_all_fails(self):
        self.assertNotEqual(acceptance.summarize(self.result()), 0)

    def test_every_row_of_a_criterion_has_to_pass(self):
        r = self.result([("C2", True), ("C2", False)])
        self.assertEqual(r.verdict("C2"), "FAIL")

    def test_a_skipped_criterion_reads_as_skip_and_is_not_counted_as_covered(self):
        r = self.result([("C2", True), ("C5", True), ("C6", True)],
                        [("C4", "--skip-telemetry")])
        self.assertEqual(r.verdict("C4"), "SKIP")
        self.assertEqual(acceptance.summarize(r), 0)

    def test_the_roster_is_criteria_not_rows(self):
        codes = [code for code, _ in acceptance.CRITERIA]
        self.assertEqual(codes, ["C2", "C4", "C5", "C6"])

    def test_the_banner_claims_only_the_ack_half_of_5_and_6(self):
        labels = dict(acceptance.CRITERIA)
        for code in ("C5", "C6"):
            with self.subTest(code=code):
                self.assertIn("ack only", labels[code])
        line = acceptance.coverage_line()
        for code, label in acceptance.CRITERIA:
            self.assertIn(code, line)
            self.assertIn(label, line)


if __name__ == "__main__":
    unittest.main()
