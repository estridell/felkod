"""Hardware-free checks of how the CLI handles daemon responses. Run: python3 -m unittest discover -s tests -v"""
import importlib.machinery
import importlib.util
import json
import os
from pathlib import Path
import socket
import subprocess
import sys
import tempfile
import threading
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parent.parent
CLI = ROOT / "cli" / "felkod"
loader = importlib.machinery.SourceFileLoader("felkod_cli", str(CLI))
spec = importlib.util.spec_from_loader(loader.name, loader)
cli = importlib.util.module_from_spec(spec)
loader.exec_module(cli)

MALFORMED = (b"not json\n", b'{"ok": tr\n', b"\xff\xfe\n", b"[]\n", b"null\n", b"{}\n", b'{"ok": "yes", "data": {}}\n',
             b'{"ok": true}\n', b'{"ok": true, "data": {}, "warnings": null}\n',
             b'{"ok": true, "data": {}, "warnings": "link_lost"}\n', b'{"ok": false}\n',
             b'{"ok": true, "data": {}, "warnings": [null]}\n',
             b'{"ok": true, "data": {"value": NaN}}\n', b'{"ok": true, "data": {"value": Infinity}}\n',
             b'{"ok": true, "data": {"value": 1e999}}\n',
             b'{"ok": false, "error": "link"}\n', b'{"ok": false, "error": {"code": "link"}}\n',
             b'{"ok": false, "error": {"message": "EDIABAS_IFH_0009"}}\n')


class FakeDaemon:
    """A unix socket that answers every request line with one fixed response."""

    def __init__(self, path, response):
        self.path = path
        self.response = response

    def __enter__(self):
        self.server = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
        self.server.bind(self.path)
        self.server.listen(4)
        self.thread = threading.Thread(target=self.serve, daemon=True)
        self.thread.start()
        return self

    def serve(self):
        while True:
            try:
                connection, _ = self.server.accept()
            except OSError:
                return
            with connection:
                if connection.recv(65536):  # empty: a liveness probe that only connects
                    connection.sendall(self.response)

    def __exit__(self, *exc):
        self.server.shutdown(socket.SHUT_RDWR)
        self.server.close()
        self.thread.join(timeout=3)
        os.unlink(self.path)


class ResponseTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="felkod-tests-")
        self.path = str(Path(self.temp.name) / "fake.sock")
        self.env = patch.dict(os.environ, {"FELKOD_SOCKET": self.path, "XDG_DATA_HOME": self.temp.name})
        self.env.start()
        cli.WARNINGS.clear()
        self.addCleanup(self.temp.cleanup)
        self.addCleanup(self.env.stop)

    def test_malformed_responses_are_protocol_failures(self):
        for response in MALFORMED:
            with self.subTest(response=response):
                with FakeDaemon(self.path, response), self.assertRaises(cli.Failure) as error:
                    cli.data("status")
                self.assertEqual(error.exception.code, "daemon_protocol")

    def test_well_formed_responses_still_work(self):
        with FakeDaemon(self.path, b'{"id": 1, "ok": true, "data": {"a": 1}, "warnings": ["w"]}\n'):
            self.assertEqual(cli.data("status"), {"a": 1})
        self.assertEqual(cli.WARNINGS, ["w"])
        with FakeDaemon(self.path, b'{"id": 1, "ok": true, "data": null}\n'):
            self.assertIsNone(cli.data("status"))
        with FakeDaemon(self.path, b'{"id": 1, "ok": false, "error": {"code": "link", "message": "EDIABAS_IFH_0009"}}\n'), \
                self.assertRaises(cli.Failure) as error:
            cli.data("status")
        self.assertEqual((error.exception.code, error.exception.message), ("link", "EDIABAS_IFH_0009"))

    def test_reads_one_response_line(self):
        with FakeDaemon(self.path, b'{"id": 1, "ok": true, "data": {"a": 1}}\npartial next line'):
            self.assertEqual(cli.data("status"), {"a": 1})

    def test_timeout_is_for_the_whole_response(self):
        # Even a peer that keeps providing bytes must respect the call's overall deadline.
        with patch.object(cli.socket, "socket") as socket_factory, patch.object(cli, "CALL_TIMEOUT_S", 1), \
                patch.object(cli.time, "monotonic", side_effect=[0, 0.25, 0.5, 1.1]), \
                self.assertRaises(cli.Failure) as error:
            socket_factory.return_value.__enter__.return_value.recv.return_value = b'{"ok":'
            cli.data("status", autostart=False)
        self.assertEqual(error.exception.code, "daemon_timeout")

    def test_malformed_response_prints_json_failure(self):
        with FakeDaemon(self.path, b'{"ok": true}\n'):
            result = subprocess.run([sys.executable, str(CLI), "status"], capture_output=True, text=True, timeout=30)
        self.assertEqual(result.returncode, 1)
        self.assertEqual(json.loads(result.stdout)["error"]["code"], "daemon_protocol")
        self.assertNotIn("Traceback", result.stderr)


if __name__ == "__main__":
    unittest.main()
