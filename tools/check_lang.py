"""Localization check: python tools/check_lang.py [file.cs ...]
1) every L.T("…") / L.F("…") key has an English pair in src/lang/En.*.cs;
2) Russian string literals that are not wrapped in L.T / L.F (review: some are intentional — ids, parsed command output);
3) L.F placeholders {0}… match between Russian and English.
"""
import re, sys, pathlib

ROOT = pathlib.Path(__file__).resolve().parent.parent
SRC = ROOT / "src"
LIT = r'@?"(?:[^"\\\n]|\\.|"")*"'
CYR = re.compile(r"[А-Яа-яЁё]")


def unq(lit: str) -> str:
    if lit.startswith("@"):
        return lit[2:-1].replace('""', '"')
    s = lit[1:-1]
    return re.sub(r"\\(u[0-9a-fA-F]{4}|.)", lambda m: chr(int(m.group(1)[1:], 16)) if m.group(1)[0] == "u" and len(m.group(1)) == 5
                  else {"n": "\n", "r": "\r", "t": "\t", "0": "\0"}.get(m.group(1), m.group(1)), s)


def tables():
    d = {}
    for f in (SRC / "lang").glob("En.*.cs"):
        lits = [unq(m.group(0)) for m in re.finditer(LIT, f.read_text(encoding="utf-8-sig"))]
        for i in range(0, len(lits) - 1, 2):
            d[lits[i]] = (lits[i + 1], f.name)
    return d


def main():
    sys.stdout.reconfigure(encoding="utf-8")
    en = tables()
    files = [pathlib.Path(a) for a in sys.argv[1:]] or sorted(list(SRC.glob("*.cs")) + list(SRC.glob("Pawn/*.cs")))
    missing = unwrapped = badfmt = 0
    for f in files:
        text = f.read_text(encoding="utf-8-sig")
        wrapped_spans = []
        for m in re.finditer(r"\bL\.(T|F)\(\s*(" + LIT + r")", text):
            wrapped_spans.append(m.span(2))
            key = unq(m.group(2))
            line = text.count("\n", 0, m.start()) + 1
            if key not in en:
                missing += 1; print(f"MISSING  {f.name}:{line}: {key!r}")
            elif m.group(1) == "F":
                ph = lambda s: sorted(set(re.findall(r"(?<!\{)\{(\d+)[^}]*\}", s)))
                if ph(key) != ph(en[key][0]):
                    badfmt += 1; print(f"FORMAT   {f.name}:{line}: {key!r} -> {en[key][0]!r}")
        for m in re.finditer(LIT, text):
            if not CYR.search(m.group(0)): continue
            if any(a <= m.start() < b for a, b in wrapped_spans): continue
            ls = text.rfind("\n", 0, m.start()) + 1
            if text[ls:m.start()].lstrip().startswith("//"): continue
            unwrapped += 1
            print(f"RUSSIAN  {f.name}:{text.count(chr(10), 0, m.start()) + 1}: {unq(m.group(0))[:90]!r}")
    print(f"\nmissing={missing} unwrapped_russian={unwrapped} format_mismatch={badfmt} pairs={len(en)}")


if __name__ == "__main__":
    main()
