"""Hardware-free checks of the CLI on its own (no daemon). Run: python3 -m unittest discover -s tests -v"""
import contextlib
import importlib.machinery
import importlib.util
import io
import json
import os
from pathlib import Path
import sqlite3
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parent.parent
CLI = ROOT / "cli" / "felkod"
loader = importlib.machinery.SourceFileLoader("felkod_cli", str(CLI))
spec = importlib.util.spec_from_loader(loader.name, loader)
cli = importlib.util.module_from_spec(spec)
loader.exec_module(cli)


class FakeProcess:
    """A felkodd child that has already exited, as one that lost the start race does."""
    pid = 4321

    def poll(self):
        return 1


class CliTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="felkod-tests-")
        # URI syntax in the data directory must stay part of the path
        self.root = Path(self.temp.name) / "data?#%3F"
        self.env = patch.dict(os.environ, {"XDG_DATA_HOME": str(self.root),
                                           "FELKOD_SOCKET": str(Path(self.temp.name) / "missing.sock")})
        self.env.start()
        cli.WARNINGS.clear()
        self.addCleanup(self.temp.cleanup)
        self.addCleanup(self.env.stop)

    def run_cli(self, *args, env=None):
        return subprocess.run([sys.executable, str(CLI), *args], capture_output=True, text=True, timeout=30,
                              env={**os.environ, **(env or {})})

    def session(self):
        directory = self.root / "felkod" / "sessions" / "test"
        directory.mkdir(parents=True)
        with contextlib.closing(sqlite3.connect(directory / "session.sqlite")) as db, db:
            db.execute("CREATE TABLE samples (id INTEGER, job TEXT)")
            db.execute("INSERT INTO samples VALUES (1, 'STATUS_TEST')")

    def query(self, sql, *options):
        return cli.cmd_session(cli.build_parser().parse_args(["session", "query", "test", sql, *options]))

    def test_socket_path_without_a_passwd_entry(self):
        # e.g. a container run with --user 1234: every command must still work
        environment = {key: value for key, value in os.environ.items() if key not in ("FELKOD_SOCKET", "XDG_RUNTIME_DIR")}
        environment.update(USER="someone", TMPDIR=self.temp.name)
        with patch.dict(os.environ, environment, clear=True), patch.object(cli.pwd, "getpwuid", side_effect=KeyError(1234)):
            self.assertEqual(cli.socket_path(), Path(self.temp.name) / "felkod-someone.sock")
            with patch.object(sys, "argv", ["felkod", "status"]), contextlib.redirect_stdout(io.StringIO()) as output:
                self.assertEqual(cli.main(), 0)
        self.assertFalse(json.loads(output.getvalue())["data"]["daemon"]["running"])

    def test_start_race_loser_uses_the_winner(self):
        # Our felkodd exited (lost the race); the winner listens a moment later.
        running = iter([False, False, True])
        with patch.object(cli, "DAEMON", Path(sys.executable)), patch.object(cli.subprocess, "Popen", return_value=FakeProcess()), \
                patch.object(cli, "daemon_running", lambda: next(running)), patch.object(cli.time, "sleep"):
            self.assertEqual(cli.start_daemon(), FakeProcess.pid)

    def test_daemon_start_reports_a_felkodd_that_another_call_started(self):
        args = cli.build_parser().parse_args(["daemon", "start", "--port", "/dev/other"])
        status = {"daemon": {"pid": 1111}, "port": {"path": "/dev/felkod-kline"}}
        with patch.object(cli, "start_daemon", return_value=2222), patch.object(cli, "data", return_value=status):
            result = cli.cmd_daemon(args)
        self.assertEqual((result["started"], result["already_running"], result["port"]), (False, True, "/dev/felkod-kline"))
        self.assertEqual(len(cli.WARNINGS), 1)

    def test_start_fails_soon_when_felkodd_exits_and_none_comes_up(self):
        with patch.object(cli, "DAEMON", Path(sys.executable)), patch.object(cli.subprocess, "Popen", return_value=FakeProcess()), \
                patch.object(cli, "daemon_running", return_value=False), self.assertRaises(cli.Failure) as error:
            started = cli.time.monotonic()
            cli.start_daemon()
        self.assertEqual(error.exception.code, "daemon_start_failed")
        self.assertLess(cli.time.monotonic() - started, 5)

    def test_one_shot_read_forwards_native_options(self):
        args = cli.build_parser().parse_args(["read", "STATUS_TEST", "--native", "--gap-ms", "42"])
        with patch.object(cli, "data", return_value={}) as data:
            cli.cmd_read(args)
        data.assert_called_once_with("job.run", {"job": "STATUS_TEST", "native": True, "gap_ms": 42})

    def test_daemon_start_forwards_the_ecu_path(self):
        args = cli.build_parser().parse_args(["daemon", "start", "--ecu-path", "/custom/ecu"])
        status = {"daemon": {"pid": 1111}, "port": {"path": "/dev/felkod-kline"}}
        with patch.object(cli, "start_daemon", return_value=1111) as start, patch.object(cli, "data", return_value=status):
            self.assertTrue(cli.cmd_daemon(args)["started"])
        start.assert_called_once_with(["--ecu-path", "/custom/ecu"])

    def test_invalid_numbers_are_usage_errors(self):
        for args in (["read", "INFO", "--duration", "0"], ["read", "INFO", "--duration", "nan"],
                     ["log", "start", "--duration", "inf"], ["log", "start", "--duration", "-1"],
                     ["log", "start", "--slow-every", "0"], ["daemon", "start", "--job-timeout", "0"],
                     ["session", "query", "test", "SELECT 1", "--limit", "0"]):
            with self.subTest(args=args):
                result = self.run_cli(*args)
                self.assertEqual(result.returncode, 2, result.stdout + result.stderr)
                self.assertNotIn("Traceback", result.stderr)

    def test_session_query_reads_with_uri_characters_in_the_path(self):
        self.session()
        self.assertEqual(self.query("SELECT * FROM samples")["rows"], [[1, "STATUS_TEST"]])
        self.assertEqual(self.query("WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM n WHERE x < 3) "
                                    "SELECT sum(x) FROM n")["rows"], [[6]])
        self.assertEqual(self.query("SELECT 1 UNION ALL SELECT 2", "--limit", "1")["rows"], [[1]])
        self.assertEqual(len(cli.WARNINGS), 1)

    def test_session_query_cannot_write_or_create_files(self):
        self.session()
        created = Path(self.temp.name) / "created.sqlite"
        for sql in (f"ATTACH DATABASE '{created}' AS other", f"VACUUM INTO '{created}'", "DELETE FROM samples",
                    "PRAGMA journal_mode = DELETE"):
            with self.subTest(sql=sql), self.assertRaises(sqlite3.DatabaseError):
                self.query(sql)
            self.assertFalse(created.exists())
        self.assertEqual(self.query("SELECT COUNT(*) FROM samples")["rows"], [[1]])

    def test_io_error_is_a_json_failure(self):
        self.root.write_text("a file where the data directory should be")
        result = self.run_cli("ident", env={"FELKOD_DAEMON": sys.executable})
        self.assertEqual(result.returncode, 1, result.stderr)
        self.assertEqual(json.loads(result.stdout)["error"]["code"], "io")
        self.assertNotIn("Traceback", result.stderr)


if __name__ == "__main__":
    unittest.main()
