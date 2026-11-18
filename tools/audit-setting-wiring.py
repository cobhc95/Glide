"""Per-setting wiring audit: names the exact missing pieces for each SettingsCatalog id.

Usage:
  python tools/audit-setting-wiring.py               # every setting; exit 1 on a hard gap
  python tools/audit-setting-wiring.py tabs.cycleOrder  # checklist for one or more ids (always printed)
  python tools/audit-setting-wiring.py --warnings    # also list soft findings (Load/Read/normalisation)

Hard gaps (fail):  id prefix without a SettingEffectRegistry.OwnerByPrefix owner (rows are derived from the catalogue),
                   Toggle/Choice with neither a SettingAccessors entry nor a SettingActionCatalog.Excluded reason,
                   Choice accessor without a SettingActionCatalog.Choices option list,
                   no Settings control (BuildSettingControlMap M(...), a SettingsCard.SettingIds anchor or a
                   code-registered _settingControls entry), accessor property missing from GlideSettingsState.
Soft findings:     mapped non-toggle control not referenced in LoadControls / ReadControls (it may be loaded by a helper;
                   plain toggles with a SettingAccessors Bool/Flag row are table-driven),
                   Choice/Number property never mentioned in SettingsStore/SettingsDefaults (no normalisation).
This is a text-level check (regex), the same approach as audit-settings-search.py; it complements, not replaces, the tests.
"""
import re
import sys
from pathlib import Path

root = Path(__file__).resolve().parent.parent
S = root / "src/Glide.App/Settings"
read = lambda p: p.read_text(encoding="utf-8")

catalog = read(root / "src/Glide.Core/Settings/SettingsCatalog.cs")
effects = read(root / "src/Glide.Core/Settings/SettingEffectRegistry.cs")
action_catalog = read(root / "src/Glide.Core/Settings/SettingActionCatalog.cs")
accessors_src = read(S / "SettingAccessors.cs")
state = read(root / "src/Glide.Core/Settings/GlideSettingsState.cs")
store = read(S / "SettingsStore.cs") + read(S / "SettingsDefaults.cs")
xaml = read(S / "SettingsWindow.axaml")
window_main = read(S / "SettingsWindow.axaml.cs")
window_all = "\n".join(read(p) for p in sorted(S.glob("SettingsWindow*.cs")))
category_catalog = read(root / "src/Glide.Core/Data/GlideDataCategoryCatalog.cs")

# ---- catalogue: id -> kind ------------------------------------------------------------------------
STR = r'"(?:[^"\\]|\\.)*"'
entry_re = re.compile(r'new\("([^"]+)",\s*' + STR + r',\s*' + STR + r',\s*' + STR + r'(?:\s*\+\s*' + STR + r')*,\s*SettingKind\.(\w+)', re.S)
kind_of = {m.group(1): m.group(2) for m in entry_re.finditer(catalog)}
all_ids = set(re.findall(r'new\("([a-z][A-Za-z0-9]*\.[A-Za-z0-9.]+)",\s*"', catalog)) | set(kind_of)
data_ids = set(re.findall(r'"((?:reset|backup)\.[A-Za-z]+)"', category_catalog))
all_ids |= {i for i in data_ids if f'"{i}"' in catalog or "GlideDataCategoryCatalog" in catalog}

# ---- tables -------------------------------------------------------------------------------------------
owner_start = effects.find("OwnerByPrefix {")
owner_prefixes = set(re.findall(r'\["([^"]+)"\]\s*=\s*SettingEffectOwner\.', effects[owner_start:effects.find("};", owner_start)])) if owner_start >= 0 else set()
accessor_rows = dict(re.findall(r'\["([^"]+)"\]\s*=\s*(?:Bool|Text)\(\s*s\s*=>\s*([^\n]*)', accessors_src))
# Stage C: SettingAccessors.Values = the bindable rows above + Flag/Int/Double/Str rows (numbers, paths, non-bindable toggles).
value_rows = dict(re.findall(r'\["([^"]+)"\]\s*=\s*(?:Bool|Text|Flag|Int|Double|Str)\(\s*s\s*=>\s*([^\n]*)', accessors_src))
table_toggles = set(re.findall(r'\["([^"]+)"\]\s*=\s*(?:Bool|Flag)\(', accessors_src))
ex_start = action_catalog.find("Excluded {")
excluded = set(re.findall(r'\["([^"]+)"\]\s*=', action_catalog[ex_start:])) if ex_start >= 0 else set()
ch_start, ch_end = action_catalog.find("Choices {"), ex_start if ex_start >= 0 else len(action_catalog)
choices = set(re.findall(r'\["([^"]+)"\]\s*=', action_catalog[ch_start:ch_end])) if ch_start >= 0 else set()
mapped = dict(re.findall(r'\bM\("([^"]+)",\s*(\w+)\)', window_all))
anchors = set()
for raw in re.findall(r'SettingsCard\.SettingIds="([^"]+)"', xaml):
    anchors |= {p.strip() for p in raw.split(",") if p.strip()}
