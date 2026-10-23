# Contributing

Issues: include the command, its JSON output, the Linux distribution, the .NET version and the SGBD
(and ECU software version if known). Remove the VIN from sessions and traces before attaching them.
Do not attach BMW software, SGBDs or firmware dumps.

Pull requests: branch from `main`, keep to the existing C# and Python style, and add a check in `tests/`
for a change in behaviour. Run the checks in the README's Development section; CI runs the same ones.
After changing CLI help, regenerate `skill/SKILL.md` with `python3 skill/generate.py` (Python 3.14).
Tests stay hardware-free: EdiabasLib's test SGBDs, synthetic data, a pseudo-terminal as the ECU. Say in
the pull request what, if anything, was run on a car, with which ECU and whether `--native` was used.

Updating the EdiabasLib submodule: `daemon/Sgbd.cs` binds EdiabasLib's private register list, so check
that it still exists and works the same way.

Contributions are licensed GPL-3.0-or-later, like the rest of the repository.
