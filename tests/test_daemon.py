"""Real CLI/daemon checks using upstream's test SGBD and a pseudo-terminal, never a car."""
import json
import os
from pathlib import Path
import pty
import select
import socket
import subprocess
import sys
import tempfile
import threading
import time
import tty
import unittest

ROOT = Path(__file__).resolve().parent.parent
DAEMON = ROOT / "daemon/bin/Release/net10.0/felkodd"


@unittest.skipUnless(DAEMON.is_file(), "build the Release daemon first")
class DaemonTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="felkod-daemon-")
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.path = str(self.root / "test.sock")
        self.master, self.slave = pty.openpty()
        self.addCleanup(os.close, self.master)
        self.addCleanup(os.close, self.slave)
        tty.setraw(self.slave)
        self.arguments = [str(DAEMON), "--socket", self.path, "--port", os.ttyname(self.slave),
                          "--ecu-path", str(ROOT / "vendor/ediabaslib/EdiabasLib/Test/Ecu"),
                          "--sgbd", "cmd_test1", "--data-dir", str(self.root / "felkod")]
        self.environment = {**os.environ, "XDG_RUNTIME_DIR": str(self.root)}
        self.log = open(self.root / "daemon.log", "wb")
        self.addCleanup(self.log.close)
        self.process = subprocess.Popen(self.arguments, stdout=self.log, stderr=self.log, env=self.environment)
        self.addCleanup(self.stop_process)
        self.wait_ready()

    def wait_ready(self):
        deadline = time.monotonic() + 10
        while time.monotonic() < deadline:
            if self.process.poll() is not None:
                self.fail((self.root / "daemon.log").read_text())
            if Path(self.path).exists():
                try:
                    if self.rpc({"method": "status"})["ok"]:
                        return
                except (OSError, ValueError):
                    pass
            time.sleep(0.02)
        self.fail("daemon did not start")

    def stop_process(self):
        if self.process.poll() is None:
            self.process.terminate()
            try:
                self.process.wait(timeout=10)
            except subprocess.TimeoutExpired:
                self.process.kill()
                self.process.wait(timeout=5)

    def rpc(self, request):
        with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as connection:
            connection.settimeout(5)
            connection.connect(self.path)
            connection.sendall((json.dumps(request) + "\n").encode())
            with connection.makefile("r") as reader:
                return json.loads(reader.readline())

    def cli(self, *args):
        environment = {**os.environ, "FELKOD_SOCKET": self.path, "XDG_DATA_HOME": str(self.root)}
        result = subprocess.run([sys.executable, str(ROOT / "cli/felkod"), *args], env=environment,
                                capture_output=True, text=True, timeout=15)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        return json.loads(result.stdout)["data"]

    def test_cli_metadata_and_real_session(self):
        self.assertTrue(self.cli("status")["daemon"]["running"])
        self.assertGreater(len(self.cli("run", "_JOBS")["sets"]), 0)
        started = self.cli("log", "start", "--jobs", "INFO", "--duration", "0.1")
        deadline = time.monotonic() + 5
        while self.cli("log", "status").get("session_id") == started["session_id"]:
            self.assertLess(time.monotonic(), deadline)
            time.sleep(0.02)
        session = self.cli("session", "show", started["session_id"])
        self.assertGreater(session["samples"], 0)
        self.assertIn("summary", session["meta"])
        query = self.cli("session", "query", started["session_id"], "SELECT COUNT(*) FROM samples")
        self.assertEqual(query["rows"][0][0], session["samples"])

    def test_bad_rpc_parameters_do_not_crash_daemon(self):
        for request in ([], {"method": 1}, {"method": "log.start", "params": {"jobs": [None]}},
                        {"method": "log.start", "params": {"jobs": "INFO"}}):
            response = self.rpc(request)
            self.assertFalse(response["ok"])
            self.assertEqual(response["error"]["code"], "bad_request")
        self.assertTrue(self.rpc({"method": "status"})["ok"])

    def test_second_daemon_does_not_unlink_live_socket(self):
        second = subprocess.run(self.arguments, capture_output=True, text=True, timeout=10, env=self.environment)
        self.assertEqual(second.returncode, 1, second.stderr)
        self.assertTrue(self.rpc({"method": "status"})["ok"])

    def test_restart_recovers_stale_socket(self):
        self.process.kill()
        self.process.wait(timeout=5)
        self.assertTrue(Path(self.path).exists())
        self.process = subprocess.Popen(self.arguments, stdout=self.log, stderr=self.log, env=self.environment)
        self.wait_ready()
        self.assertTrue(self.rpc({"method": "status"})["ok"])

    def test_startup_preserves_regular_files_and_symlinks(self):
        target = self.root / "important.txt"
        target.write_text("keep this data")
        link = self.root / "linked.sock"
        link.symlink_to(target)
        for path in (target, link):
            arguments = self.arguments.copy()
            arguments[arguments.index("--socket") + 1] = str(path)
            result = subprocess.run(arguments, capture_output=True, text=True, timeout=10, env=self.environment)
            self.assertEqual(result.returncode, 1, result.stderr)
            self.assertEqual(target.read_text(), "keep this data")
            self.assertTrue(link.is_symlink())

    def test_native_raw_exchange_and_first_byte_timing(self):
        errors = []
        def ecu():
            try:
                request = b""
                deadline = time.monotonic() + 5
                while len(request) < 4:
                    if time.monotonic() >= deadline:
                        raise TimeoutError("no synthetic request")
                    if select.select([self.master], [], [], 0.05)[0]:
                        request += os.read(self.master, 4 - len(request))
                if request != bytes.fromhex("12040016"):
                    raise ValueError(request.hex())
                os.write(self.master, request)  # cable echo
                time.sleep(0.02)
                os.write(self.master, bytes.fromhex("12"))
                time.sleep(0.01)  # length arrives later; it must not inflate first_byte_ms
                os.write(self.master, bytes.fromhex("04A0B6"))
            except Exception as error:
                errors.append(error)
        thread = threading.Thread(target=ecu)
        thread.start()
        try:
            response = self.rpc({"method": "ds2.raw", "params": {"request": "12040016", "allow_write": True}})
        finally:
            thread.join(timeout=6)
        self.assertFalse(thread.is_alive())
        self.assertEqual(errors, [])
        self.assertTrue(response["ok"], response)
        data = response["data"]
        self.assertEqual(data["response"], "1204A0B6")
        self.assertGreaterEqual(data["total_ms"] - data["echo_ms"] - data["first_byte_ms"], 7)


if __name__ == "__main__":
    unittest.main()
