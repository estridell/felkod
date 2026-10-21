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
```

Options: `--port` (default `/dev/felkod-kline`), `--sgbd` (default `ms430ds0`), `--ecu-path`, `--trace-dir`.

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
