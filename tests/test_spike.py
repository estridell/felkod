"""Phase 0 spike argument and startup failures, without a cable or BMW files."""
import json
from pathlib import Path
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parent.parent
SPIKE = ROOT / "spike/bin/Release/net10.0/felkod-spike"
DAEMON = ROOT / "daemon/bin/Release/net10.0/felkodd"


@unittest.skipUnless(SPIKE.is_file(), "build the Release spike first")
class SpikeTests(unittest.TestCase):
    def test_invalid_numbers_fail_before_initializing_sgbd(self):
        with tempfile.TemporaryDirectory(prefix="felkod-spike-") as temp:
            trace = Path(temp) / "traces"
            for flag, values in (("--job-timeout", ("0", "-1", "NaN", "Infinity", "abc")),
                                 ("--duration", ("-1", "NaN", "Infinity", "abc")),
                                 ("--slow-every", ("0", "-1", "abc", "1e100"))):
                for value in values:
                    with self.subTest(flag=flag, value=value):
                        result = subprocess.run([str(SPIKE), flag, value, "--trace-dir", str(trace), "log"],
                                                capture_output=True, text=True, timeout=10)
                        self.assertEqual(result.returncode, 2, result.stderr)
                        self.assertIn(flag, result.stderr)
                        self.assertNotIn("Unhandled exception", result.stderr)
                        self.assertFalse(trace.exists())

    def test_missing_sgbd_is_a_json_failure(self):
        with tempfile.TemporaryDirectory(prefix="felkod-spike-") as temp:
            result = subprocess.run([str(SPIKE), "--ecu-path", temp, "--sgbd", "missing_test_sgbd",
                                     "--trace-dir", str(Path(temp) / "traces"), "jobs"],
                                    capture_output=True, text=True, timeout=10)
            self.assertEqual(result.returncode, 1, result.stderr)
            self.assertFalse(json.loads(result.stdout)["ok"])
            self.assertNotIn("Unhandled exception", result.stderr)


@unittest.skipUnless(DAEMON.is_file(), "build the Release daemon first")
class StartupTests(unittest.TestCase):
    def test_daemon_data_directory_failure_is_reported_without_stack_trace(self):
        with tempfile.TemporaryDirectory(prefix="felkod-startup-") as temp:
            occupied = Path(temp) / "occupied"
            occupied.write_text("preserve me")
            result = subprocess.run([str(DAEMON), "--data-dir", str(occupied)],
                                    capture_output=True, text=True, timeout=10)
            self.assertEqual(result.returncode, 1, result.stderr)
            self.assertTrue(result.stderr.startswith("felkodd:"), result.stderr)
            self.assertNotIn("Unhandled exception", result.stderr)
            self.assertEqual(occupied.read_text(), "preserve me")


if __name__ == "__main__":
    unittest.main()
