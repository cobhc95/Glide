"""Audit the master action registry (5.0.x): every user-invokable function is an AppActionRegistry entry.

Static checks (run without building; the C# guard tests in tests/Glide.Core.Tests/AppActionRegistryTests.cs and
HotkeyEndToEndTests.cs cover the runtime side):
  1. every GlideCommand enum value has a registry entry and a handler in CreateCommandDispatcher (no bare
     Task.CompletedTask placeholders);
  2. every bindable Toggle / Choice setting in SettingsCatalog has an accessor in SettingAccessors.cs, or an
     exclusion reason in SettingActionCatalog.Excluded;
  3. every Choice option list in SettingActionCatalog.Choices matches the Settings combo box items (same strings,
     same order) so a cycle hotkey walks exactly the values Settings offers;
  4. every MenuItem built in MainWindow*.cs is tagged with MenuActions.Tag / MenuActions.Context (or is a
     separator / submenu header), so context-menu items stay tied to the registry;
  5. every status-bar Button in MainWindow.axaml is mapped in StatusButtonActionIds;
  6. no hand-written HotkeyActionDefinition / GestureActionDefinition lists outside the registry.
Exit code 1 on any violation.
"""
import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

root = Path(__file__).resolve().parent.parent
src = root / "src"
core = src / "Glide.Core"
app = src / "Glide.App"


def read(path: Path) -> str:
    return path.read_text(encoding="utf-8")


problems: list[str] = []

# ---- 1. commands --------------------------------------------------------------------------------------------
enum_text = read(core / "Commands/GlideCommand.cs")
enum_body = enum_text[enum_text.index("{") + 1: enum_text.rindex("}")]
enum_body = re.sub(r"//.*", "", enum_body)
enum_body = re.sub(r"/\*.*?\*/", "", enum_body, flags=re.S)
enum_body = re.sub(r"///.*", "", enum_body)
commands = [c.strip().split("=")[0].strip() for c in enum_body.split(",") if c.strip()]
commands = [c for c in commands if c and c not in ("None", "ChangeSetting")]
registry = read(core / "Commands/AppActionRegistry.cs")
dispatcher = read(app / "MainWindow.Commands.cs")
for command in commands:
    if f"GlideCommand.{command})" not in registry and f"GlideCommand.{command}," not in registry:
        problems.append(f"GlideCommand.{command} has no AppActionRegistry entry")
    if f"[GlideCommand.{command}]" not in dispatcher:
        problems.append(f"GlideCommand.{command} has no CreateCommandDispatcher handler")
placeholders = re.findall(r"\[GlideCommand\.(\w+)\]\s*=\s*\(\)\s*=>\s*(?:Task\.CompletedTask|\{\s*(?:return\s+)?Task\.CompletedTask;?\s*\})", dispatcher)
problems += [f"GlideCommand.{c} is mapped to a bare Task.CompletedTask placeholder" for c in placeholders]

# ---- 2. bindable settings have accessors or exclusions ----------------------------------------------------------
catalog = read(core / "Settings/SettingsCatalog.cs")
kinds = dict(re.findall(r'new\("([^"]+)",\s*"[^"]+",\s*"[^"]*",\s*"(?:[^"\\]|\\.)*",\s*SettingKind\.(\w+)', catalog))
actions_catalog = read(core / "Settings/SettingActionCatalog.cs")
choices_block = actions_catalog[actions_catalog.index("Choices { get; }"):actions_catalog.index("SkippedCycleValues")]
excluded_block = actions_catalog[actions_catalog.index("Excluded { get; }"):actions_catalog.index("IsBindable")]
choices = {m.group(1): re.findall(r'"((?:[^"\\]|\\.)*)"', m.group(2)) for m in re.finditer(r'\["([^"]+)"\]\s*=\s*\[(.*?)\],', choices_block)}
excluded = set(re.findall(r'\["([^"]+)"\]\s*=', excluded_block))
accessors = set(re.findall(r'\["([^"]+)"\]\s*=\s*(?:Bool|Text)\(', read(app / "Settings/SettingAccessors.cs")))
for setting_id, kind in kinds.items():
    if kind not in ("Toggle", "Choice") or setting_id in excluded:
        continue
    if kind == "Choice" and setting_id not in choices:
        problems.append(f"Choice setting {setting_id} has neither an option list (SettingActionCatalog.Choices) nor an exclusion reason")
        continue
    if setting_id not in accessors:
        problems.append(f"bindable setting {setting_id} has no accessor in SettingAccessors.cs")
for setting_id in excluded:
    if setting_id not in kinds:
        problems.append(f"SettingActionCatalog.Excluded names an unknown setting: {setting_id}")

