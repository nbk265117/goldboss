"""Checks that every localization key referenced by the WPF app exists in every language file.
Dynamic keys like Loc.Instance[$"EntityType.{t}"] are expanded from the domain enums."""
import json, pathlib, re, sys
root = pathlib.Path(__file__).resolve().parent.parent
app = root / "src" / "DofusCompanion.App"
langs = {p.stem: json.loads(p.read_text(encoding="utf-8")) for p in (app / "Localization").glob("*.json")}
used = set()
for f in list(app.rglob("*.xaml")) + list(app.rglob("*.cs")):
    if "obj" in f.parts or "bin" in f.parts: continue
    t = f.read_text(encoding="utf-8")
    used |= set(re.findall(r"\{l:T ([A-Za-z0-9.]+)\}", t))
    used |= {k for k in re.findall(r'(?:Loc\.Instance|\bL)(?:\[|\.Format\()"([A-Za-z0-9.]+)"', t)}
    used |= {k for k in re.findall(r'"(Error\.[A-Za-z]+)"', t)}
# dynamic prefixes -> enum members
enums = {}
for f in (root / "src").rglob("*.cs"):
    if "obj" in f.parts: continue
    for name, body in re.findall(r"public enum (\w+)\s*\{([^}]*)\}", f.read_text(encoding="utf-8")):
        enums[name] = [m for m in re.findall(r"^\s*(\w+)\s*(?:=\s*\w+)?,?", body, re.M) if m]
dyn = {"EntityType": "EntityType", "MechanicType": "MechanicType", "Verification": "VerificationStatus", "Reliability": "SourceReliability",
       "NoteKind": "NoteKind", "Condition": "ConditionResult", "Gap": "GapKind", "Stale": "StalenessReason", "Confirmation": "ConfirmationState",
       "RecordKind": "RecordKind", "ChangeKind": "ChangeKind", "Conflict": "ConflictReason", "Resolution": "Resolution"}
for prefix, enum in dyn.items():
    for m in enums.get(enum, []): used.add(f"{prefix}.{m}")
for m in enums.get("RelationType", []):
    used |= {f"Relation.{m}.Out", f"Relation.{m}.In"}
used |= {"Hotkey.ToggleOverlay", "Hotkey.ToggleClickThrough", "Hotkey.Capture", "Theme.Dark", "Theme.Light"}
bad = 0
for lang, table in sorted(langs.items()):
    missing = sorted(k for k in used if k not in table)
    for k in missing: print(f"{lang}: missing {k}"); bad += 1
keys = [set(t) for t in langs.values()]
for lang, table in langs.items():
    extra = set(table) - set.union(*[k for k in keys])
for a in langs:
    for b in langs:
        if a < b:
            for k in sorted(set(langs[a]) ^ set(langs[b])): print(f"{a}/{b}: key only in one file: {k}"); bad += 1
print(f"{len(used)} keys used, {len(langs)} languages, {bad} problem(s)")
sys.exit(1 if bad else 0)
