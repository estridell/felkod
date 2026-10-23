"""Real CLI/daemon checks: upstream EdiabasLib's test SGBDs and a pseudo-terminal as the ECU, never a car.
Run: python3 -m unittest discover -s tests -v (after dotnet build -c Release daemon)"""
from datetime import datetime
import json
import os
from pathlib import Path
import pty
import select
import socket
import sqlite3
import subprocess
import sys
import tempfile
import threading
import time
import tty
import unittest

ROOT = Path(__file__).resolve().parent.parent
CLI = ROOT / "cli" / "felkod"
DAEMON = ROOT / "daemon" / "bin" / "Release" / "net10.0" / "felkodd"
ECU_PATH = ROOT / "vendor" / "ediabaslib" / "EdiabasLib" / "Test" / "Ecu"
# A DS2 DME SGBD from upstream's tests; STATUS_UBATT sends one telegram.
SGBD = "dm338ds1_interbyte"
UBATT_REQUEST = bytes.fromhex("120906000000D802C7")


def telegram(*data):
    body = bytes([data[0], len(data) + 2, *data[1:]])
    checksum = 0
    for byte in body:
        checksum ^= byte
    return body + bytes([checksum])


class FakeEcu(threading.Thread):
    """Answers DS2 requests on a pty's master side: echoes each one (as the cable does), then writes
    what reply(index, request) returns, a list of (delay_s, bytes). Records when each request arrived."""

    def __init__(self, master, reply):
        super().__init__(daemon=True)
        self.master = master
        self.reply = reply
        self.requests = []
        self.received_at = []
        self.errors = []
        self.stopping = False

    def read(self, count):
        data = b""
        while len(data) < count:
            if self.stopping:
                return None
            if select.select([self.master], [], [], 0.05)[0]:
                data += os.read(self.master, count - len(data))
        return data

    def run(self):
        try:
            while (head := self.read(2)) is not None:
                rest = self.read(head[1] - 2)
                if rest is None:
                    return
                request = head + rest
                self.received_at.append(time.time())
                self.requests.append(request)
                os.write(self.master, request)
                for delay, data in self.reply(len(self.requests) - 1, request):
                    time.sleep(delay)
                    os.write(self.master, data)
        except Exception as error:
            self.errors.append(error)

    def stop(self):
        self.stopping = True
        self.join(timeout=5)