# ---- 3. choice lists match the Settings combo boxes ---------------------------------------------------------------
settings_code = read(app / "Settings/SettingsWindow.axaml.cs")
mapping = {sid: ctl for sid, ctl in re.findall(r'M\("([^"]+)",\s*(\w+)\)', settings_code)}
A = "{https://github.com/avaloniaui}"
X = "{http://schemas.microsoft.com/winfx/2006/xaml}"
tree = ET.parse(app / "Settings/SettingsWindow.axaml")
combos = {e.get(X + "Name"): [i.get("Content") for i in e.iter(A + "ComboBoxItem") if i.get("Content") is not None]
          for e in tree.iter(A + "ComboBox")}
# Combo boxes filled in code: they show display labels while each item's Tag holds the stored value, so their option
# list (the stored values) cannot equal XAML items. The C# test named here checks labels, order and stored values.
#  - ExplorerDefaultViewCombo (5.0.6): shows ExplorerViewModes.Labels ("Thumbnails (L)"...) over ExplorerViewModes.Names;
#    Release506Tests.Explorer_default_view_shows_the_explorer_tab_labels_and_keeps_the_stored_names.
code_populated = {"ExplorerDefaultViewCombo"}
for setting_id, options in choices.items():
    control = mapping.get(setting_id)
    if control is None:
        problems.append(f"Choices names {setting_id}, which has no Settings editor")
        continue
    if control in code_populated:
        if f"{control}.Items.Add(" not in settings_code:
            problems.append(f"{setting_id}: {control} is listed as filled in code, but SettingsWindow no longer fills it")
        continue
    xaml_items = combos.get(control)
    if xaml_items is None:
        problems.append(f"{setting_id}: editor {control} is not a ComboBox")
    elif [o.replace("\\'", "'") for o in options] != xaml_items:
        problems.append(f"{setting_id}: option list {options} differs from the Settings combo box {xaml_items}")

# ---- 4. every MenuItem in the main window builders is tagged ---------------------------------------------------------
untagged = []
for path in sorted(app.glob("MainWindow*.cs")):
    for number, line in enumerate(read(path).splitlines(), 1):
        if "new MenuItem" not in line:
            continue
        if 'Header = "-"' in line or "MenuActions." in line:
            continue
        # Submenu headers carry no action of their own; their children are checked.
        if re.search(r'var (sizeMenu|positionMenu|view|sort|group|menu) = new MenuItem', line):
            continue
        if "var item = new MenuItem { Header = header, IsEnabled = enabled }" in line or \
           "var item = new MenuItem { Header = check ?" in line or \
           "var item = new MenuItem { Header = label, Icon = MenuIcon(icon), IsEnabled = button.IsEnabled }" in line:
            continue  # local helpers that tag on the next lines
        untagged.append(f"{path.name}:{number}: {line.strip()[:110]}")
problems += [f"MenuItem not tied to the registry (use MenuActions.Tag / MenuActions.Context): {u}" for u in untagged]

# ---- 5. status-bar buttons are mapped -----------------------------------------------------------------------------------
main_tree = ET.parse(app / "MainWindow.axaml")
status = next((e for e in main_tree.iter() if e.get(X + "Name") == "ViewerStatusSurface"), None)
status_layout = read(app / "MainWindow.StatusLayout.cs")
if status is None:
    problems.append("ViewerStatusSurface not found in MainWindow.axaml")
else:
    names = [b.get(X + "Name") for b in status.iter(A + "Button")] + ["StatusRestoreButton"]
    for name in names:
        if f"[{name}] =" not in status_layout:
            problems.append(f"status-bar button {name} is not mapped in StatusButtonActionIds")

# ---- 6. no second hand-written action list ------------------------------------------------------------------------------
for path in sorted(src.rglob("*.cs")):
    if "obj" in path.parts or path.name in ("AppActionRegistry.cs",):
        continue
    text = read(path)
    if re.search(r"new HotkeyActionDefinition\(|new\(\"[\w.]+\", \"[^\"]+\", \"[^\"]+\", GlideCommand\.", text) and path.name != "HotkeyCatalog.cs":
        problems.append(f"{path.relative_to(root)} declares hotkey actions outside AppActionRegistry")
    if path.name == "GestureCatalog.cs" and re.search(r"new\((\w+), \"[^\"]+\", GlideCommand\.", text):
        problems.append("GestureCatalog.cs declares gesture actions by hand again (read them from AppActionRegistry)")

if problems:
    print("Action registry audit FAILED:")
    for problem in problems:
        print("  -", problem)
    raise SystemExit(1)
print(f"Action registry audit passed: {len(commands)} commands registered and dispatched; "
      f"{len(accessors)} setting accessors, {len(choices)} choice lists in sync with Settings, {len(excluded)} documented exclusions; "
      f"menu builders and {len(names)} status-bar buttons tied to the registry.")
