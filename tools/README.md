# Signature tooling

Scripts for (re-)deriving the gamedata signatures in `.asset/gamedata/yappershq.sendproxy.jsonc`
when CS2 updates. See `docs/REVERSE_ENGINEERING.md` §8 for the full manual recipe.

## makesig.py (nosoop)

Upstream: https://github.com/nosoop/ghidra_scripts/blob/master/makesig.py (vendored, attribution kept).
Ghidra script (Jython) — generates the **shortest unique** signature for the function at the cursor /
entry, auto-wildcarding only `ADDRESS`/`DYNAMIC` operands (rip-relative refs, relocations). Run it from
the Ghidra GUI: put the cursor in the target function → Script Manager → makesig.py → "start of function".
Outputs both IDA (`55 48 ?`) and SourceMod (`\x55\x48\x2A`) forms.

Note: Ghidra 11+/12 dropped bundled Jython — install the Jython extension to use it in the GUI, or use
the Java port below for headless runs.

## GhidraEncodeFieldSig.java (headless port)

A self-contained Java GhidraScript implementing the same makesig algorithm, hardcoded to
`CFlattenedSerializer::EncodeField` (Ghidra addr `0x4334e0` = file-vaddr `0x3334e0` + the 0x100000 ELF
image base). Run headless:

```bash
analyzeHeadless <ghidra_project_dir> <projName> -process -noanalysis \
  -scriptPath <dir-with-this-script> -postScript GhidraEncodeFieldSig.java
```

Output (build 2026-06-02 libnetworksystem.so):
```
fn entry: 004334e0
MATCHES: 1
BYTES: 21
IDA_SIG: 55 48 89 E5 41 57 49 89 D7 41 56 41 55 41 54 41 BC 01 00 00 00
```

To re-target another function, change `toAddr(0x4334e0L)` (use the Ghidra address = file-vaddr +
image base). For a clean derivation point on a new build, find the entry via the string-anchor walk in
the RE doc, then run this to get the canonical shortest-unique sig.
