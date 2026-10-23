"""DS2 framing and timing checks against a pseudo-terminal, never a car."""
import unittest

import test_daemon as fixture


@unittest.skipUnless(fixture.DAEMON.is_file(), "build the Release daemon first")
class TransportTests(unittest.TestCase):
    # Reuse the daemon/pty fixture without inheriting and rerunning its unrelated tests.
    setUp = fixture.DaemonTests.setUp
    cli = fixture.DaemonTests.cli
    cli_quiet = fixture.DaemonTests.cli_quiet
    start = fixture.DaemonTests.start
    ecu = fixture.DaemonTests.ecu
    rpc = fixture.DaemonTests.rpc

    def test_reply_timeout_applies_to_each_byte_gap(self):
        response = fixture.telegram(0x12, 0xA0, *range(60))
        self.ecu(lambda index, request: [(0, response[:3]), (0.05, response[3:])])
        self.start()
        result = self.rpc("ds2.raw", {"request": fixture.UBATT_REQUEST.hex(), "gap_ms": 0})
        self.assertFalse(result["ok"], result)
        self.assertEqual(result["error"]["code"], "link_corrupt")

    def test_reply_can_arrive_slowly_with_valid_byte_gaps(self):
        response = fixture.telegram(0x12, 0xA0, *range(12))
        self.ecu(lambda index, request: [(0, response[:2])] + [(0.01, bytes([b])) for b in response[2:]])
        self.start()
        result = self.rpc("ds2.raw", {"request": fixture.UBATT_REQUEST.hex(), "gap_ms": 0})
        self.assertTrue(result["ok"], result)
        self.assertEqual(result["data"]["response"], response.hex().upper())

    def test_native_read_retries_a_corrupt_reply_once(self):
        good = fixture.telegram(0x12, 0xA0, 0x12, 0x34)
        bad = good[:-1] + bytes([good[-1] ^ 1])
        ecu = self.ecu(lambda index, request: [(0, bad if index == 0 else good)])
        self.start()
        result = self.rpc("job.run", {"job": "STATUS_UBATT", "native": True, "gap_ms": 0})
        self.assertTrue(result["ok"], result)
        self.assertEqual(ecu.requests, [fixture.UBATT_REQUEST] * 2)

    def test_raw_exchange_does_not_retry_a_corrupt_reply(self):
        good = fixture.telegram(0x12, 0xA0, 0x12, 0x34)
        bad = good[:-1] + bytes([good[-1] ^ 1])
        ecu = self.ecu(lambda index, request: [(0, bad)])
        self.start()
        result = self.rpc("ds2.raw", {"request": fixture.UBATT_REQUEST.hex(), "gap_ms": 0})
        self.assertFalse(result["ok"], result)
        self.assertEqual(result["error"]["code"], "link_corrupt")
        self.assertEqual(ecu.requests, [fixture.UBATT_REQUEST])

    def test_native_read_rejects_an_ecu_error_status(self):
        self.ecu(lambda index, request: [(0, fixture.telegram(0x12, 0xA2))])
        self.start()
        result = self.rpc("job.run", {"job": "STATUS_UBATT", "native": True, "gap_ms": 0})
        self.assertFalse(result["ok"], result)
        self.assertEqual(result["error"]["code"], "ecu_rejected")

    def test_native_read_rejects_changed_length_after_a_good_reply(self):
        good = fixture.telegram(0x12, 0xA0, 0x12, 0x34)
        short = fixture.telegram(0x12, 0xA0)
        self.ecu(lambda index, request: [(0, good if index == 0 else short)])
        self.start()
        params = {"job": "STATUS_UBATT", "native": True, "gap_ms": 0}
        first = self.rpc("job.run", params)
        self.assertTrue(first["ok"], first)
        second = self.rpc("job.run", params)
        self.assertFalse(second["ok"], second)
        self.assertEqual(second["error"]["code"], "ecu_reply_length")

    def test_offline_decode_uses_the_length_learned_from_live_replies(self):
        good = fixture.telegram(0x12, 0xA0, 0x12, 0x34)
        self.ecu(lambda index, request: [(0, good)])
        self.start()
        live = self.rpc("job.run", {"job": "STATUS_UBATT", "native": True, "gap_ms": 0})
        self.assertTrue(live["ok"], live)
        decoded = self.rpc("ds2.decode", {"job": "STATUS_UBATT", "response": good.hex()})
        self.assertTrue(decoded["ok"], decoded)
        self.assertEqual(decoded["data"]["sets"], live["data"]["sets"])
        short = fixture.telegram(0x12, 0xA0)
        rejected = self.rpc("ds2.decode", {"job": "STATUS_UBATT", "response": short.hex()})
        self.assertFalse(rejected["ok"], rejected)
        self.assertEqual(rejected["error"]["code"], "ecu_reply_length")

    def test_offline_decode_does_not_establish_live_reply_lengths(self):
        good = fixture.telegram(0x12, 0xA0, 0x12, 0x34)
        self.ecu(lambda index, request: [(0, good)])
        self.start()
        # As with the first live reply, no expected length is available yet. Decoding an offline
        # frame must not make its length authoritative for later live samples.
        short = fixture.telegram(0x12, 0xA0)
        decoded = self.rpc("ds2.decode", {"job": "STATUS_UBATT", "response": short.hex()})
        self.assertTrue(decoded["ok"], decoded)
        live = self.rpc("job.run", {"job": "STATUS_UBATT", "native": True, "gap_ms": 0})
        self.assertTrue(live["ok"], live)


if __name__ == "__main__":
    unittest.main()
