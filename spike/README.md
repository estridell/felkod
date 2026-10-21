# Phase 0 spike

Answers one question: can EdiabasLib talk to the MS43 through the K+DCAN cable on Linux?
It compiles EdiabasLib's sources (submodule in `vendor/ediabaslib`) for `net10.0`, since upstream
only targets Windows and Android. Output is JSON; raw EDIABAS/IFH traces land in `traces/<timestamp>/`.

```
dotnet build -c Release
alias spike=./bin/Release/net10.0/felkod-spike
spike jobs                          # offline: list MS430DS0.prg jobs
spike results STATUS_MESSWERTEBLOCK # offline: a job's results
spike run IDENT                     # needs the car
spike bench STATUS_MESSWERTEBLOCK 20
spike log                           # loop until Ctrl+C, one JSON line per job in traces/<ts>/log.jsonl
```

Options: `--port` (default `/dev/felkod-kline`), `--sgbd` (default `ms430ds0`), `--ecu-path`, `--trace-dir`,
`--job-timeout` (seconds, default 15), `--allow-write yes` (needed for any job that is not read-only:
`STEUERN_*`, `*_LOESCHEN`, `*_PROGRAMMIEREN`, ...). A job whose `JOB_STATUS` is not `OKAY` (the ECU
rejected it) counts as failed and its values are never reported.

Log options: `--jobs` (comma list, default `STATUS_MESSWERTEBLOCK`), `--slow-jobs` (run every
`--slow-every` cycles, default 10), `--duration` (seconds, 0 = until Ctrl+C).
The log survives stalls, ignition cycles and cable replugs: a failed job writes a `link_lost` event,
a fresh connection is probed every second (`still_down` every 10 s), and logging resumes with a
`link_restored` event as soon as the DME answers. Other job failures write a `job_error` line and
logging continues. Stop with Ctrl+C, or `kill -TERM <pid>` for a background run; the summary is
printed and traces are flushed. A second signal exits immediately.

## One-time setup (done 2026-10-21)

- `dotnet-sdk` (10.0) installed, user added to `uucp` (log out and in once)
- `99-felkod.rules` copied to `/etc/udev/rules.d/`: 1 ms FTDI latency, `/dev/felkod-kline` symlink

## In the car

1. Cable switch in the position that bridges OBD pins 7 and 8.
2. Plug in the cable, then check: `ls -l /dev/felkod-kline` and
   `cat /sys/bus/usb-serial/devices/ttyUSB0/latency_timer` (should print 1).
3. Ignition on (position 2), engine off:
   ```
   spike run IDENT
   spike run FS_LESEN
   spike run STATUS_MESSWERTEBLOCK
   spike bench STATUS_MESSWERTEBLOCK 20
   spike bench STATUS_MOTORDREHZAHL 50
   ```
4. Engine idling: repeat `run STATUS_MESSWERTEBLOCK` and both `bench` lines.

Keep the `traces/` folders: they hold the raw telegrams for the replay simulator.
