# felkod

Agent-first diagnostics for a 2002 BMW E46 320Ci (M54B22, Siemens MS43), built on
[EdiabasLib](https://github.com/uholeschak/ediabaslib).

The goal is INPA / NCS Expert / MS4X Flasher / TunerPro functionality behind one CLI
that returns structured JSON, with background logging. Agents do their own research on MS43 internals.

Status: v0.1 in progress. Phase 0 (EdiabasLib on Linux talking to the car) passed on 2026-10-21.

## Layout

- `daemon/` — `felkodd` (C#, .NET 10): owns the K-line, runs SGBD jobs via EdiabasLib, logging sessions into SQLite
- `cli/felkod` — the CLI (Python, stdlib only): talks to felkodd over a unix socket, starts it when needed
- `skill/` — `SKILL.md` for agents, generated from the CLI's `--help` by `skill/generate.py`
- `devtools/` — fake DS2 ECU replaying the recorded car session, and `smoke.sh` (end-to-end checks)
- `spike/` — Phase 0 test program
- `vendor/ediabaslib/` — EdiabasLib (git submodule, GPLv3)
- `vendor/bmw/` — SGBDs, INPA scripts and NCS data from the cable kit (not committed; see `vendor/README.md`)

## Setup

```
git submodule update --init
dotnet build -c Release daemon
ln -s "$PWD/cli/felkod" ~/.local/bin/felkod
```

Linux: install `spike/99-felkod.rules` into `/etc/udev/rules.d/` (1 ms FTDI latency, `/dev/felkod-kline`)
and add your user to `uucp`.

## Use

```
felkod status
felkod ident
felkod faults
felkod log start --jobs STATUS_MESSWERTEBLOCK,STATUS_LMM_SPANNUNG --slow-jobs STATUS_LAMBDA_ADD_1,STATUS_LAMBDA_MUL_1
felkod log stop
felkod sessions
felkod session query <id> "SELECT t, name, value, unit FROM series WHERE name LIKE 'STAT_MOTORDREHZAHL%'"
```

`--native` (log, read, bench) sends single-telegram jobs with felkodd's own DS2 transport and a
configurable gap (`--gap-ms`, default BMW's 100 ms), then decodes the reply through the SGBD, so values are
identical to EdiabasLib's while the rate is set by the wire, not EdiabasLib's fixed 100 ms regeneration time.
Raw telegrams are kept in the session's `raw` table and can be re-decoded with `felkod ds2 decode`.

Sessions live in `~/.local/share/felkod/sessions/<id>/` (`session.sqlite`, `meta.json`, raw telegram trace `ifh.trc`).
Jobs that change the car need `--allow-write`; `faults clear` needs `--confirm` and saves the faults to
`~/.local/share/felkod/audit.jsonl` first.

## Hardware

K+DCAN cable with FTDI FT232RL, switch on K (left) so OBD pins 7 and 8 are bridged. On Linux it appears as `/dev/ttyUSB0`.
