# felkod

Agent-first diagnostics for the BMW E46 with a Siemens MS43 engine ECU (M54 engines), built on
[EdiabasLib](https://github.com/uholeschak/ediabaslib).

felkod puts INPA-style diagnostics behind one CLI that prints structured JSON, so AI agents (and people)
can identify the DME, read and clear faults, run any SGBD job, and log live data in the background into
SQLite sessions that survive stalls, ignition cycles and cable replugs. felkod reports what the ECU and
BMW's own job descriptions say; it does not interpret the data.

## Status

v0.1, developed against one car: a 2002 E46 320Ci (M54B22, MS43, software 430066).

- Tested on the car: identification, fault reading, EdiabasLib-based logging and link recovery across
  stalls and ignition cycles (engine running, via the Phase 0 spike in `spike/`); the felkod daemon and
  CLI with the engine off.
- Tested only against a simulator so far: the native DS2 transport (`--native`).
- MS43 over DS2 (K-line) only. Other ECUs may work wherever EdiabasLib and an SGBD do, but are untested.

## Not affiliated with BMW, no BMW files included

felkod is not affiliated with or endorsed by BMW. It contains no BMW software. To talk to the car it
needs the SGBD files (`.prg`, `.grp`) from your own EDIABAS/INPA installation, e.g. its `Ecu` folder; see
`vendor/README.md` for where felkod expects them.

Use at your own risk. felkod only reads by default; anything that changes the car needs an explicit flag
(see Safety). No flashing workflow is implemented.

## Requirements

- Linux (developed on Arch). Windows and macOS are untested.
- .NET 10 SDK, Python 3 (standard library only)
- A K+DCAN USB cable with an FTDI chip, switched to K-line mode (OBD pins 7 and 8 bridged for the E46)
- BMW SGBD files from an EDIABAS/INPA installation (see above)

## Setup

```
git clone --recurse-submodules https://github.com/estridell/felkod.git
cd felkod
# copy your EDIABAS Ecu folder to vendor/bmw/ediabas/ecu
dotnet build -c Release daemon
mkdir -p ~/.local/bin
ln -s "$PWD/cli/felkod" ~/.local/bin/felkod
```

Add `~/.local/bin` to your `PATH` if needed. For an existing checkout, run
`git submodule update --init --recursive` before building. To keep your SGBDs elsewhere, start with
`felkod daemon start --ecu-path /path/to/Ecu`; subsequent CLI calls use that running daemon.
Stop it before changing its port, SGBD or ECU path.

Linux serial access: install `spike/99-felkod.rules` into `/etc/udev/rules.d/` (1 ms FTDI latency timer,
`/dev/felkod-kline` symlink) and add your user to the group that owns the port (`uucp` on Arch,
`dialout` on Debian/Ubuntu). The rule keeps the distribution's serial group. Reload with
`sudo udevadm control --reload-rules`, replug the cable, and log out and back in after changing groups.
If you have several FTDI cables, narrow the rule by serial number to avoid an ambiguous symlink.

## Use

```
felkod status                    # daemon, cable and link state
felkod ident                     # DME identification
felkod faults                    # stored faults with their environment data
felkod jobs [JOB]                # the SGBD's jobs, or one job's arguments and results
felkod run STATUS_UBATT          # any read-only SGBD job
felkod log start --jobs STATUS_MESSWERTEBLOCK,STATUS_LMM_SPANNUNG --slow-jobs STATUS_LAMBDA_ADD_1,STATUS_LAMBDA_MUL_1
felkod log stop
felkod sessions
felkod session query <id> "SELECT t, name, value, unit FROM series WHERE name LIKE 'STAT_MOTORDREHZAHL%'"
```

Commands print one JSON object: `{"ok": true, "data": ..., "warnings": [...]}` or
`{"ok": false, "error": {"code": ..., "message": ...}}`; exit status 0 = ok, 1 = error, 2 = usage.
Help and usage errors are plain text.
The daemon (`felkodd`) starts on first use and owns the cable; a faults read while a log runs is
interleaved between log steps. `skill/SKILL.md` is the full CLI reference for agents, generated from
`--help`.

`--native` (log, read, bench) sends single-telegram jobs with felkodd's own DS2 transport and a
configurable gap between telegrams (`--gap-ms`, default BMW's 100 ms), then decodes the reply through the
SGBD, so values are identical to EdiabasLib's while the rate is set by the wire rather than EdiabasLib's
fixed 100 ms regeneration time. Raw telegrams are kept in the session's `raw` table and can be decoded
again with `felkod ds2 decode`.

Sessions live in `~/.local/share/felkod/sessions/<id>/` (`session.sqlite`, `meta.json`, raw telegram
trace `ifh.trc`); `felkod session show <id>` lists the tables.
`XDG_DATA_HOME` overrides the data root. Session queries accept `SELECT`/`WITH`, including SQLite
functions; writes, `ATTACH`, `VACUUM` and `PRAGMA` are refused. Use `--limit` to bound returned rows.
Native telegrams are in SQLite's `raw` table; `ifh.trc` contains exchanges made through EdiabasLib.
Sessions and traces may contain VINs and other vehicle identifiers; review them before sharing.

## Safety

- Jobs that are not read-only (`STEUERN_*`, `*_LOESCHEN`, `*_PROGRAMMIEREN`, ...) need `--allow-write`.
- `faults clear` needs `--confirm`, refuses below 11.5 V battery, and saves the faults to
  `~/.local/share/felkod/audit.jsonl` before clearing; every write is recorded there.
- `ds2 raw` sends only telegrams that a read-only SGBD job sends, unless `--allow-write`.
- Replies are only decoded when their DS2 status is OK by BMW's own status table and their length
  matches earlier good replies for the same single-telegram request. Length checking is learned:
  it cannot identify the first short reply, and multi-telegram jobs can legitimately vary in length.

Read-only classification follows MS43 job-name conventions (`STATUS_*`, `LESEN_*`, `*_LESEN`,
`IDENT`, `IDENT_AIF`, `INFO`, `ECU_CONFIG`) plus the built-in metadata jobs. It is not an analysis of
SGBD bytecode: use trusted, reviewed SGBDs, especially when experimenting with another ECU.
The daemon socket is accessible only to its owner, who can explicitly authorize writes.

## Development

From a checkout with submodules initialized:

```
dotnet build -c Release daemon
dotnet build -c Release spike
dotnet run --project tests -c Release
python3 -m unittest discover -s tests -v
python3 skill/generate.py
git diff --exit-code -- skill/SKILL.md
```

These checks run in CI on Linux with .NET 10 and Python 3.12. They use upstream EdiabasLib's test
SGBD, temporary session databases and a pseudo-terminal ECU; no BMW files, cable or car are required. They cover interpreter
isolation, write guards, session persistence, concurrent log starts, CLI options and read-only SQL.
They do not establish real-car transport behavior. Native DS2 still needs on-car validation before
being treated as production-ready. See [CONTRIBUTING.md](CONTRIBUTING.md) for contribution guidance.

## Layout

- `daemon/` — `felkodd` (C#, .NET 10): owns the K-line, runs SGBD jobs via EdiabasLib and native DS2,
  logging sessions into SQLite
- `cli/felkod` — the CLI (Python, stdlib only)
- `skill/` — `SKILL.md` for agents, generated from the CLI's `--help` by `skill/generate.py`
- `spike/` — the Phase 0 test program, and the udev rule
- `vendor/ediabaslib/` — EdiabasLib (git submodule, GPLv3)
- `vendor/bmw/` — your BMW SGBD files (not in the repo; see `vendor/README.md`)

## License

GPL-3.0-or-later (see `LICENSE`). felkodd compiles in EdiabasLib, which is GPLv3.
