# vendor

`vendor/ediabaslib/` is the pinned upstream EdiabasLib submodule (GPLv3). Initialize it with
`git submodule update --init --recursive` from the repository root.

`vendor/bmw/` is an optional local directory for files from your own EDIABAS/INPA installation.
Those files are proprietary and are not supplied by this project. The directory is gitignored;
never commit it or ship it. Only the ECU folder is needed to run felkod; the other paths below
are optional reference material.

| Path | From your installation | Used for |
| --- | --- | --- |
| `bmw/ediabas/ecu/` | `EDIABAS/Ecu` | SGBDs (`.prg`) and group files (`.grp`) that EdiabasLib executes. MS43 is `MS430DS0.prg`, resolved via `d_motor.grp` |
| `bmw/ediabas/doku/` | `EDIABAS/Doku` | EDIABAS reference (BEST/2, API, FAQ) |
| `bmw/ediabas/obd.ini` | `OBD_INI/obd.ini` | Windows OBD interface config, reference only |
| `bmw/inpa/sgdat/` | `EC-APPS/INPA/SGDAT` | INPA scripts. The 87 `.ips` files are readable source; `.ipo` are compiled |
| `bmw/inpa/cfgdat/` | `EC-APPS/INPA/CFGDAT` | INPA menu config per chassis (`E46.ENG`) |
| `bmw/ncs/daten-e46/` | `NCSEXPER/DATEN/E46` | NCS Expert coding data for the E46 |
| `bmw/ncs/sgdat/` | `NCSEXPER/SGDAT` | NCS Expert SGBDs |
| `bmw/docs/` | documentation | E46 code list, ECU lists, INPA FAQ, install guide |

Note: file names in `ecu/` mix upper and lower case (`d_motor.grp`, `D_MOTOR2.grp`).
Linux is case sensitive, so check how EdiabasLib resolves names before renaming anything.

Copy your ECU folder into `vendor/bmw/ediabas/ecu/`, or use
`felkod daemon start --ecu-path /path/to/Ecu`. The project does not provide downloads for BMW files.
