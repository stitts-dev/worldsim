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


class FireDrainTest(unittest.TestCase):
    def test_a_stale_ack_is_drained_before_the_fire(self):
        # An ack already on the topic when the harness subscribes must not be
        # counted against the fire this run publishes.
        client = FakeMqtt([(ACK_TOPIC, ack())], [(ACK_TOPIC, ack())])
        result = acceptance.Result()
        acceptance.check_fire(result, "sound", "C5", connect=lambda **kw: client)
        self.assertTrue(result.rows[0][2], result.rows[0][3])
        order = [entry[0] for entry in client.log]
        self.assertLess(order.index("messages"), order.index("publish"))

    def test_a_duplicate_ack_after_the_fire_fails(self):
        client = FakeMqtt([], [(ACK_TOPIC, ack()), (ACK_TOPIC, ack())])
        result = acceptance.Result()
        acceptance.check_fire(result, "sound", "C5", connect=lambda **kw: client)
        self.assertFalse(result.rows[0][2])
        self.assertIn("2 acks, expected 1", result.rows[0][3])


if __name__ == "__main__":
    unittest.main()
