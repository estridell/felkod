"""Daemon startup failures, without a cable or BMW files."""
from pathlib import Path
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parent.parent
DAEMON = ROOT / "daemon/bin/Release/net10.0/felkodd"
# Upstream EdiabasLib's test SGBDs; cmd_ident.grp is a group file
ECU_PATH = ROOT / "vendor/ediabaslib/EdiabasLib/Test/Ecu"


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

    def test_group_file_is_refused_by_name(self):
        # A group file needs the car to pick its variant; it must not fail with a bare EDIABAS_IFH code.
        with tempfile.TemporaryDirectory(prefix="felkod-startup-") as temp:
            result = subprocess.run([str(DAEMON), "--sgbd", "cmd_ident", "--ecu-path", str(ECU_PATH),
                                     "--data-dir", temp, "--socket", str(Path(temp) / "felkod.sock")],
                                    capture_output=True, text=True, timeout=10)
        self.assertEqual(result.returncode, 1, result.stderr)
        self.assertIn("cmd_ident is a group file", result.stderr)
        self.assertNotIn("Unhandled exception", result.stderr)


if __name__ == "__main__":
    unittest.main()
