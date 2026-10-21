# vendor

`vendor/bmw/` holds files taken from the BMW Standard Tools kit that came with the cable
(`Inpa_2026_01_OBD_Specialisten.7z`, EDIABAS 7.3.0, NCS Expert 4.0.1, data files v74).
They are BMW's proprietary files, so the directory is gitignored. Never commit it or ship it.

| Path | From the kit | Used for |
| --- | --- | --- |
| `bmw/ediabas/ecu/` | `EDIABAS/Ecu` | SGBDs (`.prg`) and group files (`.grp`) that EdiabasLib executes. MS43 is `MS430DS0.prg`, resolved via `d_motor.grp` |
| `bmw/ediabas/doku/` | `EDIABAS/Doku` | EDIABAS reference (BEST/2, API, FAQ) |
| `bmw/ediabas/obd.ini` | `OBD_INI/obd.ini` | Windows OBD interface config, reference only |
| `bmw/inpa/sgdat/` | `EC-APPS/INPA/SGDAT` | INPA scripts. The 87 `.ips` files are readable source; `.ipo` are compiled |
| `bmw/inpa/cfgdat/` | `EC-APPS/INPA/CFGDAT` | INPA menu config per chassis (`E46.ENG`) |
| `bmw/ncs/daten-e46/` | `NCSEXPER/DATEN/E46` | NCS Expert coding data for the E46 |
| `bmw/ncs/sgdat/` | `NCSEXPER/SGDAT` | NCS Expert SGBDs |
| `bmw/docs/` | kit root | E46 code list, ECU lists, INPA FAQ, install guide |

Note: file names in `ecu/` mix upper and lower case (`d_motor.grp`, `D_MOTOR2.grp`).
Linux is case sensitive, so check how EdiabasLib resolves names before renaming anything.

To repopulate, extract those folders from the kit archive (the password is in the seller's download info)
into the paths above.
