# Contributing

Pull requests are welcome — bug fixes, translations, new checks for disks and hardware.

- Build: `.\build.ps1` (only the C# compiler that ships with Windows / .NET Framework 4.x is needed; C# 5 syntax).
- UI text: the source strings are Russian, wrapped in `L.T("…")` / `L.F("… {0}", x)`; add the English pair to `src/lang/En.*.cs`
  and run `python tools/check_lang.py`. New languages are welcome.
- Anything that touches disk wipe / partitioning must be tested on a spare disk and described in the PR.
- The program must not send any new data without describing it in README → Privacy.

By contributing you agree that your contribution is licensed under the [MIT License](LICENSE).
