"""Daemon startup failures, without a cable or BMW files."""
from pathlib import Path
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parent.parent
DAEMON = ROOT / "daemon/bin/Release/net10.0/felkodd"


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
