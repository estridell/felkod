# vendor

`vendor/ediabaslib/` is the EdiabasLib git submodule (GPLv3): `git submodule update --init`.

`vendor/bmw/` holds files from your own EDIABAS/INPA installation. They are BMW's proprietary files,
not part of felkod, so the directory is gitignored. Never commit it or ship it. felkod needs only
`bmw/ediabas/ecu/` (or another directory passed with `felkod daemon start --ecu-path`); the rest is
reference material.

| Path | From the installation | Used for |
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