@unittest.skipUnless(DAEMON.is_file(), "build the Release daemon first")
class DaemonTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="felkod-daemon-")
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.socket = str(self.root / "felkod.sock")
        self.master, slave = pty.openpty()
        tty.setraw(slave)
        self.port = os.ttyname(slave)
        self.addCleanup(os.close, self.master)
        self.addCleanup(os.close, slave)
        self.env = {**os.environ, "FELKOD_SOCKET": self.socket, "XDG_DATA_HOME": str(self.root), "FELKOD_PORT": self.port}
        self.addCleanup(self.cli_quiet, "daemon", "stop")

    def cli(self, *args):
        result = subprocess.run([sys.executable, str(CLI), *args], env=self.env, capture_output=True, text=True, timeout=60)
        self.assertNotIn("Traceback", result.stderr)
        self.assertEqual(result.returncode, 0, result.stdout)
        return json.loads(result.stdout)["data"]

    def cli_quiet(self, *args):
        subprocess.run([sys.executable, str(CLI), *args], env=self.env, capture_output=True, timeout=60)

    def start(self):
        return self.cli("daemon", "start", "--sgbd", SGBD, "--ecu-path", str(ECU_PATH))

    def ecu(self, reply):
        ecu = FakeEcu(self.master, reply)
        ecu.start()
        self.addCleanup(ecu.stop)
        return ecu

    def rpc(self, method, params=None):
        with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as connection:
            connection.settimeout(30)
            connection.connect(self.socket)
            connection.sendall((json.dumps({"id": 1, "method": method, "params": params or {}}) + "\n").encode())
            with connection.makefile("r") as reader:
                return json.loads(reader.readline())

    def test_concurrent_starts_all_succeed(self):
        # Every call that auto-starts felkodd at the same time ends up with the one that won.
        starts = [subprocess.Popen([sys.executable, str(CLI), "daemon", "start", "--sgbd", SGBD, "--ecu-path", str(ECU_PATH)],
                                   env=self.env, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True) for _ in range(4)]
        results = []
        for start in starts:
            stdout, stderr = start.communicate(timeout=60)
            self.assertNotIn("Traceback", stderr)
            self.assertEqual(start.returncode, 0, stdout)
            results.append(json.loads(stdout)["data"])
        self.assertEqual(sum(result["started"] for result in results), 1, results)
        self.assertTrue(self.cli("status")["daemon"]["running"])

    def test_second_daemon_leaves_the_running_one(self):
        self.start()
        pid = self.cli("status")["daemon"]["pid"]
        second = subprocess.run([str(DAEMON), "--socket", self.socket, "--port", self.port, "--sgbd", SGBD,
                                 "--ecu-path", str(ECU_PATH), "--data-dir", str(self.root / "second")],
                                capture_output=True, text=True, timeout=60)
        self.assertEqual(second.returncode, 1, second.stderr)
        self.assertIn("another felkodd", second.stderr)
        self.assertEqual(self.cli("status")["daemon"]["pid"], pid)

    def test_restart_after_a_crash_replaces_the_stale_socket(self):
        self.start()
        os.kill(self.cli("status")["daemon"]["pid"], 9)
        time.sleep(0.2)
        self.assertTrue(Path(self.socket).exists())
        self.assertTrue(self.start()["started"])

    def test_one_shot_native_read_uses_native_transport(self):
        ecu = self.ecu(lambda index, request: [(0.01, telegram(0x12, 0xA0, 0x12, 0x34))])
        self.start()
        results = self.cli("read", "STATUS_UBATT", "--native", "--gap-ms", "0")["results"]
        self.assertEqual(results[0]["job"], "STATUS_UBATT")
        # EdiabasLib would first run the SGBD's init job; native sends only the job's own telegram
        self.assertEqual(ecu.requests, [UBATT_REQUEST])

    def test_first_byte_ms_is_the_first_reply_byte(self):
        reply = telegram(0x12, 0xA0, 0x12, 0x34)
        # The address byte follows the echo at once, the length byte 17 ms later (felkodd allows about
        # 21 ms between reply bytes); those 17 ms must not count as the ECU's latency.
        self.ecu(lambda index, request: [(0, reply[:1]), (0.017, reply[1:])])
        self.start()
        response = self.rpc("ds2.raw", {"request": UBATT_REQUEST.hex(), "gap_ms": 0})
        self.assertTrue(response["ok"], response)
        self.assertLess(response["data"]["first_byte_ms"], 8)

    def test_native_sample_is_stamped_when_its_final_request_went_out(self):
        good = telegram(0x12, 0xA0, 0x12, 0x34)
        # The first reply is garbled (wrong address) and followed by 0.3 s of noise, so the resend
        # goes out well after the sample started.
        def reply(index, request):
            return [(0.01, b"\x55\x05")] + [(0.01, b"\x55")] * 30 if index == 0 else [(0.01, good)]
        ecu = self.ecu(reply)
        self.start()
        session = self.cli("log", "start", "--jobs", "STATUS_UBATT", "--native", "--gap-ms", "200")["session_id"]
        deadline = time.monotonic() + 10
        while len(ecu.requests) < 3 and time.monotonic() < deadline:
            time.sleep(0.05)
        self.cli("log", "stop")
        db = sqlite3.connect(self.root / "felkod" / "sessions" / session / "session.sqlite")
        (t, elapsed, retries), second = db.execute(
            "SELECT s.t, s.elapsed_s, r.retries FROM samples s JOIN raw r ON r.sample_id = s.id ORDER BY s.id LIMIT 2").fetchall()
        db.close()
        self.assertEqual(retries, 1)
        sent = datetime.fromisoformat(t).timestamp()
        self.assertAlmostEqual(sent, ecu.received_at[1], delta=0.1)
        # elapsed_s follows the same moments: the next sample was sent one gap plus one exchange later
        self.assertAlmostEqual(second[1] - elapsed, ecu.received_at[2] - ecu.received_at[1], delta=0.1)


if __name__ == "__main__":
    unittest.main()
