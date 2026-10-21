"""Hardware-free CLI regressions. Run: python3 -m unittest discover -s tests -v"""
import importlib.machinery
import importlib.util
import json
import os
from pathlib import Path
import socket
import sqlite3
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


class CliTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="felkod-tests-")
        self.root = Path(self.temp.name) / "data?#fragment"
        self.env = patch.dict(os.environ, {"XDG_DATA_HOME": str(self.root),
                                         "FELKOD_SOCKET": str(Path(self.temp.name) / "missing.sock")})
        self.env.start()
        cli.WARNINGS.clear()
        self.addCleanup(self.env.stop)
        self.addCleanup(self.temp.cleanup)

    def database(self):
        directory = self.root / "felkod" / "sessions" / "test"
        directory.mkdir(parents=True)
        with sqlite3.connect(directory / "session.sqlite") as db:
            db.execute("CREATE TABLE samples (id INTEGER, job TEXT)")
            db.execute("INSERT INTO samples VALUES (1, 'STATUS_TEST')")
        return directory

    def test_query_handles_uri_characters_and_closes_connection(self):
        self.database()
        args = cli.build_parser().parse_args(["session", "query", "test", "SELECT * FROM samples"])
        self.assertEqual(cli.cmd_session(args)["rows"], [[1, "STATUS_TEST"]])

    def test_session_sql_cannot_write_other_files(self):
        self.database()
        destination = Path(self.temp.name) / "should-not-exist.sqlite"
        for sql in (f"ATTACH DATABASE '{destination}' AS writable",
                    f"VACUUM INTO '{destination}'", "DELETE FROM samples", "PRAGMA writable_schema=ON"):
            with self.subTest(sql=sql), self.assertRaises(sqlite3.DatabaseError):
                args = cli.build_parser().parse_args(["session", "query", "test", sql])
                cli.cmd_session(args)
            self.assertFalse(destination.exists())

    def test_recursive_cte_remains_available(self):
        self.database()
        args = cli.build_parser().parse_args(["session", "query", "test",
            "WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x+1 FROM n WHERE x<3) SELECT sum(x) FROM n"])
        self.assertEqual(cli.cmd_session(args)["rows"], [[6]])

    def test_session_id_cannot_escape_sessions(self):
        for value in ("..", ".", "../other", "/tmp/other"):
            with self.subTest(value=value), self.assertRaises(cli.Failure) as error:
                cli.session_db(value)
            self.assertEqual(error.exception.code, "bad_request")

    def test_query_limit_and_warning(self):
        self.database()
        args = cli.build_parser().parse_args(["session", "query", "test", "SELECT 1 UNION ALL SELECT 2", "--limit", "1"])
        self.assertEqual(cli.cmd_session(args)["rows"], [[1]])
        self.assertEqual(len(cli.WARNINGS), 1)

    def test_one_shot_read_forwards_native_options(self):
        args = cli.build_parser().parse_args(["read", "STATUS_TEST", "--native", "--gap-ms", "42"])
        with patch.object(cli, "data", return_value={}) as call:
            cli.cmd_read(args)
        call.assert_called_once_with("job.run", {"job": "STATUS_TEST", "native": True, "gap_ms": 42})

    def test_daemon_start_forwards_custom_ecu_directory(self):
        args = cli.build_parser().parse_args(["daemon", "start", "--ecu-path", "/custom/ecu"])
        with patch.object(cli, "start_daemon", return_value=True) as start:
            cli.cmd_daemon(args)
        start.assert_called_once_with(["--ecu-path", "/custom/ecu"])

    def test_invalid_numeric_options_fail_before_daemon_start(self):
        for arguments in (["read", "INFO", "--duration", "nan"], ["log", "start", "--duration", "inf"],
                          ["log", "start", "--slow-every", "0"], ["daemon", "start", "--job-timeout", "0"],
                          ["session", "query", "test", "SELECT 1", "--limit", "-1"]):
            result = subprocess.run([sys.executable, str(CLI), *arguments], capture_output=True, text=True)
            self.assertEqual(result.returncode, 2, result.stderr)
            self.assertNotIn("Traceback", result.stderr)

    def test_malformed_session_metadata_is_skipped(self):
        directory = self.database()
        (directory / "meta.json").write_text("[]")
        self.assertEqual(cli.cmd_sessions(None)["sessions"], [])

    def test_io_error_is_json(self):
        # A regular file cannot contain the daemon's log directory.
        self.root.write_text("not a directory")
        with patch.dict(os.environ, {"FELKOD_DAEMON": sys.executable}):
            result = subprocess.run([sys.executable, str(CLI), "ident"], capture_output=True, text=True)
        self.assertEqual(result.returncode, 1)
        self.assertEqual(json.loads(result.stdout)["error"]["code"], "io")
        self.assertNotIn("Traceback", result.stderr)

    def test_invalid_daemon_response_is_json_failure(self):
        path = str(Path(self.temp.name) / "fake.sock")
        for response in (b"not json\n", b"[]\n", b'{"ok": false}\n'):
            with self.subTest(response=response), socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as server:
                server.bind(path)
                server.listen(1)
                def reply():
                    connection, _ = server.accept()
                    with connection:
                        connection.recv(4096)
                        connection.sendall(response)
                thread = threading.Thread(target=reply)
                thread.start()
                try:
                    with patch.dict(os.environ, {"FELKOD_SOCKET": path}), self.assertRaises(cli.Failure) as error:
                        cli.call("status", autostart=False)
                    self.assertEqual(error.exception.code, "daemon_protocol")
                finally:
                    thread.join(timeout=3)
                    os.unlink(path)


if __name__ == "__main__":
    unittest.main()