code_registered = set(re.findall(r'_settingControls\["([^"]+)"\]\s*=', window_all))
state_props = set(re.findall(r'public\s+[\w<>\[\]?, ]+\s+(\w+)\s*\{\s*get;', state))


def method_body(src, name):
    m = re.search(r'\b[\w<>?]+\s+' + name + r'\s*\([^)]*\)\s*\{', src)
    if not m:
        return ""
    i = m.end() - 1
    depth, j = 0, i
    while j < len(src):
        depth += {"{": 1, "}": -1}.get(src[j], 0)
        if depth == 0:
            return src[i:j]
        j += 1
    return src[i:]


load_body = method_body(window_main, "LoadControls")
read_body = method_body(window_main, "ReadControls")


def check(setting_id):
    hard, soft = [], []
    kind = kind_of.get(setting_id, "?")
    dynamic = setting_id.startswith(("gesture.", "reset.", "backup."))
    if setting_id.split(".")[0] not in owner_prefixes:
        hard.append("id prefix has no owner in SettingEffectRegistry.OwnerByPrefix (src/Glide.Core/Settings/SettingEffectRegistry.cs)")
    if kind in ("Toggle", "Choice") and not dynamic:
        if setting_id not in accessor_rows and setting_id not in excluded:
            hard.append("Toggle/Choice without SettingAccessors entry or SettingActionCatalog.Excluded reason")
        if kind == "Choice" and setting_id in accessor_rows and setting_id not in choices and setting_id not in excluded:
            hard.append("Choice accessor without SettingActionCatalog.Choices option list")
    if setting_id not in mapped and setting_id not in anchors and setting_id not in code_registered and not dynamic:
        hard.append("no Settings control: add M(\"id\", Control) in SettingsWindow BuildSettingControlMap (or a SettingsCard.SettingIds anchor for actions)")
    prop = None
    if setting_id in value_rows:
        pm = re.match(r's\.(\w+)', value_rows[setting_id].strip())
        prop = pm.group(1) if pm else None
        if prop and prop not in state_props:
            hard.append(f"accessor property {prop} not found in GlideSettingsState")
    control = mapped.get(setting_id)
    # Plain toggles with a Bool/Flag accessor are loaded/read by LoadMappedToggles/ReadMappedToggles (no hand-written line).
    if control and kind in ("Toggle", "Choice", "Number", "Text") and setting_id not in table_toggles:
        if not re.search(r'\b' + control + r'\b', load_body):
            soft.append(f"{control} not referenced in LoadControls (fine if a helper loads it)")
        if not re.search(r'\b' + control + r'\b', read_body):
            soft.append(f"{control} not referenced in ReadControls (fine if a helper reads it)")
    if prop and setting_id in accessor_rows and kind in ("Choice", "Number") and not re.search(r'\b' + prop + r'\b', store):
        soft.append(f"{prop} not mentioned in SettingsStore/SettingsDefaults (no load normalisation or default)")
    return kind, hard, soft


# Platform availability (Stage C prep): SettingsCatalog.WindowsOnlyIds marks Windows-only settings; all others are all platforms.
wo_start = catalog.find("WindowsOnlyIds {")
windows_only = set(re.findall(r'"([a-z][A-Za-z0-9]*\.[A-Za-z0-9.]+)"', catalog[wo_start:catalog.find("};", wo_start)])) if wo_start >= 0 else set()
platform_problems = [f"{i}: listed in SettingsCatalog.WindowsOnlyIds but not a catalogued setting" for i in sorted(windows_only - all_ids)]

args = [a for a in sys.argv[1:] if not a.startswith("--")]
show_soft = "--warnings" in sys.argv
targets = args or sorted(all_ids)
hard_total = soft_total = 0
for sid in targets:
    if sid not in all_ids:
        print(f"{sid}: not in SettingsCatalog")
        hard_total += 1
        continue
    kind, hard, soft = check(sid)
    hard_total += len(hard)
    soft_total += len(soft)
    if args or hard or (show_soft and soft):
        print(f"{sid} [{kind}]")
        for h in hard:
            print(f"  MISSING  {h}")
        for s in soft if (args or show_soft) else []:
            print(f"  check    {s}")
        if args and not hard and not soft:
            print("  ok: effect row, accessor/exclusion, control map, state property")

for problem in platform_problems:
    print(f"  MISSING  {problem}")
hard_total += len(platform_problems)
if hard_total:
    print(f"Setting wiring audit FAILED: {hard_total} missing piece(s).")
    raise SystemExit(1)
print(f"Setting wiring audit passed: {len(all_ids)} settings checked ({len(kind_of)} with parsed kind); "
      f"{len(windows_only)} Windows-only, the rest all platforms; {soft_total} soft finding(s){'' if show_soft else ' (run with --warnings to list)'}.")
