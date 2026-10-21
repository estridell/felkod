# felkod

Agent-first diagnostics for a 2002 BMW E46 320Ci (M54B22, Siemens MS43), built on
[EdiabasLib](https://github.com/uholeschak/ediabaslib).

The goal is INPA / NCS Expert / MS4X Flasher / TunerPro functionality behind one CLI
that returns structured JSON, with background logging and built-in MS43 knowledge.

Status: planning. Nothing is built yet.

## Layout

- `vendor/bmw/` — SGBDs, INPA scripts and NCS data from the cable kit (not committed; see `vendor/README.md`)

## Hardware

K+DCAN cable with FTDI FT232RL, pins 7 and 8 bridged for the E46. On Linux it appears as `/dev/ttyUSB0`.
