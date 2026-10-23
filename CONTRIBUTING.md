# Contributing

Open an issue with reproduction steps, expected behavior, actual output, Linux distribution,
.NET version and the SGBD/ECU version. Review traces for VINs or other identifiers before attaching
them. Do not attach BMW software, SGBDs, firmware dumps or other proprietary files.

For changes, branch from `main` and open a pull request. Follow the existing C# and Python style,
include regression coverage for behavior changes, and run the checks in README's Development section.
Regenerate `skill/SKILL.md` with `python3 skill/generate.py` when changing CLI help; it needs
Python 3.14, the version CI checks it with, because argparse formats help differently between versions.

Keep tests hardware-free using the upstream test SGBDs or synthetic data. State separately what was
tested on a car, which ECU/software version was used, and whether the native transport was involved.
Never assume a simulator result establishes safe behavior on a real ECU. Changes to write guards,
reply validation or retry behavior should explain how uncertain replies are handled.

Contributions are provided under the repository's GPL-3.0-or-later license. Retain upstream notices
and do not update the EdiabasLib submodule without reviewing compatibility, including the register
binding shim in `daemon/Sgbd.cs`.

For a security issue, use GitHub's private vulnerability reporting if it is enabled for this
repository. Otherwise contact the maintainer privately through their GitHub profile before posting
exploit details publicly.
